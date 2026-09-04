using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Data;

namespace PCConnect.Infrastructure.Contexts.Identity;

public sealed class WebAuthnOptions
{
    /// <summary>
    /// The relying party id: the registrable domain the credential is bound to.
    /// A passkey created for one RP id cannot be used against another, which is
    /// what makes WebAuthn phishing-resistant.
    /// </summary>
    public string RelyingPartyId { get; set; } = "localhost";

    public string RelyingPartyName { get; set; } = "PCConnect";

    /// <summary>
    /// Origins allowed to complete a ceremony. Checked exactly, never by suffix:
    /// a suffix check would accept <c>evil-pcconnect.example</c>.
    /// </summary>
    public List<string> AllowedOrigins { get; init; } = ["http://localhost:5173"];

    public int ChallengeTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// WebAuthn level 2 registration and assertion, implemented directly against
/// <c>System.Formats.Cbor</c> and <c>System.Security.Cryptography</c>.
///
/// Attestation is requested as <c>none</c> and not verified: this is a consumer
/// product with no authenticator allow-list, so an attestation statement would
/// be collected and ignored. Stated rather than implied (ADR-0010).
/// </summary>
public sealed class WebAuthnService(
    Db db,
    IClock clock,
    ITokenIssuer tokens,
    SecurityEventLog audit,
    IOptions<WebAuthnOptions> options)
{
    private const int Es256 = -7;
    private const int Rs256 = -257;
    private const int EdDsa = -8;

    private readonly WebAuthnOptions _options = options.Value;

    // ── registration ─────────────────────────────────────────────────────────

    public async Task<PasskeyRegistrationOptions> BeginRegistrationAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);

        var user = await IdentityService.LoadUserAsync(connection, null, userId, ct)
            ?? throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such account.");

        var existing = (await connection.QueryAsync<byte[]>(new CommandDefinition(
            "SELECT credential_id FROM webauthn_credentials WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = userId }, cancellationToken: ct))).ToList();

        var challenge = RandomNumberGenerator.GetBytes(32);
        var challengeId = await StoreChallengeAsync(connection, challenge, "registration", userId, ct);

        return new PasskeyRegistrationOptions(
            challengeId,
            Base64Url(challenge),
            new RelyingParty(_options.RelyingPartyId, _options.RelyingPartyName),
            new PasskeyUser(Base64Url(user.PublicId.ToByteArray()), user.Username, user.DisplayName),
            [new PublicKeyCredentialParameter("public-key", Es256), new PublicKeyCredentialParameter("public-key", Rs256)],
            existing.Select(id => new PublicKeyCredentialDescriptor("public-key", Base64Url(id))).ToList(),
            new AuthenticatorSelection(null, "preferred", false, "preferred"),
            _options.ChallengeTimeoutSeconds * 1000,
            "none");
    }

    public async Task<PasskeySummary> CompleteRegistrationAsync(
        long userId, PasskeyRegistrationRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);

        var challenge = await ConsumeChallengeAsync(connection, request.ChallengeId, "registration", userId, ct);
        var clientData = ParseClientData(request.ClientDataJson, "webauthn.create", challenge);
        _ = clientData;

        var attestation = FromBase64Url(request.AttestationObject);
        var authData = ExtractAuthenticatorData(attestation);
        var parsed = ParseAuthenticatorData(authData);

        VerifyRpIdHash(parsed.RpIdHash);

        if (!parsed.UserPresent)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "The authenticator did not report user presence.");
        }

        if (parsed.CredentialId is null || parsed.CosePublicKey is null)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "The attestation did not contain a credential.");
        }

        // Reject a key we cannot verify with at registration time rather than
        // discovering it at the first sign-in attempt.
        _ = ImportCoseKey(parsed.CosePublicKey);

        var credentialId = parsed.CredentialId;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? "Passkey"
            : request.DisplayName.Trim()[..Math.Min(request.DisplayName.Trim().Length, 128)];

        try
        {
            var publicId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO webauthn_credentials
                    (user_id, credential_id, public_key_cose, signature_counter, aaguid,
                     transports, is_backup_eligible, is_uv_capable, display_name)
                VALUES
                    (@UserId, @CredentialId, @PublicKey, @Counter, @Aaguid,
                     @Transports, @BackupEligible, @UvCapable, @DisplayName)
                RETURNING public_id
                """,
                new
                {
                    UserId = userId,
                    CredentialId = credentialId,
                    PublicKey = parsed.CosePublicKey,
                    Counter = (long)parsed.SignCount,
                    Aaguid = parsed.Aaguid,
                    Transports = string.Join(',', request.Transports ?? []),
                    BackupEligible = parsed.BackupEligible,
                    UvCapable = parsed.UserVerified,
                    DisplayName = displayName,
                }, cancellationToken: ct));

            await audit.WriteAsync(userId, SecurityEventNames.PasskeyRegistered, true, ctx,
                new { credentialId = Base64Url(credentialId) }, ct);

            return new PasskeySummary(publicId.ToString(), displayName, clock.UtcNow, null, parsed.BackupState);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            throw AppException.Conflict(ErrorCodes.PasskeyVerificationFailed, "That passkey is already registered.");
        }
    }

    // ── assertion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Begins an assertion. <paramref name="userId"/> is null for a usernameless
    /// sign-in, in which case the credential itself identifies the account.
    /// </summary>
    public async Task<PasskeyAssertionOptions> BeginAssertionAsync(long? userId, string ceremony, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);

        var allow = new List<PublicKeyCredentialDescriptor>();
        if (userId is { } id)
        {
            var credentials = await connection.QueryAsync<byte[]>(new CommandDefinition(
                "SELECT credential_id FROM webauthn_credentials WHERE user_id = @UserId AND revoked_at IS NULL",
                new { UserId = id }, cancellationToken: ct));

            allow.AddRange(credentials.Select(c => new PublicKeyCredentialDescriptor("public-key", Base64Url(c))));

            if (allow.Count == 0)
            {
                throw AppException.NotFound(ErrorCodes.PasskeyUnknownCredential,
                    "This account has no passkey registered.");
            }
        }

        var challenge = RandomNumberGenerator.GetBytes(32);
        var challengeId = await StoreChallengeAsync(connection, challenge, ceremony, userId, ct);

        return new PasskeyAssertionOptions(
            challengeId,
            Base64Url(challenge),
            _options.RelyingPartyId,
            allow,
            _options.ChallengeTimeoutSeconds * 1000,
            // Step-up is the point at which we insist on a PIN or biometric: it
            // is what makes "a stolen unlocked phone cannot shut down your PC"
            // true rather than aspirational (ADR-0011).
            ceremony == "step_up" ? "required" : "preferred");
    }

    public sealed record AssertionResult(long UserId, Guid UserPublicId, long CredentialRowId, bool UserVerified);

    public async Task<AssertionResult> CompleteAssertionAsync(
        PasskeyAssertionRequest request, string ceremony, long? expectedUserId, RequestContext ctx, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);

        var challenge = await ConsumeChallengeAsync(connection, request.ChallengeId, ceremony, expectedUserId, ct);
        ParseClientData(request.ClientDataJson, "webauthn.get", challenge);

        var credentialId = FromBase64Url(request.CredentialId);
        var credential = await connection.QuerySingleOrDefaultAsync<CredentialRow>(new CommandDefinition("""
            SELECT w.id AS Id, w.user_id AS UserId, u.public_id AS UserPublicId,
                   w.public_key_cose AS PublicKeyCose, w.signature_counter AS SignatureCounter
              FROM webauthn_credentials w
              JOIN users u ON u.id = w.user_id
             WHERE w.credential_id = @CredentialId AND w.revoked_at IS NULL AND u.deleted_at IS NULL
            """, new { CredentialId = credentialId }, cancellationToken: ct));

        if (credential is null || (expectedUserId is { } expected && credential.UserId != expected))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyUnknownCredential, "That passkey is not recognised.");
        }

        var authenticatorData = FromBase64Url(request.AuthenticatorData);
        var parsed = ParseAuthenticatorData(authenticatorData);
        VerifyRpIdHash(parsed.RpIdHash);

        if (!parsed.UserPresent)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "The authenticator did not report user presence.");
        }

        if (ceremony == "step_up" && !parsed.UserVerified)
        {
            throw AppException.Unauthorized(ErrorCodes.AuthStepUpInvalid,
                "Confirming this action needs your device PIN, fingerprint or face.");
        }

        // The signed payload is authenticatorData || SHA-256(clientDataJSON).
        var clientDataHash = SHA256.HashData(FromBase64Url(request.ClientDataJson));
        var signedPayload = new byte[authenticatorData.Length + clientDataHash.Length];
        authenticatorData.CopyTo(signedPayload, 0);
        clientDataHash.CopyTo(signedPayload, authenticatorData.Length);

        if (!VerifySignature(credential.PublicKeyCose, signedPayload, FromBase64Url(request.Signature)))
        {
            await audit.WriteAsync(credential.UserId, SecurityEventNames.StepUpFailed, false, ctx,
                new { reason = "bad_signature" }, ct);
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "That passkey could not be verified.");
        }

        // A counter that fails to advance on an authenticator that uses counters
        // means the credential has been cloned. Refuse and flag it.
        if (parsed.SignCount > 0 && parsed.SignCount <= credential.SignatureCounter)
        {
            await audit.WriteAsync(credential.UserId, SecurityEventNames.PasskeyCounterRegressed, false, ctx,
                new { stored = credential.SignatureCounter, presented = parsed.SignCount }, ct);
            throw AppException.Unauthorized(ErrorCodes.PasskeyCounterRegressed,
                "That passkey reported an out-of-order counter and has been rejected.");
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE webauthn_credentials
               SET signature_counter = @Counter, last_used_at = now()
             WHERE id = @Id
            """, new { Counter = (long)parsed.SignCount, credential.Id }, cancellationToken: ct));

        await audit.WriteAsync(credential.UserId, SecurityEventNames.PasskeyUsed, true, ctx, new { ceremony }, ct);

        return new AssertionResult(credential.UserId, credential.UserPublicId, credential.Id, parsed.UserVerified);
    }

    public async Task<IReadOnlyList<PasskeySummary>> ListAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<PasskeyRow>(new CommandDefinition("""
            SELECT public_id AS PublicId, display_name AS DisplayName, created_at AS CreatedAt,
                   last_used_at AS LastUsedAt, is_backup_eligible AS IsBackupEligible
              FROM webauthn_credentials
             WHERE user_id = @UserId AND revoked_at IS NULL
             ORDER BY created_at DESC
            """, new { UserId = userId }, cancellationToken: ct));

        return rows.Select(r => new PasskeySummary(
            r.PublicId.ToString(), r.DisplayName, r.CreatedAt, r.LastUsedAt, r.IsBackupEligible)).ToList();
    }

    public async Task RevokeAsync(long userId, Guid passkeyId, RequestContext ctx, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE webauthn_credentials SET revoked_at = now()
             WHERE user_id = @UserId AND public_id = @PublicId AND revoked_at IS NULL
            """, new { UserId = userId, PublicId = passkeyId }, cancellationToken: ct));

        if (affected == 0)
        {
            throw AppException.NotFound(ErrorCodes.PasskeyUnknownCredential, "No such passkey.");
        }

        await audit.WriteAsync(userId, SecurityEventNames.PasskeyRevoked, true, ctx, new { passkeyId }, ct);
    }

    public async Task<bool> HasPasskeyAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM webauthn_credentials WHERE user_id = @UserId AND revoked_at IS NULL)",
            new { UserId = userId }, cancellationToken: ct));
    }

    // ── challenge storage ────────────────────────────────────────────────────

    private async Task<string> StoreChallengeAsync(
        Npgsql.NpgsqlConnection connection, byte[] challenge, string ceremony, long? userId, CancellationToken ct)
    {
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO webauthn_challenges (challenge, ceremony, user_id, expires_at)
            VALUES (@Challenge, @Ceremony, @UserId, @ExpiresAt)
            RETURNING id
            """,
            new
            {
                Challenge = challenge,
                Ceremony = ceremony,
                UserId = userId,
                ExpiresAt = clock.UtcNow.AddSeconds(_options.ChallengeTimeoutSeconds),
            }, cancellationToken: ct));

        // The id is signed into an opaque handle so a caller cannot enumerate or
        // guess another ceremony's row.
        return $"{id}.{Base64Url(SHA256.HashData(challenge))[..16]}";
    }

    private async Task<byte[]> ConsumeChallengeAsync(
        Npgsql.NpgsqlConnection connection, string challengeId, string ceremony, long? userId, CancellationToken ct)
    {
        var dot = challengeId?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (dot <= 0 || !long.TryParse(challengeId![..dot], out var id))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyChallengeInvalid, "That sign-in attempt is no longer valid.");
        }

        var row = await connection.QuerySingleOrDefaultAsync<ChallengeRow>(new CommandDefinition("""
            SELECT id, challenge, ceremony, user_id AS UserId, expires_at AS ExpiresAt, consumed_at AS ConsumedAt
              FROM webauthn_challenges WHERE id = @Id
            """, new { Id = id }, cancellationToken: ct));

        if (row is null ||
            row.ConsumedAt is not null ||
            row.ExpiresAt <= clock.UtcNow ||
            !string.Equals(row.Ceremony, ceremony, StringComparison.Ordinal) ||
            (userId is not null && row.UserId is not null && row.UserId != userId) ||
            !string.Equals(challengeId[(dot + 1)..], Base64Url(SHA256.HashData(row.Challenge))[..16], StringComparison.Ordinal))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyChallengeInvalid, "That sign-in attempt is no longer valid.");
        }

        var consumed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE webauthn_challenges SET consumed_at = now() WHERE id = @Id AND consumed_at IS NULL",
            new { Id = id }, cancellationToken: ct));

        if (consumed == 0)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyChallengeInvalid, "That sign-in attempt was already used.");
        }

        return row.Challenge;
    }

    // ── parsing and verification ─────────────────────────────────────────────

    private sealed record ClientData(string Type, string Challenge, string Origin);

    private ClientData ParseClientData(string clientDataJsonB64, string expectedType, byte[] expectedChallenge)
    {
        ClientData? data;
        try
        {
            var json = Encoding.UTF8.GetString(FromBase64Url(clientDataJsonB64));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            data = new ClientData(
                root.GetProperty("type").GetString() ?? string.Empty,
                root.GetProperty("challenge").GetString() ?? string.Empty,
                root.GetProperty("origin").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "The passkey response was malformed.");
        }

        if (!string.Equals(data.Type, expectedType, StringComparison.Ordinal))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "The passkey response was for a different kind of ceremony.");
        }

        if (!CryptographicOperations.FixedTimeEquals(FromBase64Url(data.Challenge), expectedChallenge))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyChallengeInvalid,
                "The passkey response did not answer the challenge that was issued.");
        }

        if (!_options.AllowedOrigins.Contains(data.Origin, StringComparer.Ordinal))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "The passkey response came from an origin this server does not accept.");
        }

        return data;
    }

    private void VerifyRpIdHash(byte[] rpIdHash)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_options.RelyingPartyId));
        if (!CryptographicOperations.FixedTimeEquals(rpIdHash, expected))
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "That passkey belongs to a different site.");
        }
    }

    internal sealed record AuthenticatorData(
        byte[] RpIdHash,
        bool UserPresent,
        bool UserVerified,
        bool BackupEligible,
        bool BackupState,
        uint SignCount,
        Guid? Aaguid,
        byte[]? CredentialId,
        byte[]? CosePublicKey);

    internal static AuthenticatorData ParseAuthenticatorData(byte[] data)
    {
        if (data.Length < 37)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "Authenticator data was truncated.");
        }

        var rpIdHash = data[..32];
        var flags = data[32];
        var signCount = (uint)((data[33] << 24) | (data[34] << 16) | (data[35] << 8) | data[36]);

        var userPresent = (flags & 0x01) != 0;
        var userVerified = (flags & 0x04) != 0;
        var backupEligible = (flags & 0x08) != 0;
        var backupState = (flags & 0x10) != 0;
        var attestedCredentialData = (flags & 0x40) != 0;

        if (!attestedCredentialData)
        {
            return new AuthenticatorData(rpIdHash, userPresent, userVerified, backupEligible, backupState, signCount, null, null, null);
        }

        if (data.Length < 55)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "Attested credential data was truncated.");
        }

        var aaguid = new Guid(data.AsSpan(37, 16), bigEndian: true);
        var credentialIdLength = (data[53] << 8) | data[54];

        if (data.Length < 55 + credentialIdLength)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "Credential id was truncated.");
        }

        var credentialId = data[55..(55 + credentialIdLength)];
        var cose = data[(55 + credentialIdLength)..];

        return new AuthenticatorData(rpIdHash, userPresent, userVerified, backupEligible, backupState,
            signCount, aaguid, credentialId, cose);
    }

    /// <summary>Pulls <c>authData</c> out of the CBOR attestation object.</summary>
    internal static byte[] ExtractAuthenticatorData(byte[] attestationObject)
    {
        try
        {
            var reader = new CborReader(attestationObject, CborConformanceMode.Lax);
            var count = reader.ReadStartMap();

            for (var i = 0; i < (count ?? 0); i++)
            {
                var key = reader.ReadTextString();
                if (string.Equals(key, "authData", StringComparison.Ordinal))
                {
                    return reader.ReadByteString();
                }

                reader.SkipValue();
            }
        }
        catch (CborContentException)
        {
            // fall through to the shared error below
        }
        catch (InvalidOperationException)
        {
            // fall through to the shared error below
        }

        throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
            "The attestation object did not contain authenticator data.");
    }

    private sealed record CoseKey(int Algorithm, ECParameters? Ec, RSAParameters? Rsa);

    internal static bool VerifySignature(byte[] cosePublicKey, byte[] payload, byte[] signature)
    {
        var key = ImportCoseKey(cosePublicKey);

        if (key.Ec is { } ec)
        {
            using var ecdsa = ECDsa.Create(ec);
            // WebAuthn ES256 signatures are DER-encoded, not raw r||s.
            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        if (key.Rsa is { } rsa)
        {
            using var rsaKey = RSA.Create(rsa);
            return rsaKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return false;
    }

    private static CoseKey ImportCoseKey(byte[] cose)
    {
        var reader = new CborReader(cose, CborConformanceMode.Lax);
        int? kty = null, alg = null, crv = null;
        byte[]? x = null, y = null, n = null, e = null;

        try
        {
            var count = reader.ReadStartMap();
            for (var i = 0; i < (count ?? 0); i++)
            {
                var label = reader.ReadInt32();
                switch (label)
                {
                    case 1: kty = reader.ReadInt32(); break;
                    case 3: alg = reader.ReadInt32(); break;
                    case -1 when kty == 2: crv = reader.ReadInt32(); break;
                    case -1 when kty == 3: n = reader.ReadByteString(); break;
                    case -2 when kty == 3: e = reader.ReadByteString(); break;
                    case -2: x = reader.ReadByteString(); break;
                    case -3: y = reader.ReadByteString(); break;
                    default: reader.SkipValue(); break;
                }
            }
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or FormatException)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "The credential public key was malformed.");
        }

        if (alg == EdDsa)
        {
            // .NET 10 has no in-box Ed25519 verifier. Rather than pull in a native
            // dependency for the small number of authenticators that prefer it,
            // Ed25519 keys are refused at registration with a clear message.
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
                "This authenticator uses Ed25519, which this server does not yet support. Use a different passkey.");
        }

        if (kty == 2 && crv == 1 && x is not null && y is not null)
        {
            return new CoseKey(Es256, new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            }, null);
        }

        if (kty == 3 && n is not null && e is not null)
        {
            return new CoseKey(Rs256, null, new RSAParameters { Modulus = n, Exponent = e });
        }

        throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed,
            "This authenticator uses a key type this server does not support.");
    }

    internal static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string value)
    {
        var normalised = value.Replace('-', '+').Replace('_', '/');
        normalised += (normalised.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            return Convert.FromBase64String(normalised);
        }
        catch (FormatException)
        {
            throw AppException.Unauthorized(ErrorCodes.PasskeyVerificationFailed, "A passkey field was not valid base64url.");
        }
    }

    private sealed record ChallengeRow
    {
        public long Id { get; init; }
        public byte[] Challenge { get; init; } = [];
        public string Ceremony { get; init; } = string.Empty;
        public long? UserId { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? ConsumedAt { get; init; }
    }

    private sealed record CredentialRow
    {
        public long Id { get; init; }
        public long UserId { get; init; }
        public Guid UserPublicId { get; init; }
        public byte[] PublicKeyCose { get; init; } = [];
        public long SignatureCounter { get; init; }
    }

    private sealed record PasskeyRow
    {
        public Guid PublicId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public bool IsBackupEligible { get; init; }
    }

    internal ITokenIssuer Tokens => tokens;
}
