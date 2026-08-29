using System.Windows;

namespace PCConnect.Windows.Companion;

public partial class App : Application, IDisposable
{
    private CompanionPipeClient? client;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        client = new CompanionPipeClient(window);
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
