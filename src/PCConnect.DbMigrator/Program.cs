using PCConnect.DbMigrator;

// pcconnect-migrate — schema migrations and verification gates.
//
//   pcconnect-migrate up      [--allow-destructive]
//   pcconnect-migrate down
//   pcconnect-migrate status
//   pcconnect-migrate verify
//   pcconnect-migrate rewrap-deks [--status]
//
// The connection string comes from PCCONNECT_DATABASE__CONNECTIONSTRING or
// DATABASE_URL. It is never a command-line argument: arguments end up in shell
// history and process listings.

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
var allowDestructive = args.Contains("--allow-destructive", StringComparer.Ordinal);

var connectionString =
    Environment.GetEnvironmentVariable("PCCONNECT_DATABASE__CONNECTIONSTRING")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("No database configured. Set PCCONNECT_DATABASE__CONNECTIONSTRING or DATABASE_URL.");
    return 2;
}

var migrator = new Migrator(connectionString);

try
{
    switch (command)
    {
        case "up":
        {
            var applied = await migrator.UpAsync(allowDestructive);
            Console.WriteLine(applied == 0 ? "Already up to date." : $"Applied {applied} migration(s).");
            return 0;
        }

        case "down":
        {
            var reverted = await migrator.DownAsync();
            Console.WriteLine(reverted is null ? "Nothing to revert." : $"Reverted {reverted}.");
            return 0;
        }

        case "status":
        {
            foreach (var status in await migrator.StatusAsync())
            {
                var state = status.Applied ? (status.ChecksumMatches ? "applied" : "APPLIED (CHECKSUM MISMATCH)") : "pending";
                Console.WriteLine($"{status.Version,-6} {state,-28} {status.Name}");
            }

            return 0;
        }

        case "verify":
        {
            var failures = await migrator.VerifyAsync();
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

        case "rewrap-deks":
        {
            // Finishes a KEK rotation. Safe to run repeatedly, safe to
            // interrupt, and safe to run while the API is serving.
            var envelope = KekRotation.EncryptorFromEnvironment();
            var rotation = new KekRotation(connectionString);

            var progress = args.Contains("--status", StringComparer.Ordinal)
                ? await rotation.StatusAsync(envelope)
                : await rotation.RewrapAsync(envelope);

            Console.WriteLine(KekRotation.Describe(progress, envelope.CurrentKekId));
            return progress.Remaining == 0 ? 0 : 1;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Use up, down, status, verify or rewrap-deks.");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
