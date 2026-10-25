using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;

    private bool _loading;
    private bool _exiting;
    private bool _settingsHotkeysSuspended;
    private bool _initialized;
    private bool _isAdmin;
    private bool _wasVisibleBeforeFreeze;

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

        _tray = new TrayService();
        _tray.FreezeRequested += StartFreeze;
        _tray.UnlockRequested += Panic;
        _tray.ShowRequested += ShowFromTray;
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += ExitApplication;

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
                $"Version {result.LatestVersion} is available. Open About to get it.");
    }

    public void StartFreezeFromExternal() => StartFreeze();

    public void Panic() => _controller.Release(FreezeEndReason.UserUnlocked);

    public void DisposeShell()
    {
        _exiting = true;

        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        _settingsWindow = null;

        try { _aboutWindow?.Close(); } catch { /* ignore */ }
        _aboutWindow = null;

        _tray?.Dispose();
        _tray = null;

        _hotkeys.Dispose();
    }

    // Freeze control

    /// <summary>Set from the command line; applies to the next freeze, then clears.</summary>
    public FreezeMode? ModeOverride { get; set; }

    private void StartFreeze()
    {
        if (_controller.IsActive)
        {
            _controller.Release(FreezeEndReason.UserUnlocked);
            return;
        }

        SaveSettings();

        var mode = ModeOverride ?? _settings.Mode;
        ModeOverride = null;

        switch (_controller.TryStart(mode))
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
            var args = $"--freeze --minimized --allow-instance --mode hard " +
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
        _exiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    // Controller events

    private void OnFreezeStarted(object? sender, FreezeStartedEventArgs e)
    {
        _wasVisibleBeforeFreeze = IsVisible;
        if (IsVisible) Hide();

        _tray?.SetFrozen(true);
    }

    private void OnFreezeEnded(object? sender, FreezeEndedEventArgs e)
    {
        _tray?.SetFrozen(false);
        UpdateStatusIdle();
        UpdateStats();

        if (_wasVisibleBeforeFreeze && !_exiting)
        {
            ShowFromTray();
            _tray?.ShowBalloon("Freeze released", $"Input is yours again after {FormatDuration(e.Elapsed)}.");
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

        _tray?.SetFrozen(true);
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
        if (!int.TryParse(item.Tag.ToString(), out var seconds)) return;

        _settings.DurationSeconds = seconds;
        SaveSettings();
        UpdateStatusIdle();
    }

    private void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ModeBox.SelectedItem is not ComboBoxItem { Tag: not null } item) return;
        if (!Enum.TryParse<FreezeMode>(item.Tag.ToString(), out var mode)) return;

        _settings.Mode = mode;
        SaveSettings();
        UpdateModePresentation();
        UpdateStatusIdle();
    }

    private void LoadSettingsToUi()
    {
        _loading = true;

        SelectByTag(ModeBox, _settings.Mode.ToString());

        SelectByTag(DurationBox, _settings.DurationSeconds switch
        {
            30 => "30",
            60 => "60",
            300 => "300",
            900 => "900",
            _ => "0"
        });

        ChkBlockAllKeys.IsChecked = _settings.BlockAllKeys;
        ChkBlockMouse.IsChecked = _settings.BlockMouse;
        ChkSystemKeys.IsChecked = _settings.BlockSystemKeys;
        ChkKeepAwake.IsChecked = _settings.KeepDisplayAwake;

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
            _ => string.Empty
        };

        AdminNotice.IsOpen = mode == FreezeMode.Hard && !_isAdmin;
        PinNotice.IsOpen = mode == FreezeMode.Pin && !_settings.HasPin;
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

        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.LockOpen24;
        StatusTitle.Text = "Ready to freeze";
        StatusDetail.Text = "Keyboard and mouse are yours.";
        StatusTime.Text = _settings.IsIndefinite
            ? "∞"
            : FormatDuration(TimeSpan.FromSeconds(_settings.DurationSeconds));
        BtnFreeze.Content = "Freeze input now";
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
        StartupService.Apply(_settings.StartWithWindows);

        UpdateHotkeyLabels();

        return ApplyHotkeys();
    }

    private string? ApplyHotkeys()
    {
        _hotkeys.UnregisterStart();

        // While the settings window is open the global start hotkey is released, so a
        // combination can be recorded without launching a freeze in the middle of it.
        if (_settingsHotkeysSuspended) return null;

        if (Hotkeys.TryParse(_settings.StartHotkey, out var binding) && binding.IsValid)
        {
            if (!_hotkeys.RegisterStart(binding))
                return _hotkeys.LastError;
        }

        return null;
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
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
    }

    // Window commands

    private void Freeze_Click(object sender, RoutedEventArgs e) => StartFreeze();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void About_Click(object sender, RoutedEventArgs e) => OpenAbout();

    private void OpenAbout()
    {
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
