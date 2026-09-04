using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCConnect.Companion.ViewModels;

namespace PCConnect.Companion.Views;

/// <summary>
/// The confirmation a destructive command needs (ADR-0011).
///
/// The wording names the actual consequence — "Shut down Study PC?" — rather
/// than asking the user to confirm an abstraction. A dialog that says "Are you
/// sure?" teaches people to click yes.
/// </summary>
public partial class StepUpWindow : Window
{
    public StepUpWindow(string commandType, string deviceName, string deviceStatus)
    {
        InitializeComponent();

        var verb = ShellViewModel.Describe(commandType);

        PromptText.Text = $"{verb} {deviceName}?";
        DeviceStatusText.Text = deviceStatus;
        ConfirmButton.Content = $"{verb} {deviceName}";

        if (Application.Current.TryFindResource(ShellViewModel.IconFor(commandType)) is Geometry glyph)
        {
            CommandIcon.Data = glyph;
        }

        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string? EnteredPassword { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length == 0)
        {
            ErrorText.Text = "Type your PCConnect password.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        EnteredPassword = PasswordBox.Password;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirm(sender, e);
        }
    }
}
