using System.Data;
using System.Net;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Identity;

public sealed class LoginAttemptGuard(NpgsqlDataSource dataSource, IOpaqueTokenService tokens, IClock clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public async Task CheckAsync(string normalizedLogin, string remoteAddress, CancellationToken cancellationToken)
    {
        var accountHash = tokens.Hash(normalizedLogin);
        try
        {
            await using var command = dataSource.CreateCommand("SELECT blocked_until FROM authentication_throttles WHERE account_hash=@accountHash AND network_address=@network");
            command.Parameters.AddWithValue("accountHash", accountHash);
            command.Parameters.Add(new("network", NpgsqlDbType.Inet) { Value = ParseAddress(remoteAddress) });
            if (await command.ExecuteScalarAsync(cancellationToken) is DateTimeOffset blockedUntil && blockedUntil > clock.UtcNow)
                throw new RequestRateLimitedException("authentication_throttled");
        }
        finally { CryptographicOperations.ZeroMemory(accountHash); }
    }

    public async Task RecordFailureAsync(string normalizedLogin, string remoteAddress, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var accountHash = tokens.Hash(normalizedLogin);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var attempts = 1;
            var windowStarted = now;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT attempts,window_started_at FROM authentication_throttles WHERE account_hash=@accountHash AND network_address=@network FOR UPDATE";
                AddKey(select, accountHash, remoteAddress);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var existingWindow = reader.GetFieldValue<DateTimeOffset>(1);
                    if (existingWindow > now - Window) { attempts = reader.GetInt32(0) + 1; windowStarted = existingWindow; }
                }
            }
            var blockMinutes = attempts < 5 ? 0 : Math.Min(1 << Math.Min(attempts - 5, 6), 60);
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO authentication_throttles(account_hash,network_address,window_started_at,attempts,blocked_until,updated_at)
                VALUES(@accountHash,@network,@window,@attempts,@blocked,@now)
                ON CONFLICT(account_hash,network_address) DO UPDATE SET window_started_at=EXCLUDED.window_started_at,
                  attempts=EXCLUDED.attempts,blocked_until=EXCLUDED.blocked_until,updated_at=EXCLUDED.updated_at;
                """;
            AddKey(upsert, accountHash, remoteAddress);
            upsert.Parameters.AddWithValue("window", windowStarted);
            upsert.Parameters.AddWithValue("attempts", attempts);
            upsert.Parameters.Add(new("blocked", NpgsqlDbType.TimestampTz) { Value = blockMinutes == 0 ? DBNull.Value : now.AddMinutes(blockMinutes) });
            upsert.Parameters.AddWithValue("now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(accountHash); }
    }

    public async Task RecordSuccessAsync(string normalizedLogin, string remoteAddress, CancellationToken cancellationToken)
    {
        var accountHash = tokens.Hash(normalizedLogin);
        try
        {
            await using var command = dataSource.CreateCommand("DELETE FROM authentication_throttles WHERE account_hash=@accountHash AND network_address=@network");
            AddKey(command, accountHash, remoteAddress);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(accountHash); }
    }

    private static void AddKey(NpgsqlCommand command, byte[] accountHash, string remoteAddress)
    {
        command.Parameters.AddWithValue("accountHash", accountHash);
        command.Parameters.Add(new("network", NpgsqlDbType.Inet) { Value = ParseAddress(remoteAddress) });
    }

    private static IPAddress ParseAddress(string value) => IPAddress.TryParse(value, out var address) ? address : IPAddress.None;
}
