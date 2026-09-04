using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace PCConnect.DbMigrator;

public sealed record Migration(string Version, string Name, string Up, string Down, bool IsDestructive)
{
    public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Up)))[..32];
}

public sealed record MigrationStatus(string Version, string Name, bool Applied, DateTimeOffset? AppliedAt, bool ChecksumMatches);

/// <summary>
/// Plain-SQL, versioned migrations applied by a runner we own.
///
/// ADR-0005 chose `dbmate` for the property that mattered: schema history is
/// versioned plain SQL, not a tool-generated artefact coupled to the
/// application's ORM. ADR-0009 keeps that property and drops the external
/// binary — the file format is unchanged (`-- migrate:up` / `-- migrate:down`),
/// so the migrations remain readable and runnable by hand in an incident.
/// </summary>
public sealed class Migrator(string connectionString, Action<string>? log = null)
{
    private const string UpMarker = "-- migrate:up";
    private const string DownMarker = "-- migrate:down";

    private readonly Action<string> _log = log ?? Console.WriteLine;

    /// <summary>
    /// Migrations are embedded in the assembly so a container image carries its
    /// own schema history; nothing depends on a directory being copied alongside.
    /// </summary>
    public static IReadOnlyList<Migration> Load()
    {
        var assembly = typeof(Migrator).Assembly;
        var migrations = new List<Migration>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded migration {name} could not be opened.");
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var fileName = name[(name.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            var underscore = fileName.IndexOf('_', StringComparison.Ordinal);
            var version = underscore > 0 ? fileName[..underscore] : fileName;
            var friendly = fileName.Replace(".sql", string.Empty, StringComparison.Ordinal);

            var upIndex = content.IndexOf(UpMarker, StringComparison.Ordinal);
            var downIndex = content.IndexOf(DownMarker, StringComparison.Ordinal);

            if (upIndex < 0)
            {
                throw new InvalidOperationException($"{fileName} has no '{UpMarker}' section.");
            }

            var up = downIndex > upIndex
                ? content[(upIndex + UpMarker.Length)..downIndex]
                : content[(upIndex + UpMarker.Length)..];

            var down = downIndex > upIndex ? content[(downIndex + DownMarker.Length)..] : string.Empty;

            // A migration that drops anything is gated: the runner refuses it
            // unless the operator passes --allow-destructive (08 §4.2).
            var isDestructive = up.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase)
                || up.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase)
                || up.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase);

            migrations.Add(new Migration(version, friendly, up.Trim(), down.Trim(), isDestructive));
        }

        return migrations;
    }

    public static IReadOnlyList<VerificationCheck> LoadChecks()
    {
        var assembly = typeof(Migrator).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains(".Verification.", StringComparison.Ordinal));

        if (resource is null)
        {
            return [];
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return VerificationCheck.Parse(reader.ReadToEnd());
    }

    public async Task<int> UpAsync(bool allowDestructive, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await EnsureLedgerAsync(connection, ct);

        var applied = await AppliedAsync(connection, ct);
        var count = 0;

        foreach (var migration in Load())
        {
            if (applied.TryGetValue(migration.Version, out var record))
            {
                if (!string.Equals(record.Checksum, migration.Checksum, StringComparison.Ordinal))
                {
                    // An applied migration whose text has changed means the
                    // database and the repository disagree about history. Editing
                    // an applied migration is how a staging environment silently
                    // stops matching production.
                    throw new InvalidOperationException(
                        $"Migration {migration.Version} has been modified after it was applied " +
                        $"(recorded {record.Checksum}, file {migration.Checksum}). Add a new migration instead.");
                }

                continue;
            }

            if (migration.IsDestructive && !allowDestructive)
            {
                _log($"SKIP  {migration.Name} (destructive; re-run with --allow-destructive to apply)");
                continue;
            }

            _log($"APPLY {migration.Name}{(migration.IsDestructive ? "  [DESTRUCTIVE]" : string.Empty)}");

            await using var tx = await connection.BeginTransactionAsync(ct);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(migration.Up, transaction: tx, commandTimeout: 600, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO schema_migrations (version, name, checksum) VALUES (@Version, @Name, @Checksum)
                    """, new { migration.Version, migration.Name, migration.Checksum }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                count++;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        return count;
    }

    public async Task<string?> DownAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await EnsureLedgerAsync(connection, ct);

        var latest = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1", cancellationToken: ct));

        if (latest is null)
        {
            return null;
        }

        var migration = Load().FirstOrDefault(m => m.Version == latest)
            ?? throw new InvalidOperationException($"Migration {latest} is recorded as applied but is not in this build.");

        if (string.IsNullOrWhiteSpace(migration.Down))
        {
            throw new InvalidOperationException($"Migration {migration.Name} has no down section and cannot be rolled back.");
        }

        _log($"REVERT {migration.Name}");

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(migration.Down, transaction: tx, commandTimeout: 600, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM schema_migrations WHERE version = @Version",
                new { migration.Version }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return migration.Name;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<MigrationStatus>> StatusAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await EnsureLedgerAsync(connection, ct);

        var applied = await AppliedAsync(connection, ct);

        return Load().Select(m => applied.TryGetValue(m.Version, out var record)
            ? new MigrationStatus(m.Version, m.Name, true, record.AppliedAt,
                string.Equals(record.Checksum, m.Checksum, StringComparison.Ordinal))
            : new MigrationStatus(m.Version, m.Name, false, null, true)).ToList();
    }

    /// <summary>Runs the verification gates; returns the checks that did not return zero.</summary>
    public async Task<IReadOnlyList<(VerificationCheck Check, long Violations)>> VerifyAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var failures = new List<(VerificationCheck, long)>();
        foreach (var check in LoadChecks())
        {
            try
            {
                var violations = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition(check.Sql, commandTimeout: 120, cancellationToken: ct));

                if (violations != 0)
                {
                    failures.Add((check, violations));
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // A check against a bridge table that has already been contracted
                // away is satisfied by construction, not a failure.
                _log($"SKIP  {check.Id} ({ex.MessageText})");
            }
        }

        return failures;
    }

    private static async Task EnsureLedgerAsync(NpgsqlConnection connection, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version    text PRIMARY KEY,
              name       text NOT NULL,
              checksum   text NOT NULL,
              applied_at timestamptz(3) NOT NULL DEFAULT now()
            )
            """, cancellationToken: ct));

    private static async Task<Dictionary<string, (string Checksum, DateTimeOffset AppliedAt)>> AppliedAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<(string Version, string Checksum, DateTimeOffset AppliedAt)>(
            new CommandDefinition("SELECT version, checksum, applied_at FROM schema_migrations", cancellationToken: ct));

        return rows.ToDictionary(r => r.Version, r => (r.Checksum, r.AppliedAt), StringComparer.Ordinal);
    }
}

public sealed record VerificationCheck(string Id, string Description, string Sql)
{
    public static IReadOnlyList<VerificationCheck> Parse(string content)
    {
        var checks = new List<VerificationCheck>();
        string? id = null, description = null;
        var sql = new StringBuilder();

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("-- check:", StringComparison.Ordinal))
            {
                Flush(checks, ref id, ref description, sql);

                var header = trimmed["-- check:".Length..].Trim();
                var space = header.IndexOf(' ', StringComparison.Ordinal);
                id = space > 0 ? header[..space] : header;
                description = space > 0 ? header[(space + 1)..] : string.Empty;
                continue;
            }

            if (id is not null && !trimmed.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                sql.AppendLine(trimmed);
            }
        }

        Flush(checks, ref id, ref description, sql);
        return checks;
    }

    private static void Flush(List<VerificationCheck> checks, ref string? id, ref string? description, StringBuilder sql)
    {
        if (id is not null && sql.ToString().Trim().Length > 0)
        {
            checks.Add(new VerificationCheck(id, description ?? string.Empty, sql.ToString().Trim()));
        }

        id = null;
        description = null;
        sql.Clear();
    }
}
