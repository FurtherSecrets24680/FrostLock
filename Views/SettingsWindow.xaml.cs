using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly MobileControlService _mobile;
    private MobilePairingQrWindow? _pairingQrWindow;
    private readonly DispatcherTimer _mobileStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;
    private bool _newPinRevealed;
    private bool _confirmPinRevealed;
    private SettingsSection _section = SettingsSection.General;

    public SettingsWindow(AppSettings settings, Func<string?> validateAndApply, MobileControlService mobile)
    {
        InitializeComponent();

        _settings = settings;
        _validateAndApply = validateAndApply;
        _mobile = mobile;
        RunSettingsPath.Text = SettingsService.SettingsFilePath;

        LoadFromSettings();
        WireEvents();

        _mobile.NetworkChanged += Mobile_NetworkChanged;
        _mobile.PairingCompleted += Mobile_PairingCompleted;
        _mobileStatusTimer.Tick += (_, _) => UpdateMobileStatus();
        _mobileStatusTimer.Start();
        Closed += (_, _) =>
        {
            _mobileStatusTimer.Stop();
            _mobile.NetworkChanged -= Mobile_NetworkChanged;
            _mobile.PairingCompleted -= Mobile_PairingCompleted;
            RevokeDisplayedInvite();
        };
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
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!IsLoaded || _section != SettingsSection.Hotkeys) return;

                TxtUnlockHotkey.Focus();
                Keyboard.Focus(TxtUnlockHotkey);
                HotkeysBlock.BringIntoView();
            }));
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
        EnableMobile.IsChecked = _settings.MobileControlEnabled;
        AllowPublicTesting.IsChecked = _settings.AllowMobileOnPublicProfileForTesting;
        AllowPinRelease.IsChecked = _settings.MobilePinReleaseAllowed;
        AllowTaskControl.IsChecked = _settings.MobileTaskControlEnabled;

        SelectTag(StyleBox, _settings.LockScreenStyle.ToString());
        SelectTag(DisplaysBox, _settings.LockScreenOnAllDisplays ? "All" : "Primary");
        SelectTag(ThemeBox, _settings.Theme.ToString());
        TxtLockMessage.Text = _settings.LockScreenMessage;

        ChkShowClock.IsChecked = _settings.ShowClockOnOverlay;
        ChkCheckUpdates.IsChecked = _settings.CheckForUpdatesOnStartup;
        ChkLockOnRelease.IsChecked = _settings.LockWindowsOnRelease;

        ArmBox.Value = _settings.ArmSeconds;
        SafetyBox.Value = _settings.SafetyMaxSeconds / 60;
        HoldBox.Value = _settings.UnlockHoldMs / 1000.0;
        ChkIdleFreeze.IsChecked = _settings.IdleFreezeEnabled;
        IdleBox.Value = _settings.IdleFreezeMinutes;
        UpdateIdleBoxState();

        TxtUnlockHotkey.Text = _settings.UnlockHotkey;
        TxtStartHotkey.Text = _settings.StartHotkey;
        TxtExtendHotkey.Text = _settings.ExtendHotkey;

        UpdateBackgroundLabel();

        _loading = false;

        UpdateMobileDetails();
        LoadPinState();
        LoadPanicState();
    }

    private void LoadPanicState()
    {
        TxtPanicStatus.Text = _settings.HasPanicPhrase
            ? "A phrase is set. It appears on the PIN lock screen."
            : "No phrase set. A forgotten PIN means killing FrostLock.";

        BtnRemovePanic.IsEnabled = _settings.HasPanicPhrase;

        PanicNewBox.Clear();
        PanicConfirmBox.Clear();

        PanicErrorBar.IsOpen = false;
        PanicOkBar.IsOpen = false;
    }

    private void ShowPanicMessage(string? error)
    {
        PanicErrorBar.Message = error ?? string.Empty;
        PanicErrorBar.IsOpen = !string.IsNullOrEmpty(error);
        PanicOkBar.IsOpen = string.IsNullOrEmpty(error) && _settings.HasPanicPhrase;
    }

    private void SavePanic_Click(object sender, RoutedEventArgs e)
    {
        var phrase = PanicNewBox.Password;
        var confirm = PanicConfirmBox.Password;

        if (phrase.Length < 20)
        {
            ShowPanicMessage("Use at least 20 characters. The phrase has to be slow to type.");
            return;
        }

        if (!string.Equals(phrase, confirm, StringComparison.Ordinal))
        {
            ShowPanicMessage("The two phrases do not match.");
            return;
        }

        if (_settings.HasPin && _settings.VerifyPin(phrase))
        {
            ShowPanicMessage("The emergency phrase must differ from your PIN.");
            return;
        }

        _settings.SetPanicPhrase(phrase);
        Report(_validateAndApply());

        LoadPanicState();
        ShowPanicMessage(null);
    }

    private void RemovePanic_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClearPanicPhrase();
        Report(_validateAndApply());
        LoadPanicState();
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
        BindToggle(ChkLockOnRelease, v => _settings.LockWindowsOnRelease = v);
        BindToggle(ChkIdleFreeze, v => _settings.IdleFreezeEnabled = v);

        ChkIdleFreeze.Checked += (_, _) => UpdateIdleBoxState();
        ChkIdleFreeze.Unchecked += (_, _) => UpdateIdleBoxState();

        TxtLockMessage.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.LockScreenMessage = TxtLockMessage.Text;
            Report(_validateAndApply());
        };

        ArmBox.ValueChanged += (_, _) => CommitNumbers();
        SafetyBox.ValueChanged += (_, _) => CommitNumbers();
        HoldBox.ValueChanged += (_, _) => CommitNumbers();
        IdleBox.ValueChanged += (_, _) => CommitNumbers();
        ArmBox.LostFocus += (_, _) => CommitNumbers();
        SafetyBox.LostFocus += (_, _) => CommitNumbers();
        HoldBox.LostFocus += (_, _) => CommitNumbers();
        IdleBox.LostFocus += (_, _) => CommitNumbers();

        Closing += (_, _) =>
        {
            Keyboard.ClearFocus();
            CommitNumbers();
        };

        PinNewBox.KeyDown += OnPinKey;
        PinConfirmBox.KeyDown += OnPinKey;
        PinNewShown.KeyDown += OnPinKey;
        PinConfirmShown.KeyDown += OnPinKey;

        PanicNewBox.KeyDown += OnPanicKey;
        PanicConfirmBox.KeyDown += OnPanicKey;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !IsHotkeyFieldFocused()) Close();
        };

        Loaded += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => NavigateToSection(_section)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void EnableMobile_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (EnableMobile.IsChecked == true
            && AppDialog.Show(this,
                "Enable a local HTTPS listener for explicitly paired phones on private IPv4 networks? Do not enable this on an untrusted network or forward its port from your router.",
                "Enable LAN control", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loading = true;
            EnableMobile.IsChecked = false;
            _loading = false;
            return;
        }

        _settings.MobileControlEnabled = EnableMobile.IsChecked == true;
        if (!_settings.MobileControlEnabled)
        {
            _settings.MobileTaskControlEnabled = false;
            _loading = true;
            AllowTaskControl.IsChecked = false;
            _loading = false;
            RevokeDisplayedInvite();
        }
        UpdateMobileDetails();
        Report(_validateAndApply());
        await _mobile.StartAsync();
        UpdateMobileDetails();
    }

    private async void AllowPublicTesting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AllowPublicTesting.IsChecked == true
            && AppDialog.Show(this,
                "This allows FrostLock to accept phone connections while Windows is using a Public network profile. Other devices on that network may be able to reach FrostLock. Continue only on a trusted test network.",
                "Unsafe development override", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loading = true;
            AllowPublicTesting.IsChecked = false;
            _loading = false;
            return;
        }

        _settings.AllowMobileOnPublicProfileForTesting = AllowPublicTesting.IsChecked == true;
        Report(_validateAndApply());
        await _mobile.StopAsync();
        await _mobile.StartAsync();
        UpdateMobileDetails();
    }

    private void AllowTaskControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AllowTaskControl.IsChecked == true
            && AppDialog.Show(this,
                "Any paired phone will be able to end programs and start new ones on this PC. FrostLock and Windows core processes stay protected, but unsaved work in a closed program can be lost. Continue?",
                "Enable phone task control", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loading = true;
            AllowTaskControl.IsChecked = false;
            _loading = false;
            return;
        }

        _settings.MobileTaskControlEnabled = AllowTaskControl.IsChecked == true;
        Report(_validateAndApply());
    }

    private void AllowPinRelease_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AllowPinRelease.IsChecked == true && !_settings.MobilePinReleaseAllowed)
        {
            _loading = true;
            AllowPinRelease.IsChecked = false;
            _loading = false;
            PinReleaseConfirmation.Visibility = Visibility.Visible;
            PinReleaseConfirmation.BringIntoView();
            return;
        }

        _settings.MobilePinReleaseAllowed = AllowPinRelease.IsChecked == true;
        PinReleaseConfirmation.Visibility = Visibility.Collapsed;
        Report(_validateAndApply());
    }

    private void ConfirmPinRelease_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        AllowPinRelease.IsChecked = true;
        _loading = false;
        PinReleaseConfirmation.Visibility = Visibility.Collapsed;
        _settings.MobilePinReleaseAllowed = true;
        Report(_validateAndApply());
    }

    private void CancelPinRelease_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        AllowPinRelease.IsChecked = false;
        _loading = false;
        PinReleaseConfirmation.Visibility = Visibility.Collapsed;
    }

    private void Mobile_NetworkChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RevokeDisplayedInvite();
            UpdateMobileDetails();
        });
    }

    private void Mobile_PairingCompleted(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RevokeDisplayedInvite();
            UpdateMobileDetails();
        });
    }

    private void UpdateMobileDetails()
    {
        MobileDetailsPanel.Visibility = _settings.MobileControlEnabled ? Visibility.Visible : Visibility.Collapsed;
        var listening = _mobile.ListeningAddresses;
        var selectedAddress = AddressBox.SelectedItem as string;
        var addresses = listening.Select(address => address.ToString()).ToArray();
        AddressBox.ItemsSource = addresses;
        if (selectedAddress is not null && addresses.Contains(selectedAddress, StringComparer.Ordinal))
            AddressBox.SelectedItem = selectedAddress;
        else if (addresses.Length > 0)
            AddressBox.SelectedIndex = 0;

        AddressBox.IsEnabled = _settings.MobileControlEnabled && addresses.Length > 0;
        CreateInviteButton.IsEnabled = _settings.MobileControlEnabled && addresses.Length > 0;

        UpdateMobileStatus();

        var devices = _mobile.Devices;
        DevicesList.ItemsSource = devices;
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMobileStatus()
    {
        ListenerStatus.Text = !_settings.MobileControlEnabled
            ? "LAN control is off."
            : _mobile.IsRunning
                ? _settings.AllowMobileOnPublicProfileForTesting
                    ? $"Development override active on private IPv4, port {MobileControlService.Port}."
                    : $"Listening on private IPv4, port {MobileControlService.Port}. Windows Firewall may need a private-profile rule."
                : $"Not listening. {_mobile.LastError ?? "No private IPv4 interface is available."}";

    }

    private void CreateInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_pairingQrWindow is { IsVisible: true })
        {
            _pairingQrWindow.Activate();
            _pairingQrWindow.RefreshCode();
            return;
        }

        if (AddressBox.SelectedItem is not string addressText || !IPAddress.TryParse(addressText, out var address))
        {
            UpdateMobileDetails();
            return;
        }

        try
        {
            var window = new MobilePairingQrWindow(
                _mobile.CreateInvite(address),
                () => _mobile.CreateInvite(address),
                _mobile.RevokeInvite)
            {
                Owner = this
            };
            _pairingQrWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_pairingQrWindow, window)) _pairingQrWindow = null;
            };
            window.Show();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Could not create pairing code", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateMobileDetails();
        }
    }

    private void RevokeDisplayedInvite()
    {
        _pairingQrWindow?.Close();
        _pairingQrWindow = null;
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PairedMobileDevice device) return;
        if (AppDialog.Show(this, $"Remove {device.Name} from this PC? It will no longer be able to connect to FrostLock here.",
                "Revoke paired phone", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _mobile.RevokeDevice(device.DeviceId);
        UpdateMobileDetails();
    }

    private bool IsHotkeyFieldFocused()
        => ReferenceEquals(Keyboard.FocusedElement, TxtUnlockHotkey)
           || ReferenceEquals(Keyboard.FocusedElement, TxtStartHotkey)
           || ReferenceEquals(Keyboard.FocusedElement, TxtExtendHotkey);

    private void LockScreen_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        if (StyleBox.SelectedItem is ComboBoxItem { Tag: string styleTag }
            && Enum.TryParse<LockScreenStyle>(styleTag, out var style))
            _settings.LockScreenStyle = style;

        _settings.LockScreenOnAllDisplays = DisplaysBox.SelectedItem is ComboBoxItem { Tag: "All" };

        Report(_validateAndApply());
    }

    private void BrowseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a lock-screen background",
            CheckFileExists = true,
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        _settings.LockScreenBackgroundPath = dialog.FileName;
        UpdateBackgroundLabel();
        Report(_validateAndApply());
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        _settings.LockScreenBackgroundPath = string.Empty;
        UpdateBackgroundLabel();
        Report(_validateAndApply());
    }

    private void UpdateBackgroundLabel()
    {
        var path = _settings.LockScreenBackgroundPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            TxtBackgroundName.Text = "No image selected.";
            TxtBackgroundName.ToolTip = null;
            BtnClearBackground.IsEnabled = false;
            return;
        }

        BtnClearBackground.IsEnabled = true;
        TxtBackgroundName.ToolTip = path;
        TxtBackgroundName.Text = File.Exists(path)
            ? Path.GetFileName(path)
            : $"{Path.GetFileName(path)} (file not found)";
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

        if (HoldBox.Value is { } hold && !double.IsNaN(hold))
            _settings.UnlockHoldMs = (int)Math.Round(Math.Clamp(hold, 0d, 5d) * 1000);

        if (IdleBox.Value is { } idle && !double.IsNaN(idle))
            _settings.IdleFreezeMinutes = (int)Math.Round(Math.Clamp(idle, 1d, 1440d));

        _loading = true;
        ArmBox.Value = _settings.ArmSeconds;
        SafetyBox.Value = _settings.SafetyMaxSeconds / 60;
        HoldBox.Value = _settings.UnlockHoldMs / 1000.0;
        IdleBox.Value = _settings.IdleFreezeMinutes;
        _loading = false;

        Report(_validateAndApply());
    }

    private void UpdateIdleBoxState()
    {
        var enabled = ChkIdleFreeze.IsChecked == true;
        IdleBox.IsEnabled = enabled;
        IdleOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_loading) return;
        if (sender is not Wpf.Ui.Controls.TextBox box) return;

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
        bool editingStart = ReferenceEquals(box, TxtStartHotkey);
        var others = editingUnlock
            ? new[] { _settings.StartHotkey, _settings.ExtendHotkey }
            : editingStart
                ? new[] { _settings.UnlockHotkey, _settings.ExtendHotkey }
                : new[] { _settings.UnlockHotkey, _settings.StartHotkey };

        if (others.Any(other => Hotkeys.TryParse(other, out var parsed) && parsed == binding))
        {
            Report("Unlock, start and extend hotkeys must be different combinations.");
            e.Handled = true;
            return;
        }

        if (editingUnlock)
            _settings.UnlockHotkey = binding.Display;
        else if (editingStart)
            _settings.StartHotkey = binding.Display;
        else
            _settings.ExtendHotkey = binding.Display;

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

    private void OnPanicKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SavePanic_Click(sender, e);
            e.Handled = true;
        }
    }

    private void EditJson_Click(object sender, RoutedEventArgs e)
    {
        var editor = new SettingsFileWindow(_settings, s => ApplyImportedSettings(s)) { Owner = this };

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
            File.WriteAllText(dialog.FileName, SettingsService.Serialize(_settings, includeSecrets: false));
            ShowConfigStatus(Wpf.Ui.Controls.InfoBarSeverity.Success,
                "Settings exported", "Saved without the PIN. " + dialog.FileName);
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

            var answer = AppDialog.Show(this,
                "Replace all current settings with the values from this file?\n\n" +
                dialog.FileName + "\n\n" +
                "The PIN and emergency phrase already set on this PC are kept. Lifetime statistics come from the file.",
                "Import settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            var hotkeyError = ApplyImportedSettings(imported, preservePin: true);

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

    private string? ApplyImportedSettings(AppSettings imported, bool preservePin = false)
    {
        var pinSalt = _settings.PinSalt;
        var pinHash = _settings.PinHash;
        var pinFailures = _settings.PinFailures;
        var pinLockedUntilUtc = _settings.PinLockedUntilUtc;
        var pinLockoutLevel = _settings.PinLockoutLevel;
        var panicSalt = _settings.PanicSalt;
        var panicHash = _settings.PanicHash;
        var panicFailures = _settings.PanicFailures;
        var panicLockedUntilUtc = _settings.PanicLockedUntilUtc;
        var panicLockoutLevel = _settings.PanicLockoutLevel;

        _settings.CopyFrom(imported, includeStats: true);

        if (preservePin)
        {
            _settings.PinSalt = pinSalt;
            _settings.PinHash = pinHash;
            _settings.PinFailures = pinFailures;
            _settings.PinLockedUntilUtc = pinLockedUntilUtc;
            _settings.PinLockoutLevel = pinLockoutLevel;
            _settings.PanicSalt = panicSalt;
            _settings.PanicHash = panicHash;
            _settings.PanicFailures = panicFailures;
            _settings.PanicLockedUntilUtc = panicLockedUntilUtc;
            _settings.PanicLockoutLevel = panicLockoutLevel;
        }

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
