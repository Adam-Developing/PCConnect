namespace PCConnect.Core;

/// <summary>The principal an authenticated request resolves to, after every check.</summary>
public sealed record CallerIdentity
{
    public required long UserId { get; init; }

    public required Guid UserPublicId { get; init; }

    /// <summary>Set only for device tokens. A user token can never carry one.</summary>
    public long? DeviceId { get; init; }

    public Guid? DevicePublicId { get; init; }

    public required string ClientKind { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    public required string TokenId { get; init; }

    public bool IsDevice => DeviceId is not null;

    public bool Has(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    public void Require(string scope)
    {
        if (!Has(scope))
        {
            throw AppException.Forbidden(
                ErrorCodes.AuthScopeInsufficient,
                $"This token does not carry the '{scope}' scope.");
        }
    }
}

public sealed record AccessTokenRequest
{
    public required Guid Subject { get; init; }

    public required string ClientKind { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    public Guid? DeviceId { get; init; }

    public string? FamilyId { get; init; }
}

public sealed record IssuedAccessToken(string Token, string TokenId, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints and validates access tokens. Validation is exposed as well as minting
/// because the realtime hub authenticates its handshake with the same code path
/// as HTTP — the S2-06 failure was a second, weaker one.
/// </summary>
public interface ITokenIssuer
{
    IssuedAccessToken IssueAccessToken(AccessTokenRequest request);

    /// <summary>A short-lived, single-purpose token proving a recent step-up (ADR-0011).</summary>
    IssuedAccessToken IssueStepUpToken(Guid subject, string method);

    /// <summary>256 bits of entropy plus the SHA-256 the database stores.</summary>
    (string Token, byte[] Hash) CreateOpaqueToken();

    byte[] HashOpaqueToken(string token);

    TimeSpan AccessTokenLifetime { get; }

    TimeSpan RefreshTokenLifetime { get; }
}
