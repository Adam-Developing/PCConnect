using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PCConnect.Infrastructure.Database.Migrations;

[DbContext(typeof(PCConnectDbContext))]
[Migration("202608260001_InitialCanonicalV2")]
public sealed class InitialCanonicalV2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCConnect.Database.v2-canonical-schema.sql")
            ?? throw new InvalidOperationException("Embedded canonical schema was not found.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var firstStatement = Array.FindIndex(lines, line => !IsCommentOrBlank(line));
        var lastStatement = Array.FindLastIndex(lines, line => !IsCommentOrBlank(line));
        if (firstStatement < 0 || lastStatement <= firstStatement ||
            !string.Equals(lines[firstStatement].Trim(), "BEGIN;", StringComparison.Ordinal) ||
            !string.Equals(lines[lastStatement].Trim(), "COMMIT;", StringComparison.Ordinal))
            throw new InvalidOperationException("Canonical schema must retain its explicit transaction boundary.");
        var sql = string.Join('\n', lines[(firstStatement + 1)..lastStatement]).Trim();
        if (sql.Length == 0)
            throw new InvalidOperationException("Canonical schema transaction body must not be empty.");
        migrationBuilder.Sql(sql, suppressTransaction: false);
    }

    private static bool IsCommentOrBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The canonical initial migration has no destructive automatic downgrade. Restore a disposable database or roll forward.");
}
