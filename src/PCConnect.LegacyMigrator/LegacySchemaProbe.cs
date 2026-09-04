using Dapper;
using MySqlConnector;

namespace PCConnect.LegacyMigrator;

/// <summary>
/// The committed dump <c>DB/pcconnect.sql</c> describes a different generation
/// of the schema than the deployed code queries (S2-02): the dump has
/// <c>pcnames(PCID, Username TEXT, PCName)</c> while the code reads
/// <c>pcnames.UserID/.Request/.Value/.Time</c> and <c>users.api_key</c>.
///
/// Rather than guess which one a given database is, the importer asks
/// <c>information_schema</c> and adapts. An import that assumes a shape and
/// fails at row 4,000 is worse than one that checks first.
/// </summary>
public sealed class LegacySchemaProbe(MySqlConnection connection)
{
    private HashSet<string> _columns = [];
    private HashSet<string> _tables = [];

    public async Task LoadAsync(string database, CancellationToken ct = default)
    {
        var columns = await connection.QueryAsync<(string TableName, string ColumnName)>(
            new CommandDefinition("""
                SELECT TABLE_NAME, COLUMN_NAME
                  FROM information_schema.COLUMNS
                 WHERE TABLE_SCHEMA = @Database
                """, new { Database = database }, cancellationToken: ct));

        _columns = columns
            .Select(c => $"{c.TableName.ToLowerInvariant()}.{c.ColumnName.ToLowerInvariant()}")
            .ToHashSet(StringComparer.Ordinal);

        _tables = _columns
            .Select(c => c[..c.IndexOf('.', StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);
    }

    public bool HasTable(string table) => _tables.Contains(table.ToLowerInvariant());

    public bool HasColumn(string table, string column) =>
        _columns.Contains($"{table.ToLowerInvariant()}.{column.ToLowerInvariant()}");

    /// <summary>
    /// True when the database is the generation the deployed code queries: a
    /// per-user device table with a command mailbox on it.
    /// </summary>
    public bool IsCurrentGeneration =>
        HasColumn("pcnames", "UserID") && HasColumn("users", "api_key");

    public string Describe() => IsCurrentGeneration
        ? "current generation (pcnames.UserID, users.api_key present)"
        : "older generation (pcnames keyed by Username; no users.api_key)";
}
