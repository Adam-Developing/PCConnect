using System.Data;
using System.Net;
using System.Net.Mail;
using Npgsql;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Worker;

public sealed class EmailDeliveryWorker(
    NpgsqlDataSource dataSource,
    IEmailCipher cipher,
    IClock clock,
    IConfiguration configuration,
    ILogger<EmailDeliveryWorker> logger) : BackgroundService
{
    private readonly Guid instanceId = Guid.CreateVersion7();
    private static readonly Action<ILogger, Exception?> CycleFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(6001, nameof(CycleFailed)), "Email delivery cycle failed");
    private static readonly Action<ILogger, Exception?> ConfigurationMissing = LoggerMessage.Define(
        LogLevel.Warning, new EventId(6002, nameof(ConfigurationMissing)), "SMTP configuration is absent; encrypted email outbox messages remain pending");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var smtp = SmtpOptions.Read(configuration);
        if (!smtp.IsConfigured)
        {
            ConfigurationMissing(logger, null);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var row in await ClaimAsync(stoppingToken))
                {
                    try
                    {
                        var payload = cipher.Decrypt(row.Id, row.Template, new(row.Ciphertext, row.Nonce, row.Tag, row.KeyId));
                        await SendAsync(smtp, row.Template, payload, stoppingToken);
                        await MarkSentAsync(row.Id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        await ReleaseAsync(row.Id, exception.GetType().Name, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { CycleFailed(logger, exception); }
        }
    }

    private async Task<IReadOnlyList<EmailRow>> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = "DELETE FROM email_outbox WHERE sent_at IS NULL AND expires_at<=@now";
            expire.Parameters.AddWithValue("now", now);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
              SELECT id FROM email_outbox WHERE sent_at IS NULL AND expires_at>@now AND available_at<=@now
                AND (claimed_until IS NULL OR claimed_until<=@now)
              ORDER BY created_at,id LIMIT 20 FOR UPDATE SKIP LOCKED
            )
            UPDATE email_outbox e SET claimed_by=@worker,claimed_until=@lease,attempt_count=attempt_count+1
            FROM candidates c WHERE e.id=c.id
            RETURNING e.id,e.template,e.payload_ciphertext,e.payload_nonce,e.payload_tag,e.encryption_key_id;
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker", instanceId);
        command.Parameters.AddWithValue("lease", now.AddSeconds(30));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<EmailRow>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4), reader.GetString(5)));
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task SendAsync(SmtpOptions options, string template, EmailPayload payload, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["Email:PublicBaseUrl"]?.TrimEnd('/') ?? "https://pcconnect.adamdeveloping.co.uk";
        var (subject, path) = template switch
        {
            "verify_email" => ("Verify your PCConnect email", "verify-email"),
            "confirm_email_change" => ("Confirm your PCConnect email change", "verify-email"),
            "reset_password" => ("Reset your PCConnect password", "reset-password"),
            _ => throw new InvalidOperationException("Unknown email template.")
        };
        var link = $"{baseUrl}/{path}#token={Uri.EscapeDataString(payload.Token)}";
        using var message = new MailMessage(options.FromAddress, payload.Recipient, subject,
            $"Open this one-time PCConnect link before it expires:\r\n\r\n{link}\r\n\r\nIf you did not request this, you can ignore this message.");
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseTls,
            Credentials = string.IsNullOrWhiteSpace(options.Username)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(options.Username, options.Password)
        };
        await client.SendMailAsync(message, cancellationToken);
    }

    private async Task MarkSentAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE email_outbox SET sent_at=@now,claimed_by=NULL,claimed_until=NULL,last_error_code=NULL
            WHERE id=@id AND claimed_by=@worker AND sent_at IS NULL;
            """);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReleaseAsync(Guid id, string code, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE email_outbox SET claimed_by=NULL,claimed_until=NULL,available_at=@retry,last_error_code=@code
            WHERE id=@id AND claimed_by=@worker AND sent_at IS NULL;
            """);
        command.Parameters.AddWithValue("retry", clock.UtcNow.AddMinutes(1));
        command.Parameters.AddWithValue("code", code[..Math.Min(100, code.Length)]);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("worker", instanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record EmailRow(Guid Id, string Template, byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyId);
    private sealed record SmtpOptions(string Host, int Port, bool UseTls, string Username, string Password, string FromAddress)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
        public static SmtpOptions Read(IConfiguration configuration) => new(
            configuration["Email:Smtp:Host"] ?? string.Empty,
            configuration.GetValue("Email:Smtp:Port", 587),
            configuration.GetValue("Email:Smtp:UseTls", true),
            configuration["Email:Smtp:Username"] ?? string.Empty,
            configuration["Email:Smtp:Password"] ?? string.Empty,
            configuration["Email:Smtp:FromAddress"] ?? string.Empty);
    }
}
