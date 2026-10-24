# FrostLock

A modern Windows input-freeze / screen-lock utility. It takes the keyboard and mouse away for a
set period (or until you release it), shows a proper lock screen, and always gives you a way back.

Inspired by **[KeyFreeze](https://keyfreeze.com/)** — a tiny Windows utility that blocks the
keyboard and mouse without locking the screen. FrostLock keeps that core idea (no keyboard, no
mouse, nothing to click) and rebuilds it as a modern app: a real lock screen with a countdown, and
several independent ways back out, so Ctrl+Alt+Del is never the only option.

- WPF, `net8.0-windows`, built on **[WPF-UI](https://github.com/lepoco/wpfui)** (Fluent/WinUI controls).
- Follows the Windows 11 system theme, accent colour and Mica backdrop, so it looks like a stock
  Windows app rather than a custom skin.
- Four freeze modes, from a friendly overlay to a real OS-level input freeze.
- A full-screen lock screen on **every** monitor, with a live countdown and progress bar.
- Multiple independent release paths so you can never get stuck.

---

## Modes

| Mode | What it does | Admin? | How you get out |
|------|--------------|--------|-----------------|
| **Gentle** | Full-screen lock screen + suppresses system keys (Win, Alt+Tab, Alt+F4, Ctrl+Esc, Alt+Esc…). The mouse stays live, so the on-screen **Unlock** button works. | No | Unlock button · unlock hotkey · timer |
| **Strict** | Adds system-wide low-level keyboard **and** mouse hooks, so keystrokes and clicks are swallowed globally. | No | Unlock hotkey · timer |
| **PIN** | A soft lock: typing and the mouse still work, system keys are swallowed exactly as in Strict, and the lock screen asks for your PIN. The unlock hotkey is **not** honoured here. | No | PIN · timer |
| **Hard** | Calls Win32 `BlockInput(true)` on a dedicated worker thread — nothing responds, not even the overlay. | **Yes** | Timer / safety cap · `Ctrl+Alt+Del` |

Hard mode offers to relaunch itself elevated (UAC) if you're not already an administrator.

## Features

- **Arm delay** (default 3s) — a cancellable countdown on the lock screen before input is taken away.
- **Safety auto-release** (off by default) — an optional absolute ceiling for an "until I unlock"
  freeze, as a silent backstop. With it off (the default) “until I unlock” really means indefinitely.
- **PIN lock** — a PIN unlocks the screen; only a salted PBKDF2-SHA256 hash is stored.
- **Configurable hotkeys** — a global *start* hotkey and an *unlock* combo that is detected inside
  the hook, so it still works while everything else is blocked.
- **Multi-monitor** — one correctly sized, DPI-correct lock screen per display.
- **Tray integration** — freeze, release, open, settings and exit from the notification area, in a
  Fluent menu that matches the app theme.
- **Start with Windows**, start hidden, minimise/close to tray.
- **Keep the display awake** while frozen.
- **Duration presets** — 30s / 1m / 5m / 15m / until I unlock.
- **Statistics** — freeze count and total frozen time.
- **About dialog** — publisher (FurtherSecrets), app version, .NET/WPF-UI/Windows details,
  elevation state and the settings path, with a *Copy details* button for bug reports and a link to
  the source on GitHub.
- **Sound feedback** on freeze and release (toggleable).
- **JSON settings** at `%AppData%\FrostLock\settings.json`.
- **Single instance** with command-line signalling to the running instance.

The window uses a `FluentWindow` with an extended title bar, Mica backdrop and rounded corners;
settings are laid out as Windows Settings-style cards with Fluent toggles and number boxes.The lock screen is deliberately plain and dark: large countdown, thin progress bar, and a release hint
that never prints the unlock combination — anyone looking at the screen should not learn how to get out.

## Hotkeys (defaults)

| Action | Combo |
|--------|-------|
| Start a freeze from anywhere | `Ctrl+Alt+L` |
| Release a Gentle/Strict freeze | `Ctrl+Alt+U` |

All of these are editable in **Settings → Hotkeys**: click a field and press the combination, and the
recorded combo is validated immediately (the global start hotkey is released while Settings is open so
recording cannot launch a freeze).

## Command line

FrostLock is scriptable, and the elevated relaunch uses the same switches internally.

```
FrostLock.exe [--freeze] [--panic] [--mode gentle|strict|hard]
              [--seconds N] [--arm N] [--minimized] [--allow-instance]
```

| Switch | Meaning |
|--------|---------|
| `--freeze` | Ask the running instance to start a freeze (or start one if none is running). |
| `--panic` | Ask the running instance to release immediately. |
| `--mode` | Freeze mode to use for this invocation. |
| `--seconds` | Freeze length; `0` means "until I unlock". |
| `--arm` | Arm delay in seconds. |
| `--minimized` | Start hidden in the tray. |
| `--allow-instance` | Start even if another instance already exists. |

Command-line values apply to that session only — they never overwrite your saved defaults.

Examples:

```powershell
# Freeze for 90 seconds without any admin rights, from a script
FrostLock.exe --freeze --mode strict --seconds 90 --minimized

# Release a running freeze
FrostLock.exe --panic
```

## Build and run

Requires the .NET 8 SDK (or newer) on Windows. The only package dependency is `WPF-UI` 4.3.0,
restored automatically by `dotnet build`.

```powershell
cd FrostLock
dotnet build -c Release
.\bin\Release\net8.0-windows\FrostLock.exe
```

Standalone copy that needs no installed runtime:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Installer

`installer\build-installer.cmd` publishes a self-contained win-x64 build (single file) and wraps it in an
Inno Setup 6 installer:

```powershell
cd FrostLock
.\installer\build-installer.cmd
```

The result is `installer\Output\FrostLock-Setup-1.0.0.exe` (~60 MB, no .NET runtime needed on the target
machine). It installs per-user by default to `%LocalAppData%\Programs\FrostLock` with no UAC prompt; the
setup's install-mode page can switch to an all-users install. It creates a Start Menu shortcut, an
optional desktop shortcut, and an Apps & features entry with an uninstaller, and it asks to close a
running FrostLock first. Uninstalling keeps `%AppData%\FrostLock\settings.json`.

Bump `<Version>` in `FrostLock.csproj` and `MyAppVersion` in `installer\FrostLock.iss` together.
Inno Setup 6 is installed per-user by winget (`winget install JRSoftware.InnoSetup`); the build script
also finds a machine-wide install.

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

## Honest limitations

- **This is a focus/distraction tool, not a security boundary.** It cannot defend against an
  administrator, `Ctrl+Alt+Del`, Task Manager run from a secure desktop, safe mode, or physical
  access. Treat it as a speed bump, not a lock.
- **`Ctrl+Alt+Del` cannot be intercepted** by any user-mode application — Windows reserves it as the
  secure attention sequence. That is by design, and it is what makes Hard mode survivable.
- **Hard mode cannot be released by FrostLock's own hotkey.** `BlockInput` freezes the input queue
  at the OS level, so the app listens but cannot act on keystrokes. Use the timer / safety cap, or
  press `Ctrl+Alt+Del`, which makes Windows force-release the block.
- **PIN mode has no reset inside the lock.** The PIN is stored only as a salted hash, so a forgotten
  PIN means ending FrostLock (Ctrl+Alt+Del → Task Manager) or releasing it with
  `FrostLock.exe --panic`.
- **A non-elevated hook cannot cover elevated applications.** If input directed at an elevated
  foreground window must also be frozen, use Hard mode (which runs elevated).
- If you pick Hard mode, choose "until I unlock", *and* disable the safety cap, `Ctrl+Alt+Del` is
  your only exit — exactly like [KeyFreeze](https://keyfreeze.com/). The default cap exists to
  prevent that.