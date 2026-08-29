using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

return await MigrationProgram.RunAsync(args, CancellationToken.None);

internal static class MigrationProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] CompatibilityRoutes = ["device_poll", "reminder_poll", "reminder_create", "command_create_until_day_45"];

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var options = Options.Parse(args);
            var started = DateTimeOffset.UtcNow;
            var snapshotBytes = await File.ReadAllBytesAsync(options.SnapshotPath, cancellationToken);
            if (snapshotBytes.Length > 100 * 1024 * 1024) throw new InvalidOperationException("Snapshot exceeds the 100 MiB safety limit.");
            var snapshot = JsonSerializer.Deserialize<LegacySnapshot>(snapshotBytes, JsonOptions)
                ?? throw new InvalidOperationException("Legacy snapshot is empty.");
            CryptographicOperations.ZeroMemory(snapshotBytes);

            var checksumKey = ReadKey("PCCONNECT_MIGRATION_CHECKSUM_KEY");
            var compatibilityKey = ReadKey("PCCONNECT_LEGACY_CREDENTIAL_HASHING_KEY");
            try
            {
                var analysis = Analyze(snapshot, checksumKey);
                ImportCounts counts;
                if (options.Mode == "dry_run") counts = analysis.Counts;
                else
                {
                    if (analysis.Quarantines.Count > 0 || analysis.Collisions.Count > 0)
                        throw new InvalidOperationException("Import is blocked by unresolved quarantine or collision cases.");
                    counts = await ImportAsync(snapshot, analysis, options, compatibilityKey, cancellationToken);
                }

                var manifest = BuildManifest(snapshot, analysis, counts, options, started, DateTimeOffset.UtcNow, checksumKey);
                var manifestDirectory = Path.GetDirectoryName(options.ManifestPath);
                if (!string.IsNullOrEmpty(manifestDirectory)) Directory.CreateDirectory(manifestDirectory);
                await File.WriteAllTextAsync(options.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
                Console.WriteLine($"Migration {options.Mode} completed: {counts.Users} users, {counts.Devices} devices, {counts.Reminders} reminders; manifest written.");
                return analysis.Quarantines.Count > 0 || analysis.Collisions.Count > 0 ? 2 : 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(checksumKey);
                CryptographicOperations.ZeroMemory(compatibilityKey);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Migration failed safely: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static Analysis Analyze(LegacySnapshot snapshot, byte[] hmacKey)
    {
        var quarantines = new Dictionary<string, int>(StringComparer.Ordinal);
        var collisions = new Dictionary<string, int>(StringComparer.Ordinal);
        var validUsers = new List<ValidatedUser>();
        foreach (var user in snapshot.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrWhiteSpace(user.Email)) { Add(quarantines, "invalid_user_shape"); continue; }
            if (user.PasswordSha256 is null || user.PasswordSha256.Length != 64 || !user.PasswordSha256.All(Uri.IsHexDigit)) { Add(quarantines, "invalid_verifier"); continue; }
            if (!TryInstant(user.CreatedAt, out var createdAt)) { Add(quarantines, "unparseable_user_timestamp"); continue; }
            DateOnly? dateOfBirth = null;
            if (user.DateOfBirth is not null)
            {
                if (!DateOnly.TryParseExact(user.DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)) { Add(quarantines, "invalid_date_of_birth"); continue; }
                dateOfBirth = parsedDate;
            }
            validUsers.Add(new(user, Normalization.AccountIdentifier(user.Username), Normalization.AccountIdentifier(user.Email), createdAt, dateOfBirth, Checksum(user, hmacKey)));
        }
        foreach (var group in validUsers.GroupBy(x => x.Username, StringComparer.Ordinal).Where(x => x.Count() > 1)) Add(collisions, "duplicate_normalized_username", group.Count());
        foreach (var group in validUsers.GroupBy(x => x.Email, StringComparer.Ordinal).Where(x => x.Count() > 1)) Add(collisions, "duplicate_normalized_email", group.Count());

        var userIds = validUsers.Select(x => x.Source.Id).ToHashSet(StringComparer.Ordinal);
        var validDevices = new List<ValidatedDevice>();
        foreach (var device in snapshot.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || !userIds.Contains(device.UserId)) { Add(quarantines, "orphan_device"); continue; }
            if (string.IsNullOrWhiteSpace(device.DisplayName)) { Add(quarantines, "invalid_device_name"); continue; }
            DateTimeOffset? lastSeen = null;
            if (device.LastSeenAt is not null)
            {
                if (!TryInstant(device.LastSeenAt, out var parsed)) { Add(quarantines, "unparseable_device_timestamp"); continue; }
                lastSeen = parsed;
            }
            var capabilities = device.Capabilities?.Where(IsCapability).Distinct(StringComparer.Ordinal).ToArray() ?? ["lock", "shutdown", "restart"];
            if (capabilities.Length == 0) { Add(quarantines, "unknown_device_capability"); continue; }
            validDevices.Add(new(device, Normalization.DeviceName(device.DisplayName), lastSeen, capabilities, Checksum(device, hmacKey)));
        }
        foreach (var group in validDevices.GroupBy(x => $"{x.Source.UserId}\0{x.NormalizedName}", StringComparer.Ordinal).Where(x => x.Count() > 1)) Add(collisions, "duplicate_normalized_device_name", group.Count());

        var validReminders = new List<ValidatedReminder>();
        foreach (var reminder in snapshot.Reminders)
        {
            if (string.IsNullOrWhiteSpace(reminder.Id) || !userIds.Contains(reminder.UserId)) { Add(quarantines, "orphan_reminder"); continue; }
            if (string.IsNullOrWhiteSpace(reminder.Text) || reminder.Text.Length > 2000) { Add(quarantines, "invalid_reminder_text"); continue; }
            if (!DateTime.TryParseExact(reminder.LocalStart, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localStart)) { Add(quarantines, "unparseable_reminder_timestamp"); continue; }
            var timezone = string.IsNullOrWhiteSpace(reminder.Timezone) ? "Europe/London" : reminder.Timezone;
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException) { Add(quarantines, "unknown_timezone"); continue; }
            validReminders.Add(new(reminder, DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), timezone, reminder.Timezone is null, Checksum(reminder, hmacKey)));
        }
        return new(validUsers, validDevices, validReminders, quarantines, collisions, new(validUsers.Count, validDevices.Count, validReminders.Count));
    }

    private static async Task<ImportCounts> ImportAsync(LegacySnapshot snapshot, Analysis analysis, Options options, byte[] compatibilityKey, CancellationToken cancellationToken)
    {
        var target = Environment.GetEnvironmentVariable("PCCONNECT_MIGRATION_TARGET")
            ?? throw new InvalidOperationException("PCCONNECT_MIGRATION_TARGET is required for non-dry-run imports.");
        await using var dataSource = NpgsqlDataSource.Create(target);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(72426602)";
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var userMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var user in analysis.Users)
        {
            var id = await MappingIdAsync(connection, transaction, options.SourceSystem, "user", user.Source.Id, user.Checksum, cancellationToken);
            userMap[user.Source.Id] = id;
            await UpsertUserAsync(connection, transaction, id, user, options, compatibilityKey, cancellationToken);
        }
        var deviceMap = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var device in analysis.Devices)
        {
            var id = await MappingIdAsync(connection, transaction, options.SourceSystem, "device", device.Source.Id, device.Checksum, cancellationToken);
            deviceMap[device.Source.Id] = id;
            await UpsertDeviceAsync(connection, transaction, id, userMap[device.Source.UserId], device, cancellationToken);
        }

        var reminderKeyId = Environment.GetEnvironmentVariable("PCCONNECT_MIGRATION_REMINDER_KEY_ID")
            ?? throw new InvalidOperationException("PCCONNECT_MIGRATION_REMINDER_KEY_ID is required.");
        var reminderKey = Environment.GetEnvironmentVariable("PCCONNECT_MIGRATION_REMINDER_KEY")
            ?? throw new InvalidOperationException("PCCONNECT_MIGRATION_REMINDER_KEY is required.");
        var cipher = new ReminderCipher(new SecurityOptions { ActiveReminderKeyId = reminderKeyId, ReminderWrappingKeys = new() { [reminderKeyId] = reminderKey } });
        foreach (var reminder in analysis.Reminders)
        {
            var id = await MappingIdAsync(connection, transaction, options.SourceSystem, "reminder", reminder.Source.Id, reminder.Checksum, cancellationToken);
            await UpsertReminderAsync(connection, transaction, id, userMap[reminder.Source.UserId], reminder, deviceMap, cipher, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return analysis.Counts;
    }

    private static async Task<Guid> MappingIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sourceSystem, string entityType, string legacyId, string checksum, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO legacy_id_map(source_system,entity_type,legacy_id,v2_id,source_row_checksum)
            VALUES(@source,@entity,@legacy,@id,@checksum)
            ON CONFLICT(source_system,entity_type,legacy_id) DO UPDATE SET source_row_checksum=EXCLUDED.source_row_checksum,last_reconciled_at=now()
            RETURNING v2_id;
            """;
        command.Parameters.AddWithValue("source", sourceSystem);
        command.Parameters.AddWithValue("entity", entityType);
        command.Parameters.AddWithValue("legacy", legacyId);
        command.Parameters.AddWithValue("id", StableMappingId(sourceSystem, entityType, legacyId));
        command.Parameters.AddWithValue("checksum", checksum);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Mapping insert failed."));
    }

    private static async Task UpsertUserAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, ValidatedUser user, Options options, byte[] compatibilityKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO users(id,username,email,display_name,date_of_birth,marketing_opt_in,marketing_consent_at,timezone,timezone_assumed,account_state,created_at,updated_at)
            VALUES(@id,@username,@email,@name,@dob,@marketing,@consent,'Europe/London',true,@state,@created,@now)
            ON CONFLICT(id) DO UPDATE SET username=EXCLUDED.username,email=EXCLUDED.email,display_name=EXCLUDED.display_name,date_of_birth=EXCLUDED.date_of_birth,
              marketing_opt_in=EXCLUDED.marketing_opt_in,marketing_consent_at=EXCLUDED.marketing_consent_at,updated_at=EXCLUDED.updated_at,row_version=users.row_version+1;
            INSERT INTO password_credentials(user_id,legacy_sha256,changed_at) VALUES(@id,@password,@now)
            ON CONFLICT(user_id) DO UPDATE SET legacy_sha256=CASE WHEN password_credentials.password_hash IS NULL THEN EXCLUDED.legacy_sha256 ELSE NULL END;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("username", user.Source.Username.Trim());
        command.Parameters.AddWithValue("email", user.Source.Email.Trim());
        command.Parameters.AddWithValue("name", string.IsNullOrWhiteSpace(user.Source.DisplayName) ? user.Source.Username.Trim() : user.Source.DisplayName.Trim());
        command.Parameters.Add(new("dob", NpgsqlDbType.Date) { Value = user.DateOfBirth is null ? DBNull.Value : user.DateOfBirth.Value });
        command.Parameters.AddWithValue("marketing", user.Source.MarketingOptIn);
        command.Parameters.Add(new("consent", NpgsqlDbType.TimestampTz) { Value = user.Source.MarketingOptIn && TryInstant(user.Source.MarketingConsentAt, out var consent) ? consent : DBNull.Value });
        command.Parameters.AddWithValue("state", user.Source.Enabled ? "active" : "disabled");
        command.Parameters.AddWithValue("created", user.CreatedAt);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("password", user.Source.PasswordSha256!.ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(user.Source.ApiKey))
        {
            using var hmac = new HMACSHA256(compatibilityKey);
            var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(user.Source.ApiKey));
            await using var compat = connection.CreateCommand();
            compat.Transaction = transaction;
            compat.CommandText = """
                INSERT INTO legacy_compat_credentials(id,user_id,credential_hash,permitted_routes,created_at,expires_at)
                VALUES(uuidv7(),@userId,@hash,@routes,@now,@expires)
                ON CONFLICT(credential_hash) DO UPDATE SET permitted_routes=EXCLUDED.permitted_routes,expires_at=EXCLUDED.expires_at;
                """;
            compat.Parameters.AddWithValue("userId", id);
            compat.Parameters.AddWithValue("hash", digest);
            compat.Parameters.AddWithValue("routes", CompatibilityRoutes);
            compat.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            compat.Parameters.AddWithValue("expires", options.CompatSunset);
            await compat.ExecuteNonQueryAsync(cancellationToken);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static async Task UpsertDeviceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid userId, ValidatedDevice device, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices(id,user_id,platform,display_name,display_name_normalized,agent_version,protocol_version,timezone,capabilities,status,last_seen_at,enrolled_at)
            VALUES(@id,@userId,@platform::platform_type,@name,@normalized,@version,2,@timezone,@capabilities::device_capability[],'offline',@lastSeen,@now)
            ON CONFLICT(id) DO UPDATE SET display_name=EXCLUDED.display_name,display_name_normalized=EXCLUDED.display_name_normalized,
              agent_version=EXCLUDED.agent_version,timezone=EXCLUDED.timezone,capabilities=EXCLUDED.capabilities,status='offline',last_seen_at=EXCLUDED.last_seen_at,row_version=devices.row_version+1;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("platform", IsPlatform(device.Source.Platform) ? device.Source.Platform! : "windows");
        command.Parameters.AddWithValue("name", device.Source.DisplayName.Trim());
        command.Parameters.AddWithValue("normalized", device.NormalizedName);
        command.Parameters.AddWithValue("version", device.Source.AgentVersion ?? "legacy");
        command.Parameters.Add(new("timezone", NpgsqlDbType.Text) { Value = device.Source.Timezone is null ? DBNull.Value : device.Source.Timezone });
        command.Parameters.AddWithValue("capabilities", device.Capabilities);
        command.Parameters.Add(new("lastSeen", NpgsqlDbType.TimestampTz) { Value = device.LastSeenAt is null ? DBNull.Value : device.LastSeenAt.Value });
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertReminderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid userId, ValidatedReminder reminder, Dictionary<string, Guid> deviceMap, ReminderCipher cipher, CancellationToken cancellationToken)
    {
        var encrypted = cipher.Encrypt(id, userId, reminder.Source.Text);
        var targets = reminder.Source.TargetDeviceIds?.Where(deviceMap.ContainsKey).Select(x => deviceMap[x]).Distinct().ToArray() ?? [];
        var mode = targets.Length == 0 ? "all_devices" : "selected_devices";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reminders(id,user_id,target_mode,timezone,timezone_assumed,local_start,recurrence_rule,text_ciphertext,text_nonce,text_tag,wrapped_data_key,wrapping_key_id,text_aad_version,created_at,updated_at)
            VALUES(@id,@userId,@mode::reminder_target_mode,@timezone,@assumed,@local,@rrule,@cipher,@nonce,@tag,@wrapped,@keyId,@aad,@now,@now)
            ON CONFLICT(id) DO UPDATE SET target_mode=EXCLUDED.target_mode,timezone=EXCLUDED.timezone,timezone_assumed=EXCLUDED.timezone_assumed,
              local_start=EXCLUDED.local_start,recurrence_rule=EXCLUDED.recurrence_rule,text_ciphertext=EXCLUDED.text_ciphertext,text_nonce=EXCLUDED.text_nonce,
              text_tag=EXCLUDED.text_tag,wrapped_data_key=EXCLUDED.wrapped_data_key,wrapping_key_id=EXCLUDED.wrapping_key_id,text_aad_version=EXCLUDED.text_aad_version,
              updated_at=EXCLUDED.updated_at,row_version=reminders.row_version+1;
            DELETE FROM reminder_targets WHERE reminder_id=@id;
            INSERT INTO reminder_targets(reminder_id,device_id,created_at) SELECT @id,unnest(@targets::uuid[]),@now;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("mode", mode);
        command.Parameters.AddWithValue("timezone", reminder.Timezone);
        command.Parameters.AddWithValue("assumed", reminder.TimezoneAssumed);
        command.Parameters.Add(new("local", NpgsqlDbType.Timestamp) { Value = reminder.LocalStart });
        command.Parameters.Add(new("rrule", NpgsqlDbType.Text) { Value = reminder.Source.RecurrenceRule is null ? DBNull.Value : reminder.Source.RecurrenceRule });
        command.Parameters.AddWithValue("cipher", encrypted.Ciphertext);
        command.Parameters.AddWithValue("nonce", encrypted.Nonce);
        command.Parameters.AddWithValue("tag", encrypted.Tag);
        command.Parameters.AddWithValue("wrapped", encrypted.WrappedDataKey);
        command.Parameters.AddWithValue("keyId", encrypted.WrappingKeyId);
        command.Parameters.AddWithValue("aad", encrypted.TextAadVersion);
        command.Parameters.AddWithValue("targets", targets);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object BuildManifest(LegacySnapshot snapshot, Analysis analysis, ImportCounts counts, Options options, DateTimeOffset started, DateTimeOffset finished, byte[] key)
    {
        var result = analysis.Quarantines.Count == 0 && analysis.Collisions.Count == 0 ? "passed" : "failed";
        object Entity(string name, int source, int imported, IEnumerable<string> ids) => new
        {
            entity = name, sourceCount = source, importedCount = imported, updatedCount = 0,
            skippedCount = source - imported, targetCount = imported, mappingChecksum = AggregateChecksum(ids, key)
        };
        return new
        {
            manifestVersion = 1, runId = Guid.CreateVersion7(started), mode = options.Mode,
            sourceSnapshotId = snapshot.SourceSnapshotId, sourceSchemaChecksum = snapshot.SourceSchemaChecksum,
            startedAt = started, finishedAt = finished,
            entities = new[]
            {
                Entity("users", snapshot.Users.Count, counts.Users, analysis.Users.Select(x => x.Source.Id)),
                Entity("password_credentials", snapshot.Users.Count, counts.Users, analysis.Users.Select(x => x.Source.Id)),
                Entity("legacy_compat_credentials", snapshot.Users.Count(x => !string.IsNullOrWhiteSpace(x.ApiKey)), analysis.Users.Count(x => !string.IsNullOrWhiteSpace(x.Source.ApiKey)), analysis.Users.Where(x => !string.IsNullOrWhiteSpace(x.Source.ApiKey)).Select(x => x.Source.Id)),
                Entity("devices", snapshot.Devices.Count, counts.Devices, analysis.Devices.Select(x => x.Source.Id)),
                Entity("reminders", snapshot.Reminders.Count, counts.Reminders, analysis.Reminders.Select(x => x.Source.Id)),
                Entity("reminder_targets", snapshot.Reminders.Sum(x => x.TargetDeviceIds?.Count ?? 0), analysis.Reminders.Sum(x => x.Source.TargetDeviceIds?.Count ?? 0), analysis.Reminders.Select(x => x.Source.Id))
            },
            quarantines = analysis.Quarantines, collisions = analysis.Collisions, result
        };
    }

    private static string Checksum<T>(T value, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))).ToLowerInvariant();
    }
    private static string AggregateChecksum(IEnumerable<string> ids, byte[] key) => Checksum(ids.Order(StringComparer.Ordinal).ToArray(), key);
    private static Guid StableMappingId(string sourceSystem, string entityType, string legacyId)
    {
        // RFC 9562 UUIDv8 carrying a truncated SHA-256 digest. The fixed namespace
        // is PCConnect-specific; source values are length-delimited so distinct
        // triples cannot become ambiguous.
        var namespaceBytes = new Guid("43a66e2c-14d8-5c48-a457-29c8ebea816f").ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes($"{sourceSystem.Length}:{sourceSystem}{entityType.Length}:{entityType}{legacyId.Length}:{legacyId}");
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA256.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
    private static byte[] ReadKey(string name)
    {
        var encoded = Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
        var key = Convert.FromBase64String(encoded);
        if (key.Length != 32) throw new InvalidOperationException($"{name} must decode to 32 bytes.");
        return key;
    }
    private static bool TryInstant(string? value, out DateTimeOffset instant) => DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
    private static bool IsCapability(string value) => value is "lock" or "sleep" or "hibernate" or "sign_out" or "restart" or "shutdown" or "reminders";
    private static bool IsPlatform(string? value) => value is "windows" or "android" or "ios" or "macos" or "linux" or "web" or "unknown";
    private static void Add(Dictionary<string, int> values, string key, int count = 1) => values[key] = values.GetValueOrDefault(key) + count;

    private sealed record Options(string SnapshotPath, string ManifestPath, string Mode, string SourceSystem, DateTimeOffset CompatSunset)
    {
        public static Options Parse(string[] args)
        {
            string Required(string name)
            {
                var index = Array.IndexOf(args, name);
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"{name} is required.");
                return args[index + 1];
            }
            string? Optional(string name)
            {
                var index = Array.IndexOf(args, name);
                return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
            }
            var mode = Optional("--mode") ?? "dry_run";
            if (mode is not ("dry_run" or "full" or "delta")) throw new ArgumentException("--mode must be dry_run, full, or delta.");
            if (!DateTimeOffset.TryParseExact(Required("--compat-sunset"), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sunset))
                throw new ArgumentException("--compat-sunset must be an ISO-8601 instant.");
            return new(Path.GetFullPath(Required("--snapshot")), Path.GetFullPath(Required("--manifest")), mode, Required("--source-system"), sunset);
        }
    }

    private sealed record LegacySnapshot(string SourceSnapshotId, string SourceSchemaChecksum, List<LegacyUser> Users, List<LegacyDevice> Devices, List<LegacyReminder> Reminders);
    private sealed record LegacyUser(string Id, string Username, string Email, string? DisplayName, string? DateOfBirth, bool MarketingOptIn, string? MarketingConsentAt, string? PasswordSha256, string? ApiKey, bool Enabled, string CreatedAt);
    private sealed record LegacyDevice(string Id, string UserId, string DisplayName, string? Platform, string? AgentVersion, string? Timezone, List<string>? Capabilities, string? LastSeenAt);
    private sealed record LegacyReminder(string Id, string UserId, string Text, string LocalStart, string? Timezone, string? RecurrenceRule, List<string>? TargetDeviceIds);
    private sealed record ValidatedUser(LegacyUser Source, string Username, string Email, DateTimeOffset CreatedAt, DateOnly? DateOfBirth, string Checksum);
    private sealed record ValidatedDevice(LegacyDevice Source, string NormalizedName, DateTimeOffset? LastSeenAt, string[] Capabilities, string Checksum);
    private sealed record ValidatedReminder(LegacyReminder Source, DateTime LocalStart, string Timezone, bool TimezoneAssumed, string Checksum);
    private sealed record Analysis(List<ValidatedUser> Users, List<ValidatedDevice> Devices, List<ValidatedReminder> Reminders, Dictionary<string, int> Quarantines, Dictionary<string, int> Collisions, ImportCounts Counts);
    private sealed record ImportCounts(int Users, int Devices, int Reminders);
}
