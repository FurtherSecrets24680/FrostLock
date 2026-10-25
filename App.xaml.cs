using System.Threading;
using System.Windows;
using FrostLock.Models;
using FrostLock.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FrostLock;

public partial class App : System.Windows.Application
{
    private const string MutexName = "FrostLock.SingleInstance.v1";
    private const string ShowEventName = "FrostLock.Evt.Show.v1";
    private const string FreezeEventName = "FrostLock.Evt.Freeze.v1";
    private const string PanicEventName = "FrostLock.Evt.Panic.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _freezeEvent;
    private EventWaitHandle? _panicEvent;
    private CancellationTokenSource? _listenerCts;

    public AppSettings Settings { get; private set; } = new();
    public FreezeController Controller { get; private set; } = null!;

    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Adopt the Windows 11 look: follow the system light/dark setting, use the system
        // accent colour, and let the window backdrop be Mica.
        var theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);

        var options = CliOptions.Parse(e.Args);

        // Always create the mutex, even with --allow-instance, so CLI commands (--panic /
        // --freeze) can still reach an instance that was started elevated with --allow-instance.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew && !options.AllowInstance)
        {
            SignalExistingInstance(options);
            Shutdown();
            return;
        }

        Settings = SettingsService.Load();

        ThemeService.Apply(Settings.Theme);
        ThemeService.StartWatching();

        StartupService.Apply(Settings.StartWithWindows);

        // CLI overrides are session-scoped; they never overwrite the user's saved defaults.
        Controller = new FreezeController(Settings, Dispatcher)
        {
            DurationOverrideSeconds = options.SecondsOverride,
            ArmOverrideSeconds = options.ArmOverride
        };

        _window = new MainWindow(Settings, Controller)
        {
            ModeOverride = options.ModeOverride
        };
        MainWindow = _window;

        bool startHidden = options.StartMinimized || Settings.StartMinimized;
        _window.Initialize(startHidden);

        StartEventListener();

        if (options.Freeze)
            Dispatcher.BeginInvoke(() => _window.StartFreezeFromExternal());
        else if (options.Panic)
            Dispatcher.BeginInvoke(() => _window.Panic());
    }

    private void StartEventListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _freezeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, FreezeEventName);
        _panicEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PanicEventName);

        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _freezeEvent, _panicEvent };
            while (!token.IsCancellationRequested)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(handles, 500);
                }
                catch
                {
                    return;
                }

                if (index < 0) continue;

                Dispatcher.BeginInvoke(() =>
                {
                    if (_window is null) return;
                    switch (index)
                    {
                        case 0: _window.ShowFromTray(); break;
                        case 1: _window.StartFreezeFromExternal(); break;
                        case 2: _window.Panic(); break;
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "FrostLock.InstanceListener"
        };
        thread.Start();
    }

    private static void SignalExistingInstance(CliOptions options)
    {
        try
        {
            var name = options.Panic ? PanicEventName : options.Freeze ? FreezeEventName : ShowEventName;
            EventWaitHandle.OpenExisting(name).Set();
        }
        catch
        {
            // Existing instance not reachable; nothing else to do.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _listenerCts?.Cancel(); } catch { /* ignore */ }
        try { _window?.DisposeShell(); } catch { /* ignore */ }
        try { Controller?.Dispose(); } catch { /* ignore */ }

        _showEvent?.Dispose();
        _freezeEvent?.Dispose();
        _panicEvent?.Dispose();

        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();

        base.OnExit(e);
    }
}

/// <summary>Parsed command line for automation and the elevated relaunch.</summary>
internal sealed class CliOptions
{
    public bool Freeze { get; private set; }
    public bool Panic { get; private set; }
    public bool StartMinimized { get; private set; }
    public bool AllowInstance { get; private set; }
    public FreezeMode? ModeOverride { get; private set; }
    public int? SecondsOverride { get; private set; }
    public int? ArmOverride { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim();
            switch (a.ToLowerInvariant())
            {
                case "--freeze":
                case "-f":
                    o.Freeze = true;
                    break;
                case "--panic":
                    o.Panic = true;
                    break;
                case "--minimized":
                case "--tray":
                case "-m":
                    o.StartMinimized = true;
                    break;
                case "--allow-instance":
                    o.AllowInstance = true;
                    break;
                case "--mode":
                    if (i + 1 < args.Length && Enum.TryParse<FreezeMode>(args[++i], ignoreCase: true, out var mode))
                        o.ModeOverride = mode;
                    break;
                case "--seconds":
                case "-s":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sec) && sec >= 0)
                        o.SecondsOverride = sec;
                    break;
                case "--arm":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var arm) && arm >= 0)
                        o.ArmOverride = arm;
                    break;
            }
        }

        return o;
    }
}