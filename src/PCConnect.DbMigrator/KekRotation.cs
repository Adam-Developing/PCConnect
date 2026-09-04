using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using PCConnect.Infrastructure.Security;

namespace PCConnect.DbMigrator;

/// <summary>
/// Finishes a key encryption key rotation.
///
/// Rotating the KEK is not a flag day: a new key becomes current, new data keys
/// are wrapped with it, and the previous key stays configured so existing users
/// can still be served. What was missing was the step that ends that state.
/// Until every data key is rewrapped, removing the old KEK locks those users
/// out of their own reminders permanently — so "rotation" without this is a
/// promise the deployment can never keep, and `deploy/.env.example` says the
/// previous key is needed only "until every data key has been rewrapped".
///
/// Only the wrapper changes. The data key underneath is the same bytes, so no
/// reminder is re-encrypted and nothing can be lost by interrupting this: a
/// half-finished run leaves some users on the old KEK and the rest on the new
/// one, which is exactly the state the system already tolerates.
/// </summary>
public sealed class KekRotation(string connectionString)
{
    /// <param name="Rewrapped">Data keys moved to the current KEK by this run.</param>
    /// <param name="Remaining">Data keys still not under the current KEK.</param>
    /// <param name="Unreadable">
    /// Of those, the ones whose wrapping KEK is not configured at all. They are
    /// not a queue that will drain - they are stranded until that key comes
    /// back, and they are the reason a rotation can look stuck.
    /// </param>
    public sealed record Progress(
        int Rewrapped,
        int Remaining,
        IReadOnlyDictionary<string, int> ByKekId,
        IReadOnlyDictionary<string, int> Unreadable);

    /// <summary>
    /// Reads the KEK configuration the way every service reads it, from the
    /// environment. Keys are never command-line arguments: arguments end up in
    /// shell history and process listings.
    /// </summary>
    public static EnvelopeEncryptor EncryptorFromEnvironment()
    {
        var options = new EnvelopeOptions
        {
            CurrentKekId = Environment.GetEnvironmentVariable("PCCONNECT_KEK__CURRENTKEKID") ?? "k1",
        };

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key as string;
            if (name is null || !name.StartsWith("PCCONNECT_KEK__KEYS__", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = name["PCCONNECT_KEK__KEYS__".Length..];
            if (entry.Value is string value && !string.IsNullOrWhiteSpace(value))
            {
                options.Keys[id] = value;
            }
        }

        if (options.Keys.Count == 0)
        {
            throw new InvalidOperationException(
                "No KEK is configured. Set PCCONNECT_KEK__KEYS__<id> for the current key and for every key " +
                "still named in users.dek_kek_id.");
        }

        return new EnvelopeEncryptor(Options.Create(options));
    }

    public async Task<Progress> StatusAsync(EnvelopeEncryptor envelope, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var counts = (await connection.QueryAsync<(string KekId, int Count)>(new CommandDefinition("""
            SELECT dek_kek_id AS "KekId", count(*)::int AS "Count"
              FROM users
             WHERE dek_wrapped IS NOT NULL
             GROUP BY dek_kek_id
             ORDER BY dek_kek_id
            """, cancellationToken: ct))).ToList();

        var remaining = counts.Where(c => c.KekId != envelope.CurrentKekId).Sum(c => c.Count);

        var unreadable = counts
            .Where(c => c.KekId != envelope.CurrentKekId && !envelope.CanUnwrapWith(c.KekId))
            .ToDictionary(c => c.KekId, c => c.Count, StringComparer.Ordinal);

        return new Progress(
            0, remaining, counts.ToDictionary(c => c.KekId, c => c.Count, StringComparer.Ordinal), unreadable);
    }

    /// <summary>
    /// Rewraps every data key not already under the current KEK, one user per
    /// statement. Small statements on purpose: this runs against a live
    /// database, and one transaction over every user would hold row locks for
    /// the length of the whole rotation.
    ///
    /// A key it cannot unwrap is skipped and counted, not thrown on. Aborting
    /// at the first one meant a single user whose KEK had been lost blocked the
    /// rotation for everybody else - and, because each pass re-selected the same
    /// rows, the loop could not terminate either. The paging is by id for the
    /// same reason: progress has to be made even when a row cannot be.
    /// </summary>
    public async Task<Progress> RewrapAsync(
        EnvelopeEncryptor envelope, int batchSize = 200, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var rewrapped = 0;
        long after = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = (await connection.QueryAsync<(long Id, byte[] Wrapped, string KekId)>(new CommandDefinition("""
                SELECT id AS "Id", dek_wrapped AS "Wrapped", dek_kek_id AS "KekId"
                  FROM users
                 WHERE dek_wrapped IS NOT NULL
                   AND dek_kek_id IS DISTINCT FROM @Current
                   AND id > @After
                 ORDER BY id
                 LIMIT @Batch
                """, new { Current = envelope.CurrentKekId, After = after, Batch = batchSize }, cancellationToken: ct))).ToList();

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var (id, wrapped, kekId) in batch)
            {
                after = id;

                if (!envelope.CanUnwrapWith(kekId))
                {
                    // Counted by StatusAsync at the end. Nothing is written:
                    // a row we cannot read is a row we must not replace.
                    continue;
                }

                var dek = envelope.UnwrapDataKey(wrapped, kekId);
                try
                {
                    var (newWrapped, newKekId) = envelope.WrapDataKey(dek);

                    // Guarded on the old wrapper: if a request rewrapped this
                    // user between the read and the write, that write wins and
                    // this one does nothing rather than overwriting it.
                    var changed = await connection.ExecuteAsync(new CommandDefinition("""
                        UPDATE users
                           SET dek_wrapped = @NewWrapped, dek_kek_id = @NewKekId, updated_at = now()
                         WHERE id = @Id AND dek_kek_id = @OldKekId AND dek_wrapped = @OldWrapped
                        """,
                        new
                        {
                            Id = id,
                            NewWrapped = newWrapped,
                            NewKekId = newKekId,
                            OldKekId = kekId,
                            OldWrapped = wrapped,
                        }, cancellationToken: ct));

                    rewrapped += changed;
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(dek);
                }
            }
        }

        var status = await StatusAsync(envelope, ct);
        return status with { Rewrapped = rewrapped };
    }

    /// <summary>
    /// A one-line summary for the console, so the operator can see whether the
    /// previous key is safe to remove yet.
    /// </summary>
    public static string Describe(Progress progress, string currentKekId)
    {
        var builder = new StringBuilder();
        builder.Append($"Rewrapped {progress.Rewrapped} data key(s). ");

        foreach (var (kekId, count) in progress.ByKekId.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append($"{kekId}={count} ");
        }

        if (progress.Remaining == 0)
        {
            builder.Append($"— every data key is under '{currentKekId}'. The previous KEK can be removed from the environment.");
            return builder.ToString();
        }

        builder.Append($"— {progress.Remaining} still on an older KEK. Keep the previous key configured.");

        foreach (var (kekId, count) in progress.Unreadable.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // Named explicitly, because this is not something waiting to
            // happen: without that key those users cannot be rewrapped, and
            // cannot read their own reminders either.
            builder.Append($" {count} of them are wrapped with '{kekId}', which is not configured — restore it.");
        }

        return builder.ToString();
    }
}
