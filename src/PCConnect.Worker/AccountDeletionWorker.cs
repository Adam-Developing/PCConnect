using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Worker;

public sealed class AccountDeletionWorker(
    NpgsqlDataSource dataSource,
    SecurityOptions security,
    IClock clock,
    ILogger<AccountDeletionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(7001, nameof(CycleFailed)), "Account deletion cycle failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var job in await ClaimAsync(stoppingToken)) await DeleteAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { CycleFailed(logger, exception); }
        }
    }

    private async Task<IReadOnlyList<DeletionJob>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
              SELECT id FROM account_deletion_jobs WHERE status='queued' ORDER BY requested_at,id
              LIMIT 10 FOR UPDATE SKIP LOCKED
            )
            UPDATE account_deletion_jobs j SET status='processing' FROM candidates c WHERE j.id=c.id
            RETURNING j.id,j.user_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<DeletionJob>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return jobs;
    }

    private async Task DeleteAsync(DeletionJob job, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        try
        {
            var key = security.DecodeDeletionKey();
            byte[] digest;
            try
            {
                using var hmac = new HMACSHA256(key);
                digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"pcconnect.deleted.v1|{job.UserId:D}"));
            }
            finally { CryptographicOperations.ZeroMemory(key); }

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO deletion_tombstones(subject_digest,deleted_at,deletion_job_id,restore_replay_version)
                VALUES(@digest,@now,@jobId,1) ON CONFLICT(deletion_job_id) DO NOTHING;
                DELETE FROM users WHERE id=@userId AND account_state='deletion_pending';
                UPDATE account_deletion_jobs SET status='completed',completed_at=@now,failure_code=NULL WHERE id=@jobId AND status='processing';
                """;
            command.Parameters.AddWithValue("digest", digest);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("jobId", job.Id);
            command.Parameters.AddWithValue("userId", job.UserId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            CryptographicOperations.ZeroMemory(digest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await using var failed = dataSource.CreateCommand("""
                UPDATE account_deletion_jobs SET status='failed',failure_code=@code WHERE id=@id AND status='processing';
                """);
            failed.Parameters.AddWithValue("code", exception.GetType().Name[..Math.Min(100, exception.GetType().Name.Length)]);
            failed.Parameters.AddWithValue("id", job.Id);
            await failed.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record DeletionJob(Guid Id, Guid UserId);
}
