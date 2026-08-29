using System.Data;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Identity;

public interface IStepUpService
{
    Task<StepUpOptions> CreateOptionsAsync(Guid userId, Guid sessionId, StepUpIntent intent, CancellationToken cancellationToken);
    Task<StepUpGrant> CompleteAsync(Guid userId, Guid sessionId, StepUpCompletion completion, CancellationToken cancellationToken);
}

public sealed class StepUpService(
    NpgsqlDataSource dataSource,
    IFido2 fido2,
    IOpaqueTokenService tokens,
    IPasswordHasher passwords,
    IClock clock) : IStepUpService
{
    public async Task<StepUpOptions> CreateOptionsAsync(Guid userId, Guid sessionId, StepUpIntent intent, CancellationToken cancellationToken)
    {
        ValidateIntent(intent);
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        var expires = now.AddMinutes(5);

        var descriptors = new List<PublicKeyCredentialDescriptor>();
        await using (var passkeys = dataSource.CreateCommand("SELECT credential_id FROM passkeys WHERE user_id=@userId AND revoked_at IS NULL"))
        {
            passkeys.Parameters.AddWithValue("userId", userId);
            await using var reader = await passkeys.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                descriptors.Add(new PublicKeyCredentialDescriptor(reader.GetFieldValue<byte[]>(0)));
        }

        AssertionOptions? passkeyOptions = null;
        byte[] challengeHash;
        object storedIntent;
        if (descriptors.Count > 0)
        {
            passkeyOptions = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = descriptors,
                UserVerification = UserVerificationRequirement.Required
            });
            challengeHash = tokens.Hash(Base64Url(passkeyOptions.Challenge));
            storedIntent = new { stepUpIntent = intent, options = passkeyOptions };
        }
        else
        {
            var nonce = tokens.Create();
            challengeHash = tokens.Hash(nonce);
            storedIntent = new { stepUpIntent = intent };
        }

        await using var command = dataSource.CreateCommand("""
            INSERT INTO webauthn_challenges(id,user_id,session_id,purpose,challenge_hash,intent,created_at,expires_at)
            VALUES(@id,@userId,@sessionId,'step_up',@hash,@intent::jsonb,@now,@expires);
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("hash", challengeHash);
        command.Parameters.AddWithValue("intent", JsonSerializer.Serialize(storedIntent));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", expires);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new(
            id,
            expires,
            passkeyOptions is null ? ["password"] : ["passkey", "password"],
            passkeyOptions is null ? null : JsonSerializer.SerializeToElement(passkeyOptions));
    }

    public async Task<StepUpGrant> CompleteAsync(Guid userId, Guid sessionId, StepUpCompletion completion, CancellationToken cancellationToken)
    {
        if (completion.Method is not ("password" or "passkey"))
            throw new ArgumentException("The step-up method must be password or passkey.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        StepUpIntent intent;
        JsonElement storedIntent;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT intent FROM webauthn_challenges WHERE id=@id AND user_id=@userId AND session_id=@sessionId
                  AND purpose='step_up' AND consumed_at IS NULL AND expires_at>@now FOR UPDATE;
                """;
            select.Parameters.AddWithValue("id", completion.IntentId);
            select.Parameters.AddWithValue("userId", userId);
            select.Parameters.AddWithValue("sessionId", sessionId);
            select.Parameters.AddWithValue("now", now);
            var raw = await select.ExecuteScalarAsync(cancellationToken) as string;
            if (raw is null) throw new ResourceGoneException("step_up_intent_expired");
            using var document = JsonDocument.Parse(raw);
            storedIntent = document.RootElement.Clone();
            intent = storedIntent.GetProperty("stepUpIntent").Deserialize<StepUpIntent>()
                ?? throw new InvalidOperationException("Stored step-up intent is invalid.");
        }

        if (completion.Method == "password")
        {
            await VerifyPasswordAsync(connection, transaction, userId, completion.Proof, now, cancellationToken);
        }
        else
        {
            await VerifyPasskeyAsync(connection, transaction, userId, completion.Proof, storedIntent, now, cancellationToken);
        }

        var grant = tokens.Create();
        var expires = now.AddMinutes(5);
        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            UPDATE webauthn_challenges SET consumed_at=@now WHERE id=@intentId;
            INSERT INTO step_up_grants(id,user_id,session_id,grant_hash,authentication_method,intent,target_device_id,command,
                idempotency_key,authenticated_at,created_at,expires_at)
            VALUES(@grantId,@userId,@sessionId,@grantHash,@method,@intent,@deviceId,@command::command_type,
                @idempotencyKey,@now,@now,@expires);
            """;
        create.Parameters.AddWithValue("now", now);
        create.Parameters.AddWithValue("intentId", completion.IntentId);
        create.Parameters.AddWithValue("grantId", Guid.CreateVersion7(now));
        create.Parameters.AddWithValue("userId", userId);
        create.Parameters.AddWithValue("sessionId", sessionId);
        create.Parameters.AddWithValue("grantHash", tokens.Hash(grant));
        create.Parameters.AddWithValue("method", completion.Method);
        create.Parameters.AddWithValue("intent", intent.Intent.WireValue());
        create.Parameters.Add(new("deviceId", NpgsqlDbType.Uuid) { Value = intent.DeviceId is null ? DBNull.Value : intent.DeviceId.Value });
        create.Parameters.Add(new("command", NpgsqlDbType.Text) { Value = intent.CommandType is null ? DBNull.Value : intent.CommandType.Value.WireValue() });
        create.Parameters.AddWithValue("idempotencyKey", intent.IdempotencyKey);
        create.Parameters.AddWithValue("expires", expires);
        await create.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(grant, expires);
    }

    private async Task VerifyPasswordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        JsonElement proof,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!proof.TryGetProperty("password", out var passwordElement) || passwordElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Password proof is required.");
        var password = passwordElement.GetString()!;
        PasswordPolicy.ValidatePresented(password);

        string? hash;
        string? legacy;
        await using (var credential = connection.CreateCommand())
        {
            credential.Transaction = transaction;
            credential.CommandText = "SELECT password_hash,legacy_sha256 FROM password_credentials WHERE user_id=@userId FOR UPDATE";
            credential.Parameters.AddWithValue("userId", userId);
            await using var reader = await credential.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException();
            hash = reader.IsDBNull(0) ? null : reader.GetString(0);
            legacy = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var valid = hash is not null
            ? await passwords.VerifyAsync(password, hash, cancellationToken)
            : legacy is not null && passwords.VerifyLegacySha256(password, legacy);
        if (!valid) throw new AuthenticationFailureException();

        if (hash is not null) return;
        var upgraded = await passwords.HashAsync(password, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE password_credentials SET password_hash=@hash,hash_algorithm='argon2id',hash_parameters=@parameters::jsonb,
                legacy_sha256=NULL,migrated_at=@now,changed_at=@now WHERE user_id=@userId AND legacy_sha256=@legacy;
            """;
        update.Parameters.AddWithValue("hash", upgraded);
        update.Parameters.AddWithValue("parameters", JsonSerializer.Serialize(new
        {
            memoryKiB = Argon2IdPasswordHasher.MemoryKiB,
            iterations = Argon2IdPasswordHasher.Iterations,
            parallelism = Argon2IdPasswordHasher.Parallelism
        }));
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("userId", userId);
        update.Parameters.AddWithValue("legacy", legacy!);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task VerifyPasskeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        JsonElement proof,
        JsonElement storedIntent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!storedIntent.TryGetProperty("options", out var optionsElement))
            throw new AuthenticationFailureException();
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(proof.GetRawText())
            ?? throw new ArgumentException("Invalid WebAuthn assertion proof.");

        byte[] publicKey;
        uint signCount;
        await using (var credential = connection.CreateCommand())
        {
            credential.Transaction = transaction;
            credential.CommandText = """
                SELECT public_key,sign_count FROM passkeys
                WHERE credential_id=@id AND user_id=@userId AND revoked_at IS NULL FOR UPDATE;
                """;
            credential.Parameters.AddWithValue("id", response.RawId);
            credential.Parameters.AddWithValue("userId", userId);
            await using var reader = await credential.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException();
            publicKey = reader.GetFieldValue<byte[]>(0);
            signCount = checked((uint)reader.GetInt64(1));
        }

        var options = AssertionOptions.FromJson(optionsElement.GetRawText());
        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = response,
            OriginalOptions = options,
            StoredPublicKey = publicKey,
            StoredSignatureCounter = signCount,
            IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                args.CredentialId.AsSpan().SequenceEqual(response.RawId) &&
                (args.UserHandle.Length == 0 || args.UserHandle.AsSpan().SequenceEqual(userId.ToByteArray())))
        }, cancellationToken);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE passkeys SET sign_count=@counter,last_used_at=@now WHERE credential_id=@id AND user_id=@userId";
        update.Parameters.AddWithValue("counter", checked((long)result.SignCount));
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("id", response.RawId);
        update.Parameters.AddWithValue("userId", userId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateIntent(StepUpIntent intent)
    {
        if (intent.IdempotencyKey == Guid.Empty) throw new ArgumentException("A non-empty idempotency key is required.");
        if (intent.Intent == StepUpIntentType.Command && (intent.DeviceId is null || intent.CommandType is null))
            throw new ArgumentException("Command step-up must bind a device and command type.");
        if (intent.Intent != StepUpIntentType.Command && intent.CommandType is not null)
            throw new ArgumentException("Only command step-up may bind a command type.");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
