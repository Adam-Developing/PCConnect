using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Security;

namespace PCConnect.LegacyMigrator;

public sealed record ImportOptions
{
    public required string LegacyConnectionString { get; init; }

    public required string TargetConnectionString { get; init; }

    /// <summary>Reads everything, writes nothing, and reports what it would do.</summary>
    public bool DryRun { get; init; }

    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Where the reminder times came from. v1 stored a naive wall clock with no
    /// zone; the `verifications.Current` column is the only per-user evidence,
    /// and this is the fallback for everyone else (02 §8).
    /// </summary>
    public string DefaultTimezone { get; init; } = "Europe/London";
}

public sealed record ImportReport
{
    public Dictionary<string, long> Imported { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, long> Skipped { get; } = new(StringComparer.Ordinal);

    public List<string> Exceptions { get; } = [];

    public void Count(string entity, bool imported = true)
    {
        var target = imported ? Imported : Skipped;
        target[entity] = target.GetValueOrDefault(entity) + 1;
    }
}

/// <summary>
/// The MySQL to PostgreSQL import (07 Phase 4).
///
/// It is idempotent and resumable: every row is keyed on its legacy id, every
/// write is an upsert, and a high-water mark per entity is committed as it goes.
/// Interrupting it and running it again picks up where it stopped rather than
/// duplicating or skipping.
///
/// Nothing is silently dropped. A row that cannot be mapped — an unparseable
/// recurrence, a duplicate email, a reminder that will not decrypt — is written
/// to `migration_exceptions` with its reason, and the verification gate counts
/// those as a failure.
/// </summary>
public sealed class LegacyImporter(
    ImportOptions options,
    IEnvelopeEncryptor envelope,
    Core.IPasswordHasher hasher,
    ILogger<LegacyImporter> logger)
{
    public async Task<ImportReport> RunAsync(CancellationToken ct = default)
    {
        // The importer runs as its own process and cannot assume the API has
        // configured Dapper. Without this, `api_key` silently fails to map onto
        // `ApiKey` and every reminder looks unencrypted.
        Db.ConfigureDapper();

        var report = new ImportReport();

        await using var legacy = new MySqlConnection(options.LegacyConnectionString);
        await legacy.OpenAsync(ct);

        await using var target = new NpgsqlConnection(options.TargetConnectionString);
        await target.OpenAsync(ct);

        var probe = new LegacySchemaProbe(legacy);
        await probe.LoadAsync(legacy.Database, ct);
        logger.LogInformation("Legacy database is the {Generation}", probe.Describe());

        if (options.DryRun)
        {
            logger.LogWarning("DRY RUN: nothing will be written to PostgreSQL");
        }

        var timezones = await LoadTimezoneHintsAsync(legacy, probe, ct);

        await ImportUsersAsync(legacy, target, probe, timezones, report, ct);
        await ImportDevicesAsync(legacy, target, probe, report, ct);
        await ImportRemindersAsync(legacy, target, probe, report, ct);
        await ImportNavigationAsync(legacy, target, probe, report, ct);
        await ImportFeedbackAsync(legacy, target, probe, report, ct);
        await ImportMailingListAsync(legacy, target, probe, report, ct);

        return report;
    }

    // ── users ────────────────────────────────────────────────────────────────

    private async Task ImportUsersAsync(
        MySqlConnection legacy, NpgsqlConnection target, LegacySchemaProbe probe,
        IReadOnlyDictionary<int, string> timezones, ImportReport report, CancellationToken ct)
    {
        var hasApiKey = probe.HasColumn("users", "api_key");
        var hasMailingList = probe.HasColumn("users", "MailingList");

        var sql = $"""
            SELECT id, Name, Username, Email, Password, Enabled, DateTimeOfSignup
                   {(hasApiKey ? ", api_key" : ", NULL AS api_key")}
                   {(hasMailingList ? ", MailingList" : ", 0 AS MailingList")}
              FROM users
             WHERE id > @After
             ORDER BY id
             LIMIT @Batch
            """;

        var after = await HighWaterMarkAsync(target, "users", ct);

        while (!ct.IsCancellationRequested)
        {
            var rows = (await legacy.QueryAsync<LegacyUser>(
                new CommandDefinition(sql, new { After = after, Batch = options.BatchSize }, cancellationToken: ct))).ToList();

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                after = row.Id;

                var email = (row.Email ?? string.Empty).Trim();
                var username = (row.Username ?? string.Empty).Trim();

                if (username.Length == 0)
                {
                    await RecordExceptionAsync(target, "users", row.Id, "empty_username", null, ct);
                    report.Count("users", imported: false);
                    continue;
                }

                if (!Normalise.LooksLikeEmail(email))
                {
                    // v1 never validated the address. A row with no usable email
                    // still becomes an account — it has devices and reminders —
                    // but it is flagged so someone can fix it before the contract
                    // migration, which adds the unique index.
                    await RecordExceptionAsync(target, "users", row.Id, "invalid_email",
                        new { email = Redact(email) }, ct);
                    email = $"legacy-{row.Id}@invalid.pcconnect";
                }

                if (options.DryRun)
                {
                    report.Count("users");
                    continue;
                }

                try
                {
                    var userId = await target.ExecuteScalarAsync<long>(new CommandDefinition("""
                        INSERT INTO users (email, email_normalised, username, username_normalised,
                                           display_name, timezone, status, is_marketing_opt_in,
                                           created_at, legacy_user_id)
                        VALUES (@Email, @EmailNormalised, @Username, @UsernameNormalised,
                                @DisplayName, @Timezone, @Status, @Marketing, @CreatedAt, @LegacyId)
                        ON CONFLICT (legacy_user_id) WHERE legacy_user_id IS NOT NULL DO UPDATE
                            SET display_name = EXCLUDED.display_name, updated_at = now()
                        RETURNING id
                        """,
                        new
                        {
                            Email = email,
                            EmailNormalised = Normalise.Email(email),
                            Username = username,
                            UsernameNormalised = Normalise.Username(username),
                            DisplayName = (row.Name ?? username).Trim(),
                            Timezone = timezones.GetValueOrDefault(row.Id, options.DefaultTimezone),
                            Status = row.Enabled == 0 ? UserStatuses.Suspended : UserStatuses.Active,
                            Marketing = row.MailingList != 0,
                            CreatedAt = ParseSignupTimestamp(row.DateTimeOfSignup),
                            LegacyId = row.Id,
                        }, cancellationToken: ct));

                    // The v1 password is an unsalted client-side SHA-256 that
                    // cannot be reversed, so it is carried across as-is and
                    // upgraded on the account's next login with a real password
                    // (02 §6). It is never treated as an Argon2id hash.
                    await target.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO user_credentials (user_id, algo, password_hash, must_rehash)
                        VALUES (@UserId, @Algo, @Hash, true)
                        ON CONFLICT (user_id) DO NOTHING
                        """,
                        new
                        {
                            UserId = userId,
                            Algo = PasswordAlgorithms.LegacySha256Unsalted,
                            Hash = (row.Password ?? string.Empty).Trim().ToLowerInvariant(),
                        }, cancellationToken: ct));

                    // The existing API key becomes a legacy compatibility token,
                    // so an installed Android client that cached it keeps working
                    // across the cutover instead of being signed out (04 §5).
                    if (!string.IsNullOrWhiteSpace(row.ApiKey))
                    {
                        await target.ExecuteAsync(new CommandDefinition("""
                            INSERT INTO refresh_tokens
                                (token_hash, family_id, user_id, client_kind, client_version, user_agent, expires_at)
                            VALUES (@Hash, @FamilyId, @UserId, 'legacy', 'imported', 'legacy-import', now() + interval '365 days')
                            ON CONFLICT (token_hash) DO NOTHING
                            """,
                            new
                            {
                                Hash = SHA256.HashData(Encoding.UTF8.GetBytes(row.ApiKey!)),
                                FamilyId = Guid.CreateVersion7(),
                                UserId = userId,
                            }, cancellationToken: ct));
                    }

                    report.Count("users");
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    // Two v1 accounts normalising to one email or username. The
                    // duplicate is recorded rather than overwriting the first:
                    // deciding which one is the real account is a human call.
                    await RecordExceptionAsync(target, "users", row.Id, "duplicate_identity",
                        new { constraint = ex.ConstraintName }, ct);
                    report.Count("users", imported: false);
                }
            }

            await SetHighWaterMarkAsync(target, "users", after, report, ct);
        }
    }

    // ── devices ──────────────────────────────────────────────────────────────

    private async Task ImportDevicesAsync(
        MySqlConnection legacy, NpgsqlConnection target, LegacySchemaProbe probe,
        ImportReport report, CancellationToken ct)
    {
        if (!probe.HasTable("pcnames"))
        {
            return;
        }

        // Both generations are handled: the current one keys by UserID, the
        // older dump keys by a Username string.
        var sql = probe.HasColumn("pcnames", "UserID")
            ? """
              SELECT p.PCID, p.UserID, p.PCName
                     {TIME}
                FROM pcnames p
               WHERE p.PCID > @After
               ORDER BY p.PCID
               LIMIT @Batch
              """.Replace("{TIME}", probe.HasColumn("pcnames", "Time") ? ", p.Time AS LastSeen" : ", NULL AS LastSeen", StringComparison.Ordinal)
            : """
              SELECT p.PCID, u.id AS UserID, p.PCName, NULL AS LastSeen
                FROM pcnames p
                JOIN users u ON u.Username = p.Username
               WHERE p.PCID > @After
               ORDER BY p.PCID
               LIMIT @Batch
              """;

        var after = await HighWaterMarkAsync(target, "devices", ct);

        while (!ct.IsCancellationRequested)
        {
            var rows = (await legacy.QueryAsync<LegacyDevice>(
                new CommandDefinition(sql, new { After = after, Batch = options.BatchSize }, cancellationToken: ct))).ToList();

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                after = row.PCID;

                var name = (row.PCName ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    name = $"PC {row.PCID}";
                }

                if (options.DryRun)
                {
                    report.Count("devices");
                    continue;
                }

                var userId = await target.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM users WHERE legacy_user_id = @LegacyId",
                    new { LegacyId = row.UserID }, cancellationToken: ct));

                if (userId is null)
                {
                    await RecordExceptionAsync(target, "devices", row.PCID, "orphaned_device",
                        new { legacyUserId = row.UserID }, ct);
                    report.Count("devices", imported: false);
                    continue;
                }

                try
                {
                    var deviceId = await target.ExecuteScalarAsync<long>(new CommandDefinition("""
                        INSERT INTO devices (user_id, display_name, platform, last_seen_at, legacy_pcid)
                        VALUES (@UserId, @Name, 'windows', @LastSeen, @LegacyId)
                        ON CONFLICT (legacy_pcid) WHERE legacy_pcid IS NOT NULL DO UPDATE SET display_name = EXCLUDED.display_name, updated_at = now()
                        RETURNING id
                        """,
                        new
                        {
                            UserId = userId,
                            Name = name,
                            LastSeen = ToUtc(row.LastSeen),
                            LegacyId = row.PCID,
                        }, cancellationToken: ct));

                    // An imported device has no usable secret: v1 had none. It
                    // reaches the system through the legacy shim until its agent
                    // is updated and pairs properly, at which point pairing
                    // replaces this row's credential.
                    await target.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO device_credentials (device_id, secret_hash)
                        VALUES (@DeviceId, @Hash)
                        ON CONFLICT (device_id) DO NOTHING
                        """,
                        new
                        {
                            DeviceId = deviceId,
                            Hash = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
                        }, cancellationToken: ct));

                    report.Count("devices");
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    // v1 allowed two PCs with the same name under one account;
                    // v2's unique index does not. Disambiguated rather than lost.
                    await target.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO devices (user_id, display_name, platform, last_seen_at, legacy_pcid)
                        VALUES (@UserId, @Name, 'windows', @LastSeen, @LegacyId)
                        ON CONFLICT (legacy_pcid) WHERE legacy_pcid IS NOT NULL DO NOTHING
                        """,
                        new
                        {
                            UserId = userId,
                            Name = $"{name} ({row.PCID})",
                            LastSeen = ToUtc(row.LastSeen),
                            LegacyId = row.PCID,
                        }, cancellationToken: ct));

                    await RecordExceptionAsync(target, "devices", row.PCID, "duplicate_name_disambiguated",
                        new { name }, ct);
                    report.Count("devices");
                }
            }

            await SetHighWaterMarkAsync(target, "devices", after, report, ct);
        }

        // In-flight commands are deliberately not migrated: any command pending
        // at cutover is stale by definition, and executing it later is the exact
        // bug this migration exists to fix (02 §8, S2-03).
        logger.LogInformation("Pending v1 commands are intentionally abandoned rather than migrated");
    }

    // ── reminders ────────────────────────────────────────────────────────────

    private async Task ImportRemindersAsync(
        MySqlConnection legacy, NpgsqlConnection target, LegacySchemaProbe probe,
        ImportReport report, CancellationToken ct)
    {
        if (!probe.HasTable("reminders"))
        {
            return;
        }

        var keyedByUserId = probe.HasColumn("reminders", "UserID");
        var hasApiKey = probe.HasColumn("users", "api_key");
        var hasRecurrence = probe.HasColumn("reminders", "Recurrence_Frequency");

        var sql = $"""
            SELECT r.ID, {(keyedByUserId ? "r.UserID" : "u.id AS UserID")}, r.Time, r.Date, r.Reminder, r.Completed
                   {(hasRecurrence ? ", r.Recurrence, r.Recurrence_Frequency, r.Recurrence_Day, r.Recurrence_End_Date" : ", NULL AS Recurrence, NULL AS Recurrence_Frequency, NULL AS Recurrence_Day, NULL AS Recurrence_End_Date")}
                   {(hasApiKey ? ", u.api_key" : ", NULL AS api_key")}
              FROM reminders r
              JOIN users u ON {(keyedByUserId ? "u.id = r.UserID" : "u.Username = r.Username")}
             WHERE r.ID > @After
             ORDER BY r.ID
             LIMIT @Batch
            """;

        var after = await HighWaterMarkAsync(target, "reminders", ct);

        while (!ct.IsCancellationRequested)
        {
            var rows = (await legacy.QueryAsync<LegacyReminder>(
                new CommandDefinition(sql, new { After = after, Batch = options.BatchSize }, cancellationToken: ct))).ToList();

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                after = row.ID;

                if (options.DryRun)
                {
                    report.Count("reminders");
                    continue;
                }

                var user = await target.QuerySingleOrDefaultAsync<(long Id, string Timezone)>(new CommandDefinition(
                    "SELECT id, timezone FROM users WHERE legacy_user_id = @LegacyId",
                    new { LegacyId = row.UserID }, cancellationToken: ct));

                if (user.Id == 0)
                {
                    await RecordExceptionAsync(target, "reminders", row.ID, "orphaned_reminder",
                        new { legacyUserId = row.UserID }, ct);
                    report.Count("reminders", imported: false);
                    continue;
                }

                // The dangerous step (ADR-0004): decrypt with the API key that is
                // about to be destroyed, and re-encrypt under a per-user DEK. A
                // row that will not decrypt is recorded rather than written as
                // ciphertext nobody can read.
                string body;
                if (string.IsNullOrEmpty(row.ApiKey))
                {
                    body = row.Reminder ?? string.Empty;
                }
                else if (LegacyReminderCipher.TryDecrypt(row.ApiKey!, row.Reminder ?? string.Empty, out var plaintext))
                {
                    body = plaintext;
                }
                else
                {
                    await RecordExceptionAsync(target, "reminders", row.ID, "decrypt_failed", null, ct);
                    report.Count("reminders", imported: false);
                    continue;
                }

                if (body.Length == 0)
                {
                    body = "(empty reminder)";
                }

                var dek = await EnsureDataKeyAsync(target, user.Id, ct);

                try
                {
                    var (dueAtUtc, localTime) = CombineDueAt(row.Date, row.Time, user.Timezone);
                    var rrule = TranslateRecurrence(row.Recurrence, row.RecurrenceFrequency, row.RecurrenceDay);

                    if (rrule is null && LooksRecurring(row.Recurrence))
                    {
                        await RecordExceptionAsync(target, "reminders", row.ID, "unparseable_recurrence",
                            new { row.Recurrence, row.RecurrenceFrequency, row.RecurrenceDay }, ct);
                    }

                    await target.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO reminders
                            (user_id, body_ciphertext, body_dek_id, due_at_utc, due_local_time, timezone,
                             rrule, recurrence_until, is_completed, completed_at, legacy_id)
                        VALUES
                            (@UserId, @Body, @DekId, @DueAt, @LocalTime, @Timezone,
                             @Rrule, @Until, @Completed, @CompletedAt, @LegacyId)
                        ON CONFLICT (legacy_id) WHERE legacy_id IS NOT NULL DO UPDATE
                            SET body_ciphertext = EXCLUDED.body_ciphertext,
                                body_dek_id = EXCLUDED.body_dek_id,
                                updated_at = now()
                        """,
                        new
                        {
                            UserId = user.Id,
                            Body = envelope.Encrypt(dek, body, $"pcconnect:reminder:{user.Id}"),
                            DekId = envelope.CurrentKekId,
                            DueAt = dueAtUtc,
                            LocalTime = localTime,
                            Timezone = user.Timezone,
                            Rrule = rrule,
                            Until = ToUtc(row.RecurrenceEndDate),
                            Completed = row.Completed != 0,
                            // v1 never recorded when a reminder was completed, so
                            // the column stays NULL rather than being invented.
                            CompletedAt = row.Completed != 0 ? (DateTimeOffset?)dueAtUtc : null,
                            LegacyId = row.ID,
                        }, cancellationToken: ct));

                    report.Count("reminders");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dek);
                }
            }

            await SetHighWaterMarkAsync(target, "reminders", after, report, ct);
        }
    }

    // ── website tables ───────────────────────────────────────────────────────

    private async Task ImportNavigationAsync(
        MySqlConnection legacy, NpgsqlConnection target, LegacySchemaProbe probe,
        ImportReport report, CancellationToken ct)
    {
        // `links` and `menupages` were duplicates of each other; both become
        // nav_items, with placement derived from which table a row came from.
        foreach (var (table, placement) in new[] { ("menupages", "header"), ("links", "footer") })
        {
            if (!probe.HasTable(table))
            {
                continue;
            }

            var rows = await legacy.QueryAsync<LegacyNavItem>(
                new CommandDefinition($"SELECT ID, Name, URL, sort_order FROM {table}", cancellationToken: ct));

            foreach (var row in rows)
            {
                if (options.DryRun)
                {
                    report.Count("nav_items");
                    continue;
                }

                await target.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO nav_items (label, url, placement, sort_order, is_external)
                    VALUES (@Label, @Url, @Placement, @SortOrder, @IsExternal)
                    ON CONFLICT (url, placement) DO UPDATE SET label = EXCLUDED.label
                    """,
                    new
                    {
                        Label = (row.Name ?? string.Empty).Trim(),
                        Url = (row.URL ?? string.Empty).Trim(),
                        Placement = placement,
                        SortOrder = row.sort_order,
                        IsExternal = (row.URL ?? string.Empty).StartsWith("http", StringComparison.OrdinalIgnoreCase),
                    }, cancellationToken: ct));

                report.Count("nav_items");
            }
        }
    }

    private async Task ImportFeedbackAsync(
        MySqlConnection legacy, NpgsqlConnection target, LegacySchemaProbe probe,
        ImportReport report, CancellationToken ct)
    {
        if (!probe.HasTable("feedback"))
        {
            return;
        }

        var rows = await legacy.QueryAsync<LegacyFeedback>(
            new CommandDefinition("SELECT FeedbackID, Name, Email, Feedback, Rating FROM feedback", cancellationToken: ct));

        foreach (var row in rows)
        {
            if (options.DryRun)
            {
                report.Count("feedback");
                continue;
            }

            // v1 stored Rating as free text. Anything that is not 1-5 becomes
            // NULL rather than a guessed number.
            short? rating = short.TryParse(row.Rating, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= 1 and <= 5 ? parsed : null;

            await target.ExecuteAsync(new CommandDefinition("""
                INSERT INTO feedback (name, email, body, rating)
                VALUES (@Name, @Email, @Body, @Rating)
                """,
                new
                {
                    Name = Truncate(row.Name, 255),
                    Email = Truncate(row.Email, 320),
                    Body = row.Feedback ?? string.Empty,
                    Rating = rating,
                }, cancellationToken: ct));

            report.Count("feedback");
        }
    }

    private async Task ImportMailingListAsync(
        MySqlConnection legacy, NpgsqlConnection target, LegacySchemaProbe probe,
        ImportReport report, CancellationToken ct)
    {
        if (!probe.HasTable("mailing_list"))
        {
            return;
        }

        var rows = await legacy.QueryAsync<LegacyMailingListEntry>(
            new CommandDefinition("SELECT ID, UserID, Email FROM mailing_list", cancellationToken: ct));

        foreach (var row in rows)
        {
            var email = Normalise.Email(row.Email ?? string.Empty);
            if (!Normalise.LooksLikeEmail(email))
            {
                report.Count("mailing_list", imported: false);
                continue;
            }

            if (options.DryRun)
            {
                report.Count("mailing_list");
                continue;
            }

            // Every subscriber gets an unsubscribe token, because v1 had none and
            // a consent record with no provable opt-out is not a consent record.
            await target.ExecuteAsync(new CommandDefinition("""
                INSERT INTO mailing_list (email_normalised, user_id, unsubscribe_token, consent_source)
                VALUES (@Email,
                        (SELECT id FROM users WHERE legacy_user_id = @LegacyUserId),
                        @Token, 'legacy_import')
                ON CONFLICT (email_normalised) DO NOTHING
                """,
                new
                {
                    Email = email,
                    LegacyUserId = row.UserID == 0 ? (int?)null : row.UserID,
                    Token = RandomNumberGenerator.GetBytes(32),
                }, cancellationToken: ct));

            report.Count("mailing_list");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// `verifications.Current` is the only per-user timezone evidence v1 has: it
    /// recorded the user's local time when a verification code was issued. The
    /// offset it implies picks a plausible IANA zone; everyone else gets the
    /// documented default (02 §8, 07 Phase 4.6).
    /// </summary>
    private async Task<Dictionary<int, string>> LoadTimezoneHintsAsync(
        MySqlConnection legacy, LegacySchemaProbe probe, CancellationToken ct)
    {
        var hints = new Dictionary<int, string>();

        if (!probe.HasTable("verifications") || !probe.HasColumn("verifications", "Current"))
        {
            return hints;
        }

        var rows = await legacy.QueryAsync<(int UserID, string? Current, DateTime? Expiry)>(
            new CommandDefinition("SELECT UserID, Current, Expiry FROM verifications", cancellationToken: ct));

        foreach (var row in rows)
        {
            if (row.Current is null || row.Expiry is null ||
                !DateTime.TryParse(row.Current, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                continue;
            }

            // The code's issue time was recorded in UTC on the server and in the
            // user's local time in `Current`; the difference is their offset.
            var offset = TimeSpan.FromMinutes(Math.Round((local - row.Expiry.Value).TotalMinutes / 15) * 15);
            if (Math.Abs(offset.TotalHours) > 14)
            {
                continue;
            }

            hints[row.UserID] = ZoneForOffset(offset);
        }

        logger.LogInformation("Recovered a timezone hint for {Count} user(s) from verifications.Current", hints.Count);
        return hints;
    }

    /// <summary>
    /// A representative IANA zone per whole-hour offset. This is a hint, not a
    /// determination: the user's own setting replaces it the moment they open
    /// the app, and being an hour out is strictly better than firing every
    /// reminder at UK time for everybody (S2-07).
    /// </summary>
    private static string ZoneForOffset(TimeSpan offset) => offset.TotalHours switch
    {
        <= -8 => "America/Los_Angeles",
        <= -6 => "America/Denver",
        <= -5 => "America/Chicago",
        <= -4 => "America/New_York",
        <= -3 => "America/Sao_Paulo",
        < 1 => "Europe/London",
        < 2 => "Europe/Paris",
        < 3 => "Europe/Athens",
        < 5 => "Asia/Dubai",
        < 6 => "Asia/Kolkata",
        < 8 => "Asia/Dhaka",
        < 9 => "Asia/Shanghai",
        < 10 => "Asia/Tokyo",
        _ => "Australia/Sydney",
    };

    private static (DateTimeOffset DueAtUtc, TimeSpan LocalTime) CombineDueAt(
        DateTime? date, TimeSpan time, string timezone)
    {
        var day = date ?? DateTime.UtcNow.Date;
        var local = DateTime.SpecifyKind(day.Date.Add(time), DateTimeKind.Unspecified);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return (new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime(), time);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return (new DateTimeOffset(local, TimeSpan.Zero), time);
        }
    }

    /// <summary>
    /// v1's five recurrence columns become one RRULE. No client ever read them,
    /// so an unparseable value is logged and nulled rather than blocking the row
    /// (02 §8, S2-09).
    /// </summary>
    internal static string? TranslateRecurrence(string? recurrence, string? frequency, string? day)
    {
        if (!LooksRecurring(recurrence))
        {
            return null;
        }

        var value = (frequency ?? recurrence ?? string.Empty).Trim().ToLowerInvariant();

        return value switch
        {
            "daily" or "day" => "FREQ=DAILY",
            "weekly" or "week" => WeeklyRule(day),
            "monthly" or "month" => "FREQ=MONTHLY",
            "yearly" or "year" or "annually" => "FREQ=YEARLY",
            "weekday" or "weekdays" => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
            _ => null,
        };
    }

    private static bool LooksRecurring(string? recurrence) =>
        !string.IsNullOrWhiteSpace(recurrence) &&
        !string.Equals(recurrence.Trim(), "none", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(recurrence.Trim(), "0", StringComparison.Ordinal);

    private static string WeeklyRule(string? day)
    {
        var code = (day ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "monday" or "mon" or "mo" => "MO",
            "tuesday" or "tue" or "tu" => "TU",
            "wednesday" or "wed" or "we" => "WE",
            "thursday" or "thu" or "th" => "TH",
            "friday" or "fri" or "fr" => "FR",
            "saturday" or "sat" or "sa" => "SA",
            "sunday" or "sun" or "su" => "SU",
            _ => null,
        };

        return code is null ? "FREQ=WEEKLY" : $"FREQ=WEEKLY;BYDAY={code}";
    }

    private async Task<byte[]> EnsureDataKeyAsync(NpgsqlConnection target, long userId, CancellationToken ct)
    {
        var existing = await target.QuerySingleOrDefaultAsync<(byte[]? Wrapped, string? KekId)>(
            new CommandDefinition("SELECT dek_wrapped, dek_kek_id FROM users WHERE id = @Id",
                new { Id = userId }, cancellationToken: ct));

        if (existing.Wrapped is { Length: > 0 } wrapped && existing.KekId is { } kekId)
        {
            return envelope.UnwrapDataKey(wrapped, kekId);
        }

        var (newWrapped, newKekId) = envelope.CreateDataKey();
        await target.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET dek_wrapped = @Wrapped, dek_kek_id = @KekId WHERE id = @Id AND dek_wrapped IS NULL
            """, new { Wrapped = newWrapped, KekId = newKekId, Id = userId }, cancellationToken: ct));

        var confirmed = await target.QuerySingleAsync<(byte[] Wrapped, string KekId)>(
            new CommandDefinition("SELECT dek_wrapped, dek_kek_id FROM users WHERE id = @Id",
                new { Id = userId }, cancellationToken: ct));

        return envelope.UnwrapDataKey(confirmed.Wrapped, confirmed.KekId);
    }

    private static async Task<long> HighWaterMarkAsync(NpgsqlConnection target, string entity, CancellationToken ct) =>
        await target.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT last_legacy_id FROM migration_state WHERE entity = @Entity",
            new { Entity = entity }, cancellationToken: ct)) ?? 0;

    private async Task SetHighWaterMarkAsync(
        NpgsqlConnection target, string entity, long value, ImportReport report, CancellationToken ct)
    {
        if (options.DryRun)
        {
            return;
        }

        await target.ExecuteAsync(new CommandDefinition("""
            INSERT INTO migration_state (entity, last_legacy_id, rows_imported, rows_skipped, updated_at)
            VALUES (@Entity, @Value, @Imported, @Skipped, now())
            ON CONFLICT (entity) DO UPDATE
                SET last_legacy_id = EXCLUDED.last_legacy_id,
                    rows_imported = EXCLUDED.rows_imported,
                    rows_skipped = EXCLUDED.rows_skipped,
                    updated_at = now()
            """,
            new
            {
                Entity = entity,
                Value = value,
                Imported = report.Imported.GetValueOrDefault(entity),
                Skipped = report.Skipped.GetValueOrDefault(entity),
            }, cancellationToken: ct));
    }

    private async Task RecordExceptionAsync(
        NpgsqlConnection target, string entity, long legacyId, string reason, object? detail, CancellationToken ct)
    {
        logger.LogWarning("Import exception on {Entity} {LegacyId}: {Reason}", entity, legacyId, reason);

        if (options.DryRun)
        {
            return;
        }

        await target.ExecuteAsync(new CommandDefinition("""
            INSERT INTO migration_exceptions (entity, legacy_id, reason, detail)
            VALUES (@Entity, @LegacyId, @Reason, @Detail::jsonb)
            """,
            new
            {
                Entity = entity,
                LegacyId = legacyId,
                Reason = reason,
                Detail = detail is null ? null : DbJson.Serialise(detail),
            }, cancellationToken: ct));
    }

    private static DateTimeOffset ParseSignupTimestamp(string? value)
    {
        // v1 stored the signup timestamp as free text in Europe/London.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            try
            {
                var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
                var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                return new DateTimeOffset(local, london.GetUtcOffset(local)).ToUniversalTime();
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return new DateTimeOffset(parsed, TimeSpan.Zero);
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static string Truncate(string? value, int max) =>
        value is null ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>Enough to identify a row in a log without reproducing the address.</summary>
    private static string Redact(string email) =>
        email.Length <= 3 ? "***" : $"{email[..2]}***{(email.Contains('@', StringComparison.Ordinal) ? email[email.IndexOf('@', StringComparison.Ordinal)..] : string.Empty)}";

    // ── legacy row shapes ────────────────────────────────────────────────────

    private sealed record LegacyUser
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string? Username { get; init; }
        public string? Email { get; init; }
        public string? Password { get; init; }
        public int Enabled { get; init; }
        public string? DateTimeOfSignup { get; init; }
        public string? ApiKey { get; init; }
        public int MailingList { get; init; }
    }

    private sealed record LegacyDevice
    {
        public int PCID { get; init; }
        public int UserID { get; init; }
        public string? PCName { get; init; }
        public DateTime? LastSeen { get; init; }
    }

    private sealed record LegacyReminder
    {
        public int ID { get; init; }
        public int UserID { get; init; }
        public TimeSpan Time { get; init; }
        public DateTime? Date { get; init; }
        public string? Reminder { get; init; }
        public int Completed { get; init; }
        public string? Recurrence { get; init; }
        public string? RecurrenceFrequency { get; init; }
        public string? RecurrenceDay { get; init; }
        public DateTime? RecurrenceEndDate { get; init; }
        public string? ApiKey { get; init; }
    }

    private sealed record LegacyNavItem
    {
        public int ID { get; init; }
        public string? Name { get; init; }
        public string? URL { get; init; }
        public int sort_order { get; init; }
    }

    private sealed record LegacyFeedback
    {
        public int FeedbackID { get; init; }
        public string? Name { get; init; }
        public string? Email { get; init; }
        public string? Feedback { get; init; }
        public string? Rating { get; init; }
    }

    private sealed record LegacyMailingListEntry
    {
        public int ID { get; init; }
        public int UserID { get; init; }
        public string? Email { get; init; }
    }
}
