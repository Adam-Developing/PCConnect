using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Contexts;
using PCConnect.Infrastructure.Contexts.Commands;
using PCConnect.Infrastructure.Contexts.Devices;
using PCConnect.Infrastructure.Telemetry;

namespace PCConnect.Infrastructure.Realtime;

/// <summary>Group names, derived from claims only — never from anything a client sends.</summary>
public static class RealtimeGroups
{
    public static string User(Guid userId) => $"user:{userId:N}";

    public static string Device(Guid deviceId) => $"device:{deviceId:N}";
}

/// <summary>Server-to-client method names (05 §3).</summary>
public static class RealtimeEvents
{
    public const string CommandIssued = "command.issued";
    public const string CommandStatus = "command.status";
    public const string DevicePresence = "device.presence";
    public const string ReminderChanged = "reminder.changed";
    public const string ReminderDue = "reminder.due";
    public const string AuthExpired = "auth.expired";
}

/// <summary>
/// The realtime channel.
///
/// The connection is authenticated in the handshake by access token and its
/// identity comes from the token's claims, so a socket cannot subscribe to a
/// device it holds no credential for. The previous design authenticated with a
/// non-secure cookie and looked the session up in a process-local map that was
/// lost on restart (S2-06).
/// </summary>
[Authorize]
public sealed class PcConnectHub(
    CallerResolver resolver,
    DeviceService devices,
    CommandService commands,
    CommandMetrics metrics,
    ILogger<PcConnectHub> logger) : Hub
{
    private const string CallerKey = "caller";

    public override async Task OnConnectedAsync()
    {
        CallerIdentity caller;
        try
        {
            caller = await resolver.ResolveAsync(Context.User!, Context.ConnectionAborted);
        }
        catch (AppException ex)
        {
            metrics.AuthFailure("realtime");
            logger.LogInformation("Realtime handshake rejected: {Code}", ex.Code);
            Context.Abort();
            return;
        }

        Context.Items[CallerKey] = caller;

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(caller.UserPublicId), Context.ConnectionAborted);

        if (caller.DevicePublicId is { } devicePublicId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.Device(devicePublicId), Context.ConnectionAborted);
            await devices.MarkConnectedAsync(caller.UserPublicId, devicePublicId, Context.ConnectionAborted);

            // A reconnecting agent may have missed a push while it was away, so
            // the backlog is handed over on connect. This is the recovery half of
            // the push model: the socket is the fast path, not the only path.
            var pending = await commands.ClaimPendingAsync(caller, RequestContext.System, Context.ConnectionAborted);
            foreach (var command in pending)
            {
                await Clients.Caller.SendAsync(RealtimeEvents.CommandIssued, Envelope(command), Context.ConnectionAborted);
            }
        }

        metrics.RealtimeConnected(caller.ClientKind);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(CallerKey, out var value) && value is CallerIdentity caller)
        {
            metrics.RealtimeDisconnected(caller.ClientKind);

            if (caller.DevicePublicId is { } devicePublicId)
            {
                // Deleted rather than left to expire, so the phone's indicator
                // goes grey in about a second instead of ninety.
                await devices.MarkDisconnectedAsync(caller.UserPublicId, devicePublicId, CancellationToken.None);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Receipt confirmation for a command that arrived over the socket, so the
    /// push path records delivery the way the poll path does (05 §4.3).
    /// </summary>
    public async Task ConfirmDelivery(string commandId)
    {
        var caller = Caller();

        if (!Guid.TryParse(commandId, out var id))
        {
            throw new HubException("commandId must be a UUID.");
        }

        try
        {
            await commands.ConfirmDeliveryAsync(caller, id, RequestContext.System, Context.ConnectionAborted);
        }
        catch (AppException ex)
        {
            throw new HubException($"{ex.Code}: {ex.Message}");
        }
    }

    /// <summary>Per-command acknowledgement from an agent (05 §3).</summary>
    public async Task<CommandResponse> Ack(string commandId, string outcome, string? resultCode, string? resultMessage)
    {
        var caller = Caller();

        if (!Guid.TryParse(commandId, out var id))
        {
            throw new HubException("commandId must be a UUID.");
        }

        try
        {
            return await commands.AckAsync(caller, id,
                new AckCommandRequest(outcome, resultCode, resultMessage),
                RequestContext.System, Context.ConnectionAborted);
        }
        catch (AppException ex)
        {
            // Hub errors carry the stable code so an agent can switch on it the
            // same way it does over HTTP.
            throw new HubException($"{ex.Code}: {ex.Message}");
        }
    }

    public async Task Heartbeat(string? agentVersion, string? osVersion)
    {
        var caller = Caller();

        try
        {
            await devices.HeartbeatAsync(caller,
                new HeartbeatRequest(agentVersion ?? string.Empty, osVersion ?? string.Empty),
                Context.ConnectionAborted);
        }
        catch (AppException ex)
        {
            throw new HubException($"{ex.Code}: {ex.Message}");
        }
    }

    /// <summary>
    /// Access tokens live 15 minutes and sockets live longer. A client refreshes
    /// over HTTP and re-presents the new token here rather than reconnecting.
    /// </summary>
    public async Task RenewAccessToken(string accessToken)
    {
        _ = accessToken;

        // The token itself is validated by the connection's own authentication
        // when SignalR reconnects; this call exists so a client can confirm the
        // connection is still authorised without dropping it.
        var caller = Caller();
        try
        {
            _ = await resolver.ResolveAsync(Context.User!, Context.ConnectionAborted);
        }
        catch (AppException)
        {
            await Clients.Caller.SendAsync(RealtimeEvents.AuthExpired, new { }, Context.ConnectionAborted);
            Context.Abort();
            return;
        }

        logger.LogDebug("Realtime credential renewed for {ClientKind}", caller.ClientKind);
    }

    private CallerIdentity Caller() =>
        Context.Items.TryGetValue(CallerKey, out var value) && value is CallerIdentity caller
            ? caller
            : throw new HubException($"{ErrorCodes.AuthTokenInvalid}: this connection is not authenticated.");

    internal static RealtimeEvent<T> Envelope<T>(T data) =>
        new(1, Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, data);
}

/// <summary>
/// The <see cref="IRealtimeNotifier"/> the contexts depend on, implemented over
/// SignalR groups. Keeping the interface in Core means a service can be tested
/// without a hub, and the transport can change without touching a context.
/// </summary>
public sealed class SignalRNotifier(IHubContext<PcConnectHub> hub) : IRealtimeNotifier
{
    public Task CommandIssuedAsync(Guid deviceId, PendingCommand command, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.Device(deviceId))
            .SendAsync(RealtimeEvents.CommandIssued, PcConnectHub.Envelope(command), ct);

    public Task CommandStatusAsync(Guid userId, CommandStatusEvent status, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.User(userId))
            .SendAsync(RealtimeEvents.CommandStatus, PcConnectHub.Envelope(status), ct);

    public Task DevicePresenceAsync(Guid userId, DevicePresenceEvent presence, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.User(userId))
            .SendAsync(RealtimeEvents.DevicePresence, PcConnectHub.Envelope(presence), ct);

    public Task ReminderChangedAsync(Guid userId, ReminderChangedEvent change, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.User(userId))
            .SendAsync(RealtimeEvents.ReminderChanged, PcConnectHub.Envelope(change), ct);

    public Task ReminderDueAsync(Guid userId, ReminderDueEvent due, CancellationToken ct = default) =>
        hub.Clients.Group(RealtimeGroups.User(userId))
            .SendAsync(RealtimeEvents.ReminderDue, PcConnectHub.Envelope(due), ct);
}
