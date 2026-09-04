using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using PCConnect.Core.Domain;
using PCConnect.DbMigrator;
using PCConnect.Infrastructure.Security;
using PCConnect.LegacyMigrator;
using Shouldly;
using Testcontainers.MySql;

namespace PCConnect.IntegrationTests;

/// <summary>
/// The v1 to v2 import, run against a real MySQL holding the production-shaped
/// legacy schema and rows that look like the ones in the live database:
/// unsalted SHA-256 passwords, AES-256-CBC reminder text keyed by the API key,
/// naive wall-clock times, five recurrence columns, and rows that do not map
/// cleanly.
/// </summary>
[Collection(ApiCollection.Name)]
public class MigrationTests(ApiFixture fixture) : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder("mysql:8.4")
        .WithDatabase("pcconnect_v1")
        .WithUsername("pcconnect")
        .WithPassword("test-only-not-a-secret")
        .Build();

    private const string ApiKey = "0123456789abcdef0123456789abcdef";

    public async ValueTask InitializeAsync()
    {
        await _mysql.StartAsync();
        await SeedLegacyAsync();
        await ResetImportStateAsync();
    }

    /// <summary>
    /// Each test method gets its own legacy database but shares the PostgreSQL
    /// fixture, so the bridge state has to be cleared between them — otherwise
    /// the second test's import starts from the first one's high-water mark and
    /// silently does nothing.
    /// </summary>
    private async Task ResetImportStateAsync()
    {
        await using var target = new NpgsqlConnection(fixture.ConnectionString);
        await target.OpenAsync();

        // Deleting the imported users cascades to their devices and reminders.
        await target.ExecuteAsync("DELETE FROM users WHERE legacy_user_id IS NOT NULL");
        await target.ExecuteAsync("DELETE FROM migration_state");
        await target.ExecuteAsync("DELETE FROM migration_exceptions");
        await target.ExecuteAsync("DELETE FROM nav_items");
        await target.ExecuteAsync("DELETE FROM feedback");
        await target.ExecuteAsync("DELETE FROM mailing_list");
    }

    public async ValueTask DisposeAsync() => await _mysql.DisposeAsync();

    /// <summary>Reproduces the production-shaped v1 schema and a representative set of rows.</summary>
    private async Task SeedLegacyAsync()
    {
        await using var connection = new MySqlConnection(_mysql.GetConnectionString());
        await connection.OpenAsync();

        var baseline = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "db", "legacy", "legacy_baseline_mysql.sql"));

        // Comments are stripped before splitting on ';'. Both halves matter: a
        // chunk that merely starts with a comment still contains a statement,
        // and a trailing comment containing a semicolon would otherwise split a
        // CREATE TABLE in half.
        var sql = string.Join('\n', baseline
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Select(line =>
            {
                var comment = line.IndexOf("--", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            })
            .Where(line => line.Trim().Length > 0));

        foreach (var statement in sql.Split(';'))
        {
            var trimmed = statement.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                await connection.ExecuteAsync(trimmed);
            }
            catch (MySqlException ex)
            {
                throw new InvalidOperationException($"Legacy baseline statement failed: {trimmed}", ex);
            }
        }

        await connection.ExecuteAsync("""
            INSERT INTO users (id, Name, Username, DateOfBirth, Email, Password, Enabled, DateTimeOfSignup, MailingList, api_key)
            VALUES
              (1, 'Alice Example', 'alice', '1990-01-01', 'Alice@Example.com', @AliceHash, 1, '2023-04-01 09:15:00', 1, @ApiKey),
              (2, 'Bob Example',   'bob',   '1985-05-05', 'bob@example.com',   @BobHash,   1, '2024-08-12 18:00:00', 0, @ApiKey),
              -- disabled account, and an address v1 never validated
              (3, 'Carol Example', 'carol', '1979-09-09', 'not-an-email',      @BobHash,   0, 'not a date',          0, NULL)
            """,
            new
            {
                AliceHash = Normalise.Sha256Hex("alice-original-password"),
                BobHash = Normalise.Sha256Hex("bob-original-password"),
                ApiKey = ApiKey,
            });

        await connection.ExecuteAsync("""
            INSERT INTO pcnames (PCID, UserID, PCName, Request, Value, Time)
            VALUES
              (10, 1, 'Study-PC', 'Shutdown', 1, '2026-08-30 21:00:00'),
              (11, 1, 'Study-PC', NULL, 0, NULL),
              (12, 2, 'Work-Laptop', NULL, 0, '2026-09-01 08:00:00'),
              (13, 99, 'Orphaned-PC', NULL, 0, NULL)
            """);

        await connection.ExecuteAsync("""
            INSERT INTO reminders (ID, UserID, Time, Date, Reminder, Completed,
                                   Recurrence, Recurrence_Frequency, Recurrence_Day, Recurrence_End_Date)
            VALUES
              (100, 1, '09:00:00', '2026-12-01', @Milk,   0, 'none', NULL,     NULL,     NULL),
              (101, 1, '18:30:00', '2026-12-02', @Emoji,  1, 'Yes',  'weekly', 'Monday', '2027-01-01'),
              (102, 2, '07:15:00', '2026-12-03', @Dentist,0, 'Yes',  'fortnightly-ish', NULL, NULL),
              (103, 1, '12:00:00', '2026-12-04', 'not-encrypted-at-all', 0, 'none', NULL, NULL, NULL)
            """,
            new
            {
                Milk = EncryptLikeV1("Buy milk"),
                Emoji = EncryptLikeV1("Bins out 🗑️ and देवनागरी"),
                Dentist = EncryptLikeV1("Ring the dentist"),
            });

        await connection.ExecuteAsync("""
            INSERT INTO verifications (ID, TypeID, Code, Expiry, Current, UserID, IP)
            VALUES (1, 1, '123456', '2026-01-01 12:00:00', '2026-01-01 17:30:00', 2, '127.0.0.1')
            """);

        await connection.ExecuteAsync(
            "INSERT INTO menupages (ID, Name, URL, sort_order) VALUES (1, 'Download', '/download.html', 1)");
        await connection.ExecuteAsync(
            "INSERT INTO links (ID, Name, URL, sort_order) VALUES (1, 'Privacy', '/privacy.html', 1)");
        await connection.ExecuteAsync("""
            INSERT INTO feedback (FeedbackID, Name, Email, Feedback, Rating, IP)
            VALUES (1, 'Alice', 'alice@example.com', 'Works well', '5', '127.0.0.1'),
                   (2, 'Bob', 'bob@example.com', 'Could be faster', 'quite good', '127.0.0.1')
            """);
        await connection.ExecuteAsync("""
            INSERT INTO mailing_list (ID, UserID, Email) VALUES (1, 1, 'Alice@Example.com'), (2, 0, 'friend@example.com')
            """);
    }

    private static string EncryptLikeV1(string plaintext)
    {
        var key = new byte[32];
        Encoding.UTF8.GetBytes(ApiKey).CopyTo(key, 0);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);

        return $"{Convert.ToBase64String(aes.IV)}:{Convert.ToBase64String(cipher)}";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCConnect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private (LegacyImporter Importer, EnvelopeEncryptor Envelope) CreateImporter(bool dryRun = false)
    {
        var envelope = new EnvelopeEncryptor(Options.Create(new EnvelopeOptions
        {
            Keys = new Dictionary<string, string> { ["import"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
            CurrentKekId = "import",
        }));

        var importer = new LegacyImporter(
            new ImportOptions
            {
                LegacyConnectionString = _mysql.GetConnectionString(),
                TargetConnectionString = fixture.ConnectionString,
                DryRun = dryRun,
                DefaultTimezone = "Europe/London",
            },
            envelope,
            new Argon2PasswordHasher(Options.Create(new Argon2Options())),
            NullLogger<LegacyImporter>.Instance);

        return (importer, envelope);
    }

    [Fact]
    public async Task A_dry_run_reads_everything_and_writes_nothing()
    {
        await using var target = new NpgsqlConnection(fixture.ConnectionString);
        await target.OpenAsync();

        var before = await target.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE legacy_user_id IS NOT NULL");

        var (importer, _) = CreateImporter(dryRun: true);
        var report = await importer.RunAsync();

        report.Imported["users"].ShouldBe(3);
        report.Imported["devices"].ShouldBe(4);
        report.Imported["reminders"].ShouldBe(4);

        var after = await target.ExecuteScalarAsync<long>("SELECT count(*) FROM users WHERE legacy_user_id IS NOT NULL");
        after.ShouldBe(before);
    }

    [Fact]
    public async Task The_import_carries_users_devices_and_reminders_across()
    {
        var (importer, envelope) = CreateImporter();
        await importer.RunAsync();

        await using var target = new NpgsqlConnection(fixture.ConnectionString);
        await target.OpenAsync();

        // ── users ────────────────────────────────────────────────────────────
        var alice = await target.QuerySingleAsync<(long Id, string Email, string Username, string Status, string Timezone, bool Marketing)>("""
            SELECT id, email_normalised, username_normalised, status, timezone, is_marketing_opt_in
              FROM users WHERE legacy_user_id = 1
            """);

        alice.Email.ShouldBe("alice@example.com");   // lower-cased on the way in
        alice.Username.ShouldBe("alice");
        alice.Status.ShouldBe(UserStatuses.Active);
        alice.Marketing.ShouldBeTrue();

        var carol = await target.QuerySingleAsync<(string Status, string Email)>(
            "SELECT status, email_normalised FROM users WHERE legacy_user_id = 3");

        // `Enabled = 0` becomes suspended, and an unusable address is replaced
        // with a placeholder rather than dropping the account.
        carol.Status.ShouldBe(UserStatuses.Suspended);
        carol.Email.ShouldContain("invalid.pcconnect");

        // ── credentials ──────────────────────────────────────────────────────
        var credential = await target.QuerySingleAsync<(string Algo, string Hash, bool MustRehash)>("""
            SELECT algo, password_hash, must_rehash FROM user_credentials WHERE user_id = @Id
            """, new { alice.Id });

        // The v1 hash cannot be reversed, so it is carried across as-is and
        // upgraded on the next login with a real password (02 §6).
        credential.Algo.ShouldBe(PasswordAlgorithms.LegacySha256Unsalted);
        credential.Hash.ShouldBe(Normalise.Sha256Hex("alice-original-password"));
        credential.MustRehash.ShouldBeTrue();

        // ── the existing API key keeps installed clients signed in ───────────
        var compatTokens = await target.ExecuteScalarAsync<long>("""
            SELECT count(*) FROM refresh_tokens
             WHERE user_id = @Id AND client_kind = 'legacy' AND token_hash = @Hash
            """,
            new { alice.Id, Hash = SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey)) });

        compatTokens.ShouldBe(1);

        // ── devices ──────────────────────────────────────────────────────────
        var devices = (await target.QueryAsync<(string DisplayName, int LegacyPcid)>("""
            SELECT display_name, legacy_pcid FROM devices WHERE user_id = @Id ORDER BY legacy_pcid
            """, new { alice.Id })).ToList();

        devices.Count.ShouldBe(2);
        devices[0].DisplayName.ShouldBe("Study-PC");
        // v1 allowed two PCs with the same name under one account; v2's unique
        // index does not, so the duplicate is disambiguated rather than lost.
        devices[1].DisplayName.ShouldBe("Study-PC (11)");

        // In-flight commands are abandoned rather than migrated: any command
        // pending at cutover is stale by definition (02 §8, S2-03).
        var migratedCommands = await target.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM commands WHERE issued_by_user_id = @Id", new { alice.Id });
        migratedCommands.ShouldBe(0);

        // ── reminders: decrypted from CBC, re-encrypted under a DEK ──────────
        var reminder = await target.QuerySingleAsync<(byte[] Body, string Timezone, string? Rrule, bool Completed)>("""
            SELECT body_ciphertext, timezone, rrule, is_completed FROM reminders WHERE legacy_id = 100
            """);

        var wrapped = await target.QuerySingleAsync<(byte[] Wrapped, string KekId)>(
            "SELECT dek_wrapped, dek_kek_id FROM users WHERE id = @Id", new { alice.Id });

        var dek = envelope.UnwrapDataKey(wrapped.Wrapped, wrapped.KekId);
        envelope.Decrypt(dek, reminder.Body, $"pcconnect:reminder:{alice.Id}").ShouldBe("Buy milk");
        reminder.Timezone.ShouldBe("Europe/London");

        // utf8mb3 could not hold this text at all (S2-08).
        var emoji = await target.QuerySingleAsync<byte[]>(
            "SELECT body_ciphertext FROM reminders WHERE legacy_id = 101");
        envelope.Decrypt(dek, emoji, $"pcconnect:reminder:{alice.Id}").ShouldBe("Bins out 🗑️ and देवनागरी");

        // Five recurrence columns become one RRULE (S2-09).
        var recurring = await target.QuerySingleAsync<(string? Rrule, DateTimeOffset? Until, bool Completed)>(
            "SELECT rrule, recurrence_until, is_completed FROM reminders WHERE legacy_id = 101");
        recurring.Rrule.ShouldBe("FREQ=WEEKLY;BYDAY=MO");
        recurring.Until.ShouldNotBeNull();
        recurring.Completed.ShouldBeTrue();

        // ── website tables ───────────────────────────────────────────────────
        var nav = (await target.QueryAsync<(string Label, string Placement)>(
            "SELECT label, placement FROM nav_items ORDER BY placement")).ToList();
        nav.ShouldContain(n => n.Label == "Download" && n.Placement == "header");
        nav.ShouldContain(n => n.Label == "Privacy" && n.Placement == "footer");

        var ratings = (await target.QueryAsync<short?>("SELECT rating FROM feedback ORDER BY id")).ToList();
        ratings.ShouldContain((short)5);
        // "quite good" is not a rating; it becomes NULL rather than a guess.
        ratings.ShouldContain(r => !r.HasValue);

        var unsubscribeTokens = await target.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM mailing_list WHERE octet_length(unsubscribe_token) = 32");
        unsubscribeTokens.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task The_import_is_idempotent_and_resumable()
    {
        var (importer, _) = CreateImporter();

        await importer.RunAsync();

        await using var target = new NpgsqlConnection(fixture.ConnectionString);
        await target.OpenAsync();

        var afterFirst = await target.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM users WHERE legacy_user_id IS NOT NULL");

        // Rewind the high-water marks: this is what an interrupted import looks
        // like when it starts again.
        await target.ExecuteAsync("UPDATE migration_state SET last_legacy_id = 0");

        await importer.RunAsync();

        var afterSecond = await target.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM users WHERE legacy_user_id IS NOT NULL");

        afterSecond.ShouldBe(afterFirst);

        // Three of the four: the fourth will not decrypt and is recorded as an
        // exception instead, on both passes.
        var reminders = await target.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM reminders WHERE legacy_id IS NOT NULL");
        reminders.ShouldBe(3);
    }

    [Fact]
    public async Task Rows_that_cannot_be_mapped_are_recorded_rather_than_dropped()
    {
        var (importer, _) = CreateImporter();
        await importer.RunAsync();

        await using var target = new NpgsqlConnection(fixture.ConnectionString);
        await target.OpenAsync();

        var exceptions = (await target.QueryAsync<(string Entity, long? LegacyId, string Reason)>(
            "SELECT entity, legacy_id, reason FROM migration_exceptions ORDER BY entity, legacy_id")).ToList();

        // Nothing is silently lost: each of these is a row a human has to look
        // at before the contract migration can run.
        exceptions.ShouldContain(e => e.Entity == "users" && e.LegacyId == 3 && e.Reason == "invalid_email");
        exceptions.ShouldContain(e => e.Entity == "devices" && e.LegacyId == 13 && e.Reason == "orphaned_device");
        exceptions.ShouldContain(e => e.Entity == "devices" && e.LegacyId == 11 && e.Reason == "duplicate_name_disambiguated");
        exceptions.ShouldContain(e => e.Entity == "reminders" && e.LegacyId == 102 && e.Reason == "unparseable_recurrence");
        exceptions.ShouldContain(e => e.Entity == "reminders" && e.LegacyId == 103 && e.Reason == "decrypt_failed");
    }

    [Fact]
    public async Task A_timezone_is_recovered_from_the_only_evidence_v1_kept()
    {
        var (importer, _) = CreateImporter();
        await importer.RunAsync();

        await using var target = new NpgsqlConnection(fixture.ConnectionString);
        await target.OpenAsync();

        // `verifications.Current` recorded the user's local time against a UTC
        // expiry; the difference is their offset. Bob's row implies +5:30.
        var bob = await target.ExecuteScalarAsync<string>("SELECT timezone FROM users WHERE legacy_user_id = 2");
        bob.ShouldBe("Asia/Kolkata");

        // Alice has no verification row, so she gets the documented default
        // rather than a guess — better than firing her reminders at UK time
        // whatever her actual zone (S2-07).
        var alice = await target.ExecuteScalarAsync<string>("SELECT timezone FROM users WHERE legacy_user_id = 1");
        alice.ShouldBe("Europe/London");
    }

    [Fact]
    public async Task The_contract_migration_refuses_to_run_while_the_import_has_open_questions()
    {
        var (importer, _) = CreateImporter();
        await importer.RunAsync();

        // 0007 is the one-way door. Its guards refuse while any reminder is
        // unencrypted, any account is stranded on a legacy hash, or any import
        // exception is unresolved (07 Phase 4.9, ADR-0004).
        var migrator = new Migrator(fixture.ConnectionString, _ => { });

        var error = await Should.ThrowAsync<PostgresException>(async () =>
            await migrator.UpAsync(allowDestructive: true));

        error.MessageText.ShouldContain("REFUSING to contract");
    }
}
