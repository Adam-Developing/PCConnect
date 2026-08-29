using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PCConnect.Api.Security;
using PCConnect.Infrastructure.Observability;

namespace PCConnect.Api.Realtime;

[Authorize(Policy = "Controller")]
public sealed class ControllerHub : Hub
{
    private const string CountedKey = "pcconnect.connection-counted";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{Context.User!.UserId():D}");
        Context.Items[CountedKey] = true;
        PCConnectTelemetry.RecordRealtimeConnection("controller", 1);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(CountedKey)) PCConnectTelemetry.RecordRealtimeConnection("controller", -1);
        await base.OnDisconnectedAsync(exception);
    }
}
