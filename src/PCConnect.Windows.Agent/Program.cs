using PCConnect.Windows.Agent;
using Microsoft.Extensions.Hosting.WindowsServices;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The PCConnect agent requires Windows.");

var builder = Host.CreateApplicationBuilder(args);
if (!Environment.UserInteractive && WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddWindowsService(options => options.ServiceName = "PCConnect Agent v2");
builder.Services.AddSingleton<IDeviceCredentialStore, DpapiDeviceCredentialStore>();
builder.Services.AddSingleton<IFixedCommandExecutor, WindowsFixedCommandExecutor>();
builder.Services.AddSingleton<CompanionPairingSecretStore>();
builder.Services.AddSingleton<InteractiveSessionBroker>();
builder.Services.AddSingleton(services =>
{
    var endpoint = services.GetRequiredService<IConfiguration>()["PCConnect:ApiBaseUrl"]
        ?? throw new InvalidOperationException("PCConnect:ApiBaseUrl is required.");
    var client = new HttpClient
    {
        BaseAddress = new Uri(endpoint.TrimEnd('/') + "/api/v2/"),
        Timeout = TimeSpan.FromSeconds(15)
    };
    return new AgentApiClient(client, services.GetRequiredService<IDeviceCredentialStore>());
});
builder.Services.AddSingleton<AgentRealtimeClient>();
builder.Services.AddHostedService(services => services.GetRequiredService<InteractiveSessionBroker>());
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
