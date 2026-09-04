using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PCConnect.DbMigrator;
using PCConnect.Infrastructure.Security;
using PCConnect.LegacyMigrator;

// pcconnect-import — the v1 (MySQL) to v2 (PostgreSQL) import.
//
//   pcconnect-import dry-run     read everything, write nothing, report
//   pcconnect-import import      idempotent, resumable import
//   pcconnect-import verify      run the verification gates
//   pcconnect-import status      show the high-water marks
//
// Connection strings and the KEK come from the environment. They are never
// arguments: arguments end up in shell history and process listings.
//
//   PCCONNECT_LEGACY__CONNECTIONSTRING   MySQL, read-only credentials
//   PCCONNECT_DATABASE__CONNECTIONSTRING PostgreSQL
//   PCCONNECT_KEK__KEYS__<id>            base64 32-byte key encryption key
//   PCCONNECT_KEK__CURRENTKEKID          which key to wrap new data keys with

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => o.SingleLine = true));

var logger = loggerFactory.CreateLogger("import");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

var legacyConnection = Environment.GetEnvironmentVariable("PCCONNECT_LEGACY__CONNECTIONSTRING");
var targetConnection = Environment.GetEnvironmentVariable("PCCONNECT_DATABASE__CONNECTIONSTRING")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL");

if (string.IsNullOrWhiteSpace(targetConnection))
{
    Console.Error.WriteLine("Set PCCONNECT_DATABASE__CONNECTIONSTRING to the PostgreSQL target.");
    return 2;
}

if (command is "verify")
{
    var failures = await new Migrator(targetConnection, m => logger.LogInformation("{Message}", m)).VerifyAsync();

    if (failures.Count == 0)
    {
        Console.WriteLine("All verification gates returned zero.");
        return 0;
    }

    foreach (var (check, violations) in failures)
    {
        Console.Error.WriteLine($"FAIL {check.Id}: {check.Description} -> {violations} violation(s)");
    }

    return 1;
}

if (command is "status")
{
    await using var status = new Npgsql.NpgsqlConnection(targetConnection);
    await status.OpenAsync();

    var rows = await Dapper.SqlMapper.QueryAsync<(string Entity, long LastLegacyId, long RowsImported, long RowsSkipped)>(
        status, "SELECT entity, last_legacy_id, rows_imported, rows_skipped FROM migration_state ORDER BY entity");

    foreach (var row in rows)
    {
        Console.WriteLine($"{row.Entity,-14} high-water {row.LastLegacyId,-8} imported {row.RowsImported,-8} skipped {row.RowsSkipped}");
    }

    var exceptions = await Dapper.SqlMapper.QueryAsync<(string Entity, string Reason, long Count)>(
        status, "SELECT entity, reason, count(*) FROM migration_exceptions GROUP BY entity, reason ORDER BY count(*) DESC");

    foreach (var row in exceptions)
    {
        Console.WriteLine($"EXCEPTION {row.Entity}/{row.Reason}: {row.Count}");
    }

    return 0;
}

if (command is not ("import" or "dry-run"))
{
    Console.Error.WriteLine($"Unknown command '{command}'. Use dry-run, import, verify or status.");
    return 2;
}

if (string.IsNullOrWhiteSpace(legacyConnection))
{
    Console.Error.WriteLine("Set PCCONNECT_LEGACY__CONNECTIONSTRING to the MySQL source.");
    return 2;
}

var kekKeys = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
{
    var name = entry.Key.ToString() ?? string.Empty;
    if (name.StartsWith("PCCONNECT_KEK__KEYS__", StringComparison.Ordinal))
    {
        kekKeys[name["PCCONNECT_KEK__KEYS__".Length..]] = entry.Value?.ToString() ?? string.Empty;
    }
}

if (kekKeys.Count == 0)
{
    Console.Error.WriteLine(
        "No key encryption key configured. Reminders cannot be re-encrypted without one. " +
        "Set PCCONNECT_KEK__KEYS__k1 to a base64 32-byte key.");
    return 2;
}

var envelope = new EnvelopeEncryptor(Options.Create(new EnvelopeOptions
{
    Keys = kekKeys,
    CurrentKekId = Environment.GetEnvironmentVariable("PCCONNECT_KEK__CURRENTKEKID") ?? kekKeys.Keys.First(),
}));

var hasher = new Argon2PasswordHasher(Options.Create(new Argon2Options()));

var importer = new LegacyImporter(
    new ImportOptions
    {
        LegacyConnectionString = legacyConnection,
        TargetConnectionString = targetConnection,
        DryRun = command == "dry-run",
        DefaultTimezone = Environment.GetEnvironmentVariable("PCCONNECT_IMPORT__DEFAULTTIMEZONE") ?? "Europe/London",
    },
    envelope,
    hasher,
    loggerFactory.CreateLogger<LegacyImporter>());

try
{
    var report = await importer.RunAsync();

    Console.WriteLine();
    Console.WriteLine(command == "dry-run" ? "DRY RUN — nothing was written" : "Import complete");

    foreach (var (entity, count) in report.Imported.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    {
        var skipped = report.Skipped.GetValueOrDefault(entity);
        Console.WriteLine($"  {entity,-14} {count,8} imported{(skipped > 0 ? $", {skipped} skipped" : string.Empty)}");
    }

    // The gates decide whether this import may advance a stage; a clean run that
    // fails verification has not succeeded (02 §5).
    if (command == "import")
    {
        var failures = await new Migrator(targetConnection, m => logger.LogInformation("{Message}", m)).VerifyAsync();

        if (failures.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Verification gates FAILED after import:");
            foreach (var (check, violations) in failures)
            {
                Console.Error.WriteLine($"  {check.Id}: {check.Description} -> {violations}");
            }

            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("All verification gates returned zero.");
    }

    return 0;
}
catch (Exception ex) when (ex is MySqlConnector.MySqlException or Npgsql.NpgsqlException or InvalidOperationException)
{
    logger.LogError(ex, "Import failed");
    return 1;
}
