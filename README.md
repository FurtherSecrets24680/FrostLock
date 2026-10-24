# FrostLock

FrostLock takes the keyboard and mouse away for a set period, covers each display with a lock screen, and gives you several ways to end the freeze.

The app is inspired by [KeyFreeze](https://keyfreeze.com/), a small utility that blocks input without locking the screen. FrostLock rebuilds that idea as a Windows app with a countdown, a PIN, and more than one route out of a freeze.

- WPF on `net8.0-windows`, built with **[WPF-UI](https://github.com/lepoco/wpfui)**.
- Uses the Windows 11 system theme, accent colour and Mica backdrop.
- Four freeze modes, from a friendly overlay to an OS-level input freeze.
- A lock screen on each display, scaled for its DPI.
- Several release paths, so a stuck overlay is recoverable.

---

## Modes

| Mode       | What it does                                                                                                                                                 | Admin?  | How you get out                     |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------- | ----------------------------------- |
| **Gentle** | Lock screen plus suppressed system keys (Win, Alt+Tab, Alt+F4, Ctrl+Esc, Alt+Esc). The mouse stays live, so the on-screen **Unlock** button works.           | No      | Unlock button, unlock hotkey, timer |
| **Strict** | Adds low-level keyboard and mouse hooks, so keystrokes and clicks are swallowed system-wide.                                                                 | No      | Unlock hotkey, timer                |
| **PIN**    | Typing and the mouse still work. System keys are swallowed as in Strict, and the lock screen asks for your PIN. The unlock hotkey does nothing in this mode. | No      | PIN, timer                          |
| **Hard**   | Calls Win32 `BlockInput(true)` on a dedicated worker thread. Nothing responds, the overlay included.                                                         | **Yes** | Timer or safety cap, `Ctrl+Alt+Del` |

If you are not running as an administrator, Hard mode offers to restart itself elevated through UAC.

## Features

- **Arm delay**, 3 seconds by default: a cancellable countdown on the lock screen before input is blocked.
- **Safety auto-release**: an optional ceiling for an "until I unlock" freeze. Off by default, so that option means what it says.
- **PIN lock**: only a salted PBKDF2-SHA256 hash is stored.
- **Configurable hotkeys**: a global start hotkey, plus an unlock combo that the hook watches for, so it works while other input is blocked.
- **Multi-monitor**: one lock screen per display.
- **Tray menu**: freeze, release, open, settings, exit.
- **Start with Windows**, start hidden, minimise or close to tray.
- **Display stays awake** while frozen.
- **Duration presets**: 30s, 1m, 5m, 15m, until I unlock.
- **Statistics**: freeze count and total frozen time.
- **About dialog**: publisher, version, .NET/WPF-UI/Windows details, elevation state, settings path, a *Copy details* button, and a link to the source on GitHub.
- **Sound** on freeze and release, toggleable.
- **Settings** in JSON at `%AppData%\FrostLock\settings.json`.
- **Single instance**, with command-line signalling to the copy already running.

The control panel is a `FluentWindow` with an extended title bar, Mica backdrop and rounded corners.
Settings sit in Windows Settings-style cards with Fluent toggles and number boxes. The lock screen stays plain and dark: a large countdown, a thin progress bar, and a hint that tells you how to release without printing the combination.

## Hotkeys

| Action                            | Default      |
| --------------------------------- | ------------ |
| Start a freeze from anywhere      | `Ctrl+Alt+L` |
| Release a Gentle or Strict freeze | `Ctrl+Alt+U` |

Change both in **Settings → Hotkeys**. Click a field and press the combination; the field records and validates it. FrostLock releases the global start hotkey while Settings is open, so recording a combo cannot launch a freeze.

## Command line

The elevated relaunch uses these switches too, so anything the app can do from the UI you can also script.

```
FrostLock.exe [--freeze] [--panic] [--mode gentle|strict|hard]
              [--seconds N] [--arm N] [--minimized] [--allow-instance]
```

| Switch             | Meaning                                                                       |
| ------------------ | ----------------------------------------------------------------------------- |
| `--freeze`         | Ask the running instance to start a freeze (or start one if none is running). |
| `--panic`          | Ask the running instance to release right away.                               |
| `--mode`           | Freeze mode to use for this invocation.                                       |
| `--seconds`        | Freeze length; `0` means "until I unlock".                                    |
| `--arm`            | Arm delay in seconds.                                                         |
| `--minimized`      | Start hidden in the tray.                                                     |
| `--allow-instance` | Start even if another instance already exists.                                |

Switches apply to that session. Your saved defaults stay as they are.

```powershell
# Freeze for 90 seconds without admin rights, from a script
FrostLock.exe --freeze --mode strict --seconds 90 --minimized

# Release a running freeze
FrostLock.exe --panic
```

## Build and run

Requires the .NET 8 SDK or newer on Windows. The only package reference is `WPF-UI` 4.3.0, which `dotnet build` restores.

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\FrostLock.exe
```

For a copy that runs without an installed runtime:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Installer

`installer\build-installer.cmd` publishes a self-contained win-x64 single file and wraps it in an Inno Setup 6 installer.

```powershell
.\installer\build-installer.cmd
```

The result is `installer\Output\FrostLock-Setup-1.0.0.exe`, about 60 MB, with no .NET runtime needed on the target machine. It installs per user to `%LocalAppData%\Programs\FrostLock` without a UAC prompt; the install-mode page can switch to an all-users install. Setup adds a Start Menu shortcut, an optional desktop shortcut, an Apps & features entry and an uninstaller, and asks you to close a
running FrostLock first. Uninstalling keeps `%AppData%\FrostLock\settings.json`.

Bump `<Version>` in `FrostLock.csproj` and `MyAppVersion` in `installer\FrostLock.iss` together.
Install Inno Setup 6 with `winget install JRSoftware.InnoSetup`; the build script also finds a machine-wide copy.

## Project layout

```
FrostLock/
  App.xaml(.cs)              Shell: single instance, CLI parsing, cross-instance events
  MainWindow.xaml(.cs)       Control panel: status, duration, mode, safety, tray wiring
  Views/
    OverlayWindow.xaml(.cs)  The full-screen lock screen (one per monitor)
    SettingsWindow.xaml(.cs) Preferences
  Services/
    FreezeController.cs      Freeze lifecycle: arm → engage → tick → release → stats
    HookBlocker.cs           WH_KEYBOARD_LL / WH_MOUSE_LL blocking + combo detection
    HardBlocker.cs           Win32 BlockInput on a dedicated block/unblock thread
    HotkeyService.cs         RegisterHotKey for the global start hotkey
    TrayService.cs           NotifyIcon (WinForms) + Fluent WPF context menu
    Hotkeys.cs               "Ctrl+Alt+U" ⇄ modifiers/VK parsing
    PinService.cs            PBKDF2 hashing and constant-time PIN verification
    SettingsService.cs       JSON persistence
    StartupService.cs        HKCU Run entry
    IconFactory.cs           Packaged .ico loading
  Interop/Native.cs          All P/Invoke in one place
  Models/                    AppSettings, OverlayState, enums
  Assets/frostlock.ico       App icon (10 sizes, generated from frostlock.svg)
```

## Limitations

- **FrostLock is a speed bump.** A local administrator, `Ctrl+Alt+Del`, Task Manager on the secure desktop, safe mode or physical access each end a freeze.
- **Windows reserves `Ctrl+Alt+Del`** as the secure attention sequence, so a user-mode app cannot intercept it. That is what makes Hard mode escapable.
- **Hard mode ignores FrostLock's own hotkey.** `BlockInput` freezes the input queue at the OS level, so the app can listen but not act. Release with the timer, the safety cap, or `Ctrl+Alt+Del`, which makes Windows drop the block.
- **PIN mode has no reset from inside the lock.** A forgotten PIN means ending FrostLock through Task Manager, or running `FrostLock.exe --panic`.
- **A non-elevated hook cannot cover elevated apps.** Send that input to Hard mode, which runs elevated.
- **Hard mode, "until I unlock", and no safety cap** leaves `Ctrl+Alt+Del` as the only exit, as with [KeyFreeze](https://keyfreeze.com/).
