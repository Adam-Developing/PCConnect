using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace PCConnect.Infrastructure.Data;

/// <summary>
/// One place that opens a connection, so pooling, the command timeout and the
/// snake_case mapping are configured once rather than per call site.
/// </summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource => dataSource;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        await dataSource.OpenConnectionAsync(ct);

    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction and commits, or rolls
    /// back and rethrows. Used wherever an invariant spans two statements — a
    /// command transition and its audit row are written together or not at all.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            var result = await work(connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task InTransactionAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> work,
        CancellationToken ct = default) =>
        InTransactionAsync(async (c, t) =>
        {
            await work(c, t);
            return true;
        }, ct);

    /// <summary>
    /// Applied once at startup. Dapper's default mapper does not match
    /// <c>due_at_utc</c> to <c>DueAtUtc</c> without this.
    /// </summary>
    public static void ConfigureDapper()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }
}

/// <summary>
/// Helpers for the two shapes that appear in nearly every query: a jsonb column
/// and a nullable inet.
/// </summary>
public static class DbJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialise<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialise<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);

    public static IReadOnlyList<string> StringArray(string? json) =>
        Deserialise<List<string>>(json) ?? [];

    public static IReadOnlyDictionary<string, object?>? Params(string? json) =>
        Deserialise<Dictionary<string, object?>>(json);
}

public static class DbNet
{
    /// <summary>
    /// Parses a client IP for the <c>inet</c> columns. Anything unparseable is
    /// stored as NULL rather than as a misleading literal.
    /// </summary>
    public static System.Net.IPAddress? Parse(string? ip) =>
        System.Net.IPAddress.TryParse(ip, out var parsed) ? parsed : null;
}
