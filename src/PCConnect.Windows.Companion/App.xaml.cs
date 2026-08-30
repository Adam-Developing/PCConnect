using System.Windows;

namespace PCConnect.Windows.Companion;

public partial class App : Application, IDisposable
{
    private CompanionPipeClient? client;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var startInBackground = e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;
        if (!startInBackground)
            window.Show();
        client = new CompanionPipeClient(window, startInBackground);
        window.Attach(client);
        client.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        client?.Dispose();
        client = null;
        GC.SuppressFinalize(this);
    }
}
