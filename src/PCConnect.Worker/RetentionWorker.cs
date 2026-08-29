using Npgsql;
using PCConnect.Domain;
using PCConnect.Infrastructure.Accounts;

namespace PCConnect.Worker;

public sealed class RetentionWorker(NpgsqlDataSource dataSource, IExportArtifactStore artifacts, IClock clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> SweepFailed = LoggerMessage.Define(LogLevel.Error, new EventId(8001, nameof(SweepFailed)), "Retention sweep failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { SweepFailed(logger, exception); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var command = dataSource.CreateCommand("""
            UPDATE session_refresh_tokens SET state='expired' WHERE state='active' AND expires_at<=@now;
            UPDATE device_refresh_tokens SET state='expired' WHERE state='active' AND expires_at<=@now;
            UPDATE device_enrollments SET status='expired' WHERE status='pending' AND expires_at<=@now;
            UPDATE data_export_jobs SET status='expired' WHERE status='ready' AND expires_at<=@now;
            DELETE FROM access_tokens WHERE expires_at<@accessCutoff;
            DELETE FROM webauthn_challenges WHERE expires_at<@shortCutoff;
            DELETE FROM step_up_grants WHERE expires_at<@shortCutoff;
            DELETE FROM email_tokens WHERE expires_at<@tokenCutoff;
            DELETE FROM device_sid_candidates WHERE expires_at<@now;
            DELETE FROM outbox_messages WHERE published_at<@outboxCutoff;
            DELETE FROM email_outbox WHERE sent_at<@emailCutoff;
            DELETE FROM authentication_throttles WHERE updated_at<@accessCutoff;
            DELETE FROM session_refresh_tokens WHERE expires_at<@credentialCutoff AND state<>'active';
            DELETE FROM device_refresh_tokens WHERE expires_at<@credentialCutoff AND state<>'active';
            """);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("accessCutoff", now.AddDays(-1));
        command.Parameters.AddWithValue("shortCutoff", now.AddDays(-1));
        command.Parameters.AddWithValue("tokenCutoff", now.AddDays(-7));
        command.Parameters.AddWithValue("outboxCutoff", now.AddDays(-7));
        command.Parameters.AddWithValue("emailCutoff", now.AddDays(-30));
        command.Parameters.AddWithValue("credentialCutoff", now.AddDays(-90));
        await command.ExecuteNonQueryAsync(cancellationToken);

        var expiredExports = new List<Guid>();
        await using (var select = dataSource.CreateCommand("SELECT id FROM data_export_jobs WHERE status='expired' AND storage_reference IS NOT NULL"))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) expiredExports.Add(reader.GetGuid(0));
        foreach (var exportId in expiredExports) artifacts.Delete(exportId);
        if (expiredExports.Count > 0)
        {
            await using var cleared = dataSource.CreateCommand("UPDATE data_export_jobs SET storage_reference=NULL WHERE id=ANY(@ids)");
            cleared.Parameters.AddWithValue("ids", expiredExports.ToArray());
            await cleared.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
