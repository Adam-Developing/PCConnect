using System.Data;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Domain.Reminders;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Reminders;

public interface IReminderService
{
    Task<Page<Reminder>> ListAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<Reminder> GetAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken);
    Task<Reminder> CreateAsync(Guid userId, Guid sessionId, Guid idempotencyKey, ReminderWrite request, CancellationToken cancellationToken);
    Task<Reminder> CreateLegacyAsync(Guid userId, Guid credentialId, Guid idempotencyKey, ReminderWrite request, string correlationId, CancellationToken cancellationToken);
    Task<Reminder> UpdateAsync(Guid userId, Guid reminderId, ReminderWrite request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken);
    Task<Page<ReminderDelivery>> ListAvailableDeliveriesAsync(Guid deviceId, string? cursor, int limit, CancellationToken cancellationToken);
    Task AcknowledgeDeliveryAsync(Guid deviceId, Guid deliveryId, ReminderAcknowledgement request, CancellationToken cancellationToken);
}

public sealed class ReminderService(NpgsqlDataSource dataSource, IReminderCipher cipher, IClock clock) : IReminderService
{
    public async Task<Page<Reminder>> ListAsync(Guid userId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var position = PageCursor.Decode(cursor);
        await using var command = dataSource.CreateCommand(ReminderSelect + " WHERE r.user_id=@userId AND r.deleted_at IS NULL AND (@cursorTime IS NULL OR (r.updated_at,r.id)<(@cursorTime,@cursorId)) ORDER BY r.updated_at DESC,r.id DESC LIMIT @limit");
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("cursorTime", NpgsqlDbType.TimestampTz) { Value = position is null ? DBNull.Value : position.Value.Timestamp });
        command.Parameters.Add(new("cursorId", NpgsqlDbType.Uuid) { Value = position is null ? DBNull.Value : position.Value.Id });
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Reminder>();
        var positions = new List<PagePosition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadReminder(reader, userId));
            positions.Add(new(reader.GetFieldValue<DateTimeOffset>(16), reader.GetGuid(0)));
        }
        var hasMore = items.Count > limit;
        if (hasMore) { items.RemoveAt(items.Count - 1); positions.RemoveAt(positions.Count - 1); }
        return new(items, hasMore && positions.Count > 0 ? PageCursor.Encode(positions[^1].Timestamp, positions[^1].Id) : null);
    }

    public async Task<Reminder> GetAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ReminderSelect + " WHERE r.id=@id AND r.user_id=@userId AND r.deleted_at IS NULL");
        command.Parameters.AddWithValue("id", reminderId);
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("reminder_not_found");
        return ReadReminder(reader, userId);
    }

    public async Task<Reminder> CreateAsync(Guid userId, Guid sessionId, Guid idempotencyKey, ReminderWrite request, CancellationToken cancellationToken)
    {
        Validate(request);
        if (idempotencyKey == Guid.Empty) throw new ArgumentException("A non-empty Idempotency-Key is required.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM reminders WHERE creation_session_id=@sessionId AND idempotency_key=@key";
            existing.Parameters.AddWithValue("sessionId", sessionId);
            existing.Parameters.AddWithValue("key", idempotencyKey);
            if (await existing.ExecuteScalarAsync(cancellationToken) is Guid existingId)
            {
                await transaction.CommitAsync(cancellationToken);
                return await GetAsync(userId, existingId, cancellationToken);
            }
        }
        var targets = request.TargetDeviceIds?.Distinct().ToArray() ?? [];
        await ValidateTargetsAsync(connection, transaction, userId, request.TargetMode, targets, cancellationToken);
        var reminderId = Guid.CreateVersion7(now);
        var encrypted = cipher.Encrypt(reminderId, userId, request.Text);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO reminders(id,user_id,creation_session_id,idempotency_key,target_mode,timezone,timezone_assumed,local_start,recurrence_rule,
                text_ciphertext,text_nonce,text_tag,wrapped_data_key,wrapping_key_id,text_aad_version,created_at,updated_at,row_version)
            VALUES(@id,@userId,@sessionId,@key,@targetMode::reminder_target_mode,@timezone,false,@localStart,@rrule,
                @ciphertext,@nonce,@tag,@wrappedKey,@keyId,@aadVersion,@now,@now,1);
            INSERT INTO reminder_targets(reminder_id,device_id,created_at) SELECT @id,unnest(@targets::uuid[]),@now;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(uuidv7(),'ReminderChanged','reminder',@id,1,
              jsonb_build_object('userId',@userId,'reminderId',@id,'deliveryId',NULL,'change','created'),@now);
            """;
        AddWriteParameters(insert, reminderId, userId, sessionId, idempotencyKey, request, targets, encrypted, now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(userId, reminderId, cancellationToken);
    }

    public async Task<Reminder> CreateLegacyAsync(Guid userId, Guid credentialId, Guid idempotencyKey, ReminderWrite request, string correlationId, CancellationToken cancellationToken)
    {
        Validate(request);
        if (idempotencyKey == Guid.Empty) throw new ArgumentException("A non-empty idempotency key is required.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM reminders WHERE creation_legacy_credential_id=@credentialId AND idempotency_key=@key";
            existing.Parameters.AddWithValue("credentialId", credentialId);
            existing.Parameters.AddWithValue("key", idempotencyKey);
            if (await existing.ExecuteScalarAsync(cancellationToken) is Guid existingId)
            {
                await transaction.CommitAsync(cancellationToken);
                return await GetAsync(userId, existingId, cancellationToken);
            }
        }

        var targets = request.TargetDeviceIds?.Distinct().ToArray() ?? [];
        await ValidateTargetsAsync(connection, transaction, userId, request.TargetMode, targets, cancellationToken);
        var reminderId = Guid.CreateVersion7(now);
        var encrypted = cipher.Encrypt(reminderId, userId, request.Text);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO reminders(id,user_id,creation_legacy_credential_id,idempotency_key,target_mode,timezone,timezone_assumed,local_start,recurrence_rule,
                text_ciphertext,text_nonce,text_tag,wrapped_data_key,wrapping_key_id,text_aad_version,created_at,updated_at,row_version)
            VALUES(@id,@userId,@credentialId,@key,@targetMode::reminder_target_mode,@timezone,true,@localStart,@rrule,
                @ciphertext,@nonce,@tag,@wrappedKey,@keyId,@aadVersion,@now,@now,1);
            INSERT INTO reminder_targets(reminder_id,device_id,created_at) SELECT @id,unnest(@targets::uuid[]),@now;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            VALUES(uuidv7(),'ReminderChanged','reminder',@id,1,
              jsonb_build_object('userId',@userId,'reminderId',@id,'deliveryId',NULL,'change','created'),@now);
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            VALUES('LegacyReminderCreated',@userId,'compatibility',@credentialId,'reminder',@id,'success',@correlationId,@now,
              jsonb_build_object('clientGeneration','legacy'));
            """;
        AddWriteParameters(insert, reminderId, userId, null, idempotencyKey, request, targets, encrypted, now);
        insert.Parameters.AddWithValue("credentialId", credentialId);
        insert.Parameters.AddWithValue("correlationId", correlationId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(userId, reminderId, cancellationToken);
    }

    public async Task<Reminder> UpdateAsync(Guid userId, Guid reminderId, ReminderWrite request, CancellationToken cancellationToken)
    {
        Validate(request);
        var now = clock.UtcNow;
        var targets = request.TargetDeviceIds?.Distinct().ToArray() ?? [];
        var encrypted = cipher.Encrypt(reminderId, userId, request.Text);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText = "SELECT row_version FROM reminders WHERE id=@id AND user_id=@userId AND deleted_at IS NULL FOR UPDATE";
            ownership.Parameters.AddWithValue("id", reminderId);
            ownership.Parameters.AddWithValue("userId", userId);
            var current = await ownership.ExecuteScalarAsync(cancellationToken);
            if (current is null) throw new ResourceNotFoundException("reminder_not_found");
            if (request.ExpectedVersion is not null && Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture) != request.ExpectedVersion)
                throw new ConflictException("version_conflict", "The reminder changed since it was read.");
        }
        await ValidateTargetsAsync(connection, transaction, userId, request.TargetMode, targets, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE reminders SET target_mode=@targetMode::reminder_target_mode,timezone=@timezone,local_start=@localStart,recurrence_rule=@rrule,
                text_ciphertext=@ciphertext,text_nonce=@nonce,text_tag=@tag,wrapped_data_key=@wrappedKey,wrapping_key_id=@keyId,
                text_aad_version=@aadVersion,updated_at=@now,row_version=row_version+1
            WHERE id=@id AND user_id=@userId AND deleted_at IS NULL;
            DELETE FROM reminder_targets WHERE reminder_id=@id;
            INSERT INTO reminder_targets(reminder_id,device_id,created_at) SELECT @id,unnest(@targets::uuid[]),@now;
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'ReminderChanged','reminder',id,row_version,
              jsonb_build_object('userId',user_id,'reminderId',id,'deliveryId',NULL,'change','updated'),@now
              FROM reminders WHERE id=@id AND user_id=@userId;
            """;
        AddWriteParameters(update, reminderId, userId, null, null, request, targets, encrypted, now);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0) throw new ResourceNotFoundException("reminder_not_found");
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(userId, reminderId, cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH deleted AS (
              UPDATE reminders SET deleted_at=@now,updated_at=@now,row_version=row_version+1
              WHERE id=@id AND user_id=@userId AND deleted_at IS NULL RETURNING id,user_id,row_version
            )
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'ReminderChanged','reminder',id,row_version,
              jsonb_build_object('userId',user_id,'reminderId',id,'deliveryId',NULL,'change','deleted'),@now FROM deleted;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", reminderId);
        command.Parameters.AddWithValue("userId", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ResourceNotFoundException("reminder_not_found");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Page<ReminderDelivery>> ListAvailableDeliveriesAsync(Guid deviceId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var position = PageCursor.Decode(cursor);
        await using var command = dataSource.CreateCommand("""
            SELECT rd.id,r.id,ro.occurrence_at,r.user_id,r.text_ciphertext,r.text_nonce,r.text_tag,
              r.wrapped_data_key,r.wrapping_key_id,r.text_aad_version,rd.status::text,rd.row_version
            FROM reminder_deliveries rd
            JOIN reminder_occurrences ro ON ro.id=rd.occurrence_id
            JOIN reminders r ON r.id=ro.reminder_id
            JOIN devices d ON d.id=rd.device_id AND d.user_id=r.user_id
            WHERE rd.device_id=@deviceId AND rd.status IN ('available','displayed')
              AND ro.cancelled_at IS NULL AND r.deleted_at IS NULL AND d.status<>'revoked'
              AND (@cursorTime IS NULL OR (ro.occurrence_at,rd.id)>(@cursorTime,@cursorId))
            ORDER BY ro.occurrence_at,rd.id LIMIT @limit;
            """);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.Add(new("cursorTime", NpgsqlDbType.TimestampTz) { Value = position is null ? DBNull.Value : position.Value.Timestamp });
        command.Parameters.Add(new("cursorId", NpgsqlDbType.Uuid) { Value = position is null ? DBNull.Value : position.Value.Id });
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var deliveries = new List<ReminderDelivery>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var deliveryId = reader.GetGuid(0);
            var reminderId = reader.GetGuid(1);
            var ownerId = reader.GetGuid(3);
            var encrypted = new EncryptedReminder(
                reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6),
                reader.GetFieldValue<byte[]>(7), reader.GetString(8), reader.GetInt16(9));
            deliveries.Add(new(deliveryId, reminderId, reader.GetFieldValue<DateTimeOffset>(2),
                cipher.Decrypt(reminderId, ownerId, encrypted), reader.GetString(10), reader.GetInt64(11)));
        }
        var hasMore = deliveries.Count > limit;
        if (hasMore) deliveries.RemoveAt(deliveries.Count - 1);
        return new(deliveries, hasMore && deliveries.Count > 0 ? PageCursor.Encode(deliveries[^1].OccurrenceAt, deliveries[^1].Id) : null);
    }

    public async Task AcknowledgeDeliveryAsync(Guid deviceId, Guid deliveryId, ReminderAcknowledgement request, CancellationToken cancellationToken)
    {
        if (request.State is not ("displayed" or "dismissed" or "completed")) throw new ArgumentException("Invalid reminder acknowledgement state.");
        var now = clock.UtcNow;
        if (request.AcknowledgedAt > now.AddMinutes(5) || request.AcknowledgedAt < now.AddDays(-7)) throw new ArgumentException("Acknowledgement timestamp is outside the accepted window.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH acknowledged AS (
              UPDATE reminder_deliveries rd SET status=@state::reminder_delivery_status,acknowledged_at=@ack,row_version=rd.row_version+1
              FROM reminder_occurrences ro,reminders r
              WHERE rd.id=@id AND rd.device_id=@deviceId AND rd.status IN ('pending','available','displayed')
                AND ro.id=rd.occurrence_id AND r.id=ro.reminder_id
              RETURNING rd.id,rd.row_version,r.id AS reminder_id,r.user_id
            )
            INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
            SELECT uuidv7(),'ReminderChanged','reminder_delivery',id,row_version,
              jsonb_build_object('userId',user_id,'reminderId',reminder_id,'deliveryId',id,'change','delivery_acknowledged'),@now
              FROM acknowledged;
            """;
        command.Parameters.AddWithValue("state", request.State);
        command.Parameters.AddWithValue("ack", request.AcknowledgedAt);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ConflictException("delivery_not_acknowledgeable", "The delivery is missing or already final.");
        await transaction.CommitAsync(cancellationToken);
    }

    private const string ReminderSelect = """
        SELECT r.id,r.target_mode::text,r.timezone,r.timezone_assumed,r.local_start,r.recurrence_rule,r.text_ciphertext,r.text_nonce,r.text_tag,
          r.wrapped_data_key,r.wrapping_key_id,r.text_aad_version,r.created_at,r.row_version,
          ARRAY(SELECT rt.device_id FROM reminder_targets rt WHERE rt.reminder_id=r.id ORDER BY rt.device_id),
          (SELECT min(ro.occurrence_at) FROM reminder_occurrences ro WHERE ro.reminder_id=r.id AND ro.cancelled_at IS NULL AND ro.occurrence_at>=now()),
          r.updated_at,acknowledgement.status,acknowledgement.acknowledged_at,acknowledgement.device_name
        FROM reminders r
        LEFT JOIN LATERAL (
          SELECT rd.status::text AS status,rd.acknowledged_at,d.display_name AS device_name
          FROM reminder_occurrences ro
          JOIN reminder_deliveries rd ON rd.occurrence_id=ro.id
          JOIN devices d ON d.id=rd.device_id
          WHERE ro.reminder_id=r.id AND rd.status IN ('dismissed','completed')
          ORDER BY rd.acknowledged_at DESC,rd.id DESC
          LIMIT 1
        ) acknowledgement ON true
        """;

    private Reminder ReadReminder(NpgsqlDataReader reader, Guid userId)
    {
        var id = reader.GetGuid(0);
        var encrypted = new EncryptedReminder(reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7), reader.GetFieldValue<byte[]>(8), reader.GetFieldValue<byte[]>(9), reader.GetString(10), reader.GetInt16(11));
        return new(id, cipher.Decrypt(id, userId, encrypted), reader.GetString(1) == "all_devices" ? ReminderTargetMode.AllDevices : ReminderTargetMode.SelectedDevices,
            reader.GetFieldValue<Guid[]>(14), reader.GetString(2), reader.GetBoolean(3), reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15), reader.GetFieldValue<DateTimeOffset>(12), reader.GetInt64(13),
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            reader.IsDBNull(19) ? null : reader.GetString(19));
    }

    private static void AddWriteParameters(NpgsqlCommand command, Guid id, Guid userId, Guid? sessionId, Guid? key, ReminderWrite request, Guid[] targets, EncryptedReminder encrypted, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("sessionId", NpgsqlDbType.Uuid) { Value = sessionId is null ? DBNull.Value : sessionId.Value });
        command.Parameters.Add(new("key", NpgsqlDbType.Uuid) { Value = key is null ? DBNull.Value : key.Value });
        command.Parameters.AddWithValue("targetMode", request.TargetMode.WireValue());
        command.Parameters.AddWithValue("timezone", request.Timezone);
        command.Parameters.Add(new("localStart", NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(request.LocalStart, DateTimeKind.Unspecified) });
        command.Parameters.Add(new("rrule", NpgsqlDbType.Text) { Value = request.RecurrenceRule is null ? DBNull.Value : request.RecurrenceRule });
        command.Parameters.AddWithValue("ciphertext", encrypted.Ciphertext);
        command.Parameters.AddWithValue("nonce", encrypted.Nonce);
        command.Parameters.AddWithValue("tag", encrypted.Tag);
        command.Parameters.AddWithValue("wrappedKey", encrypted.WrappedDataKey);
        command.Parameters.AddWithValue("keyId", encrypted.WrappingKeyId);
        command.Parameters.AddWithValue("aadVersion", encrypted.TextAadVersion);
        command.Parameters.AddWithValue("targets", targets);
        command.Parameters.AddWithValue("now", now);
    }

    private static async Task ValidateTargetsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, ReminderTargetMode mode, Guid[] targets, CancellationToken cancellationToken)
    {
        if (mode == ReminderTargetMode.SelectedDevices && targets.Length == 0) throw new ArgumentException("Selected-device reminders require at least one target.");
        if (mode == ReminderTargetMode.AllDevices && targets.Length != 0) throw new ArgumentException("All-device reminders cannot specify target device IDs.");
        if (targets.Length == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM devices WHERE user_id=@userId AND id=ANY(@targets) AND status<>'revoked' AND 'reminders'=ANY(capabilities)";
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("targets", targets);
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (count != targets.Length) throw new ResourceNotFoundException("reminder_target_not_found");
    }

    private static void Validate(ReminderWrite request)
    {
        if (request.Text.Length is < 1 or > 2000) throw new ArgumentException("Reminder text must be 1-2000 characters.");
        if (request.Timezone.Length is < 1 or > 100) throw new ArgumentException("Timezone must be 1-100 characters.");
        try { _ = RecurrenceScheduler.Generate(request.LocalStart, request.Timezone, request.RecurrenceRule, DateTimeOffset.UtcNow.AddDays(1), 10_000); }
        catch (TimeZoneNotFoundException exception) { throw new ArgumentException("Timezone must be a recognized IANA identifier.", nameof(request), exception); }
    }
}
