using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Identity;

public sealed class AuthenticationService(
    NpgsqlDataSource dataSource,
    IPasswordHasher passwordHasher,
    IOpaqueTokenService tokens,
    IEmailOutbox emailOutbox,
    LoginAttemptGuard loginAttempts,
    IClock clock) : IAuthenticationService
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UserSlidingLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan UserAbsoluteLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan DeviceSlidingLifetime = TimeSpan.FromDays(90);

    public async Task RegisterAsync(RegistrationRequest request, string correlationId, CancellationToken cancellationToken)
    {
        ValidateRegistration(request);
        ValidateClient(request.Client);
        var now = clock.UtcNow;
        var userId = Guid.CreateVersion7(now);
        var passwordHash = await passwordHasher.HashAsync(request.Password, cancellationToken);
        var emailToken = tokens.Create();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO users (id, username, email, display_name, date_of_birth, marketing_opt_in, marketing_consent_at, timezone, timezone_assumed, created_at, updated_at)
                    VALUES (@id, @username, @email, @displayName, @dateOfBirth, @marketing, @consentAt, @timezone, false, @now, @now);
                    INSERT INTO password_credentials (user_id, password_hash, hash_algorithm, hash_parameters, changed_at)
                    VALUES (@id, @passwordHash, 'argon2id', @parameters::jsonb, @now);
                    INSERT INTO email_tokens (id, user_id, purpose, token_hash, created_at, expires_at)
                    VALUES (@emailTokenId, @id, 'verify_email', @emailTokenHash, @now, @emailExpiry);
                    INSERT INTO audit_events (event_type, user_id, actor_kind, actor_id, outcome, correlation_id, occurred_at, metadata)
                    VALUES ('AccountRegistered', @id, 'user', @id, 'success', @correlationId, @now, '{}'::jsonb);
                    """;
                command.Parameters.AddWithValue("id", userId);
                command.Parameters.AddWithValue("username", request.Username.Trim());
                command.Parameters.AddWithValue("email", request.Email.Trim());
                command.Parameters.AddWithValue("displayName", request.DisplayName.Trim());
                command.Parameters.Add(new("dateOfBirth", NpgsqlDbType.Date) { Value = request.DateOfBirth is null ? DBNull.Value : request.DateOfBirth.Value });
                command.Parameters.AddWithValue("marketing", request.MarketingOptIn);
                command.Parameters.Add(new("consentAt", NpgsqlDbType.TimestampTz) { Value = request.MarketingOptIn ? now : DBNull.Value });
                command.Parameters.AddWithValue("timezone", request.Timezone);
                command.Parameters.AddWithValue("passwordHash", passwordHash);
                command.Parameters.AddWithValue("parameters", JsonSerializer.Serialize(new { memoryKiB = Argon2IdPasswordHasher.MemoryKiB, iterations = Argon2IdPasswordHasher.Iterations, parallelism = Argon2IdPasswordHasher.Parallelism, saltBytes = Argon2IdPasswordHasher.SaltBytes, hashBytes = Argon2IdPasswordHasher.HashBytes }));
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("emailTokenId", Guid.CreateVersion7(now));
                command.Parameters.AddWithValue("emailTokenHash", tokens.Hash(emailToken));
                command.Parameters.AddWithValue("emailExpiry", now.AddHours(24));
                command.Parameters.AddWithValue("correlationId", correlationId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await emailOutbox.EnqueueAsync(connection, transaction, userId, request.Email.Trim(), "verify_email", emailToken, now, now.AddHours(24), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ConflictException("account_exists", "An account with that username or email already exists.");
        }
        finally { emailToken = string.Empty; }
    }

    public async Task<TokenPair> PasswordLoginAsync(PasswordLoginRequest request, string remoteAddress, string correlationId, CancellationToken cancellationToken)
    {
        var login = Normalization.AccountIdentifier(request.Login);
        if (login.Length is < 1 or > 320) throw new ArgumentException("Login must be 1-320 characters.");
        await loginAttempts.CheckAsync(login, remoteAddress, cancellationToken);
        try
        {
            var result = await PasswordLoginCoreAsync(request, correlationId, cancellationToken);
            await loginAttempts.RecordSuccessAsync(login, remoteAddress, cancellationToken);
            return result;
        }
        catch (AuthenticationFailureException)
        {
            await loginAttempts.RecordFailureAsync(login, remoteAddress, cancellationToken);
            throw;
        }
    }

    private async Task<TokenPair> PasswordLoginCoreAsync(PasswordLoginRequest request, string correlationId, CancellationToken cancellationToken)
    {
        PasswordPolicy.ValidatePresented(request.Password);
        ValidateClient(request.Client);
        var login = Normalization.AccountIdentifier(request.Login);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        Guid userId = default;
        string state = string.Empty;
        string? passwordHash;
        string? legacyHash;
        var found = false;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT u.id, u.account_state, p.password_hash, p.legacy_sha256
                FROM users u JOIN password_credentials p ON p.user_id = u.id
                WHERE lower(u.username::text) = @login OR lower(u.email::text) = @login
                FOR UPDATE OF p, u;
                """;
            command.Parameters.AddWithValue("login", login);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            found = await reader.ReadAsync(cancellationToken);
            if (found)
            {
                userId = reader.GetGuid(0);
                state = reader.GetString(1);
                passwordHash = reader.IsDBNull(2) ? null : reader.GetString(2);
                legacyHash = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
            else { passwordHash = null; legacyHash = null; }
        }

        if (!found)
        {
            await transaction.RollbackAsync(cancellationToken);
            _ = await passwordHasher.HashAsync(request.Password, cancellationToken);
            throw new AuthenticationFailureException();
        }

        if (state != "active") throw new AuthenticationFailureException(state == "reset_required" ? "reset_required" : "invalid_credentials");

        var verified = passwordHash is not null
            ? await passwordHasher.VerifyAsync(request.Password, passwordHash, cancellationToken)
            : legacyHash is not null && passwordHasher.VerifyLegacySha256(request.Password, legacyHash);
        if (!verified) throw new AuthenticationFailureException();

        if (passwordHash is null)
        {
            var upgraded = await passwordHasher.HashAsync(request.Password, cancellationToken);
            await using var upgrade = connection.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                UPDATE password_credentials SET password_hash=@hash, hash_algorithm='argon2id',
                    hash_parameters=@parameters::jsonb, legacy_sha256=NULL, migrated_at=@now, changed_at=@now
                WHERE user_id=@userId AND legacy_sha256=@legacy;
                """;
            upgrade.Parameters.AddWithValue("hash", upgraded);
            upgrade.Parameters.AddWithValue("parameters", JsonSerializer.Serialize(new { memoryKiB = Argon2IdPasswordHasher.MemoryKiB, iterations = Argon2IdPasswordHasher.Iterations, parallelism = Argon2IdPasswordHasher.Parallelism }));
            upgrade.Parameters.AddWithValue("now", clock.UtcNow);
            upgrade.Parameters.AddWithValue("userId", userId);
            upgrade.Parameters.AddWithValue("legacy", legacyHash!);
            if (await upgrade.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ConflictException("credential_changed", "The credential changed during login.");
        }

        var result = await CreateUserSessionAsync(connection, transaction, userId, request.Client, correlationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<TokenPair> RefreshUserSessionAsync(string refreshToken, string correlationId, CancellationToken cancellationToken) =>
        RotateUserRefreshTokenAsync(refreshToken, correlationId, cancellationToken);

    public Task<DeviceTokenPair> RefreshDeviceAsync(string refreshToken, string correlationId, CancellationToken cancellationToken) =>
        RotateDeviceRefreshTokenAsync(refreshToken, correlationId, cancellationToken);

    public async Task LogoutAsync(Guid sessionId, string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions SET revoked_at=COALESCE(revoked_at,@now), revoked_reason=COALESCE(revoked_reason,'logout') WHERE id=@sessionId;
            UPDATE session_refresh_tokens SET state='revoked' WHERE session_id=@sessionId AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE session_id=@sessionId;
            INSERT INTO audit_events(event_type, actor_kind, actor_id, outcome, correlation_id, occurred_at, metadata)
            VALUES ('SessionRevoked','user',@sessionId,'success',@correlationId,@now,'{}'::jsonb);
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'SessionRevoked','session',@sessionId,1,
              jsonb_build_object('userId',user_id,'sessionId',@sessionId,'reason','logout'),@now FROM sessions WHERE id=@sessionId;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("correlationId", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AuthenticatedSubject?> AuthenticateAccessTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (token.Length is < 40 or > 100) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT s.user_id, a.session_id, a.device_id,
                   CASE WHEN a.session_id IS NOT NULL THEN 'user' ELSE 'device' END
            FROM access_tokens a
            LEFT JOIN sessions s ON s.id=a.session_id
            LEFT JOIN devices d ON d.id=a.device_id
            WHERE a.token_hash=@hash AND a.expires_at>@now AND a.revoked_at IS NULL
              AND ((s.id IS NOT NULL AND s.revoked_at IS NULL AND s.sliding_expires_at>@now AND s.absolute_expires_at>@now)
                OR (d.id IS NOT NULL AND d.status<>'revoked'));
            """);
        command.Parameters.AddWithValue("hash", tokens.Hash(token));
        command.Parameters.AddWithValue("now", clock.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3));
    }

    public async Task<TokenPair> IssuePasskeySessionAsync(Guid userId, ClientDescriptor client, string correlationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT account_state FROM users WHERE id=@userId FOR UPDATE";
            check.Parameters.AddWithValue("userId", userId);
            if (await check.ExecuteScalarAsync(cancellationToken) as string != "active") throw new AuthenticationFailureException();
        }
        var result = await CreateUserSessionAsync(connection, transaction, userId, client, correlationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal async Task<TokenPair> CreateUserSessionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, ClientDescriptor client, string correlationId, CancellationToken cancellationToken)
    {
        ValidateClient(client);
        var now = clock.UtcNow;
        var sessionId = Guid.CreateVersion7(now);
        var access = tokens.Create();
        var refresh = tokens.Create();
        var accessExpiry = now.Add(AccessLifetime);
        var slidingExpiry = now.Add(UserSlidingLifetime);
        var absoluteExpiry = now.Add(UserAbsoluteLifetime);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(id,user_id,platform,client_name,client_version,created_at,last_used_at,sliding_expires_at,absolute_expires_at)
            VALUES(@id,@userId,@platform::platform_type,@name,@version,@now,@now,@sliding,@absolute);
            INSERT INTO session_refresh_tokens(id,session_id,token_hash,state,issued_at,expires_at)
            VALUES(@refreshId,@id,@refreshHash,'active',@now,@sliding);
            INSERT INTO access_tokens(id,session_id,token_hash,issued_at,expires_at)
            VALUES(@accessId,@id,@accessHash,@now,@accessExpiry);
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('SessionCreated',@userId,'user',@id,'success',@correlationId,@now,'{}'::jsonb);
            """;
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("platform", client.Platform.WireValue());
        command.Parameters.AddWithValue("name", client.Name);
        command.Parameters.AddWithValue("version", client.Version);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("sliding", slidingExpiry);
        command.Parameters.AddWithValue("absolute", absoluteExpiry);
        command.Parameters.AddWithValue("refreshId", Guid.CreateVersion7(now));
        command.Parameters.AddWithValue("refreshHash", tokens.Hash(refresh));
        command.Parameters.AddWithValue("accessId", Guid.CreateVersion7(now));
        command.Parameters.AddWithValue("accessHash", tokens.Hash(access));
        command.Parameters.AddWithValue("accessExpiry", accessExpiry);
        command.Parameters.AddWithValue("correlationId", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new(access, accessExpiry, refresh, slidingExpiry, sessionId);
    }

    private async Task<TokenPair> RotateUserRefreshTokenAsync(string refreshToken, string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT t.id,t.session_id,t.state,t.expires_at,s.user_id,s.absolute_expires_at,s.revoked_at
            FROM session_refresh_tokens t JOIN sessions s ON s.id=t.session_id
            WHERE t.token_hash=@hash FOR UPDATE OF t,s;
            """;
        select.Parameters.AddWithValue("hash", tokens.Hash(refreshToken));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException();
        var tokenId = reader.GetGuid(0);
        var sessionId = reader.GetGuid(1);
        var state = reader.GetString(2);
        var tokenExpiry = reader.GetFieldValue<DateTimeOffset>(3);
        var userId = reader.GetGuid(4);
        var absoluteExpiry = reader.GetFieldValue<DateTimeOffset>(5);
        var revoked = !reader.IsDBNull(6);
        await reader.DisposeAsync();

        if (state != "active")
        {
            await RevokeUserFamilyAsync(connection, transaction, sessionId, userId, "refresh_reuse", correlationId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new ConflictException("refresh_token_reuse", "The refresh-token family was revoked.");
        }
        if (revoked || tokenExpiry <= now || absoluteExpiry <= now) throw new AuthenticationFailureException("session_expired");

        var access = tokens.Create();
        var refresh = tokens.Create();
        var newTokenId = Guid.CreateVersion7(now);
        var accessExpiry = now.Add(AccessLifetime);
        var slidingExpiry = Min(now.Add(UserSlidingLifetime), absoluteExpiry);
        await using var rotate = connection.CreateCommand();
        rotate.Transaction = transaction;
        rotate.CommandText = """
            UPDATE session_refresh_tokens SET state='rotated',consumed_at=@now WHERE id=@oldId AND state='active';
            INSERT INTO session_refresh_tokens(id,session_id,token_hash,state,issued_at,expires_at) VALUES(@newId,@sessionId,@refreshHash,'active',@now,@sliding);
            UPDATE session_refresh_tokens SET replaced_by_id=@newId WHERE id=@oldId;
            INSERT INTO access_tokens(id,session_id,token_hash,issued_at,expires_at) VALUES(@accessId,@sessionId,@accessHash,@now,@accessExpiry);
            UPDATE sessions SET last_used_at=@now,sliding_expires_at=@sliding WHERE id=@sessionId;
            """;
        rotate.Parameters.AddWithValue("now", now);
        rotate.Parameters.AddWithValue("newId", newTokenId);
        rotate.Parameters.AddWithValue("oldId", tokenId);
        rotate.Parameters.AddWithValue("sessionId", sessionId);
        rotate.Parameters.AddWithValue("refreshHash", tokens.Hash(refresh));
        rotate.Parameters.AddWithValue("sliding", slidingExpiry);
        rotate.Parameters.AddWithValue("accessId", Guid.CreateVersion7(now));
        rotate.Parameters.AddWithValue("accessHash", tokens.Hash(access));
        rotate.Parameters.AddWithValue("accessExpiry", accessExpiry);
        await rotate.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(access, accessExpiry, refresh, slidingExpiry, sessionId);
    }

    private async Task<DeviceTokenPair> RotateDeviceRefreshTokenAsync(string refreshToken, string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT t.id,t.credential_id,t.state,t.expires_at,c.device_id,c.absolute_expires_at,c.revoked_at
            FROM device_refresh_tokens t JOIN device_credentials c ON c.id=t.credential_id
            JOIN devices d ON d.id=c.device_id
            WHERE t.token_hash=@hash AND d.status<>'revoked' FOR UPDATE OF t,c;
            """;
        select.Parameters.AddWithValue("hash", tokens.Hash(refreshToken));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException();
        var tokenId = reader.GetGuid(0);
        var credentialId = reader.GetGuid(1);
        var state = reader.GetString(2);
        var tokenExpiry = reader.GetFieldValue<DateTimeOffset>(3);
        var deviceId = reader.GetGuid(4);
        var absoluteExpiry = reader.GetFieldValue<DateTimeOffset>(5);
        var revoked = !reader.IsDBNull(6);
        await reader.DisposeAsync();
        if (state != "active")
        {
            await RevokeDeviceFamilyAsync(connection, transaction, credentialId, deviceId, "refresh_reuse", correlationId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new ConflictException("refresh_token_reuse", "The device credential family was revoked.");
        }
        if (revoked || tokenExpiry <= now || absoluteExpiry <= now) throw new AuthenticationFailureException("credential_expired");

        var access = tokens.Create();
        var refresh = tokens.Create();
        var newTokenId = Guid.CreateVersion7(now);
        var accessExpiry = now.Add(AccessLifetime);
        var slidingExpiry = Min(now.Add(DeviceSlidingLifetime), absoluteExpiry);
        await using var rotate = connection.CreateCommand();
        rotate.Transaction = transaction;
        rotate.CommandText = """
            UPDATE device_refresh_tokens SET state='rotated',consumed_at=@now WHERE id=@oldId AND state='active';
            INSERT INTO device_refresh_tokens(id,credential_id,token_hash,state,issued_at,expires_at) VALUES(@newId,@credentialId,@refreshHash,'active',@now,@sliding);
            UPDATE device_refresh_tokens SET replaced_by_id=@newId WHERE id=@oldId;
            INSERT INTO access_tokens(id,device_id,token_hash,issued_at,expires_at) VALUES(@accessId,@deviceId,@accessHash,@now,@accessExpiry);
            UPDATE device_credentials SET last_used_at=@now,sliding_expires_at=@sliding WHERE id=@credentialId;
            """;
        rotate.Parameters.AddWithValue("now", now);
        rotate.Parameters.AddWithValue("newId", newTokenId);
        rotate.Parameters.AddWithValue("oldId", tokenId);
        rotate.Parameters.AddWithValue("credentialId", credentialId);
        rotate.Parameters.AddWithValue("refreshHash", tokens.Hash(refresh));
        rotate.Parameters.AddWithValue("sliding", slidingExpiry);
        rotate.Parameters.AddWithValue("accessId", Guid.CreateVersion7(now));
        rotate.Parameters.AddWithValue("deviceId", deviceId);
        rotate.Parameters.AddWithValue("accessHash", tokens.Hash(access));
        rotate.Parameters.AddWithValue("accessExpiry", accessExpiry);
        await rotate.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(deviceId, access, accessExpiry, refresh, slidingExpiry);
    }

    private static async Task RevokeUserFamilyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, Guid userId, string reason, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions SET revoked_at=COALESCE(revoked_at,@now),revoked_reason=COALESCE(revoked_reason,@reason) WHERE id=@sessionId;
            UPDATE session_refresh_tokens SET state='revoked' WHERE session_id=@sessionId AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE session_id=@sessionId;
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('RefreshTokenReuse',@userId,'user',@sessionId,'denied',@correlationId,@now,'{}'::jsonb);
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(@eventId,'SessionRevoked','session',@sessionId,1,@payload::jsonb,@now);
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("correlationId", correlationId);
        command.Parameters.AddWithValue("eventId", Guid.CreateVersion7(now));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new { userId, sessionId, reason }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RevokeDeviceFamilyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid credentialId, Guid deviceId, string reason, string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE device_credentials SET revoked_at=COALESCE(revoked_at,@now),revoked_reason=COALESCE(revoked_reason,@reason) WHERE id=@credentialId;
            UPDATE device_refresh_tokens SET state='revoked' WHERE credential_id=@credentialId AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE device_id=@deviceId;
            INSERT INTO audit_events(event_type,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('DeviceRefreshTokenReuse','device',@deviceId,'device',@deviceId,'denied',@correlationId,@now,'{}'::jsonb);
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(@eventId,'SessionRevoked','device',@deviceId,1,@payload::jsonb,@now);
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("credentialId", credentialId);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("correlationId", correlationId);
        command.Parameters.AddWithValue("eventId", Guid.CreateVersion7(now));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new { deviceId, sessionId = credentialId, reason }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static void ValidateRegistration(RegistrationRequest request)
    {
        PasswordPolicy.ValidateNew(request.Password);
        if (request.Username.Trim().Length is < 3 or > 50) throw new ArgumentException("Username must be 3-50 characters.");
        if (request.Email.Trim().Length is < 3 or > 254 || !request.Email.Contains('@', StringComparison.Ordinal)) throw new ArgumentException("Email is invalid.");
        if (request.DisplayName.Trim().Length is < 1 or > 100) throw new ArgumentException("Display name must be 1-100 characters.");
        if (request.Timezone.Length > 100) throw new ArgumentException("Timezone is too long.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.Timezone); }
        catch (TimeZoneNotFoundException exception) { throw new ArgumentException("Timezone must be a recognized IANA identifier.", nameof(request), exception); }
    }

    private static void ValidateClient(ClientDescriptor? client)
    {
        if (client is null) throw new ArgumentException("Client descriptor is required.");
        if (string.IsNullOrWhiteSpace(client.Name) || client.Name.Trim().Length > 100) throw new ArgumentException("Client name must be 1-100 characters.");
        if (string.IsNullOrWhiteSpace(client.Version) || client.Version.Trim().Length > 40) throw new ArgumentException("Client version must be 1-40 characters.");
    }
}
