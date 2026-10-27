using System.Threading;
using System.Windows;
using FrostLock.Interop;
using FrostLock.Models;
using FrostLock.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FrostLock;

public partial class App : System.Windows.Application
{
    private const string MutexName = "FrostLock.SingleInstance.v1";

    private Mutex? _mutex;
    private CancellationTokenSource? _ipcCts;

    public AppSettings Settings { get; private set; } = new();
    public FreezeController Controller { get; private set; } = null!;

    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Input must never stay blocked because of an unhandled exception. The handlers
        // only ask the blockers to release; they do not swallow the crash.
        DispatcherUnhandledException += (_, _) => ReleaseInput();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseInput();

        var options = CliOptions.Parse(e.Args);

        // Console-only flags answer immediately: no window, no settings, no mutex.
        if (options.Help || options.Version)
        {
            ConsoleBridge.WriteLine(options.Help ? CliOptions.Usage : CliOptions.VersionLine);
            Shutdown();
            return;
        }

        if (options.Error is not null)
        {
            ConsoleBridge.WriteLine($"Error: {options.Error}\n\n{CliOptions.Usage}");
            Shutdown();
            return;
        }

        var theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);

        // Always create the mutex, even with --allow-instance, so a command can still reach
        // an instance that was started elevated with --allow-instance.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew && !options.AllowInstance)
        {
            var reply = InstanceChannel.Send(options.ToCommand(), TimeSpan.FromSeconds(5));

            // Only console commands print. A plain second launch just brings the window up.
            if (options.WantsConsole)
                ConsoleBridge.WriteLine(reply.Message);

            Shutdown();
            return;
        }

        if (options.Command is "status" or "exit" or "panic" or "lock")
        {
            RunStandalone(options);
            Shutdown();
            return;
        }

        Settings = SettingsService.Load();

        ThemeService.Apply(Settings.Theme);
        ThemeService.StartWatching();

        StartupService.Apply(Settings.StartWithWindows, Settings.StartMinimized);

        Controller = new FreezeController(Settings, Dispatcher);
        _window = new MainWindow(Settings, Controller);
        MainWindow = _window;

        bool startHidden = options.StartMinimized
                           || Settings.StartMinimized
                           || options.Command is "freeze" or "panic" or "lock" or "exit";

        _window.Initialize(startHidden);

        StartIpc();

        if (options.Command is not null and not "show")
            Dispatcher.BeginInvoke(() => _window.Execute(options.ToCommand()));
    }

    /// <summary>Best-effort input release for crash handlers. Safe from any thread.</summary>
    private void ReleaseInput()
    {
        try { Controller?.EmergencyRelease(); } catch { /* ignore */ }
        try { HardBlocker.EmergencyReleaseAll(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Listens for commands from later launches. The handler runs on the pipe thread, so it
    /// marshals to the dispatcher and blocks until the UI thread has the answer.
    /// </summary>
    private void StartIpc()
    {
        _ipcCts = new CancellationTokenSource();
        var token = _ipcCts.Token;

        InstanceChannel.StartAsync(command =>
        {
            try
            {
                return Dispatcher.Invoke(() => _window is null
                    ? InstanceReply.Fail("FrostLock is still starting.")
                    : _window.Execute(command));
            }
            catch (Exception ex)
            {
                return InstanceReply.Fail(ex.Message);
            }
        }, token);
    }

    /// <summary>Runs commands that do not require a window.</summary>
    private static void RunStandalone(CliOptions options)
    {
        switch (options.Command)
        {
            case "lock":
                try
                {
                    Native.LockWorkStation();
                    ConsoleBridge.WriteLine("The session was locked.");
                }
                catch (Exception ex)
                {
                    ConsoleBridge.WriteLine($"Could not lock the session: {ex.Message}");
                }

                break;

            default:
                ConsoleBridge.WriteLine("FrostLock is not running.");
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _ipcCts?.Cancel(); } catch { /* ignore */ }
        try { _ipcCts?.Dispose(); } catch { /* ignore */ }
        try { _window?.DisposeShell(); } catch { /* ignore */ }
        try { Controller?.Dispose(); } catch { /* ignore */ }

        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();

        base.OnExit(e);
    }
}

/// <summary>Parsed command line for automation and the elevated relaunch.</summary>
internal sealed class CliOptions
{
    private const int MaxSeconds = 24 * 60 * 60;

    public string? Command { get; private set; }
    public FreezeMode? ModeOverride { get; private set; }
    public int? SecondsOverride { get; private set; }
    public int? ArmOverride { get; private set; }
    public bool? SoundOverride { get; private set; }
    public bool StartMinimized { get; private set; }
    public bool AllowInstance { get; private set; }
    public bool Help { get; private set; }
    public bool Version { get; private set; }

    /// <summary>Set when a value could not be read. Reported only when a console is available.</summary>
    public string? Error { get; private set; }

    public bool WantsConsole => Command == "status" || Help || Version;

    public static string VersionLine => $"FrostLock {UpdateService.CurrentVersion.ToString(3)}";

    public const string Usage = """
        FrostLock - freeze the keyboard and mouse for a set time.

        Usage: FrostLock.exe [command] [options]

        Commands
          --freeze, -f            Start a freeze now
          --panic                 Release the current freeze (refused during a PIN lock)
          --lock-now              Lock the Windows session
          --show                  Bring the FrostLock window to the front
          --exit                  Quit FrostLock
          --status                Print the current state and exit

        Options
          --mode <mode>           gentle, strict, hard or pin. Defaults to your saved mode.
          --duration <time>, -d   Length of the freeze: 90s, 25m, 1h30m, or 0. Also --for.
          --until-unlock          Same as --duration 0.
          --seconds <n>, -s <n>   Length in seconds. Kept for compatibility with --duration.
          --arm <n>               Seconds before input is taken away.
          --no-arm                Freeze immediately.
          --mute                  No sounds for this freeze. Also --silent, --no-sound.
          --minimized, -m         Start hidden in the tray.
          --help, -h              Show this help.
          --version, -v           Show the version.

        Commands are sent to the running FrostLock, so --duration also works when the app is
        already open. The first launch creates the window.
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim();
            if (a.Length == 0) continue;

            switch (a.ToLowerInvariant())
            {
                case "--freeze":
                case "-f":
                    o.Command ??= "freeze";
                    break;

                case "--panic":
                    o.Command ??= "panic";
                    break;

                case "--lock-now":
                case "--lock":
                    o.Command ??= "lock";
                    break;

                case "--show":
                    o.Command ??= "show";
                    break;

                case "--exit":
                case "--quit":
                    o.Command ??= "exit";
                    break;

                case "--status":
                    o.Command ??= "status";
                    break;

                case "--help":
                case "-h":
                case "-?":
                    o.Help = true;
                    break;

                case "--version":
                case "-v":
                    o.Version = true;
                    break;

                case "--minimized":
                case "--tray":
                case "-m":
                    o.StartMinimized = true;
                    break;

                case "--allow-instance":
                    o.AllowInstance = true;
                    break;

                case "--until-unlock":
                case "--indefinite":
                    o.SecondsOverride = 0;
                    break;

                case "--no-arm":
                    o.ArmOverride = 0;
                    break;

                case "--mute":
                case "--silent":
                case "--no-sound":
                    o.SoundOverride = false;
                    break;

                case "--mode":
                    if (Next(args, ref i) is { } modeText)
                    {
                        if (Enum.TryParse<FreezeMode>(modeText, ignoreCase: true, out var mode))
                            o.ModeOverride = mode;
                        else
                            o.Error ??= $"Unknown mode '{modeText}'. Use gentle, strict, hard or pin.";
                    }

                    break;

                case "--duration":
                case "--for":
                case "-d":
                    if (Next(args, ref i) is { } durationText)
                    {
                        if (TryParseDuration(durationText, out var seconds))
                            o.SecondsOverride = seconds;
                        else
                            o.Error ??= $"Could not read the duration '{durationText}'. Try 90s, 25m or 1h30m.";
                    }

                    break;

                case "--seconds":
                case "-s":
                    if (Next(args, ref i) is { } secondsText)
                    {
                        if (int.TryParse(secondsText, out var sec) && sec >= 0)
                            o.SecondsOverride = Math.Min(sec, MaxSeconds);
                        else
                            o.Error ??= $"Could not read the number of seconds '{secondsText}'.";
                    }

                    break;

                case "--arm":
                    if (Next(args, ref i) is { } armText)
                    {
                        if (int.TryParse(armText, out var arm) && arm >= 0)
                            o.ArmOverride = Math.Min(arm, 300);
                        else
                            o.Error ??= $"Could not read the arm delay '{armText}'.";
                    }

                    break;
            }
        }

        return o;
    }

    /// <summary>Builds the request sent to the running instance.</summary>
    public InstanceCommand ToCommand() => Command switch
    {
        "freeze" => new InstanceCommand
        {
            Command = "freeze",
            Mode = ModeOverride?.ToString(),
            DurationSeconds = SecondsOverride,
            ArmSeconds = ArmOverride,
            Sound = SoundOverride
        },
        "panic" => new InstanceCommand { Command = "panic" },
        "lock" => new InstanceCommand { Command = "lock" },
        "exit" => new InstanceCommand { Command = "exit" },
        "status" => new InstanceCommand { Command = "status" },
        _ => new InstanceCommand { Command = "show" }
    };

    /// <summary>
    /// Reads 90s, 25m, 1h, 1h30m or a bare number of seconds. 0 means "until I unlock".
    /// </summary>
    public static bool TryParseDuration(string text, out int seconds)
    {
        seconds = 0;

        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().ToLowerInvariant();

        if (text is "0" or "infinite" or "indefinite" or "forever" or "none")
            return true;

        int total = 0;
        int number = 0;
        bool inNumber = false;

        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                number = (number * 10) + (ch - '0');
                inNumber = true;
                continue;
            }

            if (!inNumber) return false;

            var multiplier = ch switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                _ => 0
            };

            if (multiplier == 0) return false;

            total += number * multiplier;
            number = 0;
            inNumber = false;
        }

        // A trailing number with no unit counts as seconds.
        if (inNumber) total += number;

        if (total <= 0) return false;

        seconds = Math.Min(total, MaxSeconds);
        return true;
    }

    private static string? Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) return null;
        return args[++index];
    }
}
