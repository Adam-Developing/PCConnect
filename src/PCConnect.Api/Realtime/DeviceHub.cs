using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PCConnect.Api.Security;
using PCConnect.Infrastructure.Observability;

namespace PCConnect.Api.Realtime;

[Authorize(Policy = "Device")]
public sealed class DeviceHub : Hub
{
    private const string CountedKey = "pcconnect.connection-counted";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{Context.User!.DeviceId():D}");
        Context.Items[CountedKey] = true;
        PCConnectTelemetry.RecordRealtimeConnection("device", 1);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(CountedKey)) PCConnectTelemetry.RecordRealtimeConnection("device", -1);
        await base.OnDisconnectedAsync(exception);
    }
}
