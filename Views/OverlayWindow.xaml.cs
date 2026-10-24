using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
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
    private readonly Func<string, bool>? _verifyPin;
    private readonly bool _closeRequestsUnlock;
    private bool _pinFocused;

    /// <summary>Set by the controller right before it closes the window.</summary>
    public bool AllowClose { get; set; }

    public OverlayWindow(
        System.Windows.Forms.Screen screen,
        Action onUnlock,
        Func<string, bool>? verifyPin = null,
        bool closeRequestsUnlock = true)
    {
        InitializeComponent();

        _screen = screen;
        _onUnlock = onUnlock;
        _verifyPin = verifyPin;
        _closeRequestsUnlock = closeRequestsUnlock;

        PinBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                SubmitPin();
            }
        };

        Loaded += (_, _) => PositionToScreen();
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

        ModeText.Text = state.ModeLabel;

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

        UnlockButton.Content = arming ? "Cancel freeze" : "Unlock now";

        // PIN mode owns the release: show the prompt instead of the Unlock button (the
        // button is still there during the arm delay so the freeze can be cancelled).
        bool pinMode = !arming && state.RequiresPin && _verifyPin is not null;
        PinPanel.Visibility = pinMode ? Visibility.Visible : Visibility.Collapsed;

        if (pinMode && !_pinFocused)
        {
            _pinFocused = true;
            PinBox.Focus();
        }

        // In Strict mode the pointer is dead once the freeze engages, so a disabled button
        // would just be dead weight on the lock screen. Hide it and rely on the hint.
        bool strictMouseBlocked = !arming && state.MouseBlocked && state.Mode == FreezeMode.Strict;
        UnlockButton.IsEnabled = !strictMouseBlocked;
        UnlockButton.Visibility = strictMouseBlocked || pinMode
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => _onUnlock();

    private void PinSubmit_Click(object sender, RoutedEventArgs e) => SubmitPin();

    private void SubmitPin()
    {
        if (_verifyPin is null) return;

        if (_verifyPin(PinBox.Password))
        {
            PinError.Visibility = Visibility.Collapsed;
            PinBox.Clear();
            _onUnlock();
            return;
        }

        PinError.Visibility = Visibility.Visible;
        PinBox.Clear();
        PinBox.Focus();
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
