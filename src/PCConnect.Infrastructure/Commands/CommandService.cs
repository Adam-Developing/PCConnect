using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Domain.Commands;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Security;
using PCConnect.Infrastructure.Observability;
using ContractCommandType = PCConnect.Contracts.V2.CommandType;

namespace PCConnect.Infrastructure.Commands;

public interface ICommandService
{
    Task<Command> CreateAsync(Guid userId, Guid sessionId, Guid deviceId, Guid idempotencyKey, string? stepUpGrant, CommandCreate request, string correlationId, CancellationToken cancellationToken);
    Task<Command> GetAsync(Guid userId, Guid commandId, CancellationToken cancellationToken);
    Task<Page<Command>> ListForUserAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<Command> CancelAsync(Guid userId, Guid sessionId, Guid commandId, string correlationId, CancellationToken cancellationToken);
    Task<Page<Command>> ListPendingAsync(Guid deviceId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<Command> ClaimAsync(Guid deviceId, Guid commandId, CommandClaim request, CancellationToken cancellationToken);
    Task<Command> AcknowledgeAsync(Guid deviceId, Guid commandId, CommandAcknowledgement request, CancellationToken cancellationToken);
}

public sealed class CommandService(NpgsqlDataSource dataSource, IOpaqueTokenService tokens, IClock clock) : ICommandService
{
    public async Task<Command> CreateAsync(Guid userId, Guid sessionId, Guid deviceId, Guid idempotencyKey, string? stepUpGrant, CommandCreate request, string correlationId, CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty) throw new ArgumentException("A non-empty Idempotency-Key is required.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var existing = await FindAsync(connection, transaction, "c.actor_session_id=@sessionId AND c.idempotency_key=@key AND c.user_id=@userId", sessionId, idempotencyKey, userId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        string[] capabilities;
        string status;
        await using (var device = connection.CreateCommand())
        {
            device.Transaction = transaction;
            device.CommandText = "SELECT capabilities::text[],status::text FROM devices WHERE id=@deviceId AND user_id=@userId FOR UPDATE";
            device.Parameters.AddWithValue("deviceId", deviceId);
            device.Parameters.AddWithValue("userId", userId);
            await using var reader = await device.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("device_not_found");
            capabilities = reader.GetFieldValue<string[]>(0);
            status = reader.GetString(1);
        }
        if (status == "revoked") throw new ConflictException("device_revoked", "The target device is revoked.");
        CommandPolicy.Validate(request, capabilities.Select(ParseCapability).ToArray());

        Guid? grantId = null;
        if (CommandPolicy.RequiresStepUp(request.Type))
        {
            if (string.IsNullOrWhiteSpace(stepUpGrant)) throw new AuthenticationFailureException("step_up_required");
            await using var grant = connection.CreateCommand();
            grant.Transaction = transaction;
            grant.CommandText = """
                SELECT id FROM step_up_grants WHERE grant_hash=@hash AND user_id=@userId AND session_id=@sessionId
                  AND intent='command' AND target_device_id=@deviceId AND command=@command::command_type
                  AND idempotency_key=@key AND consumed_at IS NULL AND expires_at>@now FOR UPDATE;
                """;
            grant.Parameters.AddWithValue("hash", tokens.Hash(stepUpGrant));
            grant.Parameters.AddWithValue("userId", userId);
            grant.Parameters.AddWithValue("sessionId", sessionId);
            grant.Parameters.AddWithValue("deviceId", deviceId);
            grant.Parameters.AddWithValue("command", request.Type.WireValue());
            grant.Parameters.AddWithValue("key", idempotencyKey);
            grant.Parameters.AddWithValue("now", now);
            grantId = await grant.ExecuteScalarAsync(cancellationToken) as Guid?;
            if (grantId is null) throw new AuthenticationFailureException("invalid_step_up_grant");
        }

        var commandId = Guid.CreateVersion7(now);
        var expiry = now.AddSeconds(request.ExpiresInSeconds ?? 120);
        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            INSERT INTO commands(id,user_id,target_device_id,actor_session_id,type,status,idempotency_key,step_up_grant_id,issued_at,expires_at,row_version)
            VALUES(@id,@userId,@deviceId,@sessionId,@type::command_type,'queued',@key,@grantId,@now,@expires,1);
            INSERT INTO command_events(id,command_id,sequence,from_status,to_status,actor_kind,actor_id,occurred_at,metadata)
            VALUES(@eventId,@id,1,NULL,'queued','controller',@sessionId,@now,'{}'::jsonb);
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(@outboxId,'CommandAvailable','command',@id,1,@payload::jsonb,@now);
            UPDATE step_up_grants SET consumed_at=@now WHERE id=@grantId;
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('CommandCreated',@userId,'user',@sessionId,'command',@id,'success',@correlationId,@now,jsonb_build_object('type',@type));
            """;
        create.Parameters.AddWithValue("id", commandId);
        create.Parameters.AddWithValue("userId", userId);
        create.Parameters.AddWithValue("deviceId", deviceId);
        create.Parameters.AddWithValue("sessionId", sessionId);
        create.Parameters.AddWithValue("type", request.Type.WireValue());
        create.Parameters.AddWithValue("key", idempotencyKey);
        create.Parameters.Add(new("grantId", NpgsqlDbType.Uuid) { Value = grantId is null ? DBNull.Value : grantId.Value });
        create.Parameters.AddWithValue("now", now);
        create.Parameters.AddWithValue("expires", expiry);
        create.Parameters.AddWithValue("eventId", Guid.CreateVersion7(now));
        create.Parameters.AddWithValue("outboxId", Guid.CreateVersion7(now));
        create.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new { deviceId, commandId, expiresAt = expiry }));
        create.Parameters.AddWithValue("correlationId", correlationId);
        await create.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PCConnectTelemetry.RecordCommandCreated(request.Type.WireValue());
        return new(commandId, deviceId, request.Type, CommandStatus.Queued, now, expiry, null, null, null, null, 1);
    }

    public async Task<Command> GetAsync(Guid userId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(CommandSelect + " WHERE c.id=@commandId AND c.user_id=@userId");
        command.Parameters.AddWithValue("commandId", commandId);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("command_not_found");
        return ReadCommand(reader);
    }

    public async Task<Page<Command>> ListForUserAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var position = PageCursor.Decode(cursor);
        await using var command = dataSource.CreateCommand("""
            SELECT c.id,c.target_device_id,c.type::text,c.status::text,c.issued_at,c.expires_at,c.claimed_until,c.accepted_at,c.finished_at,c.failure_code::text,c.row_version,c.updated_at
            FROM commands c WHERE c.user_id=@userId AND (@cursorTime IS NULL OR (c.updated_at,c.id)<(@cursorTime,@cursorId))
            ORDER BY c.updated_at DESC,c.id DESC LIMIT @limit;
            """);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("cursorTime", NpgsqlDbType.TimestampTz) { Value = position is null ? DBNull.Value : position.Value.Timestamp });
        command.Parameters.Add(new("cursorId", NpgsqlDbType.Uuid) { Value = position is null ? DBNull.Value : position.Value.Id });
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Command>();
        var positions = new List<PagePosition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadCommand(reader));
            positions.Add(new(reader.GetFieldValue<DateTimeOffset>(11), reader.GetGuid(0)));
        }
        var hasMore = items.Count > limit;
        if (hasMore) { items.RemoveAt(items.Count - 1); positions.RemoveAt(positions.Count - 1); }
        return new(items, hasMore && positions.Count > 0 ? PageCursor.Encode(positions[^1].Timestamp, positions[^1].Id) : null);
    }

    public async Task<Command> CancelAsync(Guid userId, Guid sessionId, Guid commandId, string correlationId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            WITH changed AS (
              UPDATE commands SET status='cancelled',finished_at=@now WHERE id=@id AND user_id=@userId AND status='queued'
              RETURNING id,user_id,target_device_id,row_version
            ), event AS (
              INSERT INTO command_events(id,command_id,sequence,from_status,to_status,actor_kind,actor_id,occurred_at,metadata)
              SELECT uuidv7(),ch.id,(SELECT max(ce.sequence)+1 FROM command_events ce WHERE ce.command_id=ch.id),'queued','cancelled','controller',@sessionId,@now,'{}'::jsonb FROM changed ch
            ), notification AS (
              INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
              SELECT uuidv7(),'CommandStatusChanged','command',id,row_version,
                jsonb_build_object('userId',user_id,'commandId',id,'deviceId',target_device_id,'status','cancelled','failureCode',NULL),@now FROM changed
            )
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            SELECT 'CommandCancelled',user_id,'user',@sessionId,'command',id,'success',@correlationId,@now,'{}'::jsonb FROM changed;
            """;
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("id", commandId);
        update.Parameters.AddWithValue("userId", userId);
        update.Parameters.AddWithValue("sessionId", sessionId);
        update.Parameters.AddWithValue("correlationId", correlationId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ConflictException("command_not_cancellable", "Only a queued command can be cancelled.");
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(userId, commandId, cancellationToken);
    }

    public async Task<Page<Command>> ListPendingAsync(Guid deviceId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var position = PageCursor.Decode(cursor);
        await using var command = dataSource.CreateCommand(CommandSelect + " WHERE c.target_device_id=@deviceId AND c.status IN ('queued','claimed') AND c.expires_at>@now AND (@cursorTime IS NULL OR (c.issued_at,c.id)>(@cursorTime,@cursorId)) ORDER BY c.issued_at,c.id LIMIT @limit");
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.Add(new("cursorTime", NpgsqlDbType.TimestampTz) { Value = position is null ? DBNull.Value : position.Value.Timestamp });
        command.Parameters.Add(new("cursorId", NpgsqlDbType.Uuid) { Value = position is null ? DBNull.Value : position.Value.Id });
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Command>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadCommand(reader));
        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, hasMore && items.Count > 0 ? PageCursor.Encode(items[^1].IssuedAt, items[^1].Id) : null);
    }

    public async Task<Command> ClaimAsync(Guid deviceId, Guid commandId, CommandClaim request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        string status;
        DateTimeOffset expires;
        DateTimeOffset? claimedUntil;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT status::text,expires_at,claimed_until FROM commands WHERE id=@id AND target_device_id=@deviceId FOR UPDATE";
            select.Parameters.AddWithValue("id", commandId);
            select.Parameters.AddWithValue("deviceId", deviceId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("command_not_found");
            status = reader.GetString(0);
            expires = reader.GetFieldValue<DateTimeOffset>(1);
            claimedUntil = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
        }
        if (expires <= now) throw new ResourceGoneException("command_expired");
        if (status == "claimed" && claimedUntil > now) throw new ConflictException("command_already_claimed", "The command has an active claim.");
        if (status is not ("queued" or "claimed")) throw new ConflictException("command_not_claimable", "The command cannot be claimed in its current state.");

        if (status == "claimed") await TransitionAsync(connection, transaction, commandId, "claimed", "queued", "agent", request.AgentInstanceId, null, now, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE commands SET status='claimed',claimed_by_instance_id=@instance,claimed_until=@until WHERE id=@id";
        update.Parameters.AddWithValue("instance", request.AgentInstanceId);
        update.Parameters.AddWithValue("until", now.AddSeconds(30));
        update.Parameters.AddWithValue("id", commandId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await AppendEventAndOutboxAsync(connection, transaction, commandId, "queued", "claimed", "agent", request.AgentInstanceId, null, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetForDeviceAsync(deviceId, commandId, cancellationToken);
    }

    public async Task<Command> AcknowledgeAsync(Guid deviceId, Guid commandId, CommandAcknowledgement request, CancellationToken cancellationToken)
    {
        if (request.LocalReplayKey != commandId) throw new ConflictException("local_replay", "The local replay key must be the command ID.");
        var target = request.State switch { "accepted" => CommandStatus.Accepted, "succeeded" => CommandStatus.Succeeded, "failed" => CommandStatus.Failed, _ => throw new ArgumentException("Unknown acknowledgement state.") };
        if ((target == CommandStatus.Failed) != (request.FailureCode is not null)) throw new ArgumentException("Failure code is required only for failed acknowledgements.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        string current;
        Guid? claimant;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT status::text,claimed_by_instance_id FROM commands WHERE id=@id AND target_device_id=@deviceId FOR UPDATE";
            select.Parameters.AddWithValue("id", commandId);
            select.Parameters.AddWithValue("deviceId", deviceId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("command_not_found");
            current = reader.GetString(0);
            claimant = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }
        if (claimant != request.AgentInstanceId) throw new ConflictException("claim_mismatch", "The acknowledgement does not own the command claim.");
        var from = ParseStatus(current);
        if (!CommandPolicy.CanTransition(from, target)) throw new ConflictException("illegal_command_transition", $"Cannot transition from {current} to {request.State}.");
        await TransitionAsync(connection, transaction, commandId, current, request.State, "agent", request.AgentInstanceId, request.FailureCode?.WireValue(), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetForDeviceAsync(deviceId, commandId, cancellationToken);
    }

    private static async Task TransitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid commandId, string from, string to, string actorKind, Guid actorId, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE commands SET status=@to::command_status,
              accepted_at=CASE WHEN @to='accepted' THEN @now ELSE accepted_at END,
              finished_at=CASE WHEN @to IN ('succeeded','failed','expired','cancelled') THEN @now ELSE finished_at END,
              failure_code=@failure::command_failure_code,
              claimed_by_instance_id=CASE WHEN @to='queued' THEN NULL ELSE claimed_by_instance_id END,
              claimed_until=CASE WHEN @to='queued' THEN NULL ELSE claimed_until END
            WHERE id=@id;
            """;
        update.Parameters.AddWithValue("to", to);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.Add(new NpgsqlParameter("failure", NpgsqlDbType.Text) { Value = failureCode is null ? DBNull.Value : failureCode });
        update.Parameters.AddWithValue("id", commandId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await AppendEventAndOutboxAsync(connection, transaction, commandId, from, to, actorKind, actorId, failureCode, now, cancellationToken);
    }

    private static async Task AppendEventAndOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid commandId, string from, string to, string actorKind, Guid actorId, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var append = connection.CreateCommand();
        append.Transaction = transaction;
        append.CommandText = """
            INSERT INTO command_events(id,command_id,sequence,from_status,to_status,actor_kind,actor_id,failure_code,occurred_at,metadata)
            SELECT uuidv7(),c.id,(SELECT max(sequence)+1 FROM command_events WHERE command_id=c.id),@from::command_status,@to::command_status,
              @actorKind,@actorId,@failure::command_failure_code,@now,'{}'::jsonb FROM commands c WHERE c.id=@id;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'CommandStatusChanged','command',id,row_version,
              jsonb_build_object('userId',user_id,'commandId',id,'deviceId',target_device_id,'status',@to,'failureCode',@failure),@now
              FROM commands WHERE id=@id;
            """;
        append.Parameters.AddWithValue("from", from);
        append.Parameters.AddWithValue("to", to);
        append.Parameters.AddWithValue("actorKind", actorKind);
        append.Parameters.AddWithValue("actorId", actorId);
        append.Parameters.Add(new NpgsqlParameter("failure", NpgsqlDbType.Text) { Value = failureCode is null ? DBNull.Value : failureCode });
        append.Parameters.AddWithValue("now", now);
        append.Parameters.AddWithValue("id", commandId);
        await append.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Command> GetForDeviceAsync(Guid deviceId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(CommandSelect + " WHERE c.id=@commandId AND c.target_device_id=@deviceId");
        command.Parameters.AddWithValue("commandId", commandId);
        command.Parameters.AddWithValue("deviceId", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("command_not_found");
        return ReadCommand(reader);
    }

    private static async Task<Command?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string predicate, Guid sessionId, Guid key, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CommandSelect + " WHERE " + predicate;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCommand(reader) : null;
    }

    private const string CommandSelect = """
        SELECT c.id,c.target_device_id,c.type::text,c.status::text,c.issued_at,c.expires_at,c.claimed_until,c.accepted_at,c.finished_at,c.failure_code::text,c.row_version
        FROM commands c
        """;

    private static Command ReadCommand(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), ParseType(reader.GetString(2)), ParseStatus(reader.GetString(3)),
        reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : ParseFailure(reader.GetString(9)), reader.GetInt64(10));

    private static ContractCommandType ParseType(string value) => value == "sign_out" ? ContractCommandType.SignOut : Enum.Parse<ContractCommandType>(value, true);
    private static CommandStatus ParseStatus(string value) => Enum.Parse<CommandStatus>(value, true);
    private static CommandFailureCode ParseFailure(string value) => value switch
    {
        "no_interactive_session" => CommandFailureCode.NoInteractiveSession,
        "permission_denied" => CommandFailureCode.PermissionDenied,
        "local_replay" => CommandFailureCode.LocalReplay,
        "execution_failed" => CommandFailureCode.ExecutionFailed,
        _ => Enum.Parse<CommandFailureCode>(value, true)
    };
    private static DeviceCapability ParseCapability(string value) => value == "sign_out" ? DeviceCapability.SignOut : Enum.Parse<DeviceCapability>(value, true);
}
