using PCConnect.Contracts.V2;

namespace PCConnect.Infrastructure.Identity;

public sealed record AuthenticatedSubject(Guid? UserId, Guid? SessionId, Guid? DeviceId, string SubjectKind);

public sealed class AuthenticationFailureException(string code = "invalid_credentials") : Exception("Authentication failed.")
{
    public string Code { get; } = code;
}

public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ResourceNotFoundException(string code = "not_found") : Exception("The requested resource was not found.")
{
    public string Code { get; } = code;
}

public sealed class ResourceGoneException(string code) : Exception("The requested resource is no longer available.")
{
    public string Code { get; } = code;
}

public sealed class RequestRateLimitedException(string code = "slow_down") : Exception("The request was made too soon.")
{
    public string Code { get; } = code;
}

public interface IAuthenticationService
{
    Task RegisterAsync(RegistrationRequest request, string correlationId, CancellationToken cancellationToken);
    Task<TokenPair> PasswordLoginAsync(PasswordLoginRequest request, string remoteAddress, string correlationId, CancellationToken cancellationToken);
    Task<TokenPair> RefreshUserSessionAsync(string refreshToken, string correlationId, CancellationToken cancellationToken);
    Task<DeviceTokenPair> RefreshDeviceAsync(string refreshToken, string correlationId, CancellationToken cancellationToken);
    Task LogoutAsync(Guid sessionId, string correlationId, CancellationToken cancellationToken);
    Task<AuthenticatedSubject?> AuthenticateAccessTokenAsync(string token, CancellationToken cancellationToken);
    Task<TokenPair> IssuePasskeySessionAsync(Guid userId, ClientDescriptor client, string correlationId, CancellationToken cancellationToken);
}
