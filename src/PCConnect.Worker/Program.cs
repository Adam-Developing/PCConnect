using Microsoft.AspNetCore.SignalR;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure;
using PCConnect.Infrastructure.Jobs;
using PCConnect.Infrastructure.Realtime;
using Serilog;
using Serilog.Formatting.Compact;

// pcconnect-worker — the scheduled half of the system.
//
// It runs the command expiry sweep, the reminder scheduler, the recurrence
// horizon, retention, and the continuous verification gates. It is a separate
// process from the API so that a slow sweep cannot delay a request and a
// restarted API does not interrupt a backfill.

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables("PCCONNECT_");

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AddPcConnectInfrastructure(builder.Configuration);
builder.Services.AddPcConnectJobs(builder.Configuration);

// The worker reaches connected clients through the same backplane the API
// publishes to. Without a backplane configured it has no way to notify anyone,
// so it says so rather than silently dropping every reminder notification.
var backplane = builder.Configuration["Cache:ConnectionString"];

if (!string.IsNullOrWhiteSpace(backplane))
{
    builder.Services.AddSignalR().AddStackExchangeRedis(backplane, options =>
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("pcconnect"));

    builder.Services.AddSingleton<IRealtimeNotifier, BackplaneNotifier>();
}
else
{
    builder.Services.AddSingleton<IRealtimeNotifier, NoOpNotifier>();
}

var host = builder.Build();

host.Services.GetRequiredService<IValidateOptions>().Validate();

await host.RunAsync();

/// <summary>
/// Publishes to the same SignalR groups the API's hub uses, over the shared
/// backplane.
///
/// SignalR keys its backplane channels on the hub's type name, which is why
/// <c>PcConnectHub</c> lives in the Infrastructure assembly both processes
/// reference: the worker never accepts a connection, but its messages have to
/// land on the API's channel to reach anyone.
/// </summary>
internal sealed class BackplaneNotifier(IHubContext<PcConnectHub> hub) : IRealtimeNotifier
{
    private static string User(Guid id) => $"user:{id:N}";

    private static string Device(Guid id) => $"device:{id:N}";

    private static RealtimeEvent<T> Envelope<T>(T data) =>
        new(1, Guid.CreateVersion7().ToString("N"), DateTimeOffset.UtcNow, data);

    public Task CommandIssuedAsync(Guid deviceId, PendingCommand command, CancellationToken ct = default) =>
        hub.Clients.Group(Device(deviceId)).SendAsync("command.issued", Envelope(command), ct);

    public Task CommandStatusAsync(Guid userId, CommandStatusEvent status, CancellationToken ct = default) =>
        hub.Clients.Group(User(userId)).SendAsync("command.status", Envelope(status), ct);

    public Task DevicePresenceAsync(Guid userId, DevicePresenceEvent presence, CancellationToken ct = default) =>
        hub.Clients.Group(User(userId)).SendAsync("device.presence", Envelope(presence), ct);

    public Task ReminderChangedAsync(Guid userId, ReminderChangedEvent change, CancellationToken ct = default) =>
        hub.Clients.Group(User(userId)).SendAsync("reminder.changed", Envelope(change), ct);

    public Task ReminderDueAsync(Guid userId, ReminderDueEvent due, CancellationToken ct = default) =>
        hub.Clients.Group(User(userId)).SendAsync("reminder.due", Envelope(due), ct);
}

internal sealed class NoOpNotifier(ILogger<NoOpNotifier> logger) : IRealtimeNotifier
{
    private void Warn(string what) =>
        logger.LogWarning("No realtime backplane configured; {Event} was not delivered to any client", what);

    public Task CommandIssuedAsync(Guid deviceId, PendingCommand command, CancellationToken ct = default)
    {
        Warn("command.issued");
        return Task.CompletedTask;
    }

    public Task CommandStatusAsync(Guid userId, CommandStatusEvent status, CancellationToken ct = default)
    {
        Warn("command.status");
        return Task.CompletedTask;
    }

    public Task DevicePresenceAsync(Guid userId, DevicePresenceEvent presence, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ReminderChangedAsync(Guid userId, ReminderChangedEvent change, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ReminderDueAsync(Guid userId, ReminderDueEvent due, CancellationToken ct = default)
    {
        Warn("reminder.due");
        return Task.CompletedTask;
    }
}
