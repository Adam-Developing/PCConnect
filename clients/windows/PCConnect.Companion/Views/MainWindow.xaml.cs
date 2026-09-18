using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCConnect.Companion.ViewModels;

namespace PCConnect.Companion.Views;

public partial class MainWindow : Window
{
    private ItemsControl? _calendarDrag;

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
                shell.Reminders.RowsReset += () => RemindersScrollViewer.ScrollToTop();
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
            else if (e.Key == Key.Escape &&
                     DataContext is ShellViewModel { Page: CompanionPage.Reminders } remindersShell)
            {
                EndCalendarDrag();
                remindersShell.Reminders.ClearSelectionCommand.Execute(null);
                e.Handled = true;
            }
        };

        Deactivated += (_, _) => EndCalendarDrag();
    }

    private static Button? CalendarDayAt(ItemsControl calendar, Point position)
    {
        var hit = calendar.InputHitTest(position) as DependencyObject;
        while (hit is not null && hit != calendar)
        {
            if (hit is Button { DataContext: DayCell } button)
            {
                return button;
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        return null;
    }

    private void OnCalendarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl { DataContext: RemindersViewModel reminders } calendar ||
            CalendarDayAt(calendar, e.GetPosition(calendar)) is not { DataContext: DayCell day } button)
        {
            return;
        }

        EndCalendarDrag();
        button.Focus();
        reminders.BeginDaySelection(day.Date,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        _calendarDrag = calendar;
        if (!calendar.CaptureMouse())
        {
            EndCalendarDrag();
        }

        e.Handled = true;
    }

    private void OnCalendarMouseMove(object sender, MouseEventArgs e)
    {
        if (_calendarDrag is not { DataContext: RemindersViewModel reminders } calendar)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndCalendarDrag();
        }
        else if (CalendarDayAt(calendar, e.GetPosition(calendar)) is { DataContext: DayCell day })
        {
            reminders.ExtendDaySelection(day.Date);
        }

        e.Handled = true;
    }

    private void OnCalendarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_calendarDrag is { DataContext: RemindersViewModel reminders } calendar)
        {
            if (CalendarDayAt(calendar, e.GetPosition(calendar)) is { DataContext: DayCell day })
            {
                reminders.ExtendDaySelection(day.Date);
            }

            EndCalendarDrag();
            e.Handled = true;
        }
    }

    private void OnCalendarLostMouseCapture(object sender, MouseEventArgs e) => EndCalendarDrag();

    private void EndCalendarDrag()
    {
        var calendar = _calendarDrag;
        _calendarDrag = null;
        (calendar?.DataContext as RemindersViewModel)?.EndDaySelection();
        if (calendar?.IsMouseCaptured == true)
        {
            calendar.ReleaseMouseCapture();
        }
    }

    // Button.Click also handles Space/Enter and accessibility activation.
    private void OnCalendarDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DayCell day } && DataContext is ShellViewModel shell)
        {
            shell.Reminders.BeginDaySelection(day.Date,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            shell.Reminders.EndDaySelection();
            e.Handled = true;
        }
    }

    private void OnCalendarMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            if (e.Delta > 0)
            {
                shell.Reminders.PreviousMonthCommand.Execute(null);
            }
            else if (e.Delta < 0)
            {
                shell.Reminders.NextMonthCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private void OnRemindersScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scrollable = e.ExtentHeight - e.ViewportHeight;
        if (e.VerticalOffset > 0 && scrollable > 0 && e.VerticalOffset >= scrollable - 80)
        {
            if (DataContext is ShellViewModel shell && shell.Reminders.HasMoreRows)
            {
                shell.Reminders.LoadMoreRowsCommand.Execute(null);
            }
        }
    }

    private void OnRemindersPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0 && RemindersScrollViewer.VerticalOffset >= RemindersScrollViewer.ScrollableHeight - 5)
        {
            if (DataContext is ShellViewModel shell && shell.Reminders.HasMoreRows)
            {
                shell.Reminders.LoadMoreRowsCommand.Execute(null);
            }
        }
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
