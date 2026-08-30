using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using PCConnect.Contracts.V2;

namespace PCConnect.Windows.Companion;

public partial class MainWindow : Window, IDisposable
{
    private CompanionPipeClient? pipeClient;
    private string? apiBaseUrl;
    private bool enrolled;
    private bool busy;
    private bool closing;
    private Guid? authorizationDeviceId;
    private string? authorizationWindowsSid;
    private CancellationTokenSource? enrollmentCancellation;
    private AccountEnrollmentClient? activeEnrollment;
    private EnrollmentStage enrollmentStage = EnrollmentStage.Account;

    public MainWindow()
    {
        InitializeComponent();
        DeviceNameInput.Text = Environment.MachineName;
    }

    public void Attach(CompanionPipeClient client) => pipeClient = client;

    public void ShowEnrollment(string serverUrl)
    {
        ReleaseActiveEnrollment();
        enrolled = false;
        apiBaseUrl = serverUrl;
        authorizationDeviceId = null;
        authorizationWindowsSid = null;
        enrollmentStage = EnrollmentStage.Account;
        DeviceNameInput.Text = Environment.MachineName;
        ShowEnrollmentStage();
        WaitingPanel.Visibility = Visibility.Collapsed;
        EnrolledPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        EnrollButton.IsEnabled = true;
        ClearError();
        ServerText.Text = $"Connected to {new Uri(serverUrl).GetLeftPart(UriPartial.Authority)}";
        SetStatus("Ready to connect this PC.", StatusKind.Ready);
        Show();
        Activate();
        LoginInput.Focus();
    }

    public void ShowAuthorization(string serverUrl, Guid deviceId, string windowsSid)
    {
        ReleaseActiveEnrollment();
        enrolled = false;
        apiBaseUrl = serverUrl;
        authorizationDeviceId = deviceId;
        authorizationWindowsSid = windowsSid;
        enrollmentStage = EnrollmentStage.Recovery;
        ShowEnrollmentStage();
        WaitingPanel.Visibility = Visibility.Collapsed;
        EnrolledPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        ClearError();
        ServerText.Text = $"Connected to {new Uri(serverUrl).GetLeftPart(UriPartial.Authority)}";
        SetStatus("Sign in to finish Windows authorization.", StatusKind.Ready);
        Show();
        Activate();
        LoginInput.Focus();
    }

    public void ShowConnected(
        string message = "PCConnect is running securely in the background.",
        bool showWindow = true)
    {
        enrolled = true;
        WaitingPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        EnrolledPanel.Visibility = Visibility.Visible;
        EnrolledMessage.Text = message;
        ConnectedDeviceNameText.Text = string.IsNullOrWhiteSpace(DeviceNameInput.Text)
            ? Environment.MachineName
            : DeviceNameInput.Text.Trim();
        SetStatus("Connected securely to the PCConnect Windows service.", StatusKind.Success);
        if (showWindow)
        {
            Show();
            Activate();
        }
    }

    public void ShowWaiting(string message)
    {
        if (!enrolled)
        {
            WaitingPanel.Visibility = Visibility.Visible;
            LoginPanel.Visibility = Visibility.Collapsed;
            EnrolledPanel.Visibility = Visibility.Collapsed;
        }
        SetStatus(message, StatusKind.Waiting);
    }

    public void ShowReminder(string text)
    {
        ShowConnected(text, showWindow: true);
        SetStatus("New PCConnect reminder", StatusKind.Ready);
    }

    private async void EnrollClick(object sender, RoutedEventArgs e)
    {
        if (busy || pipeClient is null || apiBaseUrl is null) return;

        var login = LoginInput.Text.Trim();
        var password = PasswordInput.Password;
        var deviceName = DeviceNameInput.Text.Trim();
        if (enrollmentStage is EnrollmentStage.Account or EnrollmentStage.Recovery && string.IsNullOrWhiteSpace(login))
        {
            ShowError("Enter the username or email for your PCConnect account.");
            LoginInput.Focus();
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            ShowError("Enter your PCConnect account password.");
            PasswordInput.Focus();
            return;
        }
        if (enrollmentStage == EnrollmentStage.DeviceName && string.IsNullOrWhiteSpace(deviceName))
        {
            ShowError("Enter a recognizable name for this PC.");
            DeviceNameInput.Focus();
            return;
        }

        ClearError();
        enrollmentCancellation = new CancellationTokenSource();
        var cancellationToken = enrollmentCancellation.Token;
        SetBusy(true, "Signing in…");
        var startingStage = enrollmentStage;
        var completedDeviceEnrollment = false;
        try
        {
            var progress = new Progress<string>(message => SetStatus(message, StatusKind.Working));
            if (startingStage == EnrollmentStage.Account)
            {
                var enrollment = new AccountEnrollmentClient(apiBaseUrl);
                try
                {
                    await enrollment.SignInAsync(login, password, progress, cancellationToken);
                    activeEnrollment = enrollment;
                }
                catch
                {
                    await enrollment.DisposeAsync();
                    throw;
                }
                enrollmentStage = EnrollmentStage.DeviceName;
                ShowEnrollmentStage();
                SetStatus("Signed in. Choose a name for this PC.", StatusKind.Ready);
                DeviceNameInput.Focus();
                DeviceNameInput.SelectAll();
            }
            else if (startingStage == EnrollmentStage.Recovery)
            {
                await using var enrollment = new AccountEnrollmentClient(apiBaseUrl);
                await enrollment.SignInAsync(login, password, progress, cancellationToken);
                await enrollment.AuthorizeWindowsIdentityAsync(
                    authorizationDeviceId!.Value,
                    authorizationWindowsSid!,
                    password,
                    progress,
                    cancellationToken);
                SetStatus("Windows account authorized. Finishing the secure connection…", StatusKind.Working);
            }
            else
            {
                var enrollment = activeEnrollment
                    ?? throw new InvalidOperationException("The signed-in enrollment session is no longer available.");
                DeviceTokenPair credential = await enrollment.EnrollSignedInDeviceAsync(
                    deviceName,
                    progress,
                    cancellationToken);
                SetStatus("Protecting the device credential with the Windows service…", StatusKind.Working);
                var outcome = await pipeClient.ProvisionDeviceAsync(credential, cancellationToken);
                if (outcome.RequiresAuthorization)
                {
                    if (string.IsNullOrWhiteSpace(outcome.WindowsSid))
                        throw new InvalidOperationException("The Windows service did not identify the current Windows account.");
                    await enrollment.AuthorizeWindowsIdentityAsync(
                        credential.DeviceId,
                        outcome.WindowsSid,
                        password,
                        progress,
                        cancellationToken);
                }
                SetStatus("Account linked. Finishing the secure connection…", StatusKind.Working);
                completedDeviceEnrollment = true;
            }
        }
        catch (EnrollmentException exception)
        {
            ShowError(exception.Message);
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach the PCConnect server. Check your connection and try again.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            ShowError("The Windows service could not complete setup. Please try again.");
        }
        catch (OperationCanceledException)
        {
            if (!closing)
                ShowError("Setup was cancelled before the connection was complete.");
        }
        finally
        {
            var keepingSignedInSession = !closing
                && enrollmentStage == EnrollmentStage.DeviceName
                && activeEnrollment is not null
                && (startingStage == EnrollmentStage.Account || !completedDeviceEnrollment);
            if (!keepingSignedInSession)
            {
                PasswordInput.Clear();
                if (startingStage == EnrollmentStage.DeviceName)
                    await DisposeActiveEnrollmentAsync();
            }
            Interlocked.Exchange(ref enrollmentCancellation, null)?.Dispose();
            SetBusy(false, StatusText.Text);
        }
    }

    private async void BackClick(object sender, RoutedEventArgs e)
    {
        if (busy || enrollmentStage != EnrollmentStage.DeviceName) return;
        await DisposeActiveEnrollmentAsync();
        PasswordInput.Clear();
        enrollmentStage = EnrollmentStage.Account;
        ClearError();
        ShowEnrollmentStage();
        SetStatus("Ready to sign in.", StatusKind.Ready);
        PasswordInput.Focus();
    }

    private void ShowEnrollmentStage()
    {
        var account = enrollmentStage == EnrollmentStage.Account;
        var deviceName = enrollmentStage == EnrollmentStage.DeviceName;
        var recovery = enrollmentStage == EnrollmentStage.Recovery;

        EnrollmentTitle.Text = recovery ? "Finish connecting this PC" : deviceName ? "Name this PC" : "Connect this PC";
        EnrollmentDescription.Text = recovery
            ? "The device is protected, but this Windows account still needs authorization. Sign in to the account that owns the device to finish setup."
            : deviceName
                ? "Choose how this PC will appear in your PCConnect account."
                : "Sign in to your PCConnect account. You’ll choose a name for this PC next.";
        EnrollmentSteps.Visibility = recovery ? Visibility.Collapsed : Visibility.Visible;
        AccountStepPanel.Visibility = deviceName ? Visibility.Collapsed : Visibility.Visible;
        DeviceNameStepPanel.Visibility = deviceName ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = deviceName ? Visibility.Visible : Visibility.Collapsed;
        PasswordPrivacyText.Visibility = deviceName ? Visibility.Collapsed : Visibility.Visible;

        AccountStepBadge.Background = Brush(deviceName ? "#DCFCE7" : "#DBEAFE");
        AccountStepNumber.Text = deviceName ? "✓" : "1";
        AccountStepNumber.Foreground = Brush(deviceName ? "#15803D" : "#1D4ED8");
        AccountStepText.Foreground = Brush(deviceName ? "#15803D" : "#334155");
        DeviceStepBadge.Background = Brush(deviceName ? "#DBEAFE" : "#E8ECF2");
        DeviceStepNumber.Foreground = Brush(deviceName ? "#1D4ED8" : "#526175");
        DeviceStepText.Foreground = Brush(deviceName ? "#334155" : "#526175");
        EnrollButton.Content = recovery ? "Sign in and finish setup" : deviceName ? "Connect this PC" : "Sign in and continue";
        AutomationProperties.SetHelpText(EnrollButton, recovery
            ? "Sign in and finish authorizing this Windows account"
            : deviceName ? "Connect this PC using the selected PC name" : "Sign in and continue to name this Windows PC");
    }

    private void SetBusy(bool busy, string status)
    {
        this.busy = busy;
        EnrollButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        EnrollButton.Content = busy
            ? enrollmentStage == EnrollmentStage.DeviceName ? "Connecting securely…" : "Signing in…"
            : enrollmentStage == EnrollmentStage.Recovery ? "Sign in and finish setup"
                : enrollmentStage == EnrollmentStage.DeviceName ? "Connect this PC" : "Sign in and continue";
        LoginInput.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;
        DeviceNameInput.IsEnabled = !busy;
        LoginField.IsEnabled = !busy;
        PasswordField.IsEnabled = !busy;
        DeviceNameField.IsEnabled = !busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(status, busy
            ? StatusKind.Working
            : ErrorBanner.Visibility == Visibility.Visible ? StatusKind.Error
                : enrolled ? StatusKind.Success : StatusKind.Ready);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        SetStatus("Setup needs your attention.", StatusKind.Error);
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusDot.Background = new SolidColorBrush(kind switch
        {
            StatusKind.Success => Color.FromRgb(22, 163, 74),
            StatusKind.Error => Color.FromRgb(220, 38, 38),
            StatusKind.Working => Color.FromRgb(37, 99, 235),
            StatusKind.Ready => Color.FromRgb(34, 197, 94),
            _ => Color.FromRgb(100, 116, 139)
        });
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private async Task DisposeActiveEnrollmentAsync()
    {
        var enrollment = Interlocked.Exchange(ref activeEnrollment, null);
        if (enrollment is not null)
            await DisposeEnrollmentSilentlyAsync(enrollment);
    }

    private void ReleaseActiveEnrollment()
    {
        var enrollment = Interlocked.Exchange(ref activeEnrollment, null);
        if (enrollment is not null)
            _ = DisposeEnrollmentSilentlyAsync(enrollment);
    }

    private static async Task DisposeEnrollmentSilentlyAsync(AccountEnrollmentClient enrollment)
    {
        try { await enrollment.DisposeAsync(); }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = true;
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref enrollmentCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        ReleaseActiveEnrollment();
        GC.SuppressFinalize(this);
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Hide();

    private enum EnrollmentStage { Account, DeviceName, Recovery }
    private enum StatusKind { Waiting, Ready, Working, Success, Error }
}
