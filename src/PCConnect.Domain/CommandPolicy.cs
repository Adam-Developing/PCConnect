using PCConnect.Contracts.V2;

namespace PCConnect.Domain.Commands;

public static class CommandPolicy
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);

    public static bool RequiresStepUp(CommandType command) => command is not CommandType.Lock;

    public static void Validate(CommandCreate request, IReadOnlyCollection<DeviceCapability> capabilities)
    {
        var seconds = request.ExpiresInSeconds ?? (int)DefaultLifetime.TotalSeconds;
        if (seconds is < 1 or > 300)
            throw new DomainException("invalid_command_expiry", "Command expiry must be between 1 and 300 seconds.");

        if (!Enum.TryParse<DeviceCapability>(request.Type.ToString(), out var capability) || !capabilities.Contains(capability))
            throw new DomainException("unsupported", "The target device does not advertise this capability.");

        if (request.Type == CommandType.Lock && request.ExplicitlyConfirmed is not true)
            throw new DomainException("confirmation_required", "Lock requires explicit confirmation.");
    }

    public static bool CanTransition(CommandStatus from, CommandStatus to) => (from, to) switch
    {
        (CommandStatus.Queued, CommandStatus.Claimed or CommandStatus.Expired or CommandStatus.Cancelled) => true,
        (CommandStatus.Claimed, CommandStatus.Queued or CommandStatus.Accepted or CommandStatus.Failed or CommandStatus.Expired) => true,
        (CommandStatus.Accepted, CommandStatus.Succeeded or CommandStatus.Failed) => true,
        _ => false
    };
}

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
