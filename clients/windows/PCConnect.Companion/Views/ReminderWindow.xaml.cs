using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PCConnect.Companion.Views;

public partial class ReminderWindow : Window
{
    private readonly Func<Task>? _onDone;
    private readonly Action? _onSnooze;

    public ReminderWindow(
        string body,
        DateTimeOffset dueAt,
        string background,
        string foreground,
        string pcName,
        Func<Task>? onDone = null,
        Action? onSnooze = null)
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
            SnoozeButton.Foreground = brush;
            SnoozeButton.BorderBrush = brush;
            // Both secondary buttons take the reminder's own text colour. Left
            // on the default ink they were dark-on-dark and effectively
            // invisible against the card.
            DismissButton.Foreground = brush;
        });

        // Escape dismisses. A full-screen window with no way out but the mouse
        // is a window people learn to dread.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
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

    private async void OnDone(object sender, RoutedEventArgs e)
    {
        DoneButton.IsEnabled = false;

        if (_onDone is not null)
        {
            await _onDone();
        }

        Close();
    }

    private void OnSnooze(object sender, RoutedEventArgs e)
    {
        _onSnooze?.Invoke();
        Close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
