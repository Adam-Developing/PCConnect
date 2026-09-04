using System.Windows;
using System.Windows.Input;
using PCConnect.Companion.ViewModels;

namespace PCConnect.Companion.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                // The step-up prompt is a window, so the view owns it; the view
                // model only knows that something must confirm (ADR-0011).
                shell.Devices.RequestStepUpPassword = RequestStepUpPasswordAsync;
            }
        };

        // Escape closes the activity log rather than the window, which is where
        // a full-page overlay leads people to expect it to go.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is ShellViewModel { Devices.IsLogOpen: true } shell)
            {
                shell.Devices.CloseLogCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void OnSignInClick(object sender, RoutedEventArgs e) => SignIn();

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SignIn();
        }
    }

    private void SignIn()
    {
        if (DataContext is ShellViewModel shell && shell.SignInCommand.CanExecute(PasswordBox.Password))
        {
            // The password is handed straight to the command and never stored on
            // the view model, so it does not survive in memory after sign-in.
            shell.SignInCommand.Execute(PasswordBox.Password);
            PasswordBox.Clear();
        }
    }

    private Task<string?> RequestStepUpPasswordAsync(string commandType, string deviceName)
    {
        var status = (DataContext as ShellViewModel)?.Devices.SelectedDevice?.Summary ?? string.Empty;

        var dialog = new StepUpWindow(commandType, deviceName, status) { Owner = this };
        var confirmed = dialog.ShowDialog() == true;

        return Task.FromResult(confirmed ? dialog.EnteredPassword : null);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window leaves PCConnect running in the tray, which is what
        // the v1 client did and what people expect of it.
        e.Cancel = true;
        Hide();
    }
}
