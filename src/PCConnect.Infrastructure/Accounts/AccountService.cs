using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Accounts;

public interface IAccountService
{
    Task<Profile> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<Profile> UpdateProfileAsync(Guid userId, Guid sessionId, string stepUpGrant, ProfileUpdate request, CancellationToken cancellationToken);
    Task ChangePasswordAsync(Guid userId, Guid sessionId, string stepUpGrant, PasswordChangeRequest request, CancellationToken cancellationToken);
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken);
    Task ResetPasswordAsync(TokenRequest token, string newPassword, CancellationToken cancellationToken);
    Task VerifyEmailAsync(string token, CancellationToken cancellationToken);
    Task<Page<Session>> ListSessionsAsync(Guid userId, Guid currentSessionId, string? cursor, int limit, CancellationToken cancellationToken);
    Task RevokeSessionAsync(Guid userId, Guid sessionId, Guid targetSessionId, CancellationToken cancellationToken);
    Task<ExportJob> RequestExportAsync(Guid userId, Guid sessionId, string stepUpGrant, CancellationToken cancellationToken);
    Task<ExportJob> GetExportAsync(Guid userId, Guid exportId, CancellationToken cancellationToken);
    Task<byte[]> GetExportContentAsync(Guid userId, Guid exportId, CancellationToken cancellationToken);
    Task RequestDeletionAsync(Guid userId, Guid sessionId, string stepUpGrant, CancellationToken cancellationToken);
}

public sealed class AccountService(NpgsqlDataSource dataSource, IPasswordHasher passwords, IOpaqueTokenService tokens, IEmailOutbox emailOutbox, IExportArtifactStore exports, IClock clock, StepUpGrantConsumer stepUp) : IAccountService
{
    public async Task<Profile> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ProfileSelect + " WHERE id=@userId AND account_state<>'deleted'");
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("profile_not_found");
        return ReadProfile(reader);
    }

    public async Task<Profile> UpdateProfileAsync(Guid userId, Guid sessionId, string stepUpGrant, ProfileUpdate request, CancellationToken cancellationToken)
    {
        if (request.DisplayName is null && request.Email is null && !request.DateOfBirth.IsSpecified && request.MarketingOptIn is null && request.Timezone is null)
            throw new ArgumentException("At least one profile field is required.");
        if (request.DisplayName is { } displayName && displayName.Trim().Length is < 1 or > 100) throw new ArgumentException("Display name must be 1-100 characters.");
        if (request.Email is { } email && (email.Length > 320 || !email.Contains('@', StringComparison.Ordinal))) throw new ArgumentException("Email is invalid.");
        if (request.Timezone is { } timezone) _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var now = clock.UtcNow;
        string? emailToken = null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.SecurityChange, null, cancellationToken);
        string currentEmail;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT email::text FROM users WHERE id=@userId AND account_state='active' FOR UPDATE";
            current.Parameters.AddWithValue("userId", userId);
            currentEmail = await current.ExecuteScalarAsync(cancellationToken) as string ?? throw new ResourceNotFoundException("profile_not_found");
        }
        var pendingEmail = request.Email is null || string.Equals(request.Email.Trim(), currentEmail, StringComparison.OrdinalIgnoreCase)
            ? null
            : request.Email.Trim();
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE users SET display_name=COALESCE(@displayName,display_name),
              date_of_birth=CASE WHEN @dateOfBirthSpecified THEN @dateOfBirth ELSE date_of_birth END,marketing_opt_in=COALESCE(@marketing,marketing_opt_in),
              marketing_consent_at=CASE WHEN @marketing IS NULL THEN marketing_consent_at WHEN @marketing THEN COALESCE(marketing_consent_at,@now) ELSE NULL END,
              timezone=COALESCE(@timezone,timezone),timezone_assumed=CASE WHEN @timezone IS NULL THEN timezone_assumed ELSE false END,
              updated_at=@now,row_version=row_version+1
            WHERE id=@userId AND account_state='active' AND (@expected IS NULL OR row_version=@expected)
            RETURNING id;
            """;
        update.Parameters.Add(new("displayName", NpgsqlDbType.Text) { Value = request.DisplayName is null ? DBNull.Value : request.DisplayName.Trim() });
        update.Parameters.Add(new("dateOfBirth", NpgsqlDbType.Date) { Value = request.DateOfBirth.Value is null ? DBNull.Value : request.DateOfBirth.Value.Value });
        update.Parameters.AddWithValue("dateOfBirthSpecified", request.DateOfBirth.IsSpecified);
        update.Parameters.Add(new("marketing", NpgsqlDbType.Boolean) { Value = request.MarketingOptIn is null ? DBNull.Value : request.MarketingOptIn.Value });
        update.Parameters.Add(new("timezone", NpgsqlDbType.Text) { Value = request.Timezone is null ? DBNull.Value : request.Timezone });
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("userId", userId);
        update.Parameters.Add(new("expected", NpgsqlDbType.Bigint) { Value = request.ExpectedVersion is null ? DBNull.Value : request.ExpectedVersion.Value });
        try
        {
            if (await update.ExecuteScalarAsync(cancellationToken) is null) throw new ConflictException("version_conflict", "The profile changed since it was read.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("email_conflict", "That email address is already in use.");
        }
        if (pendingEmail is not null)
        {
            await using (var uniqueness = connection.CreateCommand())
            {
                uniqueness.Transaction = transaction;
                uniqueness.CommandText = "SELECT EXISTS(SELECT 1 FROM users WHERE email=@email AND id<>@userId)";
                uniqueness.Parameters.AddWithValue("email", pendingEmail);
                uniqueness.Parameters.AddWithValue("userId", userId);
                if ((bool)(await uniqueness.ExecuteScalarAsync(cancellationToken) ?? false))
                    throw new ConflictException("email_conflict", "That email address is already in use.");
            }
            emailToken = tokens.Create();
            var expires = now.AddHours(24);
            await using (var createToken = connection.CreateCommand())
            {
                createToken.Transaction = transaction;
                createToken.CommandText = """
                    UPDATE email_tokens SET consumed_at=@now WHERE user_id=@userId AND purpose='confirm_email_change' AND consumed_at IS NULL;
                    INSERT INTO email_tokens(id,user_id,purpose,token_hash,pending_email,created_at,expires_at)
                    VALUES(@id,@userId,'confirm_email_change',@hash,@email,@now,@expires);
                    """;
                createToken.Parameters.AddWithValue("now", now);
                createToken.Parameters.AddWithValue("userId", userId);
                createToken.Parameters.AddWithValue("id", Guid.CreateVersion7(now));
                createToken.Parameters.AddWithValue("hash", tokens.Hash(emailToken));
                createToken.Parameters.AddWithValue("email", pendingEmail);
                createToken.Parameters.AddWithValue("expires", expires);
                await createToken.ExecuteNonQueryAsync(cancellationToken);
            }
            await emailOutbox.EnqueueAsync(connection, transaction, userId, pendingEmail, "confirm_email_change", emailToken, now, expires, cancellationToken);
        }
        await using (var revokeGrants = connection.CreateCommand())
        {
            revokeGrants.Transaction = transaction;
            revokeGrants.CommandText = "UPDATE step_up_grants SET consumed_at=@now WHERE user_id=@userId AND consumed_at IS NULL";
            revokeGrants.Parameters.AddWithValue("now", now);
            revokeGrants.Parameters.AddWithValue("userId", userId);
            await revokeGrants.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        emailToken = null;
        return await GetProfileAsync(userId, cancellationToken);
    }

    public async Task ChangePasswordAsync(Guid userId, Guid sessionId, string stepUpGrant, PasswordChangeRequest request, CancellationToken cancellationToken)
    {
        PasswordPolicy.ValidatePresented(request.CurrentPassword);
        PasswordPolicy.ValidateNew(request.NewPassword);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.SecurityChange, null, cancellationToken);
        string? hash;
        string? legacy;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT password_hash,legacy_sha256 FROM password_credentials WHERE user_id=@userId FOR UPDATE";
            select.Parameters.AddWithValue("userId", userId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException();
            hash = reader.IsDBNull(0) ? null : reader.GetString(0);
            legacy = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        var valid = hash is not null ? await passwords.VerifyAsync(request.CurrentPassword, hash, cancellationToken) : legacy is not null && passwords.VerifyLegacySha256(request.CurrentPassword, legacy);
        if (!valid) throw new AuthenticationFailureException();
        var replacement = await passwords.HashAsync(request.NewPassword, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE password_credentials SET password_hash=@hash,hash_algorithm='argon2id',hash_parameters=@parameters::jsonb,
              legacy_sha256=NULL,migrated_at=COALESCE(migrated_at,@now),changed_at=@now WHERE user_id=@userId;
            UPDATE step_up_grants SET consumed_at=@now WHERE user_id=@userId AND consumed_at IS NULL;
            """;
        update.Parameters.AddWithValue("hash", replacement);
        update.Parameters.AddWithValue("parameters", ArgonParameters());
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("userId", userId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = Normalization.AccountIdentifier(email);
        var now = clock.UtcNow;
        var raw = tokens.Create();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            Guid? userId;
            string? recipient;
            await using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT id,email::text FROM users WHERE lower(email::text)=@email AND account_state IN ('active','reset_required') FOR UPDATE";
                lookup.Parameters.AddWithValue("email", normalized);
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) { userId = reader.GetGuid(0); recipient = reader.GetString(1); }
                else { userId = null; recipient = null; }
            }
            if (userId is not null)
            {
                var expires = now.AddHours(1);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE email_tokens SET consumed_at=@now WHERE user_id=@userId AND purpose='reset_password' AND consumed_at IS NULL;
                    INSERT INTO email_tokens(id,user_id,purpose,token_hash,created_at,expires_at)
                    VALUES(@id,@userId,'reset_password',@hash,@now,@expires);
                    """;
                command.Parameters.AddWithValue("id", Guid.CreateVersion7(now));
                command.Parameters.AddWithValue("userId", userId.Value);
                command.Parameters.AddWithValue("hash", tokens.Hash(raw));
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("expires", expires);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await emailOutbox.EnqueueAsync(connection, transaction, userId.Value, recipient!, "reset_password", raw, now, expires, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { raw = string.Empty; }
    }

    public async Task ResetPasswordAsync(TokenRequest token, string newPassword, CancellationToken cancellationToken)
    {
        PasswordPolicy.ValidateNew(newPassword);
        var now = clock.UtcNow;
        var replacement = await passwords.HashAsync(newPassword, cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        Guid userId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT user_id FROM email_tokens WHERE purpose='reset_password' AND token_hash=@hash AND consumed_at IS NULL AND expires_at>@now FOR UPDATE";
            select.Parameters.AddWithValue("hash", tokens.Hash(token.Token));
            select.Parameters.AddWithValue("now", now);
            userId = await select.ExecuteScalarAsync(cancellationToken) is Guid id ? id : throw new ResourceGoneException("reset_token_expired");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE password_credentials SET password_hash=@passwordHash,hash_algorithm='argon2id',hash_parameters=@parameters::jsonb,
              legacy_sha256=NULL,migrated_at=COALESCE(migrated_at,@now),changed_at=@now WHERE user_id=@userId;
            UPDATE users SET account_state='active',updated_at=@now,row_version=row_version+1 WHERE id=@userId AND account_state<>'disabled';
            UPDATE email_tokens SET consumed_at=@now WHERE purpose='reset_password' AND token_hash=@tokenHash;
            UPDATE sessions SET revoked_at=COALESCE(revoked_at,@now),revoked_reason=COALESCE(revoked_reason,'password_reset') WHERE user_id=@userId;
            UPDATE session_refresh_tokens SET state='revoked' WHERE session_id IN (SELECT id FROM sessions WHERE user_id=@userId) AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE session_id IN (SELECT id FROM sessions WHERE user_id=@userId);
            UPDATE step_up_grants SET consumed_at=@now WHERE user_id=@userId AND consumed_at IS NULL;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'SessionRevoked','session',id,1,
              jsonb_build_object('userId',user_id,'sessionId',id,'reason','password_reset'),@now FROM sessions WHERE user_id=@userId;
            """;
        update.Parameters.AddWithValue("passwordHash", replacement);
        update.Parameters.AddWithValue("parameters", ArgonParameters());
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("userId", userId);
        update.Parameters.AddWithValue("tokenHash", tokens.Hash(token.Token));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task VerifyEmailAsync(string token, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH consumed AS (
              UPDATE email_tokens SET consumed_at=@now
              WHERE purpose IN ('verify_email','confirm_email_change') AND token_hash=@hash AND consumed_at IS NULL AND expires_at>@now
              RETURNING user_id,purpose,pending_email
            )
            UPDATE users u SET
              email=CASE WHEN c.purpose='confirm_email_change' THEN c.pending_email ELSE u.email END,
              email_verified_at=@now,updated_at=@now,row_version=row_version+1
            FROM consumed c WHERE u.id=c.user_id RETURNING u.id;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("hash", tokens.Hash(token));
        try
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is null) throw new ResourceGoneException("verification_token_expired");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("email_conflict", "That email address is already in use.");
        }
        await using var grants = connection.CreateCommand();
        grants.Transaction = transaction;
        grants.CommandText = "UPDATE step_up_grants SET consumed_at=@now WHERE user_id=(SELECT user_id FROM email_tokens WHERE token_hash=@hash) AND consumed_at IS NULL";
        grants.Parameters.AddWithValue("now", now);
        grants.Parameters.AddWithValue("hash", tokens.Hash(token));
        await grants.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Page<Session>> ListSessionsAsync(Guid userId, Guid currentSessionId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var position = PageCursor.Decode(cursor);
        await using var command = dataSource.CreateCommand("""
            SELECT id,platform::text,client_name,created_at,last_used_at,LEAST(sliding_expires_at,absolute_expires_at)
            FROM sessions WHERE user_id=@userId AND revoked_at IS NULL
              AND (@cursorTime IS NULL OR (last_used_at,id)<(@cursorTime,@cursorId))
            ORDER BY last_used_at DESC,id DESC LIMIT @limit;
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("cursorTime", NpgsqlDbType.TimestampTz) { Value = position is null ? DBNull.Value : position.Value.Timestamp });
        command.Parameters.Add(new("cursorId", NpgsqlDbType.Uuid) { Value = position is null ? DBNull.Value : position.Value.Id });
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<Session>();
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(new(reader.GetGuid(0), Enum.Parse<PlatformType>(reader.GetString(1), true), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetGuid(0) == currentSessionId));
        var hasMore = sessions.Count > limit;
        if (hasMore) sessions.RemoveAt(sessions.Count - 1);
        return new(sessions, hasMore && sessions.Count > 0 ? PageCursor.Encode(sessions[^1].LastUsedAt, sessions[^1].Id) : null);
    }

    public async Task RevokeSessionAsync(Guid userId, Guid sessionId, Guid targetSessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM sessions WHERE id=@target AND user_id=@userId AND revoked_at IS NULL FOR UPDATE";
            exists.Parameters.AddWithValue("target", targetSessionId);
            exists.Parameters.AddWithValue("userId", userId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) throw new ResourceNotFoundException("session_not_found");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions SET revoked_at=@now,revoked_reason='user_revoked' WHERE id=@target AND user_id=@userId AND revoked_at IS NULL;
            UPDATE session_refresh_tokens SET state='revoked' WHERE session_id IN (SELECT id FROM sessions WHERE id=@target AND user_id=@userId) AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE session_id IN (SELECT id FROM sessions WHERE id=@target AND user_id=@userId);
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(uuidv7(),'SessionRevoked','session',@target,1,
              jsonb_build_object('userId',@userId,'sessionId',@target,'reason','user_revoked'),@now);
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("target", targetSessionId);
        command.Parameters.AddWithValue("userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ExportJob> RequestExportAsync(Guid userId, Guid sessionId, string stepUpGrant, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.DataExport, null, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO data_export_jobs(id,user_id,status,created_at,expires_at) VALUES(@id,@userId,'queued',@now,@expires)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", now.AddHours(48));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(id, "queued", now, now.AddHours(48));
    }

    public async Task<ExportJob> GetExportAsync(Guid userId, Guid exportId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id,status::text,created_at,expires_at,storage_reference FROM data_export_jobs WHERE id=@id AND user_id=@userId");
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("export_not_found");
        var id = reader.GetGuid(0);
        var status = reader.GetString(1);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(3);
        if (expiresAt <= clock.UtcNow) throw new ResourceGoneException("export_expired");
        return new(id, status, reader.GetFieldValue<DateTimeOffset>(2), expiresAt,
            status == "ready" && !reader.IsDBNull(4) ? new Uri($"/api/v2/me/export/{id:D}/download", UriKind.Relative) : null);
    }

    public async Task<byte[]> GetExportContentAsync(Guid userId, Guid exportId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT status::text,expires_at,storage_reference FROM data_export_jobs WHERE id=@id AND user_id=@userId;
            """);
        command.Parameters.AddWithValue("id", exportId);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("export_not_found");
        if (reader.GetFieldValue<DateTimeOffset>(1) <= clock.UtcNow) throw new ResourceGoneException("export_expired");
        if (reader.GetString(0) != "ready" || reader.IsDBNull(2)) throw new ConflictException("export_not_ready", "The export is not ready.");
        return await exports.ReadAsync(exportId, cancellationToken);
    }

    public async Task RequestDeletionAsync(Guid userId, Guid sessionId, string stepUpGrant, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.AccountDelete, null, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE users SET account_state='deletion_pending',updated_at=@now,row_version=row_version+1 WHERE id=@userId AND account_state='active';
            INSERT INTO account_deletion_jobs(id,user_id,status,requested_at) VALUES(@jobId,@userId,'queued',@now);
            UPDATE sessions SET revoked_at=COALESCE(revoked_at,@now),revoked_reason=COALESCE(revoked_reason,'account_deletion') WHERE user_id=@userId;
            UPDATE session_refresh_tokens SET state='revoked' WHERE session_id IN (SELECT id FROM sessions WHERE user_id=@userId) AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE session_id IN (SELECT id FROM sessions WHERE user_id=@userId) OR device_id IN (SELECT id FROM devices WHERE user_id=@userId);
            UPDATE devices SET status='revoked',revoked_at=@now,row_version=row_version+1 WHERE user_id=@userId AND status<>'revoked';
            UPDATE device_credentials SET revoked_at=@now,revoked_reason='account_deletion' WHERE device_id IN (SELECT id FROM devices WHERE user_id=@userId) AND revoked_at IS NULL;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'SessionRevoked','session',id,1,
              jsonb_build_object('userId',user_id,'sessionId',id,'reason','security_change'),@now FROM sessions WHERE user_id=@userId;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'SessionRevoked','device',id,1,
              jsonb_build_object('deviceId',id,'sessionId',id,'reason','device_revoked'),@now FROM devices WHERE user_id=@userId;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("jobId", Guid.CreateVersion7(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private const string ProfileSelect = """
        SELECT id,username::text,email::text,email_verified_at IS NOT NULL,display_name,date_of_birth,marketing_opt_in,timezone,timezone_assumed,created_at,row_version FROM users
        """;

    private static Profile ReadProfile(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5), reader.GetBoolean(6), reader.GetString(7), reader.GetBoolean(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetInt64(10));

    private static string ArgonParameters() => JsonSerializer.Serialize(new { memoryKiB = Argon2IdPasswordHasher.MemoryKiB, iterations = Argon2IdPasswordHasher.Iterations, parallelism = Argon2IdPasswordHasher.Parallelism });
}
