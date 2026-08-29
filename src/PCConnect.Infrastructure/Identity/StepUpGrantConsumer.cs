using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Identity;

public sealed class StepUpGrantConsumer(IOpaqueTokenService tokens, IClock clock)
{
    public async Task<Guid> ConsumeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, Guid sessionId,
        string grant, StepUpIntentType intent, Guid? targetDeviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant)) throw new AuthenticationFailureException("step_up_required");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE step_up_grants SET consumed_at=@now
            WHERE id=(SELECT id FROM step_up_grants WHERE grant_hash=@hash AND user_id=@userId AND session_id=@sessionId
              AND intent=@intent AND target_device_id IS NOT DISTINCT FROM @targetDeviceId
              AND consumed_at IS NULL AND expires_at>@now FOR UPDATE)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("hash", tokens.Hash(grant));
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("intent", intent.WireValue());
        command.Parameters.Add(new("targetDeviceId", NpgsqlDbType.Uuid) { Value = targetDeviceId is null ? DBNull.Value : targetDeviceId.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? id : throw new AuthenticationFailureException("invalid_step_up_grant");
    }
}
