using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrostLock.Models;
using FrostLock.Services;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace FrostLock.Views;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings _settings;
    private readonly Func<string?> _validateAndApply;
    private bool _loading;
    private bool _newPinRevealed;
    private bool _confirmPinRevealed;

    public SettingsWindow(AppSettings settings, Func<string?> validateAndApply)
    {
        InitializeComponent();

        _settings = settings;
        _validateAndApply = validateAndApply;

        LoadFromSettings();
        WireEvents();
    }

    private void LoadFromSettings()
    {
        _loading = true;

        ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
        ChkStartMinimized.IsChecked = _settings.StartMinimized;
        ChkMinimizeToTray.IsChecked = _settings.MinimizeToTray;
        ChkCloseToTray.IsChecked = _settings.CloseToTray;
        ChkKeepAwake.IsChecked = _settings.KeepDisplayAwake;
        ChkShowCountdown.IsChecked = _settings.ShowCountdownOnOverlay;
        ChkPlaySound.IsChecked = _settings.PlaySound;

        ArmBox.Value = _settings.ArmSeconds;
        SafetyBox.Value = _settings.SafetyMaxSeconds / 60;

        TxtUnlockHotkey.Text = _settings.UnlockHotkey;
        TxtStartHotkey.Text = _settings.StartHotkey;

        _loading = false;

        LoadPinState();
    }

    private void LoadPinState()
    {
        TxtPinStatus.Text = _settings.HasPin
            ? "A PIN is set. PIN lock can be used."
            : "No PIN set. PIN lock is unavailable.";

        BtnRemovePin.IsEnabled = _settings.HasPin;

        ResetPinReveal();

        PinErrorBar.IsOpen = false;
        PinOkBar.IsOpen = false;
    }

    private void ResetPinReveal()
    {
        _newPinRevealed = false;
        _confirmPinRevealed = false;

        PinNewBox.Clear();
        PinNewShown.Clear();
        PinConfirmBox.Clear();
        PinConfirmShown.Clear();

        ApplyPinReveal(false, PinNewBox, PinNewShown, IconShowPinNew, BtnShowPinNew);
        ApplyPinReveal(false, PinConfirmBox, PinConfirmShown, IconShowPinConfirm, BtnShowPinConfirm);
    }

    private static void ApplyPinReveal(bool revealed, PasswordBox hidden, TextBox shown,
        Wpf.Ui.Controls.SymbolIcon icon, Wpf.Ui.Controls.Button button)
    {
        hidden.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        shown.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        icon.Symbol = revealed
            ? Wpf.Ui.Controls.SymbolRegular.EyeOff24
            : Wpf.Ui.Controls.SymbolRegular.Eye24;
        button.ToolTip = revealed ? "Hide PIN" : "Show PIN";
    }

    private void ToggleNewPinVisibility(object sender, RoutedEventArgs e)
    {
        _newPinRevealed = !_newPinRevealed;

        if (_newPinRevealed) PinNewShown.Text = PinNewBox.Password;
        else PinNewBox.Password = PinNewShown.Text;

        ApplyPinReveal(_newPinRevealed, PinNewBox, PinNewShown, IconShowPinNew, BtnShowPinNew);

        if (_newPinRevealed) PinNewShown.Focus();
        else PinNewBox.Focus();
    }

    private void ToggleConfirmPinVisibility(object sender, RoutedEventArgs e)
    {
        _confirmPinRevealed = !_confirmPinRevealed;

        if (_confirmPinRevealed) PinConfirmShown.Text = PinConfirmBox.Password;
        else PinConfirmBox.Password = PinConfirmShown.Text;

        ApplyPinReveal(_confirmPinRevealed, PinConfirmBox, PinConfirmShown, IconShowPinConfirm, BtnShowPinConfirm);

        if (_confirmPinRevealed) PinConfirmShown.Focus();
        else PinConfirmBox.Focus();
    }

    private void ShowPinMessage(string? error)
    {
        PinErrorBar.Message = error ?? string.Empty;
        PinErrorBar.IsOpen = !string.IsNullOrEmpty(error);
        PinOkBar.IsOpen = string.IsNullOrEmpty(error) && _settings.HasPin;
    }

    private void SavePin_Click(object sender, RoutedEventArgs e)
    {
        var pin = _newPinRevealed ? PinNewShown.Text : PinNewBox.Password;
        var confirm = _confirmPinRevealed ? PinConfirmShown.Text : PinConfirmBox.Password;

        if (pin.Length < 4)
        {
            ShowPinMessage("Use at least 4 characters.");
            return;
        }

        if (!string.Equals(pin, confirm, StringComparison.Ordinal))
        {
            ShowPinMessage("The two PINs do not match.");
            return;
        }

        _settings.SetPin(pin);
        Report(_validateAndApply());

        LoadPinState();
        ShowPinMessage(null);
    }

    private void RemovePin_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClearPin();
        Report(_validateAndApply());
        LoadPinState();
    }

    private void WireEvents()
    {
        BindToggle(ChkStartWithWindows, v => _settings.StartWithWindows = v);
        BindToggle(ChkStartMinimized, v => _settings.StartMinimized = v);
        BindToggle(ChkMinimizeToTray, v => _settings.MinimizeToTray = v);
        BindToggle(ChkCloseToTray, v => _settings.CloseToTray = v);
        BindToggle(ChkKeepAwake, v => _settings.KeepDisplayAwake = v);
        BindToggle(ChkShowCountdown, v => _settings.ShowCountdownOnOverlay = v);
        BindToggle(ChkPlaySound, v => _settings.PlaySound = v);

        // A NumberBox only moves typed text into Value on Enter, a spin click or focus
        // loss, so save on ValueChanged too. Enter is the usual way to set a number and
        // it fires ValueChanged but not LostFocus.
        ArmBox.ValueChanged += (_, _) => CommitNumbers();
        SafetyBox.ValueChanged += (_, _) => CommitNumbers();
        ArmBox.LostFocus += (_, _) => CommitNumbers();
        SafetyBox.LostFocus += (_, _) => CommitNumbers();

        // Dropping focus makes the boxes commit any text that was typed but never
        // confirmed, so a value set just before closing still sticks.
        Closing += (_, _) =>
        {
            Keyboard.ClearFocus();
            CommitNumbers();
        };

        PinNewBox.KeyDown += OnPinKey;
        PinConfirmBox.KeyDown += OnPinKey;
        PinNewShown.KeyDown += OnPinKey;
        PinConfirmShown.KeyDown += OnPinKey;

        PreviewKeyDown += (_, e) =>
        {
            // Esc closes the window, except while a hotkey field has focus, where the help
            // text promises it only abandons the recording.
            if (e.Key == Key.Escape && !IsHotkeyFieldFocused()) Close();
        };

        // A freshly shown settings page should always start at the top, whatever element
        // WPF decides to focus first.
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => ContentScroll.ScrollToTop()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // True while one of the hotkey capture fields owns the keyboard focus.
    private bool IsHotkeyFieldFocused()
        => ReferenceEquals(Keyboard.FocusedElement, TxtUnlockHotkey)
           || ReferenceEquals(Keyboard.FocusedElement, TxtStartHotkey);

    private void BindToggle(ToggleSwitch toggle, Action<bool> setter)
    {
        toggle.Checked += (_, _) =>
        {
            if (_loading) return;
            setter(true);
            Report(_validateAndApply());
        };

        toggle.Unchecked += (_, _) =>
        {
            if (_loading) return;
            setter(false);
            Report(_validateAndApply());
        };
    }

    private void CommitNumbers()
    {
        if (_loading) return;

        if (ArmBox.Value is { } arm && !double.IsNaN(arm))
            _settings.ArmSeconds = (int)Math.Clamp(arm, 0d, 120d);

        if (SafetyBox.Value is { } minutes && !double.IsNaN(minutes))
            _settings.SafetyMaxSeconds = (int)Math.Clamp(minutes, 0d, 24d * 60d) * 60;

        _loading = true;
        ArmBox.Value = _settings.ArmSeconds;
        SafetyBox.Value = _settings.SafetyMaxSeconds / 60;
        _loading = false;

        Report(_validateAndApply());
    }

    /// <summary>
    /// Records the combination pressed in a hotkey field instead of letting the user type
    /// one. Modifier-only presses are ignored; a modifier is required.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_loading) return;
        if (sender is not Wpf.Ui.Controls.TextBox box) return;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool win = modifiers.HasFlag(ModifierKeys.Windows)
                   || Keyboard.IsKeyDown(Key.LWin)
                   || Keyboard.IsKeyDown(Key.RWin);

        var binding = Hotkeys.Create(ctrl, alt, shift, win, virtualKey);

        if (!binding.HasModifier)
        {
            Report("Hold Ctrl, Alt, Shift or Win while pressing the key.");
            e.Handled = true;
            return;
        }

        bool editingUnlock = ReferenceEquals(box, TxtUnlockHotkey);
        var other = editingUnlock ? _settings.StartHotkey : _settings.UnlockHotkey;

        if (string.Equals(binding.Display, other, StringComparison.OrdinalIgnoreCase))
        {
            Report("The unlock and start hotkeys must be different combinations.");
            e.Handled = true;
            return;
        }

        if (editingUnlock)
            _settings.UnlockHotkey = binding.Display;
        else
            _settings.StartHotkey = binding.Display;

        box.Text = binding.Display;
        Report(_validateAndApply());
        e.Handled = true;
    }

    private void Report(string? error)
    {
        HotkeyError.Message = error ?? string.Empty;
        HotkeyError.IsOpen = !string.IsNullOrEmpty(error);
    }

    private void OnPinKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SavePin_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings.CopyFrom(new AppSettings());
        LoadFromSettings();
        Report(_validateAndApply());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
