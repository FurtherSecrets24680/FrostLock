using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrostLock.Models;
using FrostLock.Services;
using Microsoft.Win32;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace FrostLock.Views;

public enum SettingsSection
{
    General,
    Hotkeys
}

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings _settings;
    private readonly Func<string?> _validateAndApply;
    private bool _loading;
    private bool _newPinRevealed;
    private bool _confirmPinRevealed;
    private SettingsSection _section = SettingsSection.General;

    public SettingsWindow(AppSettings settings, Func<string?> validateAndApply)
    {
        InitializeComponent();

        _settings = settings;
        _validateAndApply = validateAndApply;

        RunSettingsPath.Text = SettingsService.SettingsFilePath;

        LoadFromSettings();
        WireEvents();
    }

    /// <summary>Scrolls to a section, optionally focusing the first useful field.</summary>
    public void ShowSection(SettingsSection section)
    {
        _section = section;
        if (IsLoaded) NavigateToSection(section);
    }

    private void NavigateToSection(SettingsSection section)
    {
        if (section == SettingsSection.Hotkeys)
        {
            HotkeysSection.BringIntoView();
            TxtUnlockHotkey.Focus();
            return;
        }

        ContentScroll.ScrollToTop();
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

        SelectTag(StyleBox, _settings.LockScreenStyle.ToString());
        SelectTag(DisplaysBox, _settings.LockScreenOnAllDisplays ? "All" : "Primary");
        SelectTag(ThemeBox, _settings.Theme.ToString());
        TxtLockMessage.Text = _settings.LockScreenMessage;

        ChkShowClock.IsChecked = _settings.ShowClockOnOverlay;
        ChkCheckUpdates.IsChecked = _settings.CheckForUpdatesOnStartup;

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
        BindToggle(ChkShowClock, v => _settings.ShowClockOnOverlay = v);
        BindToggle(ChkCheckUpdates, v => _settings.CheckForUpdatesOnStartup = v);
        BindToggle(ChkPlaySound, v => _settings.PlaySound = v);

        TxtLockMessage.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.LockScreenMessage = TxtLockMessage.Text;
            Report(_validateAndApply());
        };

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

        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => NavigateToSection(_section)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // True while one of the hotkey capture fields owns the keyboard focus.
    private bool IsHotkeyFieldFocused()
        => ReferenceEquals(Keyboard.FocusedElement, TxtUnlockHotkey)
           || ReferenceEquals(Keyboard.FocusedElement, TxtStartHotkey);

    private void LockScreen_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (StyleBox.SelectedItem is ComboBoxItem { Tag: string styleTag }
            && Enum.TryParse<LockScreenStyle>(styleTag, out var style))
            _settings.LockScreenStyle = style;

        _settings.LockScreenOnAllDisplays = DisplaysBox.SelectedItem is ComboBoxItem { Tag: "All" };

        Report(_validateAndApply());
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AppTheme>(tag, out var theme))
        {
            _settings.Theme = theme;
            ThemeService.Apply(theme);
        }

        Report(_validateAndApply());
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
        {
            if (item is ComboBoxItem { Tag: string t } && t == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

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

    // Settings file

    private void EditJson_Click(object sender, RoutedEventArgs e)
    {
        var editor = new SettingsFileWindow(_settings, ApplyImportedSettings) { Owner = this };

        if (editor.ShowDialog() == true)
            ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Success,
                "Settings applied", "The edited file was validated and applied.");
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export FrostLock settings",
            FileName = "frostlock-settings.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, SettingsService.Serialize(_settings));
            ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Success,
                "Settings exported", dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Could not export", ex.Message);
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import FrostLock settings",
            DefaultExt = ".json",
            CheckFileExists = true,
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (!SettingsService.TryParse(File.ReadAllText(dialog.FileName), out var imported, out var error))
            {
                ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                    "Import failed", error ?? "The file could not be read.");
                return;
            }

            var answer = MessageBox.Show(this,
                "Replace all current settings with the values from this file?\n\n" +
                dialog.FileName + "\n\n" +
                "This includes the PIN and lifetime statistics.",
                "Import settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            var hotkeyError = ApplyImportedSettings(imported);

            ShowConfigStatus(
                hotkeyError is null ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning,
                hotkeyError is null ? "Settings imported" : "Imported with a problem",
                hotkeyError ?? "The file was applied.");
        }
        catch (Exception ex)
        {
            ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Could not import", ex.Message);
        }
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SettingsService.SettingsFilePath;
            var args = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{SettingsService.SettingsDirectory}\"";

            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Could not open the folder", ex.Message);
        }
    }

    /// <summary>Applies imported settings, including the PIN and statistics, then refreshes the controls.</summary>
    private string? ApplyImportedSettings(AppSettings imported)
    {
        _settings.CopyFrom(imported, includeStats: true);

        ThemeService.Apply(_settings.Theme);

        var error = _validateAndApply();

        LoadFromSettings();
        Report(error);

        return error;
    }

    private void ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        ConfigStatus.Severity = severity;
        ConfigStatus.Title = title;
        ConfigStatus.Message = message;
        ConfigStatus.IsOpen = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings.CopyFrom(new AppSettings());
        LoadFromSettings();
        Report(_validateAndApply());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
