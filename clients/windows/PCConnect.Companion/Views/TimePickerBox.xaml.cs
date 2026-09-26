using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCConnect.Companion.Services;

namespace PCConnect.Companion.Views;

public partial class TimePickerBox : UserControl
{
    public static readonly DependencyProperty TimeTextProperty =
        DependencyProperty.Register(
            nameof(TimeText),
            typeof(string),
            typeof(TimePickerBox),
            new FrameworkPropertyMetadata("12:00", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTimeTextChanged));

    public static readonly DependencyProperty Use24HourClockProperty =
        DependencyProperty.Register(
            nameof(Use24HourClock),
            typeof(bool),
            typeof(TimePickerBox),
            new FrameworkPropertyMetadata(true, OnUse24HourClockChanged));

    private TimeOnly _time = new(12, 0);
    private bool _isUpdatingInternally;
    private FrameworkElement? _lastFocusedField;

    public string TimeText
    {
        get => (string)GetValue(TimeTextProperty);
        set => SetValue(TimeTextProperty, value);
    }

    public bool Use24HourClock
    {
        get => (bool)GetValue(Use24HourClockProperty);
        set => SetValue(Use24HourClockProperty, value);
    }

    public TimePickerBox()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateUiFromTime();
            UpdateAmPmVisibility();
        };
    }

    private static void OnTimeTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimePickerBox picker && !picker._isUpdatingInternally)
        {
            if (TimeFormatting.TryParse(e.NewValue as string, out var parsed))
            {
                picker._time = parsed;
                picker.UpdateUiFromTime();
            }
        }
    }

    private static void OnUse24HourClockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimePickerBox picker)
        {
            picker.UpdateAmPmVisibility();
            picker.UpdateUiFromTime();
            picker.PushTimeTextUpdate();
        }
    }

    private void UpdateAmPmVisibility()
    {
        if (AmPmButton != null)
        {
            AmPmButton.Visibility = Use24HourClock ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdateUiFromTime()
    {
        if (HourInput == null || MinuteInput == null) return;

        if (Use24HourClock)
        {
            HourInput.Text = _time.Hour.ToString("00", CultureInfo.InvariantCulture);
        }
        else
        {
            var h12 = _time.Hour % 12;
            if (h12 == 0) h12 = 12;
            HourInput.Text = h12.ToString("00", CultureInfo.InvariantCulture);
            if (AmPmButton != null)
            {
                AmPmButton.Content = _time.Hour < 12 ? "AM" : "PM";
            }
        }

        MinuteInput.Text = _time.Minute.ToString("00", CultureInfo.InvariantCulture);
    }

    private void PushTimeTextUpdate()
    {
        _isUpdatingInternally = true;
        try
        {
            TimeText = TimeFormatting.FormatTime(_time, Use24HourClock);
        }
        finally
        {
            _isUpdatingInternally = false;
        }
    }

    private void IncrementHour(int delta = 1)
    {
        var newHour = (_time.Hour + delta) % 24;
        if (newHour < 0) newHour += 24;
        _time = new TimeOnly(newHour, _time.Minute);
        UpdateUiFromTime();
        PushTimeTextUpdate();
        HourInput.SelectAll();
    }

    private void IncrementMinute(int delta = 1)
    {
        var newMinute = (_time.Minute + delta) % 60;
        if (newMinute < 0) newMinute += 60;
        _time = new TimeOnly(_time.Hour, newMinute);
        UpdateUiFromTime();
        PushTimeTextUpdate();
        MinuteInput.SelectAll();
    }

    private void ToggleAmPm()
    {
        var newHour = (_time.Hour + 12) % 24;
        _time = new TimeOnly(newHour, _time.Minute);
        UpdateUiFromTime();
        PushTimeTextUpdate();
    }

    // --- Hour Field Handling ---

    private void OnHourGotFocus(object sender, RoutedEventArgs e)
    {
        _lastFocusedField = HourInput;
        HourInput.SelectAll();
    }

    private void OnHourLostFocus(object sender, RoutedEventArgs e)
    {
        CommitHour();
    }

    private void CommitHour()
    {
        if (int.TryParse(HourInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var hourVal))
        {
            if (Use24HourClock)
            {
                if (hourVal >= 0 && hourVal < 24)
                {
                    _time = new TimeOnly(hourVal, _time.Minute);
                }
            }
            else
            {
                if (hourVal >= 1 && hourVal <= 12)
                {
                    var isPm = _time.Hour >= 12;
                    if (hourVal == 12)
                    {
                        _time = new TimeOnly(isPm ? 12 : 0, _time.Minute);
                    }
                    else
                    {
                        _time = new TimeOnly(isPm ? hourVal + 12 : hourVal, _time.Minute);
                    }
                }
            }
        }
        UpdateUiFromTime();
        PushTimeTextUpdate();
    }

    private void OnHourPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!char.IsDigit(e.Text, 0))
        {
            e.Handled = true;
            return;
        }

        var nextText = GetProposedText(HourInput, e.Text);
        if (int.TryParse(nextText, NumberStyles.None, CultureInfo.InvariantCulture, out var val))
        {
            if (Use24HourClock)
            {
                if (val > 23)
                {
                    e.Handled = true;
                    return;
                }
                // If user types a digit 3-9, it can only be 03..09 in 24h
                if (nextText.Length == 1 && val >= 3)
                {
                    _time = new TimeOnly(val, _time.Minute);
                    UpdateUiFromTime();
                    PushTimeTextUpdate();
                    MinuteInput.Focus();
                    MinuteInput.SelectAll();
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                if (val > 12)
                {
                    e.Handled = true;
                    return;
                }
                // In 12h, if user types 2-9, it can only be 02..09
                if (nextText.Length == 1 && val >= 2)
                {
                    var isPm = _time.Hour >= 12;
                    var targetHour = val == 12 ? (isPm ? 12 : 0) : (isPm ? val + 12 : val);
                    _time = new TimeOnly(targetHour, _time.Minute);
                    UpdateUiFromTime();
                    PushTimeTextUpdate();
                    MinuteInput.Focus();
                    MinuteInput.SelectAll();
                    e.Handled = true;
                    return;
                }
            }

            if (nextText.Length == 2)
            {
                HourInput.Text = nextText;
                CommitHour();
                MinuteInput.Focus();
                MinuteInput.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void OnHourPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                IncrementHour(1);
                e.Handled = true;
                break;
            case Key.Down:
                IncrementHour(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MinuteInput.Focus();
                MinuteInput.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnHourMouseWheel(object sender, MouseWheelEventArgs e)
    {
        IncrementHour(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // --- Minute Field Handling ---

    private void OnMinuteGotFocus(object sender, RoutedEventArgs e)
    {
        _lastFocusedField = MinuteInput;
        MinuteInput.SelectAll();
    }

    private void OnMinuteLostFocus(object sender, RoutedEventArgs e)
    {
        CommitMinute();
    }

    private void CommitMinute()
    {
        if (int.TryParse(MinuteInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minVal))
        {
            if (minVal >= 0 && minVal < 60)
            {
                _time = new TimeOnly(_time.Hour, minVal);
            }
        }
        UpdateUiFromTime();
        PushTimeTextUpdate();
    }

    private void OnMinutePreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!char.IsDigit(e.Text, 0))
        {
            e.Handled = true;
            return;
        }

        var nextText = GetProposedText(MinuteInput, e.Text);
        if (int.TryParse(nextText, NumberStyles.None, CultureInfo.InvariantCulture, out var val))
        {
            if (val > 59)
            {
                e.Handled = true;
                return;
            }

            // If user types digit 6-9, it can only be 06..09
            if (nextText.Length == 1 && val >= 6)
            {
                _time = new TimeOnly(_time.Hour, val);
                UpdateUiFromTime();
                PushTimeTextUpdate();
                if (!Use24HourClock && AmPmButton != null)
                {
                    AmPmButton.Focus();
                }
                else
                {
                    MinuteInput.SelectAll();
                }
                e.Handled = true;
                return;
            }

            if (nextText.Length == 2)
            {
                MinuteInput.Text = nextText;
                CommitMinute();
                if (!Use24HourClock && AmPmButton != null)
                {
                    AmPmButton.Focus();
                }
                else
                {
                    MinuteInput.SelectAll();
                }
                e.Handled = true;
            }
        }
    }

    private void OnMinutePreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                IncrementMinute(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1);
                e.Handled = true;
                break;
            case Key.Down:
                IncrementMinute(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -5 : -1);
                e.Handled = true;
                break;
            case Key.Left:
                HourInput.Focus();
                HourInput.SelectAll();
                e.Handled = true;
                break;
            case Key.Right when !Use24HourClock && AmPmButton != null:
                AmPmButton.Focus();
                e.Handled = true;
                break;
            case Key.Back when string.IsNullOrEmpty(MinuteInput.Text) || MinuteInput.SelectionLength == MinuteInput.Text.Length:
                HourInput.Focus();
                HourInput.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnMinuteMouseWheel(object sender, MouseWheelEventArgs e)
    {
        IncrementMinute(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // --- AM/PM Field Handling ---

    private void OnAmPmGotFocus(object sender, RoutedEventArgs e)
    {
        _lastFocusedField = AmPmButton;
    }

    private void OnAmPmClick(object sender, RoutedEventArgs e)
    {
        ToggleAmPm();
    }

    private void OnAmPmPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
            case Key.Space:
                ToggleAmPm();
                e.Handled = true;
                break;
            case Key.A when _time.Hour >= 12:
                ToggleAmPm();
                e.Handled = true;
                break;
            case Key.P when _time.Hour < 12:
                ToggleAmPm();
                e.Handled = true;
                break;
            case Key.Left:
                MinuteInput.Focus();
                MinuteInput.SelectAll();
                e.Handled = true;
                break;
        }
    }

    // --- Spin Buttons Handling ---

    private void OnSpinUpClick(object sender, RoutedEventArgs e)
    {
        if (_lastFocusedField == HourInput)
        {
            IncrementHour(1);
        }
        else if (_lastFocusedField == AmPmButton)
        {
            ToggleAmPm();
        }
        else
        {
            IncrementMinute(5);
        }
    }

    private void OnSpinDownClick(object sender, RoutedEventArgs e)
    {
        if (_lastFocusedField == HourInput)
        {
            IncrementHour(-1);
        }
        else if (_lastFocusedField == AmPmButton)
        {
            ToggleAmPm();
        }
        else
        {
            IncrementMinute(-5);
        }
    }

    private static string GetProposedText(TextBox box, string newText)
    {
        var text = box.Text ?? string.Empty;
        var start = box.SelectionStart;
        var length = box.SelectionLength;
        return text.Remove(start, length).Insert(start, newText);
    }
}
