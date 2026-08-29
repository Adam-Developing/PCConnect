using System.Data;
using System.Text.Json;
using Npgsql;
using PCConnect.Domain;
using PCConnect.Infrastructure.Accounts;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Worker;

public sealed class DataExportWorker(
    NpgsqlDataSource dataSource,
    IExportArtifactStore artifacts,
    IReminderCipher reminders,
    IClock clock,
    ILogger<DataExportWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(8001, nameof(CycleFailed)), "Data export cycle failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExpireAsync(stoppingToken);
                foreach (var job in await ClaimAsync(stoppingToken)) await BuildAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { CycleFailed(logger, exception); }
        }
    }

    private async Task<IReadOnlyList<ExportJobRow>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
              SELECT id FROM data_export_jobs
              WHERE expires_at>@now AND (status='queued' OR (status='processing' AND claimed_at<@stale))
              ORDER BY created_at,id LIMIT 5 FOR UPDATE SKIP LOCKED
            )
            UPDATE data_export_jobs j SET status='processing',claimed_at=@now,failure_code=NULL
            FROM candidates c WHERE j.id=c.id RETURNING j.id,j.user_id;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("stale", now.AddMinutes(-5));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<ExportJobRow>();
        while (await reader.ReadAsync(cancellationToken)) jobs.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return jobs;
    }

    private async Task BuildAsync(ExportJobRow job, CancellationToken cancellationToken)
    {
        try
        {
            var export = await ReadExportAsync(job.UserId, cancellationToken);
            var json = JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
            try { await artifacts.WriteAsync(job.Id, json, cancellationToken); }
            finally { Array.Clear(json); }
            await using var ready = dataSource.CreateCommand("""
                UPDATE data_export_jobs SET status='ready',storage_reference=@reference,claimed_at=NULL
                WHERE id=@id AND user_id=@userId AND status='processing';
                """);
            ready.Parameters.AddWithValue("reference", $"{job.Id:D}.pcexport");
            ready.Parameters.AddWithValue("id", job.Id);
            ready.Parameters.AddWithValue("userId", job.UserId);
            await ready.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            artifacts.Delete(job.Id);
            await using var failed = dataSource.CreateCommand("""
                UPDATE data_export_jobs SET status='failed',failure_code=@code,claimed_at=NULL WHERE id=@id AND status='processing';
                """);
            var name = exception.GetType().Name;
            failed.Parameters.AddWithValue("code", name[..Math.Min(100, name.Length)]);
            failed.Parameters.AddWithValue("id", job.Id);
            await failed.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<object> ReadExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        object profile;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT username::text,email::text,email_verified_at,display_name,date_of_birth,marketing_opt_in,timezone,created_at
                FROM users WHERE id=@userId;
                """;
            command.Parameters.AddWithValue("userId", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Export owner no longer exists.");
            profile = new
            {
                username = reader.GetString(0), email = reader.GetString(1), emailVerifiedAt = NullableInstant(reader, 2),
                displayName = reader.GetString(3), dateOfBirth = reader.IsDBNull(4) ? null : (DateOnly?)reader.GetFieldValue<DateOnly>(4),
                marketingOptIn = reader.GetBoolean(5), timezone = reader.GetString(6), createdAt = reader.GetFieldValue<DateTimeOffset>(7)
            };
        }

        var devices = new List<object>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,platform::text,display_name,agent_version,capabilities::text[],status::text,last_seen_at,enrolled_at FROM devices WHERE user_id=@userId";
            command.Parameters.AddWithValue("userId", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) devices.Add(new
            {
                id = reader.GetGuid(0), platform = reader.GetString(1), displayName = reader.GetString(2), agentVersion = reader.GetString(3),
                capabilities = reader.GetFieldValue<string[]>(4), status = reader.GetString(5), lastSeenAt = NullableInstant(reader, 6), enrolledAt = reader.GetFieldValue<DateTimeOffset>(7)
            });
        }

        var reminderRows = new List<ReminderRow>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,target_mode::text,timezone,local_start,recurrence_rule,text_ciphertext,text_nonce,text_tag,
                  wrapped_data_key,wrapping_key_id,text_aad_version,created_at,deleted_at
                FROM reminders WHERE user_id=@userId;
                """;
            command.Parameters.AddWithValue("userId", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) reminderRows.Add(new(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                new(reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7), reader.GetFieldValue<byte[]>(8), reader.GetString(9), reader.GetInt16(10)),
                reader.GetFieldValue<DateTimeOffset>(11), NullableInstant(reader, 12)));
        }
        var reminderExport = reminderRows.Select(row => new
        {
            id = row.Id, text = reminders.Decrypt(row.Id, userId, row.Encrypted), targetMode = row.TargetMode,
            timezone = row.Timezone, localStart = row.LocalStart, recurrenceRule = row.RecurrenceRule, createdAt = row.CreatedAt, deletedAt = row.DeletedAt
        }).ToArray();

        var commands = new List<object>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id,target_device_id,type::text,status::text,issued_at,expires_at,accepted_at,finished_at,failure_code::text FROM commands WHERE user_id=@userId ORDER BY issued_at";
            command.Parameters.AddWithValue("userId", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) commands.Add(new
            {
                id = reader.GetGuid(0), deviceId = reader.GetGuid(1), type = reader.GetString(2), status = reader.GetString(3),
                issuedAt = reader.GetFieldValue<DateTimeOffset>(4), expiresAt = reader.GetFieldValue<DateTimeOffset>(5),
                acceptedAt = NullableInstant(reader, 6), finishedAt = NullableInstant(reader, 7), failureCode = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
        await transaction.CommitAsync(cancellationToken);
        return new { exportVersion = 1, generatedAt = clock.UtcNow, profile, devices, reminders = reminderExport, commands };
    }

    private async Task ExpireAsync(CancellationToken cancellationToken)
    {
        var expired = new List<Guid>();
        await using (var command = dataSource.CreateCommand("UPDATE data_export_jobs SET status='expired' WHERE expires_at<=@now AND status<>'expired' RETURNING id"))
        {
            command.Parameters.AddWithValue("now", clock.UtcNow);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) expired.Add(reader.GetGuid(0));
        }
        foreach (var id in expired) artifacts.Delete(id);
    }

    private static DateTimeOffset? NullableInstant(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    private sealed record ExportJobRow(Guid Id, Guid UserId);
    private sealed record ReminderRow(Guid Id, string TargetMode, string Timezone, DateTime LocalStart, string? RecurrenceRule, EncryptedReminder Encrypted, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
