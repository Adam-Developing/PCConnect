using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PCConnect.Agent.Execution;
using PCConnect.Client;
using PCConnect.Companion.Services;
using PCConnect.Companion.ViewModels;
using PCConnect.Companion.Views;

namespace PCConnect.Companion;

/// <summary>
/// The companion runs in the user's own session. It is the half of the Windows
/// client that a person interacts with: signing in, pairing a PC, issuing
/// commands, and seeing reminders (ADR-0012).
///
/// It also serves the two session-bound commands — lock and sign out — that the
/// service in session 0 cannot perform itself.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private CancellationTokenSource? _sessionServerCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables("PCCONNECT_COMPANION_");

        builder.Services.Configure<CompanionOptions>(builder.Configuration.GetSection("Companion"));

        builder.Services.AddSingleton<ITokenStore>(sp => new DpapiTokenStore(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DpapiTokenStore>()));

        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CompanionOptions>>().Value;
            return new PcConnectClientOptions
            {
                BaseAddress = options.BaseAddress,
                ClientKind = "desktop_agent",
                ClientVersion = options.Version,
            };
        });

        builder.Services.AddHttpClient<PcConnectClient>();
        builder.Services.AddSingleton<PcConnectRealtimeClient>();

        builder.Services.AddSingleton<CompanionSettings>();
        builder.Services.AddSingleton<StartupRegistration>();

        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<DevicesViewModel>();
        builder.Services.AddSingleton<RemindersViewModel>();
        builder.Services.AddSingleton<AccountViewModel>();
        builder.Services.AddSingleton<ReminderPresenter>();
        builder.Services.AddSingleton<TrayIcon>();

        _host = builder.Build();
        await _host.StartAsync();

        StartSessionCommandServer();

        _host.Services.GetRequiredService<RemindersViewModel>().Initialise();

        var shell = _host.Services.GetRequiredService<ShellViewModel>();
        await shell.InitialiseAsync();

        _host.Services.GetRequiredService<ReminderPresenter>().Start();
        _host.Services.GetRequiredService<TrayIcon>().Show(ShowMainWindow, Shutdown);

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (MainWindow is { } existing)
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new MainWindow { DataContext = _host!.Services.GetRequiredService<ShellViewModel>() };
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Serves lock and sign-out for the service. The verbs are a closed set of
    /// two; nothing from the network reaches this code as data.
    /// </summary>
    private void StartSessionCommandServer()
    {
        _sessionServerCts = new CancellationTokenSource();
        var logger = _host!.Services.GetRequiredService<ILoggerFactory>().CreateLogger<SessionCommandServer>();

        var server = new SessionCommandServer(logger, verb => verb switch
        {
            NamedPipeSessionBridge.LockVerb => SessionActions.LockWorkstation(),
            NamedPipeSessionBridge.SignOutVerb => SessionActions.SignOut(),
            _ => false,
        });

        _ = Task.Run(() => server.RunAsync(_sessionServerCts.Token));
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _sessionServerCts?.Cancel();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}

public sealed class CompanionOptions
{
    public string BaseAddress { get; set; } = "http://localhost:5080";

    public string Version { get; set; } = "5.0.0";

    /// <summary>Reminder window colours, carried over from the v1 settings panel.</summary>
    public string ReminderBackground { get; set; } = "#CC10151A";

    public string ReminderForeground { get; set; } = "#E4EAF0";

    public bool Use24HourClock { get; set; } = true;
}

/// <summary>The two session-bound actions, through Win32 and nothing else.</summary>
internal static class SessionActions
{
    private const uint EwxLogoff = 0x00000000;
    private const uint EwxForceIfHung = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    public static bool LockWorkstation() => LockWorkStation();

    public static bool SignOut() => ExitWindowsEx(EwxLogoff | EwxForceIfHung, 0);
}
