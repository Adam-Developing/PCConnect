using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PCConnect.Infrastructure.Database;

var configuration = new ConfigurationManager();
configuration.AddEnvironmentVariables();
if (Directory.Exists("/run/secrets")) configuration.AddKeyPerFile("/run/secrets", optional: false);

var connectionString = configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
var apply = args.Contains("--apply", StringComparer.Ordinal);
var environment = configuration["PCCONNECT_ENVIRONMENT"] ?? "local";
if (environment.Equals("production", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(configuration["PCCONNECT_PRODUCTION_CHANGE_TICKET"]))
    throw new InvalidOperationException("Production migration requires PCCONNECT_PRODUCTION_CHANGE_TICKET after explicit approval.");

var options = new DbContextOptionsBuilder<PCConnectDbContext>().UseNpgsql(connectionString).Options;
await using var database = new PCConnectDbContext(options);
var pending = (await database.Database.GetPendingMigrationsAsync()).ToArray();
Console.WriteLine($"Environment: {environment}; pending migrations: {pending.Length}");
foreach (var migration in pending) Console.WriteLine(migration);
if (!apply)
{
    Console.WriteLine("Dry run only. Re-run with --apply after the environment backup and change approval gates pass.");
    return;
}

await database.Database.OpenConnectionAsync();
try
{
    await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtextextended('pcconnect-v2-migrations',0));");
    await database.Database.MigrateAsync();
    Console.WriteLine("Migrations applied successfully.");
}
finally
{
    await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtextextended('pcconnect-v2-migrations',0));");
    await database.Database.CloseConnectionAsync();
}
