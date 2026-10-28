using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrostLock.Interop;
using FrostLock.Models;

namespace FrostLock.Views;

/// <summary>
/// A full-screen "lock screen" placed on a single monitor. The controller creates one per
/// screen, drives it through <see cref="UpdateState"/>, and closes it when the freeze ends.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly System.Windows.Forms.Screen _screen;
    private readonly Action _onUnlock;
    private readonly Action? _onExtend;
    private readonly Func<string, UnlockAttempt>? _verifyPin;
    private readonly Func<string, UnlockAttempt>? _verifyPhrase;
    private readonly bool _closeRequestsUnlock;
    private readonly string? _backgroundPath;
    private bool _pinFocused;
    private bool _panicRevealed;
    private bool _canCancelArm;
    private TimeSpan _pinCooldown;
    private TimeSpan _panicCooldown;

    /// <summary>Set by the controller right before it closes the window.</summary>
    public bool AllowClose { get; set; }

    public OverlayWindow(
        System.Windows.Forms.Screen screen,
        Action onUnlock,
        Action? onExtend = null,
        Func<string, UnlockAttempt>? verifyPin = null,
        Func<string, UnlockAttempt>? verifyPhrase = null,
        bool closeRequestsUnlock = true,
        LockScreenStyle style = LockScreenStyle.Solid,
        string? backgroundPath = null)
    {
        InitializeComponent();

        _screen = screen;
        _onUnlock = onUnlock;
        _onExtend = onExtend;
        _verifyPin = verifyPin;
        _verifyPhrase = verifyPhrase;
        _closeRequestsUnlock = closeRequestsUnlock;
        _backgroundPath = backgroundPath;

        ApplyStyle(style);
        ApplyBackground(style);

        // During the arm delay the freeze is still cancellable, so Esc is a real abort.
        // Once frozen, Esc does nothing; PIN mode keeps the PIN as the only exit.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _canCancelArm)
            {
                e.Handled = true;
                _onUnlock();
            }
        };

        PinBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                SubmitPin();
            }
        };

        PanicBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                SubmitPhrase();
            }
        };

        Loaded += (_, _) => PositionToScreen();
    }

    /// <summary>
    /// The see-through style needs a layered window, so this runs before the window is
    /// shown: AllowsTransparency can only change while no handle exists yet.
    /// </summary>
    private void ApplyStyle(LockScreenStyle style)
    {
        if (style != LockScreenStyle.Dim) return;

        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
    }

    /// <summary>Loads the chosen image once, with a dark scrim so the text stays readable.</summary>
    private void ApplyBackground(LockScreenStyle style)
    {
        if (style == LockScreenStyle.None || string.IsNullOrWhiteSpace(_backgroundPath))
            return;

        if (!File.Exists(_backgroundPath))
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(_backgroundPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            bool dim = style == LockScreenStyle.Dim;
            BackdropImage.Source = bitmap;
            BackdropImage.Opacity = dim ? 0.45 : 1.0;
            BackdropImage.Visibility = Visibility.Visible;

            BackdropScrim.Background = new SolidColorBrush(
                Color.FromArgb(dim ? (byte)0x66 : (byte)0xB3, 0, 0, 0));
            BackdropScrim.Visibility = Visibility.Visible;
        }
        catch
        {
            // A bad or unsupported image falls back to the plain cover.
        }
    }

    /// <summary>Sizes and pins the overlay to its monitor in physical pixels (DPI-safe).</summary>
    public void PositionToScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var bounds = _screen.Bounds;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Native.SWP_SHOWWINDOW);
    }

    public void UpdateState(OverlayState state)
    {
        bool arming = state.Phase == FreezePhase.Arming;
        _canCancelArm = arming;

        ModeText.Text = state.ModeLabel;

        SafetyText.Visibility = state.Mode == FreezeMode.Hard
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (arming)
        {
            PhaseText.Text = "Freezing input";
            TimerText.Text = state.Remaining is { } armLeft
                ? Math.Max(1, (int)Math.Ceiling(armLeft.TotalSeconds)).ToString()
                : "...";
        }
        else
        {
            PhaseText.Text = "Input is frozen";
            TimerText.Text = state.Remaining switch
            {
                null => "Until you unlock",
                { } left when state.ShowCountdown => Format(left),
                _ => string.Empty
            };
        }

        RemainingBar.Value = state.Progress;

        HintText.Text = state.UnlockHint;

        MessageText.Text = state.Message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(state.Message)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ClockText.Text = DateTime.Now.ToString("HH:mm");
        ClockText.Visibility = state.ShowClock ? Visibility.Visible : Visibility.Collapsed;

        bool showUnlockAt = state.ShowClock && !arming && state.UnlockAt is not null;
        UnlockAtText.Visibility = showUnlockAt ? Visibility.Visible : Visibility.Collapsed;
        if (showUnlockAt)
        {
            var unlockAt = state.UnlockAt!.Value;
            var today = DateTime.Today;

            UnlockAtText.Text = unlockAt.Date == today
                ? $"Unlocks at {unlockAt:HH:mm}"
                : unlockAt.Date == today.AddDays(1)
                    ? $"Unlocks tomorrow at {unlockAt:HH:mm}"
                    : $"Unlocks {unlockAt:ddd d MMM} at {unlockAt:HH:mm}";
        }

        UnlockButton.Content = arming ? "Cancel freeze" : "Unlock now";

        // PIN mode owns the release: show the prompt instead of the Unlock button (the
        // button is still there during the arm delay so the freeze can be cancelled).
        bool pinMode = !arming && state.RequiresPin && _verifyPin is not null;
        PinPanel.Visibility = pinMode ? Visibility.Visible : Visibility.Collapsed;

        _pinCooldown = state.PinCooldownRemaining;
        bool cooling = pinMode && _pinCooldown > TimeSpan.Zero;

        PinCooldown.Visibility = cooling ? Visibility.Visible : Visibility.Collapsed;
        if (cooling)
        {
            PinCooldown.Text = CooldownText(_pinCooldown);
            PinError.Visibility = Visibility.Collapsed;
        }

        PinBox.IsEnabled = !cooling;
        PinSubmitButton.IsEnabled = !cooling;

        // The emergency phrase is offered only where it is the only way back in: the PIN
        // lock screen, and only when the user has configured one.
        bool panicAvailable = pinMode && _verifyPhrase is not null && state.PanicAvailable;

        PanicRevealButton.Visibility = panicAvailable && !_panicRevealed
            ? Visibility.Visible
            : Visibility.Collapsed;

        PanicPanel.Visibility = panicAvailable && _panicRevealed
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (panicAvailable && _panicRevealed)
        {
            _panicCooldown = state.PanicCooldownRemaining;
            bool panicCooling = _panicCooldown > TimeSpan.Zero;

            PanicCooldown.Visibility = panicCooling ? Visibility.Visible : Visibility.Collapsed;
            if (panicCooling)
            {
                PanicCooldown.Text = CooldownText(_panicCooldown);
                PanicError.Visibility = Visibility.Collapsed;
            }

            PanicBox.IsEnabled = !panicCooling;
            PanicSubmitButton.IsEnabled = !panicCooling;
        }

        if (pinMode && !_pinFocused && !cooling)
        {
            _pinFocused = true;
            PinBox.Focus();
        }

        bool strictMouseBlocked = !arming && state.MouseBlocked && state.Mode == FreezeMode.Strict;
        UnlockButton.IsEnabled = !strictMouseBlocked;
        UnlockButton.Visibility = strictMouseBlocked || pinMode
            ? Visibility.Collapsed
            : Visibility.Visible;

        ExtendButton.Content = $"+{state.ExtendMinutes} min";
        ExtendButton.IsEnabled = !state.MouseBlocked;
        ExtendButton.Visibility = !arming && state.CanExtend && !state.MouseBlocked
            ? Visibility.Visible
            : Visibility.Collapsed;

        ExtendHintText.Text = state.ExtendHint;
        ExtendHintText.Visibility = !arming && state.CanExtend && state.ExtendHint.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ActionPanel.Visibility = UnlockButton.Visibility == Visibility.Visible
                                 || ExtendButton.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => _onUnlock();

    private void ExtendButton_Click(object sender, RoutedEventArgs e) => _onExtend?.Invoke();

    private void PinSubmit_Click(object sender, RoutedEventArgs e) => SubmitPin();

    private void SubmitPin()
    {
        if (_verifyPin is null) return;
        if (_pinCooldown > TimeSpan.Zero) return;
        if (PinBox.Password.Length == 0) return;

        var attempt = _verifyPin(PinBox.Password);
        PinBox.Clear();

        if (attempt.Success)
        {
            _onUnlock();
            return;
        }

        PinBox.Focus();

        if (attempt.LockedOut)
        {
            _pinCooldown = attempt.Cooldown;
            PinCooldown.Text = CooldownText(_pinCooldown);
            PinCooldown.Visibility = Visibility.Visible;
            PinError.Visibility = Visibility.Collapsed;
            PinBox.IsEnabled = false;
            PinSubmitButton.IsEnabled = false;
            return;
        }

        PinError.Text = AttemptText(attempt, "PIN");
        PinError.Visibility = Visibility.Visible;
    }

    private void PanicReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_verifyPhrase is null) return;

        _panicRevealed = true;
        PanicRevealButton.Visibility = Visibility.Collapsed;
        PanicPanel.Visibility = Visibility.Visible;
        PanicBox.Clear();
        PanicError.Visibility = Visibility.Collapsed;
        PanicBox.Focus();
    }

    private void PanicSubmit_Click(object sender, RoutedEventArgs e) => SubmitPhrase();

    private void SubmitPhrase()
    {
        if (_verifyPhrase is null) return;
        if (_panicCooldown > TimeSpan.Zero) return;
        if (PanicBox.Password.Length == 0) return;

        var attempt = _verifyPhrase(PanicBox.Password);
        PanicBox.Clear();

        if (attempt.Success)
        {
            _onUnlock();
            return;
        }

        PanicBox.Focus();

        if (attempt.LockedOut)
        {
            _panicCooldown = attempt.Cooldown;
            PanicCooldown.Text = CooldownText(_panicCooldown);
            PanicCooldown.Visibility = Visibility.Visible;
            PanicError.Visibility = Visibility.Collapsed;
            PanicBox.IsEnabled = false;
            PanicSubmitButton.IsEnabled = false;
            return;
        }

        PanicError.Text = AttemptText(attempt, "phrase");
        PanicError.Visibility = Visibility.Visible;
    }

    private static string AttemptText(UnlockAttempt attempt, string label)
    {
        if (attempt.AttemptsRemaining <= 0)
            return $"Incorrect {label}.";

        var left = attempt.AttemptsRemaining;
        return $"Incorrect {label}. {left} attempt{(left == 1 ? string.Empty : "s")} left.";
    }

    private static string CooldownText(TimeSpan left)
    {
        if (left.TotalMinutes >= 1)
        {
            var minutes = (int)Math.Ceiling(left.TotalMinutes);
            return $"Too many attempts. Try again in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));
        return $"Too many attempts. Try again in {seconds} second{(seconds == 1 ? string.Empty : "s")}.";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A close request from the user (Alt+F4, task manager, etc.) counts as "release" —
        // except in PIN mode, where only the PIN may release the lock.
        if (!AllowClose)
        {
            e.Cancel = true;

            if (_closeRequestsUnlock)
                _onUnlock();

            return;
        }

        base.OnClosing(e);
    }
}
