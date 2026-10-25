<p align="center">
  <img src="Assets/frostlock.svg" alt="FrostLock" width="128" height="128">
</p>

# FrostLock

FrostLock takes the keyboard and mouse away for a set period, covers each display with a lock screen, and gives you several ways to end the freeze. This software is inspired by [KeyFreeze](https://keyfreeze.com/).

- WPF on `net8.0-windows`, with light, dark and follow-Windows themes, the Windows accent colour and a Mica backdrop.
- Four freeze modes, from a friendly overlay to an OS-level input freeze.
- A lock screen on each display, or the primary display only, scaled for its DPI.

---

## Requirements

- **To use FrostLock:** 64-bit Windows 10 or 11. The app itself needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0); if it is missing, Setup offers to download and install it once (about 56 MB, needs an internet connection). If it is already present, nothing is downloaded.
- **To build from source:** the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer) on Windows.
- On ARM64 Windows, FrostLock runs through x64 emulation.

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
- **PIN lock**: only a salted PBKDF2-SHA256 hash is stored. Five wrong entries pause PIN entry for 30 seconds; a correct PIN resets the count.
- **Configurable hotkeys**: a global start hotkey, plus an unlock combo that the hook watches for, so it works while other input is blocked.
- **Lock screen style**: a solid cover (default), a see-through dim veil, or none at all (input-only), with an optional message line. PIN mode always shows a cover.
- **Multi-monitor**: one lock screen per display, or the primary display only.
- **Tray menu**: freeze, release, open, settings, exit.
- **Start with Windows**, start hidden, minimise or close to tray.
- **Display stays awake** while frozen.
- **Clock**: a wall clock, plus the projected unlock time for timed freezes. Toggleable.
- **Theme**: light, dark, or follow Windows; the System choice follows live theme switches.
- **Update check**: a quiet check against GitHub releases on start (on by default), and a manual check in About that links to the release. Nothing downloads automatically.
- **Freeze duration**: presets from 30 seconds to 15 minutes, an indefinite option, or a custom timer from 1 minute to 24 hours.
- **Statistics**: freeze count and total frozen time.
- **About dialog**: publisher, version, .NET/WPF-UI/Windows details, elevation state, settings path, a *Copy details* button, and a link to the source on GitHub.
- **Sound** on freeze and release, toggleable.
- **Settings** in JSON at `%AppData%\FrostLock\settings.json`, with an in-app editor, export and import.
- **Single instance**, with command-line signalling to the copy already running.

The control panel is a `FluentWindow` with an extended title bar, Mica backdrop and rounded corners. Its **At a glance** card shows the hotkeys, lifetime statistics, and current elevation state; click **Hotkeys** to jump directly to those settings.

Settings sit in Windows Settings-style cards with Fluent toggles and number boxes. **Start hidden in the tray** is off by default. The lock screen stays deliberately dark in every theme: a clock in the header, a large countdown, a thin progress bar, an optional message, and mode-appropriate release guidance. Hard mode keeps its escape route off the lock screen; its tooltip explains the route before a freeze starts. A timed freeze also says when it will end.

## Hotkeys

| Action                            | Default      |
| --------------------------------- | ------------ |
| Start a freeze from anywhere      | `Ctrl+Alt+L` |
| Release a Gentle or Strict freeze | `Ctrl+Alt+U` |

Change both in **Settings → Hotkeys**. Click a field and press the combination; the field records and validates it. FrostLock releases the global start hotkey while Settings is open, so recording a combo cannot launch a freeze.

## Settings

Preferences are grouped into six cards, and each change applies and saves straight away. **Reset to defaults** sits at the bottom left.

| Card | Options |
| --- | --- |
| **General** | Theme (`System`, `Light`, `Dark`), start with Windows, start hidden (off by default), minimise to tray, close keeps running, keep the display awake, sound, update check on start. |
| **Lock screen** | Style (`Solid`, `Dim`, `None`), displays (`All`, `Primary`), countdown, clock, optional message. |
| **Safety** | Arm delay and the optional safety auto-release ceiling. |
| **PIN** | Set, change or remove the PIN. At least 4 characters. |
| **Hotkeys** | Unlock combo and the global start hotkey. |
| **Configuration** | Edit the JSON directly, export a backup, import one, or open the settings folder. |

The Configuration card shows the settings file path. The file and any export include the PIN hash and lifetime statistics, so treat a copy like a password.

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

`installer\build-installer.cmd` publishes a framework-dependent win-x64 app and wraps it in an Inno Setup 6 installer.

```powershell
.\installer\build-installer.cmd
```

The result is `installer\Output\FrostLock-Setup-1.1.0.exe`, about 4 MB. Setup checks for the .NET 8 Desktop Runtime and, when it is missing, offers to download and install it from Microsoft (about 56 MB). It installs per user to `%LocalAppData%\Programs\FrostLock` without a UAC prompt; the install-mode page can switch to an all-users install. Setup adds a Start Menu shortcut, an optional desktop shortcut, an Apps & features entry and an uninstaller, and asks you to close a
running FrostLock first. Uninstalling keeps `%AppData%\FrostLock\settings.json`.

Bump `<Version>` in `FrostLock.csproj` and `MyAppVersion` in `installer\FrostLock.iss` together.
Install Inno Setup 6 with `winget install JRSoftware.InnoSetup`; the build script also finds a machine-wide copy.

## Project layout

```
FrostLock/
  App.xaml(.cs)              Shell: single instance, CLI parsing, cross-instance events
  MainWindow.xaml(.cs)       Control panel: status, duration, mode, safety, tray wiring
  Views/
    OverlayWindow.xaml(.cs)  The full-screen lock screen (one per chosen display)
    SettingsWindow.xaml(.cs) Preferences
  Services/
    FreezeController.cs      Freeze lifecycle: arm → engage → tick → release → stats
    HookBlocker.cs           WH_KEYBOARD_LL / WH_MOUSE_LL blocking + combo detection
    HardBlocker.cs           Win32 BlockInput on a dedicated block/unblock thread
    HotkeyService.cs         RegisterHotKey for the global start hotkey
    TrayService.cs           NotifyIcon (WinForms) + Fluent WPF context menu
    Hotkeys.cs               "Ctrl+Alt+U" ⇄ modifiers/VK parsing
    PinService.cs            PBKDF2 hashing and constant-time PIN verification
    PinThrottle.cs           Wrong-PIN cooldown and lockout
    SettingsService.cs       JSON persistence
    StartupService.cs        HKCU Run entry
    ThemeService.cs          Light/dark/System theming, follows Windows live
    UpdateService.cs         GitHub release check
    IconFactory.cs           Packaged .ico loading
  Interop/Native.cs          All P/Invoke in one place
  Models/                    AppSettings, OverlayState, enums
  Assets/frostlock.ico       App icon (10 sizes, generated from frostlock.svg)
```

## Limitations

- **FrostLock is a speed bump.** A local administrator, `Ctrl+Alt+Del`, Task Manager on the secure desktop, safe mode or physical access each end a freeze.
- **Windows reserves `Ctrl+Alt+Del`** as the secure attention sequence, so a user-mode app cannot intercept it. That is what makes Hard mode escapable.
- **Hard mode ignores FrostLock's own hotkey.** `BlockInput` freezes the input queue at the OS level, so the app can listen but not act. Release with the timer, the safety cap, or `Ctrl+Alt+Del`, which makes Windows drop the block.
- **Hard mode keeps its escape route off the lock screen.** The combination and the timer/cap behaviour are in the tooltip on the freeze button before you start; the frozen screen only says input is held.
- **PIN mode has no reset from inside the lock.** A forgotten PIN means ending FrostLock through Task Manager, or running `FrostLock.exe --panic`. The 30-second cooldown slows guessing, but it is not a tamper-proof lockout.
- **A non-elevated hook cannot cover elevated apps.** Send that input to Hard mode, which runs elevated.
- **Hard mode, "until I unlock", and no safety cap** leaves `Ctrl+Alt+Del` as the only exit, as with [KeyFreeze](https://keyfreeze.com/).

## Credits & inspiration

FrostLock is inspired by [KeyFreeze](https://keyfreeze.com/), a small utility that blocks input without locking the screen. FrostLock rebuilds that idea as a Windows app with a countdown, a PIN, and more than one route out of a freeze.

Built with [WPF-UI](https://github.com/lepoco/wpfui) on .NET 8.
