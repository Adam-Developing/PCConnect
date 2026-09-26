using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PCConnect.Companion.ViewModels;

namespace PCConnect.Companion.Views;

public partial class MainWindow : Window
{
    private ItemsControl? _calendarDrag;
    private CompanionPage _lastPage = CompanionPage.ThisPc;
    private CalendarViewMode _lastCalendarViewMode = CalendarViewMode.Days;
    private DateOnly? _lastViewMonth;

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
                shell.Reminders.RequestReopenChoice = RequestReopenChoiceAsync;
                shell.Reminders.RowsReset += () => RemindersScrollViewer.ScrollToTop();
                shell.EventDetailOpenRequested += OpenEventDetailModal;
                shell.EventDetailCloseRequested += CloseEventDetailModal;

                shell.PropertyChanged += OnShellPropertyChanged;
                shell.Reminders.PropertyChanged += OnRemindersPropertyChanged;
                _lastPage = shell.Page;
                _lastCalendarViewMode = shell.Reminders.ViewMode;
                _lastViewMonth = shell.Reminders.ViewMonth;
            }
        };

        Loaded += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                UpdateNavPill(shell.Page, animate: false);
            }
        };

        // Escape closes the active overlay or modal
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && EventDetailOverlay.Visibility == Visibility.Visible)
            {
                CloseEventDetailModal();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && DataContext is ShellViewModel { Devices.IsLogOpen: true } shell)
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
            else if (e.Key == Key.Escape &&
                     DataContext is ShellViewModel { IsAccountMenuOpen: true } accountShell)
            {
                accountShell.IsAccountMenuOpen = false;
                e.Handled = true;
            }
        };

        Deactivated += (_, _) =>
        {
            EndCalendarDrag();
            if (DataContext is ShellViewModel { IsAccountMenuOpen: true } shell)
            {
                shell.IsAccountMenuOpen = false;
            }
        };

        LocationChanged += (_, _) => RepositionAccountPopup();
        SizeChanged += (_, _) => RepositionAccountPopup();

        PreviewMouseDown += OnWindowPreviewMouseDown;
        PreviewMouseWheel += (_, _) =>
        {
            if (DataContext is ShellViewModel { IsAccountMenuOpen: true } shell)
            {
                shell.IsAccountMenuOpen = false;
            }
        };
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ShellViewModel { IsAccountMenuOpen: true } shell)
        {
            if (IsElementOrChildOf(e.OriginalSource as DependencyObject, AccountButton))
            {
                shell.IsAccountMenuOpen = false;
                e.Handled = true;
            }
            else
            {
                shell.IsAccountMenuOpen = false;
            }
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page) && DataContext is ShellViewModel shell)
        {
            var oldPage = _lastPage;
            var newPage = shell.Page;
            if (oldPage != newPage)
            {
                _lastPage = newPage;
                OnPageChanged(oldPage, newPage);
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsAccountMenuOpen) && DataContext is ShellViewModel shellMenu)
        {
            if (shellMenu.IsAccountMenuOpen)
            {
                OpenAccountMenu();
            }
            else
            {
                CloseAccountMenu();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsEventDetailOpen) && DataContext is ShellViewModel shellDetail)
        {
            if (shellDetail.IsEventDetailOpen)
            {
                OpenEventDetailModal();
            }
            else
            {
                CloseEventDetailModal();
            }
        }
    }

    private void OnPageChanged(CompanionPage oldPage, CompanionPage newPage)
    {
        UpdateNavPill(newPage, animate: true);

        Grid? targetGrid = newPage switch
        {
            CompanionPage.ThisPc => ThisPcPageGrid,
            CompanionPage.OtherPcs => OtherPcsPageGrid,
            CompanionPage.Reminders => RemindersPageGrid,
            CompanionPage.Settings => SettingsPageGrid,
            _ => null
        };

        if (targetGrid == null) return;

        // Directional spatial continuity:
        // Moving downward in sidebar: incoming page slides up from below (+24 -> 0).
        // Moving upward in sidebar: incoming page slides down from above (-24 -> 0).
        double startY = (newPage > oldPage) ? 24 : -24;

        if (targetGrid.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            targetGrid.RenderTransform = tt;
        }

        var slide = new DoubleAnimation(startY, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        tt.BeginAnimation(TranslateTransform.YProperty, slide);
        targetGrid.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateNavPill(CompanionPage page, bool animate)
    {
        if (NavHighlightPill == null || NavHighlightTransform == null || NavContainer == null) return;

        RadioButton? targetButton = page switch
        {
            CompanionPage.ThisPc => NavThisPc,
            CompanionPage.OtherPcs => NavOtherPcs,
            CompanionPage.Reminders => NavReminders,
            CompanionPage.Settings => NavSettings,
            _ => NavThisPc
        };

        if (targetButton == null) return;

        NavContainer.UpdateLayout();
        double targetY;
        try
        {
            var transform = targetButton.TransformToVisual(NavContainer);
            targetY = transform.Transform(new Point(0, 0)).Y;
        }
        catch
        {
            targetY = (int)page * 42.0;
        }

        if (!animate)
        {
            NavHighlightTransform.Y = targetY;
            NavHighlightPill.Opacity = 1;
            return;
        }

        var moveAnim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        NavHighlightTransform.BeginAnimation(TranslateTransform.YProperty, moveAnim);

        if (NavHighlightPill.Opacity < 1)
        {
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NavHighlightPill.BeginAnimation(OpacityProperty, fadeIn);
        }
    }

    private void OnRemindersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;

        if (e.PropertyName == nameof(RemindersViewModel.ViewMode))
        {
            var oldMode = _lastCalendarViewMode;
            var newMode = shell.Reminders.ViewMode;
            if (oldMode != newMode)
            {
                _lastCalendarViewMode = newMode;
                OnCalendarViewModeChanged(oldMode, newMode);
            }
        }
        else if (e.PropertyName == nameof(RemindersViewModel.ViewMonth))
        {
            var currentMonth = shell.Reminders.ViewMonth;
            if (_lastViewMonth.HasValue && _lastViewMonth.Value != currentMonth)
            {
                OnMonthNavigationChanged(_lastViewMonth.Value, currentMonth);
            }
            _lastViewMonth = currentMonth;
        }
        else if (e.PropertyName == nameof(RemindersViewModel.IsRefreshing))
        {
            AnimateRefresh(shell.Reminders.IsRefreshing);
        }
    }

    private void AnimateRefresh(bool isRefreshing)
    {
        if (RefreshRotateTransform == null) return;

        if (isRefreshing)
        {
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(650))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            RefreshRotateTransform.BeginAnimation(RotateTransform.AngleProperty, anim);
        }
        else
        {
            RefreshRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            RefreshRotateTransform.Angle = 0;
        }
    }

    private void OnCalendarViewModeChanged(CalendarViewMode oldMode, CalendarViewMode newMode)
    {
        // Opening menu: Days -> Months, or Months -> Years (comes from middle small and expands outwards)
        if (oldMode == CalendarViewMode.Days && newMode == CalendarViewMode.Months)
        {
            AnimateOpenMenu(CalendarDaysPanel, CalendarMonthsPanel, MonthsScale);
        }
        else if (oldMode == CalendarViewMode.Months && newMode == CalendarViewMode.Years)
        {
            AnimateOpenMenu(CalendarMonthsPanel, CalendarYearsPanel, YearsScale);
        }
        // Closing menu: Years -> Months, or Months/Years -> Days (shrinks smaller into the middle as it closes)
        else if (oldMode == CalendarViewMode.Years && newMode == CalendarViewMode.Months)
        {
            AnimateCloseMenu(CalendarYearsPanel, YearsScale, CalendarMonthsPanel, MonthsScale);
        }
        else if ((oldMode == CalendarViewMode.Months || oldMode == CalendarViewMode.Years) && newMode == CalendarViewMode.Days)
        {
            var outgoing = (oldMode == CalendarViewMode.Years) ? CalendarYearsPanel : CalendarMonthsPanel;
            var outgoingScale = (oldMode == CalendarViewMode.Years) ? YearsScale : MonthsScale;
            AnimateCloseMenu(outgoing, outgoingScale, CalendarDaysPanel, DaysScale);
        }
    }

    private static void AnimateOpenMenu(FrameworkElement outgoing, FrameworkElement incoming, ScaleTransform incomingScale)
    {
        outgoing.Visibility = Visibility.Collapsed;
        incoming.Visibility = Visibility.Visible;

        var scaleAnim = new DoubleAnimation(0.82, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        incomingScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        incomingScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        incoming.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private static void AnimateCloseMenu(FrameworkElement outgoing, ScaleTransform outgoingScale, FrameworkElement incoming, ScaleTransform incomingScale)
    {
        incoming.Visibility = Visibility.Visible;

        var incomingScaleAnim = new DoubleAnimation(1.05, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var incomingFadeAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        incomingScale.BeginAnimation(ScaleTransform.ScaleXProperty, incomingScaleAnim);
        incomingScale.BeginAnimation(ScaleTransform.ScaleYProperty, incomingScaleAnim);
        incoming.BeginAnimation(OpacityProperty, incomingFadeAnim);

        // Menu being closed shrinks smaller into center: 1.0 -> 0.82
        var scaleShrink = new DoubleAnimation(1.0, 0.82, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoingScale.ScaleX = 1.0;
            outgoingScale.ScaleY = 1.0;
            outgoing.Opacity = 1.0;
        };
        outgoingScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleShrink);
        outgoingScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleShrink);
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnMonthNavigationChanged(DateOnly oldMonth, DateOnly newMonth)
    {
        if (DaysSlideTransform == null || DaysGridContainer == null) return;

        // Moving forward to next month: slides in from right (+32 -> 0).
        // Moving backward to previous month: slides in from left (-32 -> 0).
        double startX = (newMonth > oldMonth) ? 32 : -32;

        var slide = new DoubleAnimation(startX, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        DaysSlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
        DaysGridContainer.BeginAnimation(OpacityProperty, fade);
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

    private Task<ReopenChoice> RequestReopenChoiceAsync(ReminderRow row)
    {
        var tcs = new TaskCompletionSource<ReopenChoice>();

        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };

        var rescheduleItem = new MenuItem
        {
            Header = "Reschedule for today",
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Set reminder for today so it will alert on screen",
            Icon = new Path
            {
                Data = TryFindResource("Icon.Refresh") as Geometry,
                Fill = TryFindResource("Primary") as Brush ?? Brushes.DodgerBlue,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            }
        };
        rescheduleItem.Click += (_, _) => tcs.TrySetResult(ReopenChoice.RescheduleForToday);

        var overdueItem = new MenuItem
        {
            Header = "Won't rerun",
            ToolTip = "Keep original date without re-running alert",
            Icon = new Path
            {
                Data = (TryFindResource("Icon.Schedule") ?? TryFindResource("Icon.Clock")) as Geometry,
                Fill = TryFindResource("InkSoft") as Brush ?? Brushes.Gray,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            }
        };
        overdueItem.Click += (_, _) => tcs.TrySetResult(ReopenChoice.KeepOverdue);

        menu.Items.Add(rescheduleItem);
        menu.Items.Add(overdueItem);

        menu.Closed += (_, _) =>
        {
            tcs.TrySetResult(ReopenChoice.Cancel);
        };

        menu.IsOpen = true;

        return tcs.Task;
    }

    private void StatusBanner_MouseEnter(object sender, MouseEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.Reminders.PauseStatusTimer();
        }
    }

    private void StatusBanner_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.Reminders.ResumeStatusTimer();
        }
    }

    private void RepositionAccountPopup()
    {
        if (AccountPopup.IsOpen)
        {
            var offset = AccountPopup.HorizontalOffset;
            AccountPopup.HorizontalOffset = offset + 0.0001;
            AccountPopup.HorizontalOffset = offset;
        }
    }

    private int _accountMenuAnimationVersion;

    private void OpenAccountMenu()
    {
        var version = ++_accountMenuAnimationVersion;
        AccountPopup.IsOpen = true;

        var startY = AccountMenuTranslate.Y;
        if (double.IsNaN(startY) || Math.Abs(startY) < 0.01) startY = 10.0;

        var slideAnim = new DoubleAnimation(startY, 0.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeAnim = new DoubleAnimation(AccountPopupCard.Opacity, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        AccountMenuTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        AccountPopupCard.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void CloseAccountMenu()
    {
        if (!AccountPopup.IsOpen) return;
        var version = ++_accountMenuAnimationVersion;

        var startY = AccountMenuTranslate.Y;
        if (double.IsNaN(startY)) startY = 0.0;

        var slideAnim = new DoubleAnimation(startY, 10.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fadeAnim = new DoubleAnimation(AccountPopupCard.Opacity, 0.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeAnim.Completed += (_, _) =>
        {
            if (version == _accountMenuAnimationVersion && DataContext is ShellViewModel { IsAccountMenuOpen: false })
            {
                AccountPopup.IsOpen = false;
                AccountMenuTranslate.Y = 0.0;
                AccountPopupCard.Opacity = 1.0;
            }
        };

        AccountMenuTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        AccountPopupCard.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private static bool IsElementOrChildOf(DependencyObject? source, DependencyObject target)
    {
        while (source != null)
        {
            if (source == target) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private int _eventDetailAnimationVersion;

    private void OpenEventDetailModal()
    {
        var version = ++_eventDetailAnimationVersion;
        EventDetailOverlay.Visibility = Visibility.Visible;

        var backdropFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        EventDetailBackdrop.BeginAnimation(OpacityProperty, backdropFade);

        var cardFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var cardScaleX = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var cardScaleY = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var cardTranslate = new DoubleAnimation(10.0, 0.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        cardFade.Completed += (_, _) =>
        {
            if (version == _eventDetailAnimationVersion)
            {
                // Detach active animation clocks so WPF exits GPU composition and renders with crisp font hinting
                EventDetailCardContainer.BeginAnimation(OpacityProperty, null);
                EventDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                EventDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                EventDetailTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                EventDetailCardContainer.Opacity = 1.0;
                EventDetailScale.ScaleX = 1.0;
                EventDetailScale.ScaleY = 1.0;
                EventDetailTranslate.Y = 0.0;
            }
        };

        EventDetailCardContainer.BeginAnimation(OpacityProperty, cardFade);
        EventDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, cardScaleX);
        EventDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, cardScaleY);
        EventDetailTranslate.BeginAnimation(TranslateTransform.YProperty, cardTranslate);
    }

    private void CloseEventDetailModal()
    {
        if (EventDetailOverlay.Visibility != Visibility.Visible) return;
        var version = ++_eventDetailAnimationVersion;

        var backdropFade = new DoubleAnimation(EventDetailBackdrop.Opacity, 0.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        EventDetailBackdrop.BeginAnimation(OpacityProperty, backdropFade);

        var cardFade = new DoubleAnimation(EventDetailCardContainer.Opacity, 0.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var cardScaleX = new DoubleAnimation(EventDetailScale.ScaleX, 0.95, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var cardScaleY = new DoubleAnimation(EventDetailScale.ScaleY, 0.95, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var cardTranslate = new DoubleAnimation(EventDetailTranslate.Y, 8.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        cardFade.Completed += (_, _) =>
        {
            if (version == _eventDetailAnimationVersion)
            {
                EventDetailOverlay.Visibility = Visibility.Collapsed;
                EventDetailBackdrop.Opacity = 0.0;
                EventDetailCardContainer.Opacity = 1.0;
                EventDetailScale.ScaleX = 0.92;
                EventDetailScale.ScaleY = 0.92;
                EventDetailTranslate.Y = 10.0;

                if (DataContext is ShellViewModel shell)
                {
                    shell.IsEventDetailOpen = false;
                }
            }
        };

        EventDetailCardContainer.BeginAnimation(OpacityProperty, cardFade);
        EventDetailScale.BeginAnimation(ScaleTransform.ScaleXProperty, cardScaleX);
        EventDetailScale.BeginAnimation(ScaleTransform.ScaleYProperty, cardScaleY);
        EventDetailTranslate.BeginAnimation(TranslateTransform.YProperty, cardTranslate);
    }

    private void OnEventDetailBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        CloseEventDetailModal();
        e.Handled = true;
    }

    private void OnEventDetailCloseClick(object sender, RoutedEventArgs e)
    {
        CloseEventDetailModal();
    }

    private void OnDayColumnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            var sv = FindVisualChild<ScrollViewer>(d);
            if (sv is not null && sv.ScrollableHeight > 0)
            {
                var step = 48.0;
                var target = e.Delta > 0 ? sv.VerticalOffset - step : sv.VerticalOffset + step;
                target = Math.Clamp(target, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(target);
                e.Handled = true;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window leaves PCConnect running in the tray, which is what
        // the v1 client did and what people expect of it.
        e.Cancel = true;
        Hide();
    }
}
