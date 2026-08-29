using PCConnect.Windows.Agent;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The PCConnect agent requires Windows.");

var enroll = args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase);
var hostArguments = enroll ? args[1..] : args;
var builder = Host.CreateApplicationBuilder(hostArguments);
if (enroll)
{
    var endpoint = builder.Configuration["PCConnect:ApiBaseUrl"] ?? throw new InvalidOperationException("PCConnect:ApiBaseUrl is required.");
    using var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/api/v2/"), Timeout = TimeSpan.FromSeconds(15) };
    var runner = new AgentEnrollmentRunner(client, new DpapiDeviceCredentialStore(builder.Configuration));
    await runner.RunAsync(builder.Configuration["PCConnect:EnrollmentName"] ?? Environment.MachineName, CancellationToken.None);
    return;
}
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
