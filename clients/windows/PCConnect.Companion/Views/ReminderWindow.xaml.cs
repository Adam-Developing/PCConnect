using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCConnect.Companion.Views;

public partial class ReminderWindow : Window
{
    private readonly Func<Task>? _onDone;
    private readonly Action<TimeSpan>? _onSnooze;
    private TimeSpan _selectedSnooze = TimeSpan.FromMinutes(10);
    private bool _isClosing;

    public ReminderWindow(
        string body,
        DateTimeOffset dueAt,
        string background,
        string foreground,
        string pcName,
        Func<Task>? onDone = null,
        Action<TimeSpan>? onSnooze = null)
    {
        InitializeComponent();

        _onDone = onDone;
        _onSnooze = onSnooze;

        BodyText.Text = body;
        EyebrowText.Text = $"REMINDER · {pcName.ToUpperInvariant()}";
        TimeText.Text = $"{Describe(dueAt)} at {dueAt.ToLocalTime():HH:mm}";

        // The v1 client let people pick the reminder colours to cope with eye
        // strain. That setting survives the rewrite; it was a real accessibility
        // affordance, not decoration.
        TryApply(background, brush => Card.Background = brush);
        TryApply(foreground, brush =>
        {
            BodyText.Foreground = brush;
            TimeText.Foreground = brush;
            HintText.Foreground = brush;
            SnoozeBorder.BorderBrush = brush;
            SnoozeButton.Foreground = brush;
            SnoozeDropdownButton.Foreground = brush;
            DismissButton.Foreground = brush;
        });

        Loaded += (_, _) =>
        {
            var fadeAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RootGrid.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);

            var scaleAnimation = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        };

        // Escape dismisses. A full-screen window with no way out but the mouse
        // is a window people learn to dread.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                AnimateAndClose();
            }
        };
    }

    private static string Describe(DateTimeOffset dueAt)
    {
        var day = DateOnly.FromDateTime(dueAt.ToLocalTime().Date);
        var today = DateOnly.FromDateTime(DateTime.Today);

        return day == today ? "Today"
            : day == today.AddDays(1) ? "Tomorrow"
            : dueAt.ToLocalTime().ToString("dddd d MMMM", CultureInfo.CurrentCulture);
    }

    private static void TryApply(string colour, Action<Brush> apply)
    {
        try
        {
            if (ColorConverter.ConvertFromString(colour) is Color parsed)
            {
                apply(new SolidColorBrush(parsed));
            }
        }
        catch (FormatException)
        {
            // Keep the default rather than failing to show the reminder at all.
        }
    }

    private void AnimateAndClose(Action? afterClosed = null)
    {
        if (_isClosing) return;
        _isClosing = true;

        var fadeAnimation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var scaleAnimation = new DoubleAnimation(1.0, 0.95, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeAnimation.Completed += (_, _) =>
        {
            afterClosed?.Invoke();
            Close();
        };

        RootGrid.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    private async void OnCompleted(object sender, RoutedEventArgs e)
    {
        CompletedButton.IsEnabled = false;

        if (_onDone is not null)
        {
            await _onDone();
        }

        AnimateAndClose();
    }

    private void OnSnoozeDefault(object sender, RoutedEventArgs e)
    {
        AnimateAndClose(() => _onSnooze?.Invoke(_selectedSnooze));
    }

    private void OnSnoozeDropdownClick(object sender, RoutedEventArgs e)
    {
        if (SnoozeContextMenu != null)
        {
            SnoozeContextMenu.PlacementTarget = SnoozeDropdownButton;
            SnoozeContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            SnoozeContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnSnooze10m(object sender, RoutedEventArgs e)
    {
        _selectedSnooze = TimeSpan.FromMinutes(10);
        SnoozeDurationText.Text = "10 mins";
        AnimateAndClose(() => _onSnooze?.Invoke(_selectedSnooze));
    }

    private void OnSnooze30m(object sender, RoutedEventArgs e)
    {
        _selectedSnooze = TimeSpan.FromMinutes(30);
        SnoozeDurationText.Text = "30 mins";
        AnimateAndClose(() => _onSnooze?.Invoke(_selectedSnooze));
    }

    private void OnSnooze1h(object sender, RoutedEventArgs e)
    {
        _selectedSnooze = TimeSpan.FromHours(1);
        SnoozeDurationText.Text = "1 hour";
        AnimateAndClose(() => _onSnooze?.Invoke(_selectedSnooze));
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => AnimateAndClose();
}
