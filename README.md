<p align="center">
  <img src="Assets/frostlock.svg" alt="FrostLock" width="128" height="128">
</p>

# FrostLock

<p align="center">
  <a href="https://www.producthunt.com/products/frostlock?utm_source=badge-featured&utm_medium=badge&utm_campaign=badge-frostlock">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1294962&amp;theme=dark">
      <img src="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1294962&amp;theme=light" alt="FrostLock on Product Hunt" width="250" height="54">
    </picture>
  </a>
</p>

FrostLock takes the keyboard and mouse away for a set period, covers each display with a lock screen, and gives you several ways to end the freeze. This software is inspired by [KeyFreeze](https://keyfreeze.com/).

- WPF on `net10.0-windows`, with light, dark and follow-Windows themes, the Windows accent colour and a Mica backdrop.
- Five locking modes: Gentle, Strict, Hard, PIN, and USB key.
- A lock screen on each display, or the primary display only, scaled for its DPI.

> [!WARNING]
> The installer is not code-signed yet, so Windows SmartScreen may show **Windows protected your PC** on first launch. Download it only from the [Releases page](https://github.com/FurtherSecrets24680/FrostLock/releases), choose **More info** → **Run anyway**, and compare its SHA-256 with the value published for that release if you want to verify the file.

---

## Requirements

- **To use FrostLock:** 64-bit Windows 10 or 11. The app itself needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); if it is missing, Setup offers to download and install it once (about 60 MB, needs an internet connection). If it is already present, nothing is downloaded.
- **To build from source:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or newer) on Windows.
- On ARM64 Windows, FrostLock runs through x64 emulation.

> [!NOTE]
> FrostLock still runs on Windows 10, but testing happens on Windows 11. Windows 10 reached end of support in October 2025.

## Modes

| Mode       | What it does                                                                                                                                                 | Admin?  | How you get out                     |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------- | ----------------------------------- |
| **Gentle** | Lock screen plus suppressed system keys (Win, Alt+Tab, Alt+F4, Ctrl+Esc, Alt+Esc). The mouse stays live, so the on-screen **Unlock** button works.           | No      | Unlock button, unlock hotkey, timer |
| **Strict** | Uses low-level keyboard and mouse hooks to swallow input. Blocking can be configured.                                                                                 | No      | Unlock hotkey, clickable Unlock if mouse input is live, timer |
| **Hard**   | Calls Win32 `BlockInput(true)` on a dedicated worker thread to block input at the OS level.                                                                          | **Yes** | `Ctrl+Alt+Del`, then return to FrostLock and click Unlock; timer, safety cap, or watchdog |
| **PIN**    | Typing and the mouse still work. System keys are suppressed, and the lock screen asks for your PIN. The unlock hotkey does nothing in this mode.                     | No      | PIN, configured emergency phrase, or timer |
| **USB key**| Watches a selected removable drive and starts a Strict freeze based on the chosen plug/unplug behavior. The volume serial is a convenience identifier, not authentication. | No | Opposite drive action, unlock hotkey, timer, or **Stop watching**; cannot release PIN mode |

> [!IMPORTANT]
> Hard mode needs administrator rights. If FrostLock is not elevated, it offers to restart through UAC before the freeze begins.

The **Compare all locking modes** link below the Freeze card opens a comparison table and is available regardless of the selected mode.

## Features

- **Arm delay**, 3 seconds by default: a cancellable countdown on the lock screen before input is blocked. Press Esc or click **Cancel freeze** to abort.
- **Safety auto-release**: an optional ceiling for an "until I unlock" freeze. Off by default, so that option means what it says.
- **Freeze until a clock time**: choose a 24-hour time in the Freeze card. If that time has passed today, FrostLock schedules it for tomorrow and previews the deadline.
- **Extend a running freeze**: click **+5 min** on the lock screen or press `Ctrl+Alt+E`. It also works when the taskbar and Task Manager are inaccessible. Strict mode detects the combo inside its keyboard hook; other supported modes use a global hotkey. Extension is unavailable for indefinite, Hard, and USB-triggered freezes, and never passes the safety cap or 24-hour maximum.
- **PIN lock**: a salted PBKDF2-SHA256 hash is stored, wrapped with Windows DPAPI so it cannot be lifted from the settings file to another account. Five wrong entries trigger an escalating cooldown (30 seconds, 1, 5, 15, then 60 minutes); the level survives a restart. An optional long emergency phrase gives you a deliberate way back in if you forget the PIN. The PIN and phrase are the only release paths in this mode, including from the tray, CLI and main window.
- **Configurable hotkeys**: global start and extend hotkeys, plus an unlock combo that the hook watches for, so it works while other input is blocked. The unlock combo can require a hold (1.2 seconds by default) so a stray keypress does not end a freeze. FrostLock's start, unlock and extend hotkeys are disabled in USB key mode; USB-triggered freezes are controlled by the drive and **Stop watching**.
- **Lock screen style**: a solid cover (default), a see-through dim veil, or none at all (input-only), with an optional message line and an optional background image. PIN mode always shows a cover.
- **Lock Windows when the freeze ends**: an opt-in switch that calls `LockWorkStation()` after the freeze, so the desktop is protected when you walk away. Off by default.
- **USB key mode**: select a removable drive on the main screen and start watching. Choose either **Plug to freeze; unplug to release** or **Plug to release; unplug to freeze**. FrostLock's start, unlock and extend hotkeys are disabled in this mode. Use **Stop watching** in the app or tray to stop the watcher and release a USB-triggered freeze. The volume serial can be copied, so this is a convenience trigger, not secure authentication; USB cannot release PIN mode.
- **Multi-monitor**: one lock screen per display, or the primary display only.
- **Tray menu**: quick freeze for 5, 15, 30 or 60 minutes or until unlock, plus freeze, release, open, settings and exit. Release and exit are disabled during a PIN lock.
- **Start with Windows**, start hidden, minimise or close to tray.
- **Display stays awake** while frozen.
- **Clock**: a wall clock, plus the projected unlock time for timed freezes. Toggleable.
- **Theme**: light, dark, or follow Windows; the System choice follows live theme switches.
- **Update check**: a quiet check against GitHub releases on start (on by default). About verifies the installer against GitHub's published SHA-256 digest as it downloads, then checks its size and PE header before launching it. If the release has no valid digest, auto-install is refused.
- **Freeze duration**: presets from 30 seconds to 15 minutes, an indefinite option, a custom timer from 1 minute to 24 hours, or a selected clock time.
- **Statistics**: count and total time for completed freezes. An aborted start or a shutdown does not count.
- **Crash guard**: an unhandled exception or a stalled UI thread releases the input block instead of leaving the keyboard and mouse dead.
- **About dialog**: publisher, version, .NET/WPF-UI/Windows details, elevation state, settings path, a *Copy details* button, and a link to the source on GitHub.
- **Sound** on freeze and release, toggleable.
- **Settings** in JSON at `%AppData%\FrostLock\settings.json`, with an in-app editor, export and import. Exports leave the PIN out; imports keep the PIN already set on this PC.
- **Single instance**, with command-line signalling to the copy already running.

> [!IMPORTANT]
> PIN mode has no reset from outside the lock screen. Set an emergency phrase before you rely on it, or plan to end FrostLock through Task Manager or `Ctrl+Alt+Del`.

The control panel is a `FluentWindow` with an extended title bar, Mica backdrop and rounded corners. Its **At a glance** card shows the hotkeys, lifetime statistics, and current elevation state; click **Hotkeys** to open Settings at the full Hotkeys card, with the unlock field focused.

Settings sit in Windows Settings-style cards with Fluent toggles and number boxes. **Start hidden in the tray** is off by default. Choose a duration, custom length, or **Until a time…** from the Freeze card; the time picker uses a 24-hour clock and previews the next occurrence. The lock screen stays deliberately dark in every theme: a clock in the header, a large countdown, a thin progress bar, an optional message, and mode-appropriate release guidance. Hard mode keeps its escape route off the lock screen; its tooltip explains the route before a freeze starts. A timed freeze also says when it will end. For eligible timed freezes, the lock screen offers **+5 min** when mouse input is available and displays the extend shortcut.

## Hotkeys

| Action                                      | Default      |
| ------------------------------------------- | ------------ |
| Start a freeze from anywhere                | `Ctrl+Alt+L` |
| Release a Gentle or Strict freeze           | `Ctrl+Alt+U` |
| Add five minutes to a timed freeze           | `Ctrl+Alt+E` |
| Hold the unlock combo for a Gentle/Strict release | 1.2 seconds  |

Change these in **Settings → Hotkeys**: unlock, start and extend combinations, and the hold time (0–5 seconds; `0` releases on the key press). Click a field and press the combination; the field records and validates it. FrostLock releases its registered global hotkeys while Settings is open, so recording a combo cannot trigger an action. In Strict mode with every key blocked, the extend combo is detected directly by the low-level hook; otherwise it uses a registered global hotkey. The hold timer only fires while every key of the unlock combo is still down. All three hotkeys are inactive while USB key mode is selected.

## Settings

Preferences are grouped into six cards, and each change applies and saves straight away. **Reset to defaults** sits at the bottom left.

| Card | Options |
| --- | --- |
| **General** | Theme (`System`, `Light`, `Dark`), start with Windows, start hidden (off by default), minimise to tray, close keeps running, keep the display awake, sound, update check on start. |
| **Lock screen** | Style (`Solid`, `Dim`, `None`), displays (`All`, `Primary`), countdown, clock, optional message, optional background image. |
| **Safety** | Arm delay, optional safety auto-release ceiling, and **Lock Windows when the freeze ends**. |
| **PIN** | Set, change or remove the PIN. At least 4 characters. |
| **Hotkeys** | Unlock combo, global start and extend hotkeys, and the hold-to-unlock time. |
| **Configuration** | Edit the JSON directly, export a backup, import one, or open the settings folder. |

The Configuration card shows the settings file path. The stored PIN and emergency phrase hashes are DPAPI-wrapped, so the file cannot be copied to another Windows account and reused. Exports deliberately omit both secrets; importing a backup keeps the PIN and phrase already set on this PC and takes the lifetime statistics from the file. The chosen freeze-until time and extend hotkey are saved with the other preferences.

## Command line

Commands and one-off overrides are sent to an already-running FrostLock over a local named pipe. If no instance is running, commands that need the window start it hidden in the tray.

```
FrostLock.exe --freeze --duration 25m --mode strict --arm 0
FrostLock.exe --freeze --for 1h30m --mute
FrostLock.exe --freeze --until-unlock
FrostLock.exe --freeze --until 15:30
FrostLock.exe --extend
FrostLock.exe --extend 15m
FrostLock.exe --panic
FrostLock.exe --status
FrostLock.exe --show
FrostLock.exe --lock-now
FrostLock.exe --exit
FrostLock.exe --help
```

Durations accept seconds, minutes, hours, or combinations (`90s`, `25m`, `1h30m`); `0` or `--until-unlock` means indefinite. `--until HH:mm` uses 24-hour local time and the next occurrence; `--extend [duration]` adds five minutes by default. Other options include `--seconds`, `--arm`, `--no-arm`, `--minimized`, and `--version`. Overrides apply to that freeze only and do not rewrite your saved defaults.

> [!NOTE]
> `--panic` and `--exit` are refused while a PIN lock is active. The PIN or emergency phrase on the lock screen is the only way to release it; extending the timer does not unlock it.

The `--mode` option accepts `gentle`, `strict`, `hard`, `pin`, or `usbkey`. Hard mode asks for elevation when needed. USB key mode requires selecting a drive in the main window before watching can begin.

```powershell
# Freeze for 25 minutes without admin rights, from a script
FrostLock.exe --freeze --mode strict --duration 25m --minimized

# Release a running freeze
FrostLock.exe --panic
```

## Build and run

Requires the .NET 10 SDK or newer on Windows. The only package reference is `WPF-UI` 4.3.0, which `dotnet build` restores.

```powershell
dotnet build -c Release
.\bin\Release\net10.0-windows\FrostLock.exe
```

> [!TIP]
> For a machine without the .NET 10 Desktop Runtime, publish a self-contained copy:
>
> ```powershell
> dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
> ```

## Testing

```powershell
dotnet test tests\FrostLock.Tests\FrostLock.Tests.csproj -c Release
```

## Installer

`installer\build-installer.cmd` publishes a framework-dependent win-x64 app and wraps it in an Inno Setup 6 installer.

```powershell
.\installer\build-installer.cmd
```

The result is `installer\Output\FrostLock-Setup-1.4.0.exe`, about 4 MB, plus a `.sha256` sidecar containing its SHA-256 value. Setup checks for the .NET 10 Desktop Runtime and, when it is missing, offers to download and install it from Microsoft (about 60 MB). It installs per user to `%LocalAppData%\Programs\FrostLock` without a UAC prompt; the install-mode page can switch to an all-users install. Setup adds a Start Menu shortcut, an optional desktop shortcut, an Apps & features entry and an uninstaller, and asks you to close a running FrostLock first. Uninstalling keeps `%AppData%\FrostLock\settings.json`.

For a release, upload the installer to GitHub Releases so GitHub publishes its asset digest, and include the `.sha256` value in the release notes for people verifying the download manually. The in-app updater uses GitHub's asset digest metadata, not the sidecar; it refuses auto-install if that metadata is absent or invalid.

To verify a downloaded installer in PowerShell, compare its hash with the release value:

```powershell
(Get-FileHash .\FrostLock-Setup-1.4.0.exe -Algorithm SHA256).Hash.ToLowerInvariant()
```

> [!IMPORTANT]
> Bump `<Version>` in `FrostLock.csproj` and `MyAppVersion` in `installer\FrostLock.iss` together before building.

Install Inno Setup 6 with `winget install JRSoftware.InnoSetup`; the build script also finds a machine-wide copy.

## Project layout

```
FrostLock/
  App.xaml(.cs)              Shell: single instance, CLI parsing, cross-instance events
  MainWindow.xaml(.cs)       Control panel: status, duration/until-time, mode, safety, tray wiring
  Views/
    OverlayWindow.xaml(.cs)  The full-screen lock screen (one per chosen display)
    ModeComparisonWindow.xaml(.cs) Comparison table for all locking modes
    SettingsWindow.xaml(.cs) Preferences
  Services/
    FreezeController.cs      Freeze lifecycle: arm → engage → tick → release → stats
    UsbDriveService.cs       Removable-drive discovery and plug/unplug monitoring
    HookBlocker.cs           WH_KEYBOARD_LL / WH_MOUSE_LL blocking + combo detection
    HardBlocker.cs           Win32 BlockInput on a dedicated block/unblock thread
    HotkeyService.cs         RegisterHotKey for the global start and extend hotkeys
    TrayService.cs           NotifyIcon (WinForms) + Fluent WPF context menu
    Hotkeys.cs               "Ctrl+Alt+U" ⇄ modifiers/VK parsing
    PinService.cs            PBKDF2 hashing and constant-time PIN verification
    PinThrottle.cs           Wrong-PIN cooldown and lockout
    SettingsService.cs       JSON persistence
    StartupService.cs        HKCU Run entry
    ThemeService.cs          Light/dark/System theming, follows Windows live
    UpdateService.cs         GitHub release check, installer download and SHA-256 verification
    IconFactory.cs           Packaged .ico loading
  Interop/Native.cs          All P/Invoke in one place
  Models/                    AppSettings, OverlayState, enums
  Assets/frostlock.ico       App icon (10 sizes, generated from frostlock.svg)
  tests/FrostLock.Tests/     xUnit tests for throttling, settings, clock time, CLI and update digests
```

## Limitations

> [!CAUTION]
> FrostLock is a speed bump. A local administrator, `Ctrl+Alt+Del`, Task Manager on the secure desktop, safe mode or physical access each end a freeze.

- **Windows reserves `Ctrl+Alt+Del`** as the secure attention sequence, so a user-mode app cannot intercept it. That is what makes Hard mode escapable.
- **Hard mode ignores FrostLock's own hotkey.** `BlockInput` freezes the input queue at the OS level, so the app can listen but not act. Release with the timer, the safety cap, or `Ctrl+Alt+Del`, which makes Windows drop the block.
- **Hard mode keeps its escape route off the lock screen.** The combination and the timer/cap behaviour are in the tooltip on the freeze button before you start; the frozen screen only says input is held.
- **PIN mode has no reset from outside the lock screen.** A forgotten PIN means typing the configured emergency phrase, or ending FrostLock through Task Manager / `Ctrl+Alt+Del` if no phrase was configured. Settings, the tray, `--panic` and the exit command are refused while a PIN lock is active. PIN and phrase cooldowns persist and escalate, but are not tamper-proof lockouts.
- **Hard mode has a watchdog.** If the FrostLock UI thread stops responding for five seconds, the blocker releases input on its own. A deliberate release, the timer, or the safety cap still work normally.
- **USB key mode is not a security key.** A removable drive is matched by its filesystem volume serial, which can be copied or spoofed. It cannot release PIN mode.
- **A non-elevated hook cannot cover elevated apps.** Send that input to Hard mode, which runs elevated.
- **Hard mode, "until I unlock", and no safety cap** leaves `Ctrl+Alt+Del` as the only exit, as with [KeyFreeze](https://keyfreeze.com/).
- **Updates are not code-signed yet.** The updater checks the downloaded Setup asset against the SHA-256 digest published by GitHub, along with its size and PE header. A digest helps detect a corrupted or substituted download, but it does not protect against a compromised GitHub account or release; Authenticode signing is the stronger future control.

## Credits & inspiration

FrostLock is inspired by [KeyFreeze](https://keyfreeze.com/), a small utility that blocks input without locking the screen. FrostLock rebuilds that idea as a Windows app with a countdown, a PIN, and more than one route out of a freeze.

Built with [WPF-UI](https://github.com/lepoco/wpfui) on .NET 10.

## License

FrostLock is free software under the [GNU General Public License v3.0](LICENSE). You may run it, study it, share it, and modify it. If you distribute a modified version, it has to stay under the GPL and include the source.

The FrostLock name, icon, and branding are not part of that license. Forked builds must not present themselves as the official release.

Third-party components keep their own licenses: [WPF-UI](https://github.com/lepoco/wpfui) is MIT, and the .NET Desktop Runtime is MIT.

The software comes with no warranty. FrostLock blocks keyboard and mouse input, so read [Limitations](#limitations) and test a short freeze before depending on it.
