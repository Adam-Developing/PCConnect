using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Devices;

public interface IDeviceService
{
    Task<DeviceEnrollment> CreateEnrollmentAsync(DeviceEnrollmentRequest request, CancellationToken cancellationToken);
    Task ApproveEnrollmentAsync(Guid userId, string userCode, string correlationId, CancellationToken cancellationToken);
    Task<DeviceTokenPair> ExchangeDeviceCodeAsync(string deviceCode, string correlationId, CancellationToken cancellationToken);
    Task<Page<Device>> ListAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<Device> GetAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
    Task<Device> UpdateAsync(Guid userId, Guid deviceId, DeviceUpdate request, string correlationId, CancellationToken cancellationToken);
    Task RevokeAsync(Guid userId, Guid sessionId, Guid deviceId, string stepUpGrant, string correlationId, CancellationToken cancellationToken);
    Task HeartbeatAsync(Guid deviceId, Heartbeat request, CancellationToken cancellationToken);
    Task<WindowsSidStatus> RegisterSidCandidateAsync(Guid deviceId, WindowsSidCandidateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<WindowsSidStatus>> ListWindowsSidsAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken);
    Task AuthorizeWindowsSidAsync(Guid userId, Guid sessionId, Guid deviceId, string windowsSid, string stepUpGrant, string correlationId, CancellationToken cancellationToken);
    Task RevokeWindowsSidAsync(Guid userId, Guid sessionId, Guid deviceId, string windowsSid, string stepUpGrant, string correlationId, CancellationToken cancellationToken);
}

public sealed class DeviceService(NpgsqlDataSource dataSource, IOpaqueTokenService tokens, IClock clock, StepUpGrantConsumer stepUp, Microsoft.Extensions.Configuration.IConfiguration configuration) : IDeviceService
{
    private const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(365);

    public async Task<DeviceEnrollment> CreateEnrollmentAsync(DeviceEnrollmentRequest request, CancellationToken cancellationToken)
    {
        ValidateEnrollment(request);
        var now = clock.UtcNow;
        var deviceCode = tokens.Create();
        var userCode = CreateUserCode();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO device_enrollments(id,device_code_hash,user_code,requested_platform,requested_display_name,
                requested_agent_version,requested_protocol_version,requested_timezone,requested_capabilities,status,
                poll_interval_seconds,created_at,expires_at)
            VALUES(@id,@hash,@userCode,@platform::platform_type,@name,@agentVersion,@protocolVersion,@timezone,
                @capabilities::device_capability[],'pending',5,@now,@expiresAt);
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7(now));
        command.Parameters.AddWithValue("hash", tokens.Hash(deviceCode));
        command.Parameters.AddWithValue("userCode", userCode);
        command.Parameters.AddWithValue("platform", request.Platform.WireValue());
        command.Parameters.AddWithValue("name", request.DisplayName.Trim());
        command.Parameters.AddWithValue("agentVersion", request.AgentVersion);
        command.Parameters.AddWithValue("protocolVersion", request.ProtocolVersion);
        command.Parameters.Add(new("timezone", NpgsqlDbType.Text) { Value = request.Timezone is null ? DBNull.Value : request.Timezone });
        command.Parameters.AddWithValue("capabilities", request.Capabilities.Select(x => x.WireValue()).Distinct(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expiresAt", now.Add(EnrollmentLifetime));
        await command.ExecuteNonQueryAsync(cancellationToken);
        var verificationUri = new Uri(configuration["Enrollment:VerificationUri"] ?? "https://pcconnect.adamdeveloping.co.uk/device", UriKind.Absolute);
        if (verificationUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Enrollment:VerificationUri must use HTTPS.");
        return new(deviceCode, userCode, verificationUri, now.Add(EnrollmentLifetime), 5);
    }

    public async Task ApproveEnrollmentAsync(Guid userId, string userCode, string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id,status,expires_at,requested_platform::text,requested_display_name,requested_agent_version,
                requested_protocol_version,requested_timezone,requested_capabilities::text[]
            FROM device_enrollments WHERE user_code=@userCode FOR UPDATE;
            """;
        select.Parameters.AddWithValue("userCode", userCode.ToUpperInvariant());
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("enrollment_not_found");
        var enrollmentId = reader.GetGuid(0);
        var status = reader.GetString(1);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(2);
        var platform = reader.GetString(3);
        var name = reader.GetString(4);
        var agentVersion = reader.GetString(5);
        var protocolVersion = reader.GetInt32(6);
        var timezone = reader.IsDBNull(7) ? null : reader.GetString(7);
        var capabilities = reader.GetFieldValue<string[]>(8);
        await reader.DisposeAsync();
        if (expiresAt <= now)
        {
            await MarkEnrollmentExpiredAsync(connection, transaction, enrollmentId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new ResourceGoneException("enrollment_expired");
        }
        if (status != "pending") throw new ConflictException("enrollment_not_pending", "The enrollment is no longer pending.");

        var deviceId = Guid.CreateVersion7(now);
        var credentialId = Guid.CreateVersion7(now);
        await using var approve = connection.CreateCommand();
        approve.Transaction = transaction;
        approve.CommandText = """
            INSERT INTO devices(id,user_id,platform,display_name,display_name_normalized,agent_version,protocol_version,timezone,capabilities,status,enrolled_at)
            VALUES(@deviceId,@userId,@platform::platform_type,@name,@normalized,@agentVersion,@protocolVersion,@timezone,@capabilities::device_capability[],'offline',@now);
            INSERT INTO device_credentials(id,device_id,created_at,last_used_at,sliding_expires_at,absolute_expires_at)
            VALUES(@credentialId,@deviceId,@now,@now,@sliding,@absolute);
            UPDATE device_enrollments SET status='approved',approved_by_user_id=@userId,approved_at=@now,exchanged_device_id=@deviceId
            WHERE id=@enrollmentId;
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('DeviceEnrollmentApproved',@userId,'user',@userId,'device',@deviceId,'success',@correlationId,@now,'{}'::jsonb);
            """;
        approve.Parameters.AddWithValue("deviceId", deviceId);
        approve.Parameters.AddWithValue("userId", userId);
        approve.Parameters.AddWithValue("platform", platform);
        approve.Parameters.AddWithValue("name", name);
        approve.Parameters.AddWithValue("normalized", Normalization.DeviceName(name));
        approve.Parameters.AddWithValue("agentVersion", agentVersion);
        approve.Parameters.AddWithValue("protocolVersion", protocolVersion);
        approve.Parameters.Add(new("timezone", NpgsqlDbType.Text) { Value = timezone is null ? DBNull.Value : timezone });
        approve.Parameters.AddWithValue("capabilities", capabilities);
        approve.Parameters.AddWithValue("now", now);
        approve.Parameters.AddWithValue("credentialId", credentialId);
        approve.Parameters.AddWithValue("sliding", now.Add(SlidingLifetime));
        approve.Parameters.AddWithValue("absolute", now.Add(AbsoluteLifetime));
        approve.Parameters.AddWithValue("enrollmentId", enrollmentId);
        approve.Parameters.AddWithValue("correlationId", correlationId);
        try { await approve.ExecuteNonQueryAsync(cancellationToken); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("device_name_conflict", "A device with this name is already enrolled.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DeviceTokenPair> ExchangeDeviceCodeAsync(string deviceCode, string correlationId, CancellationToken cancellationToken)
    {
        if (deviceCode.Length is < 40 or > 100) throw new AuthenticationFailureException("invalid_device_code");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT e.id,e.status,e.expires_at,e.poll_interval_seconds,e.last_polled_at,e.exchanged_device_id,c.id,c.absolute_expires_at
            FROM device_enrollments e LEFT JOIN device_credentials c ON c.device_id=e.exchanged_device_id AND c.revoked_at IS NULL
            WHERE e.device_code_hash=@hash FOR UPDATE OF e;
            """;
        select.Parameters.AddWithValue("hash", tokens.Hash(deviceCode));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException("invalid_device_code");
        var enrollmentId = reader.GetGuid(0);
        var status = reader.GetString(1);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(2);
        var interval = reader.GetInt32(3);
        var lastPoll = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4);
        var deviceId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
        var credentialId = reader.IsDBNull(6) ? (Guid?)null : reader.GetGuid(6);
        var absoluteExpiry = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
        await reader.DisposeAsync();
        if (lastPoll is not null && now - lastPoll < TimeSpan.FromSeconds(interval)) throw new RequestRateLimitedException();
        await using (var polled = connection.CreateCommand())
        {
            polled.Transaction = transaction;
            polled.CommandText = "UPDATE device_enrollments SET last_polled_at=@now WHERE id=@id";
            polled.Parameters.AddWithValue("now", now);
            polled.Parameters.AddWithValue("id", enrollmentId);
            await polled.ExecuteNonQueryAsync(cancellationToken);
        }
        if (expiresAt <= now)
        {
            await MarkEnrollmentExpiredAsync(connection, transaction, enrollmentId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new ResourceGoneException("enrollment_expired");
        }
        if (status == "pending")
        {
            await transaction.CommitAsync(cancellationToken);
            throw new ConflictException("authorization_pending", "The enrollment is awaiting approval.");
        }
        if (status != "approved" || deviceId is null || credentialId is null || absoluteExpiry is null)
            throw new ResourceGoneException("device_code_consumed");

        var access = tokens.Create();
        var refresh = tokens.Create();
        var accessExpiry = now.Add(AccessLifetime);
        var refreshExpiry = now.Add(SlidingLifetime) < absoluteExpiry ? now.Add(SlidingLifetime) : absoluteExpiry.Value;
        await using var exchange = connection.CreateCommand();
        exchange.Transaction = transaction;
        exchange.CommandText = """
            INSERT INTO device_refresh_tokens(id,credential_id,token_hash,state,issued_at,expires_at)
            VALUES(@refreshId,@credentialId,@refreshHash,'active',@now,@refreshExpiry);
            INSERT INTO access_tokens(id,device_id,token_hash,issued_at,expires_at)
            VALUES(@accessId,@deviceId,@accessHash,@now,@accessExpiry);
            UPDATE device_enrollments SET status='exchanged',exchanged_at=@now WHERE id=@enrollmentId;
            INSERT INTO audit_events(event_type,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('DeviceEnrollmentExchanged','device',@deviceId,'device',@deviceId,'success',@correlationId,@now,'{}'::jsonb);
            """;
        exchange.Parameters.AddWithValue("refreshId", Guid.CreateVersion7(now));
        exchange.Parameters.AddWithValue("credentialId", credentialId.Value);
        exchange.Parameters.AddWithValue("refreshHash", tokens.Hash(refresh));
        exchange.Parameters.AddWithValue("now", now);
        exchange.Parameters.AddWithValue("refreshExpiry", refreshExpiry);
        exchange.Parameters.AddWithValue("accessId", Guid.CreateVersion7(now));
        exchange.Parameters.AddWithValue("deviceId", deviceId.Value);
        exchange.Parameters.AddWithValue("accessHash", tokens.Hash(access));
        exchange.Parameters.AddWithValue("accessExpiry", accessExpiry);
        exchange.Parameters.AddWithValue("enrollmentId", enrollmentId);
        exchange.Parameters.AddWithValue("correlationId", correlationId);
        await exchange.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(deviceId.Value, access, accessExpiry, refresh, refreshExpiry);
    }

    public async Task<Page<Device>> ListAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var position = PageCursor.Decode(cursor);
        await using var command = dataSource.CreateCommand(DeviceSelect + " WHERE d.user_id=@userId AND (@cursorTime IS NULL OR (d.enrolled_at,d.id)<(@cursorTime,@cursorId)) ORDER BY d.enrolled_at DESC,d.id DESC LIMIT @limit");
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("cursorTime", NpgsqlDbType.TimestampTz) { Value = position is null ? DBNull.Value : position.Value.Timestamp });
        command.Parameters.Add(new("cursorId", NpgsqlDbType.Uuid) { Value = position is null ? DBNull.Value : position.Value.Id });
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Device>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadDevice(reader));
        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var next = hasMore && items.Count > 0 ? PageCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null;
        return new(items, next);
    }

    public async Task<Device> GetAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(DeviceSelect + " WHERE d.id=@deviceId AND d.user_id=@userId");
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("device_not_found");
        return ReadDevice(reader);
    }

    public async Task<Device> UpdateAsync(Guid userId, Guid deviceId, DeviceUpdate request, string correlationId, CancellationToken cancellationToken)
    {
        if (request.DisplayName is null) return await GetAsync(userId, deviceId, cancellationToken);
        if (request.DisplayName.Trim().Length is < 1 or > 100) throw new ArgumentException("Device name must be 1-100 characters.");
        await using var command = dataSource.CreateCommand("""
            UPDATE devices SET display_name=@name,display_name_normalized=@normalized,row_version=row_version+1
            WHERE id=@deviceId AND user_id=@userId AND status<>'revoked' AND (@expected IS NULL OR row_version=@expected)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("name", request.DisplayName.Trim());
        command.Parameters.AddWithValue("normalized", Normalization.DeviceName(request.DisplayName));
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("expected", NpgsqlDbType.Bigint) { Value = request.ExpectedVersion is null ? DBNull.Value : request.ExpectedVersion.Value });
        try
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is null) throw new ConflictException("version_conflict", "The device changed or was revoked.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException("device_name_conflict", "A device with this name already exists.");
        }
        return await GetAsync(userId, deviceId, cancellationToken);
    }

    public async Task RevokeAsync(Guid userId, Guid sessionId, Guid deviceId, string stepUpGrant, string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.DeviceRevoke, deviceId, cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM devices WHERE id=@deviceId AND user_id=@userId AND status<>'revoked' FOR UPDATE";
            exists.Parameters.AddWithValue("deviceId", deviceId);
            exists.Parameters.AddWithValue("userId", userId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) throw new ResourceNotFoundException("device_not_found");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE devices SET status='revoked',revoked_at=@now,row_version=row_version+1 WHERE id=@deviceId AND user_id=@userId;
            UPDATE device_credentials SET revoked_at=@now,revoked_reason='device_revoked' WHERE device_id=@deviceId AND revoked_at IS NULL;
            UPDATE device_refresh_tokens SET state='revoked' WHERE credential_id IN (
                SELECT c.id FROM device_credentials c JOIN devices d ON d.id=c.device_id WHERE d.id=@deviceId AND d.user_id=@userId) AND state='active';
            UPDATE access_tokens SET revoked_at=COALESCE(revoked_at,@now) WHERE device_id=@deviceId;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'DevicePresenceChanged','device',id,row_version,
              jsonb_build_object('userId',user_id,'deviceId',id,'status','revoked','lastSeenAt',last_seen_at),@now
              FROM devices WHERE id=@deviceId;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(uuidv7(),'SessionRevoked','device',@deviceId,1,
              jsonb_build_object('deviceId',@deviceId,'sessionId',@deviceId,'reason','device_revoked'),@now);
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('DeviceRevoked',@userId,'user',@sessionId,'device',@deviceId,'success',@correlationId,@now,'{}'::jsonb);
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("correlationId", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task HeartbeatAsync(Guid deviceId, Heartbeat request, CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion < 2) throw new ArgumentException("Protocol version 2 or newer is required.");
        await using var command = dataSource.CreateCommand("""
            WITH previous AS MATERIALIZED (
              SELECT id,user_id,status::text FROM devices WHERE id=@deviceId AND status<>'revoked'
            ), updated AS (
              UPDATE devices d SET status='online',last_seen_at=@now,agent_version=@version,protocol_version=@protocol,
                capabilities=@capabilities::device_capability[],row_version=row_version+1
              FROM previous p WHERE d.id=p.id RETURNING d.id,d.user_id,d.row_version,d.last_seen_at,p.status AS old_status
            ), notification AS (
              INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
              SELECT uuidv7(),'DevicePresenceChanged','device',id,row_version,
                jsonb_build_object('userId',user_id,'deviceId',id,'status','online','lastSeenAt',last_seen_at),@now
              FROM updated WHERE old_status<>'online' RETURNING id
            )
            SELECT count(*) FROM updated;
            """);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("version", request.AgentVersion);
        command.Parameters.AddWithValue("protocol", request.ProtocolVersion);
        command.Parameters.AddWithValue("capabilities", request.Capabilities.Select(x => x.WireValue()).Distinct(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("deviceId", deviceId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new AuthenticationFailureException("device_revoked");
    }

    public async Task<WindowsSidStatus> RegisterSidCandidateAsync(Guid deviceId, WindowsSidCandidateRequest request, CancellationToken cancellationToken)
    {
        ValidateSid(request.WindowsSid, request.DisplayLabel);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var authorized = connection.CreateCommand())
        {
            authorized.Transaction = transaction;
            authorized.CommandText = """
                SELECT display_label,authorized_at FROM device_authorized_sids
                WHERE device_id=@deviceId AND windows_sid=@sid AND revoked_at IS NULL;
                """;
            authorized.Parameters.AddWithValue("deviceId", deviceId);
            authorized.Parameters.AddWithValue("sid", request.WindowsSid);
            await using var reader = await authorized.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return new(request.WindowsSid, reader.IsDBNull(0) ? request.DisplayLabel : reader.GetString(0), "authorized", null, reader.GetFieldValue<DateTimeOffset>(1));
        }
        await using var candidate = connection.CreateCommand();
        candidate.Transaction = transaction;
        candidate.CommandText = """
            INSERT INTO device_sid_candidates(device_id,windows_sid,display_label,observed_at,expires_at)
            SELECT @deviceId,@sid,@label,@now,@expires FROM devices WHERE id=@deviceId AND status<>'revoked'
            ON CONFLICT(device_id,windows_sid) DO UPDATE SET display_label=EXCLUDED.display_label,observed_at=EXCLUDED.observed_at,expires_at=EXCLUDED.expires_at
            RETURNING observed_at;
            """;
        candidate.Parameters.AddWithValue("deviceId", deviceId);
        candidate.Parameters.AddWithValue("sid", request.WindowsSid);
        candidate.Parameters.Add(new("label", NpgsqlDbType.Text) { Value = request.DisplayLabel is null ? DBNull.Value : request.DisplayLabel.Trim() });
        candidate.Parameters.AddWithValue("now", now);
        candidate.Parameters.AddWithValue("expires", now.AddDays(1));
        if (await candidate.ExecuteScalarAsync(cancellationToken) is not DateTimeOffset observedAt) throw new AuthenticationFailureException("device_revoked");
        await transaction.CommitAsync(cancellationToken);
        return new(request.WindowsSid, request.DisplayLabel, "pending", observedAt, null);
    }

    public async Task<IReadOnlyList<WindowsSidStatus>> ListWindowsSidsAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT s.windows_sid,s.display_label,'authorized',NULL::timestamptz,s.authorized_at
            FROM device_authorized_sids s JOIN devices d ON d.id=s.device_id
            WHERE s.device_id=@deviceId AND d.user_id=@userId AND s.revoked_at IS NULL
            UNION ALL
            SELECT c.windows_sid,c.display_label,'pending',c.observed_at,NULL::timestamptz
            FROM device_sid_candidates c JOIN devices d ON d.id=c.device_id
            WHERE c.device_id=@deviceId AND d.user_id=@userId AND c.expires_at>@now
              AND NOT EXISTS (SELECT 1 FROM device_authorized_sids s WHERE s.device_id=c.device_id AND s.windows_sid=c.windows_sid AND s.revoked_at IS NULL)
            ORDER BY 3,1;
            """);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WindowsSidStatus>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(new(
            reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        return items;
    }

    public async Task AuthorizeWindowsSidAsync(Guid userId, Guid sessionId, Guid deviceId, string windowsSid, string stepUpGrant, string correlationId, CancellationToken cancellationToken)
    {
        ValidateSid(windowsSid, null);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.SecurityChange, deviceId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidate AS (
              DELETE FROM device_sid_candidates c USING devices d
              WHERE c.device_id=@deviceId AND c.windows_sid=@sid AND c.expires_at>@now
                AND d.id=c.device_id AND d.user_id=@userId AND d.status<>'revoked'
              RETURNING c.display_label
            ), authorized AS (
              INSERT INTO device_authorized_sids(device_id,windows_sid,display_label,authorized_at,revoked_at)
              SELECT @deviceId,@sid,display_label,@now,NULL FROM candidate
              ON CONFLICT(device_id,windows_sid) DO UPDATE SET display_label=EXCLUDED.display_label,authorized_at=@now,revoked_at=NULL
              RETURNING windows_sid
            )
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            SELECT 'WindowsSidAuthorized',@userId,'user',@sessionId,'device',@deviceId,'success',@correlationId,@now,
              jsonb_build_object('sidHash',@sidHash) FROM authorized;
            """;
        AddSidAuditParameters(command, userId, sessionId, deviceId, windowsSid, correlationId, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ResourceNotFoundException("windows_sid_candidate_not_found");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RevokeWindowsSidAsync(Guid userId, Guid sessionId, Guid deviceId, string windowsSid, string stepUpGrant, string correlationId, CancellationToken cancellationToken)
    {
        ValidateSid(windowsSid, null);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.SecurityChange, deviceId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH revoked AS (
              UPDATE device_authorized_sids s SET revoked_at=@now FROM devices d
              WHERE s.device_id=@deviceId AND s.windows_sid=@sid AND s.revoked_at IS NULL
                AND d.id=s.device_id AND d.user_id=@userId RETURNING s.windows_sid
            )
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            SELECT 'WindowsSidRevoked',@userId,'user',@sessionId,'device',@deviceId,'success',@correlationId,@now,
              jsonb_build_object('sidHash',@sidHash) FROM revoked;
            """;
        AddSidAuditParameters(command, userId, sessionId, deviceId, windowsSid, correlationId, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ResourceNotFoundException("windows_sid_not_found");
        await transaction.CommitAsync(cancellationToken);
    }

    private const string DeviceSelect = """
        SELECT d.id,d.platform::text,d.display_name,d.agent_version,d.protocol_version,d.timezone,d.capabilities::text[],
            d.status::text,d.last_seen_at,d.enrolled_at,d.row_version FROM devices d
        """;

    private static Device ReadDevice(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), Enum.Parse<PlatformType>(reader.GetString(1), true), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<string[]>(6).Select(ParseCapability).ToArray(), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9), reader.GetInt64(10));

    private static DeviceCapability ParseCapability(string value) => value == "sign_out" ? DeviceCapability.SignOut : Enum.Parse<DeviceCapability>(value, true);

    private static async Task MarkEnrollmentExpiredAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid enrollmentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE device_enrollments SET status='expired' WHERE id=@id AND status='pending'";
        command.Parameters.AddWithValue("id", enrollmentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateUserCode() => string.Create(8, 0, static (span, _) =>
    {
        for (var index = 0; index < span.Length; index++) span[index] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
    });

    private static void ValidateEnrollment(DeviceEnrollmentRequest request)
    {
        if (request.ProtocolVersion < 2) throw new ArgumentException("Protocol version 2 or newer is required.");
        if (request.DisplayName.Trim().Length is < 1 or > 100) throw new ArgumentException("Device name must be 1-100 characters.");
        if (request.AgentVersion.Length is < 1 or > 40) throw new ArgumentException("Agent version must be 1-40 characters.");
        if (request.Capabilities.Count is 0 or > 7) throw new ArgumentException("At least one valid capability is required.");
    }

    private static void ValidateSid(string sid, string? label)
    {
        if (sid.Length is < 5 or > 184 || !System.Text.RegularExpressions.Regex.IsMatch(sid, "^S-1-(?:[0-9]+-){1,14}[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("A canonical Windows SID is required.");
        if (label?.Trim().Length > 100) throw new ArgumentException("The Windows account label is too long.");
    }

    private static void AddSidAuditParameters(NpgsqlCommand command, Guid userId, Guid sessionId, Guid deviceId, string windowsSid, string correlationId, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("sid", windowsSid);
        command.Parameters.AddWithValue("sidHash", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(windowsSid))).ToLowerInvariant());
        command.Parameters.AddWithValue("correlationId", correlationId);
        command.Parameters.AddWithValue("now", now);
    }
}
