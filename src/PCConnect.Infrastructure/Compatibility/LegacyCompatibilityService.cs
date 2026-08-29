using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Commands;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Reminders;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Compatibility;

public sealed record LegacyPrincipal(Guid CredentialId, Guid UserId);
public sealed record LegacyDueReminder(long LegacyId, string Date, string Time, string Text);

public interface ILegacyCompatibilityService
{
    Task<IReadOnlyList<string>> ListDevicesAsync(string apiKey, CancellationToken cancellationToken);
    Task<string?> PollCommandAsync(string apiKey, string deviceName, CancellationToken cancellationToken);
    Task ClearCommandAsync(string apiKey, string deviceName, CancellationToken cancellationToken);
    Task CreateCommandAsync(string apiKey, string deviceName, string requestedCommand, Guid? idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Reminder>> ListRemindersAsync(string apiKey, CancellationToken cancellationToken);
    Task CreateReminderAsync(string apiKey, string dateValue, string timeValue, string text, Guid? idempotencyKey, string correlationId, CancellationToken cancellationToken);
    Task<LegacyDueReminder?> GetDueReminderAsync(string apiKey, string deviceName, CancellationToken cancellationToken);
    Task CompleteReminderAsync(string apiKey, long legacyId, string correlationId, CancellationToken cancellationToken);
    Task TouchDeviceAsync(string apiKey, string deviceName, CancellationToken cancellationToken);
}

public sealed class LegacyCompatibilityService(
    NpgsqlDataSource dataSource,
    SecurityOptions security,
    IClock clock,
    IConfiguration configuration,
    ICommandService commands,
    IReminderService reminders,
    IReminderCipher reminderCipher) : ILegacyCompatibilityService
{
    private readonly byte[] hashingKey = security.DecodeLegacyKey();

    public async Task<IReadOnlyList<string>> ListDevicesAsync(string apiKey, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "command_create_until_day_45", commandCreation: false, cancellationToken);
        await using var command = dataSource.CreateCommand("SELECT display_name FROM devices WHERE user_id=@userId AND status<>'revoked' ORDER BY display_name_normalized");
        command.Parameters.AddWithValue("userId", principal.UserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
        return names;
    }

    public async Task<string?> PollCommandAsync(string apiKey, string deviceName, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "device_poll", commandCreation: false, cancellationToken);
        var deviceId = await DeviceAsync(principal.UserId, deviceName, cancellationToken);
        var instanceId = StableInstanceId(deviceId);
        var page = await commands.ListPendingAsync(deviceId, null, 20, cancellationToken);
        foreach (var candidate in page.Items)
        {
            try
            {
                var claimed = await commands.ClaimAsync(deviceId, candidate.Id, new(instanceId), cancellationToken);
                await commands.AcknowledgeAsync(deviceId, claimed.Id, new("accepted", instanceId, claimed.Id), cancellationToken);
                return claimed.Type == PCConnect.Contracts.V2.CommandType.Lock ? "Lock" : claimed.Type.WireValue();
            }
            catch (Exception exception) when (exception is ConflictException or ResourceGoneException) { }
        }
        return null;
    }

    public async Task ClearCommandAsync(string apiKey, string deviceName, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "device_poll", commandCreation: false, cancellationToken);
        var deviceId = await DeviceAsync(principal.UserId, deviceName, cancellationToken);
        var instanceId = StableInstanceId(deviceId);
        await using var command = dataSource.CreateCommand("""
            SELECT id FROM commands WHERE target_device_id=@deviceId AND claimed_by_instance_id=@instanceId AND status='accepted'
            ORDER BY accepted_at LIMIT 1;
            """);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("instanceId", instanceId);
        if (await command.ExecuteScalarAsync(cancellationToken) is Guid commandId)
            await commands.AcknowledgeAsync(deviceId, commandId, new("succeeded", instanceId, commandId), cancellationToken);
    }

    public async Task CreateCommandAsync(string apiKey, string deviceName, string requestedCommand, Guid? idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "command_create_until_day_45", commandCreation: true, cancellationToken);
        var normalized = LegacyCompatibilityPolicy.EnsureCommandAllowed(requestedCommand);
        var deviceId = await DeviceAsync(principal.UserId, deviceName, cancellationToken);
        var key = idempotencyKey is { } supplied && supplied != Guid.Empty ? supplied : WindowedIdempotencyKey(principal.CredentialId, deviceId, normalized);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM commands WHERE actor_legacy_credential_id=@credentialId AND idempotency_key=@key";
            existing.Parameters.AddWithValue("credentialId", principal.CredentialId);
            existing.Parameters.AddWithValue("key", key);
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            WITH eligible AS (
              SELECT id FROM devices WHERE id=@deviceId AND user_id=@userId AND status<>'revoked' AND 'lock'=ANY(capabilities::text[]) FOR UPDATE
            ), inserted AS (
              INSERT INTO commands(id,user_id,target_device_id,actor_legacy_credential_id,type,status,idempotency_key,issued_at,expires_at,row_version)
              SELECT @id,@userId,id,@credentialId,'lock','queued',@key,@now,@expires,1 FROM eligible RETURNING id
            ), event AS (
              INSERT INTO command_events(id,command_id,sequence,to_status,actor_kind,actor_id,occurred_at,metadata)
              SELECT uuidv7(),id,1,'queued','compatibility',@credentialId,@now,'{}'::jsonb FROM inserted
            ), notification AS (
              INSERT INTO outbox_messages(id,event_type,aggregate_type,aggregate_id,aggregate_version,payload,occurred_at)
              SELECT uuidv7(),'CommandAvailable','command',id,1,
                jsonb_build_object('deviceId',@deviceId,'commandId',id,'expiresAt',@expires),@now FROM inserted
            )
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            SELECT 'LegacyCommandCreated',@userId,'compatibility',@credentialId,'command',id,'success',@correlationId,@now,
              jsonb_build_object('type','lock','clientGeneration','legacy') FROM inserted;
            """;
        create.Parameters.AddWithValue("id", Guid.CreateVersion7(now));
        create.Parameters.AddWithValue("userId", principal.UserId);
        create.Parameters.AddWithValue("deviceId", deviceId);
        create.Parameters.AddWithValue("credentialId", principal.CredentialId);
        create.Parameters.AddWithValue("key", key);
        create.Parameters.AddWithValue("now", now);
        create.Parameters.AddWithValue("expires", now.AddMinutes(2));
        create.Parameters.AddWithValue("correlationId", correlationId);
        if (await create.ExecuteNonQueryAsync(cancellationToken) == 0) throw new ConflictException("device_unavailable", "The device is revoked or does not advertise lock capability.");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Reminder>> ListRemindersAsync(string apiKey, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "reminder_poll", commandCreation: false, cancellationToken);
        var all = new List<Reminder>();
        string? cursor = null;
        do
        {
            var page = await reminders.ListAsync(principal.UserId, cursor, 100, cancellationToken);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }

    public async Task CreateReminderAsync(string apiKey, string dateValue, string timeValue, string text, Guid? idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "reminder_create", commandCreation: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) throw new ArgumentException("Reminder text must be 1-2000 characters.");
        var localStart = ParseLegacyLocalStart(dateValue, timeValue);
        await using var timezoneCommand = dataSource.CreateCommand("SELECT timezone FROM users WHERE id=@userId");
        timezoneCommand.Parameters.AddWithValue("userId", principal.UserId);
        var timezone = (string?)await timezoneCommand.ExecuteScalarAsync(cancellationToken) ?? "Europe/London";
        var key = idempotencyKey is { } supplied && supplied != Guid.Empty
            ? supplied
            : WindowedIdempotencyKey(principal.CredentialId, Guid.Empty, $"reminder|{localStart:O}|{text}");
        await reminders.CreateLegacyAsync(principal.UserId, principal.CredentialId, key,
            new(text.Trim(), ReminderTargetMode.AllDevices, timezone, localStart), correlationId, cancellationToken);
    }

    public async Task<LegacyDueReminder?> GetDueReminderAsync(string apiKey, string deviceName, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "reminder_poll", commandCreation: false, cancellationToken);
        var deviceId = await DeviceAsync(principal.UserId, deviceName, cancellationToken);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rd.id,rd.legacy_numeric_id,r.id,ro.occurrence_at,r.text_ciphertext,r.text_nonce,r.text_tag,
              r.wrapped_data_key,r.wrapping_key_id,r.text_aad_version,rd.status::text
            FROM reminder_deliveries rd
            JOIN reminder_occurrences ro ON ro.id=rd.occurrence_id
            JOIN reminders r ON r.id=ro.reminder_id AND r.user_id=@userId
            WHERE rd.device_id=@deviceId AND rd.status IN ('available','displayed')
              AND ro.cancelled_at IS NULL AND ro.occurrence_at<=@now AND r.deleted_at IS NULL
            ORDER BY ro.occurrence_at,rd.id LIMIT 1 FOR UPDATE OF rd;
            """;
        command.Parameters.AddWithValue("userId", principal.UserId);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var deliveryId = reader.GetGuid(0);
        var legacyId = reader.GetInt64(1);
        var reminderId = reader.GetGuid(2);
        var occurrence = reader.GetFieldValue<DateTimeOffset>(3);
        var encrypted = new EncryptedReminder(reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6),
            reader.GetFieldValue<byte[]>(7), reader.GetString(8), reader.GetInt16(9));
        var text = reminderCipher.Decrypt(reminderId, principal.UserId, encrypted);
        var wasAvailable = reader.GetString(10) == "available";
        await reader.DisposeAsync();
        if (wasAvailable)
        {
            await using var displayed = connection.CreateCommand();
            displayed.Transaction = transaction;
            displayed.CommandText = "UPDATE reminder_deliveries SET status='displayed',acknowledged_at=@now,row_version=row_version+1 WHERE id=@id AND status='available'";
            displayed.Parameters.AddWithValue("now", now);
            displayed.Parameters.AddWithValue("id", deliveryId);
            await displayed.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(legacyId, occurrence.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), occurrence.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text);
    }

    public async Task CompleteReminderAsync(string apiKey, long legacyId, string correlationId, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "reminder_poll", commandCreation: false, cancellationToken);
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH completed AS (
              UPDATE reminder_deliveries rd SET status='completed',acknowledged_at=@now,row_version=row_version+1
              FROM reminder_occurrences ro,reminders r
              WHERE rd.legacy_numeric_id=@legacyId AND rd.status IN ('available','displayed')
                AND ro.id=rd.occurrence_id AND r.id=ro.reminder_id AND r.user_id=@userId
              RETURNING rd.id,r.id AS reminder_id
            )
            INSERT INTO audit_events(event_type,user_id,actor_kind,actor_id,target_type,target_id,outcome,correlation_id,occurred_at,metadata)
            SELECT 'LegacyReminderCompleted',@userId,'compatibility',@credentialId,'reminder_delivery',id,'success',@correlationId,@now,
              jsonb_build_object('reminderId',reminder_id,'clientGeneration','legacy') FROM completed;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("legacyId", legacyId);
        command.Parameters.AddWithValue("userId", principal.UserId);
        command.Parameters.AddWithValue("credentialId", principal.CredentialId);
        command.Parameters.AddWithValue("correlationId", correlationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ResourceNotFoundException("legacy_reminder_delivery_not_found");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task TouchDeviceAsync(string apiKey, string deviceName, CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(apiKey, "device_poll", commandCreation: false, cancellationToken);
        var deviceId = await DeviceAsync(principal.UserId, deviceName, cancellationToken);
        await using var command = dataSource.CreateCommand("UPDATE devices SET last_seen_at=@now,status='online',row_version=row_version+1 WHERE id=@id AND status<>'revoked'");
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LegacyPrincipal> AuthenticateAsync(string apiKey, string route, bool commandCreation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 512) throw new AuthenticationFailureException("invalid_legacy_credential");
        var cutover = CutoverAt();
        var now = clock.UtcNow;
        LegacyCompatibilityPolicy.EnsureAvailable(cutover, now, commandCreation);
        var hash = HMACSHA256.HashData(hashingKey, Encoding.UTF8.GetBytes(apiKey));
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT c.id,c.user_id FROM legacy_compat_credentials c JOIN users u ON u.id=c.user_id
                WHERE c.credential_hash=@hash AND c.revoked_at IS NULL AND c.expires_at>@now
                  AND @route=ANY(c.permitted_routes) AND u.account_state='active';
                """);
            command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("route", route);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException("invalid_legacy_credential");
            return new(reader.GetGuid(0), reader.GetGuid(1));
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private DateTimeOffset CutoverAt()
    {
        if (!configuration.GetValue("Compatibility:Enabled", false)) throw new ResourceGoneException("legacy_compatibility_disabled");
        return DateTimeOffset.TryParse(configuration["Compatibility:CutoverAt"], out var value)
            ? value.ToUniversalTime()
            : throw new InvalidOperationException("Compatibility:CutoverAt must be configured when compatibility is enabled.");
    }

    private async Task<Guid> DeviceAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("PCName is required.");
        await using var command = dataSource.CreateCommand("SELECT id FROM devices WHERE user_id=@userId AND display_name_normalized=@name AND status<>'revoked'");
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("name", Normalization.DeviceName(name));
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : throw new ResourceNotFoundException("device_not_found");
    }

    private Guid WindowedIdempotencyKey(Guid credentialId, Guid deviceId, string command)
    {
        var bucket = clock.UtcNow.ToUnixTimeSeconds() / 5;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"compat-v1|{credentialId:D}|{deviceId:D}|{command}|{bucket}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static DateTime ParseLegacyLocalStart(string date, string time)
    {
        var combined = $"{date.Trim()} {time.Trim()}";
        string[] formats = ["dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm", "dd/MM/yyyy hh:mm tt", "d/M/yyyy H:mm", "d/M/yyyy h:mm tt", "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "M/d/yyyy h:mm tt"];
        if (!DateTime.TryParseExact(combined, formats, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var parsed))
            throw new ArgumentException("Legacy reminder date/time is invalid; expected a recognized calendar date and time.");
        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }

    private static Guid StableInstanceId(Guid deviceId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"pcconnect-legacy-agent|{deviceId:D}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}

public static class LegacyCompatibilityPolicy
{
    public static void EnsureAvailable(DateTimeOffset cutover, DateTimeOffset now, bool commandCreation)
    {
        if (now >= cutover.AddDays(60)) throw new ResourceGoneException("legacy_compatibility_expired");
        if (commandCreation && now >= cutover.AddDays(45)) throw new ResourceGoneException("legacy_command_creation_disabled");
    }

    public static string EnsureCommandAllowed(string requestedCommand)
    {
        var normalized = requestedCommand.Trim().TrimStart(',').Trim().ToLowerInvariant();
        return normalized == "lock"
            ? normalized
            : throw new ConflictException("migration_required", "This legacy client cannot perform step-up authentication; use the v2 controller for this command.");
    }
}
