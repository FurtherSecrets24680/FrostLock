using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FrostLock.Interop;
using FrostLock.Models;
using FrostLock.Services;
using FrostLock.Views;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace FrostLock;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings _settings;
    private readonly FreezeController _controller;

    private readonly HotkeyService _hotkeys = new();
    private readonly UsbDriveService _usbDrive = new();
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;

    private bool _loading;
    private bool _exiting;
    private bool _settingsHotkeysSuspended;
    private bool _initialized;
    private bool _isAdmin;
    private bool _wasVisibleBeforeFreeze;
    private bool _usbWatching;
    private bool _usbFreezeActive;
    private bool _usbReverseBehavior;

    public MainWindow(AppSettings settings, FreezeController controller)
    {
        InitializeComponent();
        _settings = settings;
        _controller = controller;
    }

    // Lifecycle

    public void Initialize(bool startHidden)
    {
        if (_initialized) return;
        _initialized = true;

        var icon = IconFactory.WindowIconSource();
        Icon = icon;

        if (icon is not null)
            AppTitleBar.Icon = new Wpf.Ui.Controls.ImageIcon { Source = icon };

        _hotkeys.Attach(this);
        _hotkeys.StartPressed += StartFreeze;
        _usbDrive.Attach(this);
        _usbDrive.TargetPresenceChanged += OnUsbTargetPresenceChanged;

        _tray = new TrayService();
        _tray.FreezeRequested += StartFreeze;
        _tray.UnlockRequested += Panic;
        _tray.ShowRequested += ShowFromTray;
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += ExitApplication;
        _tray.StopWatchingRequested += StopUsbWatching;

        _controller.Started += OnFreezeStarted;
        _controller.Ended += OnFreezeEnded;
        _controller.Tick += OnFreezeTick;

        WireOptionToggles();

        ApplyHotkeys();
        LoadSettingsToUi();
        UpdateAdminBadge();

        if (_settings.CheckForUpdatesOnStartup)
            _ = CheckForUpdatesAsync();

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
                Hide();
        };

        if (!startHidden)
        {
            Show();
            Activate();
        }
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Background check on start. Silent unless a newer release exists.</summary>
    private async Task CheckForUpdatesAsync()
    {
        var result = await UpdateService.CheckAsync();
        if (result.Succeeded && result.UpdateAvailable)
            _tray?.ShowBalloon(
                "FrostLock update available",
                $"Version {result.LatestVersion} is available. Open About to install it.");
    }

    public void StartFreezeFromExternal() => StartFreeze();

    private void OnUsbTargetPresenceChanged(bool inserted)
    {
        if (!_usbWatching) return;

        bool shouldFreeze = _usbReverseBehavior ? !inserted : inserted;

        if (shouldFreeze)
        {
            if (_controller.IsActive) return;

            _usbFreezeActive = true;
            ModeOverride = FreezeMode.Strict;
            StartFreezeCore(fromUsb: true);
            if (!_controller.IsActive) _usbFreezeActive = false;
            return;
        }

        if (!_usbFreezeActive || !_controller.IsActive) return;
        _usbFreezeActive = false;
        _controller.RequestRelease(FreezeEndReason.UserUnlocked);
    }

    /// <summary>Executes a command received from another FrostLock process or the CLI.</summary>
    public InstanceReply Execute(InstanceCommand command)
    {
        switch (command.Command.Trim().ToLowerInvariant())
        {
            case "show":
                if (_controller.IsActive)
                    return InstanceReply.Fail("FrostLock is frozen; its settings window stays hidden until release.");
                ShowFromTray();
                return InstanceReply.Done("FrostLock window shown.");

            case "freeze":
                if (_controller.IsActive)
                    return InstanceReply.Fail("A freeze is already active.");

                if (command.Mode is not null)
                {
                    if (!Enum.TryParse<FreezeMode>(command.Mode, true, out var mode))
                        return InstanceReply.Fail($"Unknown freeze mode '{command.Mode}'.");
                    ModeOverride = mode;
                }

                _controller.DurationOverrideSeconds = command.DurationSeconds;
                _controller.ArmOverrideSeconds = command.ArmSeconds;
                _controller.PlaySoundOverride = command.Sound;

                if (!IsVisible) Hide();
                StartFreeze();

                _controller.DurationOverrideSeconds = null;
                _controller.ArmOverrideSeconds = null;
                _controller.PlaySoundOverride = null;

                return _controller.IsActive
                    ? InstanceReply.Done("Freeze started.")
                    : InstanceReply.Fail("The freeze could not be started.");

            case "panic":
                if (!_controller.IsActive)
                    return InstanceReply.Done("No freeze is active.");
                if (!_controller.RequestRelease(FreezeEndReason.UserUnlocked))
                    return InstanceReply.Fail("PIN lock is active; enter the PIN or emergency phrase on its lock screen.");
                return InstanceReply.Done("Freeze released.");

            case "lock":
                try
                {
                    return Native.LockWorkStation()
                        ? InstanceReply.Done("The Windows session was locked.")
                        : InstanceReply.Fail("Windows could not lock the session.");
                }
                catch (Exception ex)
                {
                    return InstanceReply.Fail($"Could not lock the session: {ex.Message}");
                }

            case "status":
                return InstanceReply.Done(_controller.IsActive
                    ? $"Active: {_controller.Mode}; phase {_controller.Phase}."
                    : "Idle: no freeze is active.");

            case "exit":
                if (_controller.RequiresPin)
                    return InstanceReply.Fail("PIN lock is active; enter the PIN or emergency phrase before exiting.");
                ExitApplication();
                return InstanceReply.Done("FrostLock is exiting.");

            default:
                return InstanceReply.Fail($"Unknown FrostLock command '{command.Command}'.");
        }
    }

    public void Panic()
    {
        if (_controller.RequestRelease(FreezeEndReason.UserUnlocked)) return;

        // A PIN freeze only opens from the lock screen.
        _tray?.ShowBalloon("PIN lock is active", "Enter the PIN on the lock screen to unlock.");
    }

    public void DisposeShell()
    {
        _exiting = true;

        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        _settingsWindow = null;

        try { _aboutWindow?.Close(); } catch { /* ignore */ }
        _aboutWindow = null;

        _tray?.Dispose();
        _tray = null;

        _usbDrive.Configure(false, null);
        _usbDrive.Dispose();
        _hotkeys.Dispose();
    }

    // Freeze control

    /// <summary>Set from the command line; applies to the next freeze, then clears.</summary>
    public FreezeMode? ModeOverride { get; set; }

    private void StartFreeze() => StartFreezeCore(fromUsb: false);

    private void StartFreezeCore(bool fromUsb)
    {
        if (_usbWatching && !fromUsb)
        {
            StopUsbWatching();
            return;
        }

        if (_controller.IsActive)
        {
            if (!_controller.RequestRelease(FreezeEndReason.UserUnlocked))
                _tray?.ShowBalloon("PIN lock is active", "Enter the PIN on the lock screen to unlock.");

            return;
        }

        SaveSettings();

        var mode = ModeOverride ?? _settings.Mode;
        ModeOverride = null;

        if (mode == FreezeMode.UsbKey)
        {
            StartUsbWatching();
            return;
        }

        switch (_controller.TryStart(mode, allowUnlockHotkey: !fromUsb))
        {
            case StartResult.NeedsElevation:
                PromptElevation();
                break;

            case StartResult.PinNotSet:
                MessageBox.Show(this,
                    "PIN lock needs a PIN. Set one in Settings, then start the lock again.",
                    "PIN required", MessageBoxButton.OK, MessageBoxImage.Information);
                OpenSettings();
                break;

            case StartResult.Failed:
                MessageBox.Show(this,
                    "FrostLock could not engage the input blocker. Another program may already be blocking input.",
                    "FrostLock", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void StartUsbWatching()
    {
        if (UsbDriveBox.SelectedItem is not UsbDriveOption selected)
        {
            RefreshUsbDrives();
            UsbWatchStatus.Text = "Connect a removable USB drive, select it, then start watching.";
            if (!IsVisible) ShowFromTray();
            return;
        }

        _usbDrive.Configure(true, selected.VolumeSerial);
        _usbReverseBehavior = UsbBehaviorBox.SelectedIndex == 1;
        _usbWatching = true;
        _usbFreezeActive = false;
        UsbDriveBox.IsEnabled = false;
        BtnRefreshUsbDrives.IsEnabled = false;
        UsbBehaviorBox.IsEnabled = false;
        _tray?.SetUsbWatching(true);
        UpdateUsbWatchStatus();
        UpdateStatusIdle();
    }

    private void StopUsbWatching()
    {
        if (!_usbWatching) return;

        _usbWatching = false;
        _usbDrive.Configure(false, null);
        UsbDriveBox.IsEnabled = true;
        BtnRefreshUsbDrives.IsEnabled = true;
        UsbBehaviorBox.IsEnabled = true;
        _tray?.SetUsbWatching(false);

        if (_usbFreezeActive && _controller.IsActive)
        {
            _usbFreezeActive = false;
            _controller.RequestRelease(FreezeEndReason.UserUnlocked);
        }
        else
        {
            _usbFreezeActive = false;
        }

        UsbWatchStatus.Text = "USB watching stopped.";
        UpdateStatusIdle();
    }

    private void RefreshUsbDrives_Click(object sender, RoutedEventArgs e) => RefreshUsbDrives();

    private void RefreshUsbDrives()
    {
        var selectedSerial = (UsbDriveBox.SelectedItem as UsbDriveOption)?.VolumeSerial;
        UsbDriveBox.ItemsSource = UsbDriveService.GetAvailableDrives();
        if (!string.IsNullOrWhiteSpace(selectedSerial))
            UsbDriveBox.SelectedValue = selectedSerial;
        if (UsbDriveBox.SelectedIndex < 0 && UsbDriveBox.Items.Count > 0)
            UsbDriveBox.SelectedIndex = 0;

        UpdateUsbWatchStatus();
    }

    private void UpdateUsbWatchStatus()
    {
        var selected = UsbDriveBox.SelectedItem as UsbDriveOption;
        if (!_usbWatching)
        {
            UsbWatchStatus.Text = selected is null
                ? "Not watching. Connect and select a removable drive."
                : $"Ready to watch {selected.DisplayName}.";
            return;
        }

        var driveName = selected?.DisplayName ?? "USB drive";
        if (_usbFreezeActive)
        {
            UsbWatchStatus.Text = $"Watching {driveName}. " +
                                  (_usbReverseBehavior
                                      ? "Insert it to release the freeze, or stop watching."
                                      : "Remove it to release the freeze, or stop watching.");
        }
        else if (_usbReverseBehavior)
        {
            UsbWatchStatus.Text = $"Watching {driveName}. " +
                                  (_usbDrive.TargetPresent
                                      ? "Remove it to start a freeze."
                                      : "Connect it, then remove it to start a freeze.");
        }
        else
        {
            UsbWatchStatus.Text = $"Watching {driveName}. " +
                                  (_usbDrive.TargetPresent
                                      ? "Remove and reinsert it to start a freeze."
                                      : "Insert it to freeze; remove it to release.");
        }
    }

    private void PromptElevation()
    {
        var answer = MessageBox.Show(this,
            "Hard freeze uses the Win32 BlockInput call, which requires administrator rights.\n\n" +
            "Restart FrostLock elevated and begin the freeze now?",
            "Administrator rights required", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            var args = $"--freeze --allow-instance --mode hard " +
                       $"--seconds {_settings.DurationSeconds} --arm {_settings.ArmSeconds}";

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args
            });

            ExitApplication();
        }
        catch (Win32Exception)
        {
            // The user dismissed the UAC prompt.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "FrostLock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitApplication()
    {
        if (_controller.RequiresPin)
        {
            _tray?.ShowBalloon("PIN lock is active",
                "Enter the PIN on the lock screen before exiting.");
            return;
        }

        _exiting = true;

        if (_controller.IsActive)
            _controller.Release(FreezeEndReason.Shutdown);

        System.Windows.Application.Current.Shutdown();
    }

    // Controller events

    private void OnFreezeStarted(object? sender, FreezeStartedEventArgs e)
    {
        _wasVisibleBeforeFreeze = IsVisible;
        if (IsVisible) Hide();

        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        _settingsWindow = null;

        try { _aboutWindow?.Close(); } catch { /* ignore */ }
        _aboutWindow = null;

        BtnFreeze.ToolTip = null;
        ModeBox.ToolTip = null;

        _tray?.SetFrozen(true, !_controller.RequiresPin);
    }

    private void OnFreezeEnded(object? sender, FreezeEndedEventArgs e)
    {
        _usbFreezeActive = false;
        _tray?.SetFrozen(false);
        _tray?.SetUsbWatching(_usbWatching);
        UpdateStatusIdle();
        UpdateStats();

        if (_wasVisibleBeforeFreeze && !_exiting)
        {
            ShowFromTray();

            var body = e.Reason switch
            {
                FreezeEndReason.Aborted => "The freeze was cancelled before it started.",
                FreezeEndReason.Watchdog => "Input was released because FrostLock stopped responding.",
                _ => $"Input is yours again after {FormatDuration(e.Elapsed)}."
            };

            _tray?.ShowBalloon("Freeze released", body);
        }
    }

    private void OnFreezeTick(object? sender, OverlayState state)
    {
        bool arming = state.Phase == FreezePhase.Arming;

        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.LockClosed24;
        StatusTitle.Text = arming ? "Arming freeze" : "Input frozen";
        StatusDetail.Text = arming
            ? "Input is taken away shortly. Press Esc or Cancel on the lock screen to abort."
            : state.UnlockHint;

        StatusTime.Text = state.Remaining switch
        {
            null => "∞",
            { } left when arming => Math.Max(1, (int)Math.Ceiling(left.TotalSeconds)).ToString(),
            { } left => FormatDuration(left)
        };

        BtnFreeze.Content = arming ? "Cancel freeze" : "Release now";

        _tray?.SetFrozen(true, !state.RequiresPin);
        _tray?.SetTooltip($"FrostLock — {state.ModeLabel}: {StatusDetail.Text}");
    }

    // UI wiring

    private void WireOptionToggles()
    {
        BindToggle(ChkBlockAllKeys, v => _settings.BlockAllKeys = v);
        BindToggle(ChkBlockMouse, v => _settings.BlockMouse = v);
        BindToggle(ChkSystemKeys, v => _settings.BlockSystemKeys = v);
        BindToggle(ChkKeepAwake, v => _settings.KeepDisplayAwake = v);
    }

    private void BindToggle(ToggleSwitch toggle, Action<bool> setter)
    {
        toggle.Checked += (_, _) =>
        {
            if (_loading) return;
            setter(true);
            SaveSettings();
        };

        toggle.Unchecked += (_, _) =>
        {
            if (_loading) return;
            setter(false);
            SaveSettings();
        };
    }

    private void Duration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DurationBox.SelectedItem is not ComboBoxItem { Tag: not null } item) return;

        if (string.Equals(item.Tag.ToString(), "Custom", StringComparison.OrdinalIgnoreCase))
        {
            CustomDurationRow.Visibility = Visibility.Visible;
            _loading = true;
            CustomDurationBox.Value = Math.Clamp(
                (int)Math.Ceiling(_settings.DurationSeconds / 60d), 1, 1440);
            _loading = false;
            return;
        }

        CustomDurationRow.Visibility = Visibility.Collapsed;
        if (!int.TryParse(item.Tag.ToString(), out var seconds)) return;

        _settings.DurationSeconds = seconds;
        SaveSettings();
        UpdateStatusIdle();
    }

    private void CustomDuration_ValueChanged(object sender, Wpf.Ui.Controls.NumberBoxValueChangedEventArgs e)
        => ApplyCustomDuration();

    private void CustomDuration_LostFocus(object sender, RoutedEventArgs e)
        => ApplyCustomDuration();

    private void ApplyCustomDuration()
    {
        if (_loading || DurationBox.SelectedItem is not ComboBoxItem { Tag: "Custom" }) return;
        if (CustomDurationBox.Value is not { } value || !double.IsFinite(value)) return;

        var minutes = Math.Clamp((int)Math.Round(value), 1, 1440);
        var seconds = minutes * 60;
        if (_settings.DurationSeconds == seconds) return;

        _settings.DurationSeconds = seconds;
        SaveSettings();
        UpdateStatusIdle();
    }

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ModeBox.SelectedItem is not ComboBoxItem { Tag: not null } item) return;
        if (!Enum.TryParse<FreezeMode>(item.Tag.ToString(), out var mode)) return;

        if (_usbWatching && mode != FreezeMode.UsbKey)
            StopUsbWatching();

        _settings.Mode = mode;
        SaveSettings();
        UpdateModePresentation();
        UpdateStatusIdle();
    }

    private void LoadSettingsToUi()
    {
        _loading = true;

        SelectByTag(ModeBox, _settings.Mode.ToString());

        var durationTag = _settings.DurationSeconds switch
        {
            0 => "0",
            30 => "30",
            60 => "60",
            300 => "300",
            900 => "900",
            _ => "Custom"
        };
        SelectByTag(DurationBox, durationTag);
        CustomDurationBox.Value = Math.Clamp(
            (int)Math.Ceiling(_settings.DurationSeconds / 60d), 1, 1440);
        CustomDurationRow.Visibility = durationTag == "Custom"
            ? Visibility.Visible
            : Visibility.Collapsed;

        ChkBlockAllKeys.IsChecked = _settings.BlockAllKeys;
        ChkBlockMouse.IsChecked = _settings.BlockMouse;
        ChkSystemKeys.IsChecked = _settings.BlockSystemKeys;
        ChkKeepAwake.IsChecked = _settings.KeepDisplayAwake;

        RefreshUsbDrives();

        _loading = false;

        UpdateModePresentation();
        UpdateHotkeyLabels();
        UpdateStats();
        UpdateStatusIdle();
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private void UpdateModePresentation()
    {
        var mode = _settings.Mode;

        StrictOptions.Visibility = mode == FreezeMode.Strict ? Visibility.Visible : Visibility.Collapsed;
        UsbWatchPanel.Visibility = mode == FreezeMode.UsbKey ? Visibility.Visible : Visibility.Collapsed;

        ModeDescription.Text = mode switch
        {
            FreezeMode.Gentle =>
                "A full-screen lock screen with the system keys suppressed. The mouse still works on it.",
            FreezeMode.Strict =>
                "Adds a global low-level input hook on top of the lock screen. No administrator rights needed.",
            FreezeMode.Pin =>
                "A lock screen that needs your PIN. Typing and the mouse still work, but Windows shortcuts are suppressed.",
            FreezeMode.Hard =>
                "Freezes input at the OS level with BlockInput. Requires administrator rights.",
            FreezeMode.UsbKey =>
                "Watches one removable USB drive. Choose whether insertion or removal freezes and releases; select the drive and start watching.",
            _ => string.Empty
        };

        AdminNotice.IsOpen = mode == FreezeMode.Hard && !_isAdmin;
        PinNotice.IsOpen = mode == FreezeMode.Pin && !_settings.HasPin;

        UpdateFreezeToolTip();
    }

    private void ModeComparison_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ModeComparisonWindow { Owner = this };
        dialog.ShowDialog();
    }

    private void UpdateFreezeToolTip()
    {
        if (_settings.Mode != FreezeMode.Hard)
        {
            BtnFreeze.ToolTip = null;
            ModeBox.ToolTip = null;
            return;
        }

        var text = _isAdmin
            ? "Hard freeze is running elevated. "
            : "Hard freeze needs administrator rights; FrostLock will restart elevated. ";

        text += "While it is active, press Ctrl+Alt+Del to drop the OS input block, then click Unlock.";

        if (_settings.IsIndefinite)
        {
            text += _settings.SafetyMaxSeconds > 0
                ? " The safety cap also releases it."
                : " With no timer and no safety cap, that is the only way out.";
        }
        else
        {
            text += " The timer also releases it.";
        }

        BtnFreeze.ToolTip = text;
        ModeBox.ToolTip = text;
    }

    private void UpdateHotkeyLabels()
    {
        var start = Hotkeys.TryParse(_settings.StartHotkey, out var s) ? s.Display : _settings.StartHotkey;
        var unlock = Hotkeys.TryParse(_settings.UnlockHotkey, out var u) ? u.Display : _settings.UnlockHotkey;
        TxtHotkeys.Text = $"Start {start}  ·  Unlock {unlock}";
    }

    private void UpdateAdminBadge()
    {
        _isAdmin = FreezeController.IsAdministrator();
        TxtRole.Text = _isAdmin ? "Running as administrator" : "Standard user";
        UpdateModePresentation();
    }

    private void UpdateStatusIdle()
    {
        if (_controller.IsActive) return;

        if (_usbWatching)
        {
            StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.UsbStick24;
            StatusTitle.Text = "Watching USB drive";
            StatusDetail.Text = _usbFreezeActive
                ? _usbReverseBehavior
                    ? "USB-triggered freeze is active. Insert the drive to release."
                    : "USB-triggered freeze is active. Remove the drive to release."
                : _usbDrive.TargetPresent
                    ? _usbReverseBehavior
                        ? "Drive is connected. Remove it to start a freeze."
                        : "Drive is still connected. Remove and reinsert it to trigger a freeze."
                    : _usbReverseBehavior
                        ? "Connect and remove the selected drive to start a freeze."
                        : "Waiting for the selected drive to be inserted.";
            StatusTime.Text = string.Empty;
            BtnFreeze.Content = "Stop watching";
            BtnFreeze.ToolTip = "Stop USB monitoring and release a USB-triggered freeze, if active.";
            UpdateUsbWatchStatus();
            return;
        }

        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.LockOpen24;
        StatusTitle.Text = _settings.Mode == FreezeMode.UsbKey ? "USB key mode ready" : "Ready to freeze";
        StatusDetail.Text = _settings.Mode == FreezeMode.UsbKey
            ? "Select a removable USB drive and start watching."
            : "Keyboard and mouse are yours.";
        StatusTime.Text = _settings.Mode == FreezeMode.UsbKey
            ? string.Empty
            : _settings.IsIndefinite
                ? "∞"
                : FormatDuration(TimeSpan.FromSeconds(_settings.DurationSeconds));
        BtnFreeze.Content = _settings.Mode == FreezeMode.UsbKey ? "Start watching" : "Freeze input now";
        BtnFreeze.ToolTip = null;
        UpdateFreezeToolTip();
    }

    private void UpdateStats()
    {
        TxtStats.Text = _settings.FreezeCount <= 0
            ? "No freezes yet"
            : $"{_settings.FreezeCount} freeze{(_settings.FreezeCount == 1 ? "" : "s")} · " +
              $"{FormatDuration(TimeSpan.FromSeconds(_settings.TotalFrozenSeconds))} frozen in total";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;

        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m";

        if (value.TotalMinutes >= 1)
            return value.Seconds == 0 ? $"{(int)value.TotalMinutes} min" : $"{(int)value.TotalMinutes}m {value.Seconds}s";

        return $"{(int)value.TotalSeconds} sec";
    }

    // Settings plumbing

    /// <summary>Persists settings and re-applies side effects. Returns a hotkey error, if any.</summary>
    private string? SaveSettings()
    {
        SettingsService.Save(_settings);
        StartupService.Apply(_settings.StartWithWindows, _settings.StartMinimized);

        UpdateHotkeyLabels();

        return ApplyHotkeys();
    }

    private string? ApplyHotkeys()
    {
        _hotkeys.UnregisterStart();

        // While the settings window is open the global start hotkey is released, so a
        // combination can be recorded without launching a freeze in the middle of it.
        // USB key mode is controlled by the selected drive and Stop watching instead.
        if (_settingsHotkeysSuspended || _settings.Mode == FreezeMode.UsbKey) return null;

        if (Hotkeys.TryParse(_settings.StartHotkey, out var binding) && binding.IsValid)
        {
            if (!_hotkeys.RegisterStart(binding))
                return _hotkeys.LastError;
        }

        return null;
    }

    private void OpenSettings() => OpenSettings(SettingsSection.General);

    private void OpenSettings(SettingsSection section)
    {
        if (_controller.IsActive)
        {
            _tray?.ShowBalloon("Freeze is active",
                "Settings are locked until the freeze ends.");
            return;
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            _settingsWindow.ShowSection(section);
            return;
        }

        var window = new SettingsWindow(_settings, SaveSettings);
        if (IsVisible) window.Owner = this;

        _settingsHotkeysSuspended = true;
        ApplyHotkeys();

        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _settingsHotkeysSuspended = false;
            LoadSettingsToUi();
            UpdateAdminBadge();
            ApplyHotkeys();
        };

        _settingsWindow = window;
        window.Show();
        window.ShowSection(section);
    }

    // Window commands

    private void Freeze_Click(object sender, RoutedEventArgs e) => StartFreeze();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Hotkeys_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsSection.Hotkeys);

    private void About_Click(object sender, RoutedEventArgs e) => OpenAbout();

    private void OpenAbout()
    {
        if (_controller.IsActive)
        {
            _tray?.ShowBalloon("Freeze is active",
                "Updates are locked until the freeze ends.");
            return;
        }

        if (_aboutWindow is { IsLoaded: true })
        {
            _aboutWindow.Activate();
            return;
        }

        var window = new AboutWindow();
        if (IsVisible) window.Owner = this;

        window.Closed += (_, _) => _aboutWindow = null;

        _aboutWindow = window;
        window.Show();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloon("FrostLock is still running",
                "Use the tray icon to freeze input, or reopen the window.");
            return;
        }

        base.OnClosing(e);
    }
}
