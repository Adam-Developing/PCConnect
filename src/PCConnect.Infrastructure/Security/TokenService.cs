using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PCConnect.Core;

namespace PCConnect.Infrastructure.Security;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "https://api.pcconnect.local";

    public string Audience { get; set; } = "pcconnect-api";

    /// <summary>
    /// PEM-encoded P-256 private key (SEC1 or PKCS#8). Injected as an environment
    /// variable at deploy; never a file in the repository (03 §7).
    /// </summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Previously-current public keys, by <c>kid</c>, kept for the overlap window
    /// so a rotation does not invalidate every live token at once.
    /// </summary>
    public Dictionary<string, string> RetiredPublicKeysPem { get; init; } = [];

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;

    public int StepUpTokenSeconds { get; set; } = 300;

    /// <summary>Tolerance for clock skew between the API and a client (ADR-0002).</summary>
    public int ClockSkewSeconds { get; set; } = 60;
}

public static class PcConnectClaims
{
    public const string ClientKind = "cid";
    public const string Scopes = "scp";
    public const string DeviceId = "did";
    public const string FamilyId = "fam";
    public const string StepUpMethod = "amr";
    public const string Purpose = "pur";
    public const string StepUpPurpose = "step_up";
}

/// <summary>
/// ES256 (ECDSA P-256) access tokens.
///
/// ADR-0002 specified EdDSA/Ed25519. .NET 10 has no in-box Ed25519 signer, and
/// adding a native libsodium dependency to a solo-maintained deployment costs
/// more than it buys here. ES256 keeps the properties the ADR was actually
/// after — small signatures, a pinned algorithm at the verifier, and no RSA
/// padding or `alg:none` confusion class — and is recorded as such in ADR-0009.
/// </summary>
public sealed class TokenService : ITokenIssuer, IDisposable
{
    private const string Algorithm = SecurityAlgorithms.EcdsaSha256;

    private readonly JwtOptions _options;
    private readonly IClock _clock;
    private readonly ECDsa _signingKey;
    private readonly ECDsaSecurityKey _securityKey;
    private readonly List<ECDsa> _retiredKeys = [];
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;

        if (string.IsNullOrWhiteSpace(_options.PrivateKeyPem))
        {
            throw new InvalidOperationException(
                "No JWT signing key configured. Set PCCONNECT_JWT__PRIVATEKEYPEM to a PEM-encoded P-256 private key.");
        }

        _signingKey = ECDsa.Create();
        _signingKey.ImportFromPem(_options.PrivateKeyPem);

        if (_signingKey.KeySize != 256)
        {
            throw new InvalidOperationException(
                $"JWT signing key must be P-256; the configured key is {_signingKey.KeySize}-bit.");
        }

        _securityKey = new ECDsaSecurityKey(_signingKey) { KeyId = ComputeKeyId(_signingKey) };
    }

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(_options.AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public IssuedAccessToken IssueAccessToken(AccessTokenRequest request)
    {
        var now = _clock.UtcNow;
        var expires = now.Add(AccessTokenLifetime);
        var jti = Guid.CreateVersion7().ToString("N");

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = request.Subject.ToString(),
            [JwtRegisteredClaimNames.Jti] = jti,
            [PcConnectClaims.ClientKind] = request.ClientKind,
            [PcConnectClaims.Scopes] = request.Scopes.ToArray(),
        };

        if (request.DeviceId is { } deviceId)
        {
            claims[PcConnectClaims.DeviceId] = deviceId.ToString();
        }

        if (!string.IsNullOrEmpty(request.FamilyId))
        {
            claims[PcConnectClaims.FamilyId] = request.FamilyId;
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(_securityKey, Algorithm),
        });

        return new IssuedAccessToken(token, jti, expires);
    }

    public IssuedAccessToken IssueStepUpToken(Guid subject, string method)
    {
        var now = _clock.UtcNow;
        var expires = now.AddSeconds(_options.StepUpTokenSeconds);
        var jti = Guid.CreateVersion7().ToString("N");

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = subject.ToString(),
                [JwtRegisteredClaimNames.Jti] = jti,
                [PcConnectClaims.Purpose] = PcConnectClaims.StepUpPurpose,
                [PcConnectClaims.StepUpMethod] = method,
            },
            SigningCredentials = new SigningCredentials(_securityKey, Algorithm),
        });

        return new IssuedAccessToken(token, jti, expires);
    }

    public (string Token, byte[] Hash) CreateOpaqueToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncoder.Encode(raw);
        return (token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public byte[] HashOpaqueToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// The validation parameters used by both HTTP and the realtime handshake.
    /// The algorithm is pinned: an attacker-chosen <c>alg</c> is rejected before
    /// any signature check runs.
    /// </summary>
    public TokenValidationParameters CreateValidationParameters()
    {
        var keys = new List<SecurityKey> { _securityKey };

        foreach (var (kid, pem) in _options.RetiredPublicKeysPem)
        {
            var retired = ECDsa.Create();
            retired.ImportFromPem(pem);
            _retiredKeys.Add(retired);
            keys.Add(new ECDsaSecurityKey(retired) { KeyId = kid });
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [Algorithm],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    }

    /// <summary>The public half, as JWKS, for any non-.NET verifier.</summary>
    public object BuildJwks()
    {
        var parameters = _signingKey.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "EC",
                    crv = "P-256",
                    use = "sig",
                    alg = "ES256",
                    kid = _securityKey.KeyId,
                    x = Base64UrlEncoder.Encode(parameters.Q.X!),
                    y = Base64UrlEncoder.Encode(parameters.Q.Y!),
                },
            },
        };
    }

    private static string ComputeKeyId(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        var material = new byte[parameters.Q.X!.Length + parameters.Q.Y!.Length];
        parameters.Q.X.CopyTo(material, 0);
        parameters.Q.Y.CopyTo(material, parameters.Q.X.Length);
        return Convert.ToHexString(SHA256.HashData(material))[..16].ToLowerInvariant();
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        foreach (var key in _retiredKeys)
        {
            key.Dispose();
        }
    }
}
