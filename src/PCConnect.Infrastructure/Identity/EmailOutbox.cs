using Npgsql;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Identity;

public interface IEmailOutbox
{
    Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string recipient,
        string templateName, string token, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public sealed class EmailOutbox(IEmailCipher cipher) : IEmailOutbox
{
    public async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, string recipient,
        string templateName, string token, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7(now);
        var encrypted = cipher.Encrypt(id, templateName, new(recipient, token));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO email_outbox(id,user_id,template,payload_ciphertext,payload_nonce,payload_tag,encryption_key_id,created_at,available_at,expires_at)
            VALUES(@id,@userId,@template,@ciphertext,@nonce,@tag,@keyId,@now,@now,@expires);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("template", templateName);
        command.Parameters.AddWithValue("ciphertext", encrypted.Ciphertext);
        command.Parameters.AddWithValue("nonce", encrypted.Nonce);
        command.Parameters.AddWithValue("tag", encrypted.Tag);
        command.Parameters.AddWithValue("keyId", encrypted.KeyId);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
