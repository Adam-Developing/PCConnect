using Microsoft.Extensions.DependencyInjection.Extensions;
using PCConnect.Agent;
using PCConnect.Agent.Execution;
using PCConnect.Agent.Storage;
using PCConnect.Client;
using Serilog;
using Serilog.Formatting.Compact;

// PCConnect.Agent — the Windows service that receives and executes commands.
//
// Installed with:
//   sc.exe create PCConnectAgent binPath= "<path>\PCConnect.Agent.exe" start= auto
//   sc.exe description PCConnectAgent "Receives PCConnect commands for this PC."
//
// It holds a device credential and nothing else, and it never runs a shell.

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables("PCCONNECT_AGENT_");

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AddWindowsService(options => options.ServiceName = "PCConnectAgent");

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

builder.Services.AddSingleton<ITokenStore>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>();
    var overridePath = builder.Configuration["Agent:TokenFile"];

    return string.IsNullOrWhiteSpace(overridePath)
        ? new CredentialManagerTokenStore(logger.CreateLogger<CredentialManagerTokenStore>())
        : new FileTokenStore(overridePath, logger.CreateLogger<FileTokenStore>());
});

builder.Services.AddSingleton<ISessionCommandBridge, NamedPipeSessionBridge>();
builder.Services.AddSingleton<CommandExecutor>();

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    return new PcConnectClientOptions
    {
        BaseAddress = options.BaseAddress,
        ClientKind = "desktop_agent",
        ClientVersion = options.Version,
    };
});

builder.Services.AddHttpClient<PcConnectClient>()
    .AddStandardResilienceHandler();

builder.Services.TryAddSingleton<PcConnectRealtimeClient>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
await host.RunAsync();
