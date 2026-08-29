using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Npgsql;
using NpgsqlTypes;
using PCConnect.Contracts.V2;
using PCConnect.Domain;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Identity;

public interface IPasskeyService
{
    Task<WebAuthnOptions> CreateRegistrationOptionsAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
    Task<Passkey> CompleteRegistrationAsync(Guid userId, Guid sessionId, string stepUpGrant, WebAuthnCredential request, CancellationToken cancellationToken);
    Task<WebAuthnOptions> CreateAuthenticationOptionsAsync(PasskeyAuthenticationOptionsRequest request, CancellationToken cancellationToken);
    Task<TokenPair> CompleteAuthenticationAsync(WebAuthnCredential request, string correlationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Passkey>> ListAsync(Guid userId, CancellationToken cancellationToken);
    Task RemoveAsync(Guid userId, Guid sessionId, Guid passkeyId, string stepUpGrant, CancellationToken cancellationToken);
}

public sealed class PasskeyService(
    NpgsqlDataSource dataSource,
    IFido2 fido2,
    IOpaqueTokenService tokens,
    IClock clock,
    AuthenticationService authentication,
    StepUpGrantConsumer stepUp) : IPasskeyService
{
    public async Task<WebAuthnOptions> CreateRegistrationOptionsAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var user = await LoadFidoUserAsync(userId, cancellationToken);
        var existing = await LoadDescriptorsAsync(userId, cancellationToken);
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = existing,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None
        });
        var challengeId = await StoreChallengeAsync(userId, sessionId, "register", options.Challenge, new { options }, cancellationToken);
        return new(challengeId, ToElement(options));
    }

    public async Task<Passkey> CompleteRegistrationAsync(Guid userId, Guid sessionId, string stepUpGrant, WebAuthnCredential request, CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(request.Credential.GetRawText())
            ?? throw new ArgumentException("Invalid WebAuthn attestation response.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.SecurityChange, null, cancellationToken);
        var intent = await LoadChallengeIntentAsync(connection, transaction, request.ChallengeId, "register", userId, sessionId, cancellationToken);
        var optionsJson = intent.GetProperty("options").GetRawText();
        var options = CredentialCreateOptions.FromJson(optionsJson);

        var result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response,
            OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = async (args, cancellation) =>
            {
                await using var unique = connection.CreateCommand();
                unique.Transaction = transaction;
                unique.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM passkeys WHERE credential_id=@id)";
                unique.Parameters.AddWithValue("id", args.CredentialId);
                return (bool)(await unique.ExecuteScalarAsync(cancellation) ?? false);
            }
        }, cancellationToken);

        var id = Guid.CreateVersion7(now);
        var name = $"Passkey {await CountPasskeysAsync(connection, transaction, userId, cancellationToken) + 1}";
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO passkeys(id,user_id,credential_id,public_key,public_key_algorithm,sign_count,transports,display_name,created_at)
            VALUES(@id,@userId,@credentialId,@publicKey,@algorithm,@signCount,@transports,@name,@now);
            UPDATE webauthn_challenges SET consumed_at=@now WHERE id=@challengeId;
            """;
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("userId", userId);
        insert.Parameters.AddWithValue("credentialId", result.Id);
        insert.Parameters.AddWithValue("publicKey", result.PublicKey);
        insert.Parameters.AddWithValue("algorithm", CoseAlgorithm.Read(result.PublicKey));
        insert.Parameters.AddWithValue("signCount", checked((long)result.SignCount));
        insert.Parameters.AddWithValue("transports", result.Transports.Select(x => x.ToString().ToLowerInvariant()).ToArray());
        insert.Parameters.AddWithValue("name", name);
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("challengeId", request.ChallengeId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(id, name, now, null);
    }

    public async Task<WebAuthnOptions> CreateAuthenticationOptionsAsync(PasskeyAuthenticationOptionsRequest request, CancellationToken cancellationToken)
    {
        Guid? userId = null;
        IReadOnlyList<PublicKeyCredentialDescriptor> allowed = [];
        if (!string.IsNullOrWhiteSpace(request.LoginHint))
        {
            await using var lookup = dataSource.CreateCommand("SELECT id FROM users WHERE (lower(username::text)=@login OR lower(email::text)=@login) AND account_state='active'");
            lookup.Parameters.AddWithValue("login", Normalization.AccountIdentifier(request.LoginHint));
            if (await lookup.ExecuteScalarAsync(cancellationToken) is Guid found)
            {
                userId = found;
                allowed = await LoadDescriptorsAsync(found, cancellationToken);
            }
        }
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Preferred
        });
        var challengeId = await StoreChallengeAsync(userId, null, "authenticate", options.Challenge, new { options, client = request.Client }, cancellationToken);
        return new(challengeId, ToElement(options));
    }

    public async Task<TokenPair> CompleteAuthenticationAsync(WebAuthnCredential request, string correlationId, CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(request.Credential.GetRawText())
            ?? throw new ArgumentException("Invalid WebAuthn assertion response.");
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        Guid userId;
        byte[] publicKey;
        uint signCount;
        await using (var credential = connection.CreateCommand())
        {
            credential.Transaction = transaction;
            credential.CommandText = """
                SELECT p.user_id,p.public_key,p.sign_count FROM passkeys p JOIN users u ON u.id=p.user_id
                WHERE p.credential_id=@id AND p.revoked_at IS NULL AND u.account_state='active' FOR UPDATE OF p;
                """;
            credential.Parameters.AddWithValue("id", response.RawId);
            await using var reader = await credential.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new AuthenticationFailureException();
            userId = reader.GetGuid(0);
            publicKey = reader.GetFieldValue<byte[]>(1);
            signCount = checked((uint)reader.GetInt64(2));
        }

        var intent = await LoadChallengeIntentAsync(connection, transaction, request.ChallengeId, "authenticate", userId, null, cancellationToken, allowNullStoredUser: true);
        var options = AssertionOptions.FromJson(intent.GetProperty("options").GetRawText());
        var client = intent.GetProperty("client").Deserialize<ClientDescriptor>() ?? throw new InvalidOperationException("Passkey ceremony client descriptor is missing.");
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
        update.CommandText = """
            UPDATE passkeys SET sign_count=@counter,last_used_at=@now WHERE credential_id=@id AND user_id=@userId;
            UPDATE webauthn_challenges SET consumed_at=@now WHERE id=@challengeId;
            """;
        update.Parameters.AddWithValue("counter", checked((long)result.SignCount));
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("id", response.RawId);
        update.Parameters.AddWithValue("userId", userId);
        update.Parameters.AddWithValue("challengeId", request.ChallengeId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        var session = await authentication.CreateUserSessionAsync(connection, transaction, userId, client, correlationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<Passkey>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id,display_name,created_at,last_used_at FROM passkeys WHERE user_id=@userId AND revoked_at IS NULL ORDER BY created_at");
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Passkey>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    public async Task RemoveAsync(Guid userId, Guid sessionId, Guid passkeyId, string stepUpGrant, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await stepUp.ConsumeAsync(connection, transaction, userId, sessionId, stepUpGrant, StepUpIntentType.SecurityChange, null, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE passkeys SET revoked_at=@now WHERE id=@id AND user_id=@userId AND revoked_at IS NULL RETURNING id";
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("id", passkeyId);
        command.Parameters.AddWithValue("userId", userId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null) throw new ResourceNotFoundException("passkey_not_found");
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Fido2User> LoadFidoUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT username::text,display_name FROM users WHERE id=@id AND account_state='active'");
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ResourceNotFoundException("profile_not_found");
        return new Fido2User { Id = userId.ToByteArray(), Name = reader.GetString(0), DisplayName = reader.GetString(1) };
    }

    private async Task<IReadOnlyList<PublicKeyCredentialDescriptor>> LoadDescriptorsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT credential_id FROM passkeys WHERE user_id=@userId AND revoked_at IS NULL");
        command.Parameters.AddWithValue("userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PublicKeyCredentialDescriptor>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new PublicKeyCredentialDescriptor(reader.GetFieldValue<byte[]>(0)));
        return result;
    }

    private async Task<Guid> StoreChallengeAsync(Guid? userId, Guid? sessionId, string purpose, byte[] challenge, object intent, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var id = Guid.CreateVersion7(now);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO webauthn_challenges(id,user_id,session_id,purpose,challenge_hash,intent,created_at,expires_at)
            VALUES(@id,@userId,@sessionId,@purpose,@hash,@intent::jsonb,@now,@expires);
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new("userId", NpgsqlDbType.Uuid) { Value = userId is null ? DBNull.Value : userId.Value });
        command.Parameters.Add(new("sessionId", NpgsqlDbType.Uuid) { Value = sessionId is null ? DBNull.Value : sessionId.Value });
        command.Parameters.AddWithValue("purpose", purpose);
        command.Parameters.AddWithValue("hash", tokens.Hash(Base64Url(challenge)));
        command.Parameters.AddWithValue("intent", JsonSerializer.Serialize(intent));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", now.AddMinutes(5));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private async Task<JsonElement> LoadChallengeIntentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string purpose,
        Guid userId, Guid? sessionId, CancellationToken cancellationToken, bool allowNullStoredUser = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent FROM webauthn_challenges WHERE id=@id AND purpose=@purpose AND consumed_at IS NULL AND expires_at>@now
              AND ((@allowNull AND (user_id IS NULL OR user_id=@userId)) OR (NOT @allowNull AND user_id=@userId))
              AND (@sessionId IS NULL OR session_id=@sessionId) FOR UPDATE;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("purpose", purpose);
        command.Parameters.AddWithValue("now", clock.UtcNow);
        command.Parameters.AddWithValue("allowNull", allowNullStoredUser);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.Add(new("sessionId", NpgsqlDbType.Uuid) { Value = sessionId is null ? DBNull.Value : sessionId.Value });
        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (raw is null) throw new ResourceGoneException("webauthn_challenge_expired");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static async Task<int> CountPasskeysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM passkeys WHERE user_id=@userId AND revoked_at IS NULL";
        command.Parameters.AddWithValue("userId", userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class CoseAlgorithm
{
    public static int Read(ReadOnlySpan<byte> coseKey)
    {
        var offset = 0;
        var entries = checked((int)ReadLength(coseKey, ref offset, 5));
        for (var index = 0; index < entries; index++)
        {
            var key = ReadInteger(coseKey, ref offset);
            if (key == 3) return checked((int)ReadInteger(coseKey, ref offset));
            Skip(coseKey, ref offset);
        }
        throw new CryptographicException("COSE public key does not declare an algorithm.");
    }

    private static long ReadInteger(ReadOnlySpan<byte> value, ref int offset)
    {
        var initial = Take(value, ref offset);
        var major = initial >> 5;
        var number = ReadAdditional(value, ref offset, initial & 31);
        return major switch { 0 => checked((long)number), 1 => checked(-1L - (long)number), _ => throw new CryptographicException("Expected a CBOR integer.") };
    }

    private static ulong ReadLength(ReadOnlySpan<byte> value, ref int offset, int expectedMajor)
    {
        var initial = Take(value, ref offset);
        if (initial >> 5 != expectedMajor) throw new CryptographicException("Unexpected COSE CBOR type.");
        return ReadAdditional(value, ref offset, initial & 31);
    }

    private static void Skip(ReadOnlySpan<byte> value, ref int offset)
    {
        var initial = Take(value, ref offset);
        var major = initial >> 5;
        var additional = initial & 31;
        if (major is 0 or 1 or 7) { _ = ReadAdditional(value, ref offset, additional); return; }
        var length = ReadAdditional(value, ref offset, additional);
        if (major is 2 or 3) { offset = checked(offset + (int)length); if (offset > value.Length) throw new CryptographicException("Truncated COSE key."); return; }
        if (major == 4) { for (ulong index = 0; index < length; index++) Skip(value, ref offset); return; }
        if (major == 5) { for (ulong index = 0; index < length; index++) { Skip(value, ref offset); Skip(value, ref offset); } return; }
        if (major == 6) { Skip(value, ref offset); return; }
        throw new CryptographicException("Unsupported COSE CBOR value.");
    }

    private static ulong ReadAdditional(ReadOnlySpan<byte> value, ref int offset, int additional) => additional switch
    {
        < 24 => (ulong)additional,
        24 => Take(value, ref offset),
        25 => ((ulong)Take(value, ref offset) << 8) | Take(value, ref offset),
        26 => ((ulong)Take(value, ref offset) << 24) | ((ulong)Take(value, ref offset) << 16) | ((ulong)Take(value, ref offset) << 8) | Take(value, ref offset),
        _ => throw new CryptographicException("Unsupported or indefinite COSE CBOR length.")
    };

    private static byte Take(ReadOnlySpan<byte> value, ref int offset)
    {
        if ((uint)offset >= (uint)value.Length) throw new CryptographicException("Truncated COSE key.");
        return value[offset++];
    }
}
