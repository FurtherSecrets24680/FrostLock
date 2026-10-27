using System.Collections.Generic;
using System.Runtime.InteropServices;
using FrostLock.Interop;

namespace FrostLock.Services;

/// <summary>
/// Global low-level keyboard/mouse blocker. Runs without administrator rights and can
/// selectively swallow keys, swallow the mouse, and detect an unlock combo even while
/// everything else is blocked.
/// </summary>
public sealed class HookBlocker : IDisposable
{
    public event Action? UnlockComboPressed;

    public bool BlockAllKeys { get; set; }
    public bool BlockMouse { get; set; }
    public bool BlockSystemKeys { get; set; } = true;
    public HotkeyBinding UnlockBinding { get; set; } = HotkeyBinding.None;

    /// <summary>Milliseconds the unlock combo must be held. 0 releases on the key press.</summary>
    public int UnlockHoldMs { get; set; }

    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_TAB = 0x09;
    private const int VK_F4 = 0x73;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_APPS = 0x5D;
    private const int VK_CTRL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_ALT = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_SHIFT = 0x10;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Native.LowLevelProc? _keyboardProc;
    private Native.LowLevelProc? _mouseProc;

    private readonly HashSet<int> _down = new();
    private bool _started;

    private System.Threading.Timer? _holdTimer;
    private volatile bool _comboDown;
    private int _holdFired;

    public bool Started => _started;
    public bool MouseBlocked => _started && BlockMouse;

    public bool Start()
    {
        if (_started) return true;

        var module = Native.GetModuleHandleW(null);

        // The keyboard hook also carries the unlock combo, so it is needed even when
        // nothing is being blocked.
        if (BlockAllKeys || BlockSystemKeys || UnlockBinding.IsValid)
        {
            _keyboardProc = KeyboardProc;
            _keyboardHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                _keyboardProc = null;
                return false;
            }
        }

        if (BlockMouse)
        {
            _mouseProc = MouseProc;
            _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                _mouseProc = null; // keyboard blocking still active
            }
        }

        _started = true;
        return true;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            int msg = wParam.ToInt32();
            bool isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool isUp = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;

            if (isDown) _down.Add(vk);
            else if (isUp) _down.Remove(vk);

            bool ctrl = _down.Contains(VK_CTRL) || _down.Contains(VK_LCONTROL) || _down.Contains(VK_RCONTROL);
            bool alt = _down.Contains(VK_ALT) || _down.Contains(VK_LMENU) || _down.Contains(VK_RMENU);
            bool shift = _down.Contains(VK_SHIFT) || _down.Contains(VK_LSHIFT) || _down.Contains(VK_RSHIFT);
            bool win = _down.Contains(VK_LWIN) || _down.Contains(VK_RWIN);

            // The unlock combo is honoured above everything else. With a hold time set,
            // the timer only fires while every key of the combo is still down.
            bool comboHeld = UnlockBinding.IsValid
                             && _down.Contains(UnlockBinding.VirtualKey)
                             && UnlockBinding.Matches(ctrl, alt, shift, win, UnlockBinding.VirtualKey);

            if (isDown && vk == UnlockBinding.VirtualKey && comboHeld)
            {
                if (UnlockHoldMs <= 0)
                {
                    BeginUnlock();
                    return (IntPtr)1;
                }

                if (!_comboDown)
                {
                    _comboDown = true;
                    Interlocked.Exchange(ref _holdFired, 0);

                    _holdTimer ??= new System.Threading.Timer(
                        _ => OnHoldElapsed(), null, Timeout.Infinite, Timeout.Infinite);
                    _holdTimer.Change(UnlockHoldMs, Timeout.Infinite);
                }

                return (IntPtr)1;
            }

            if (!comboHeld)
            {
                _comboDown = false;
                try { _holdTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* ignore */ }
            }

            if (ShouldBlockKey(vk, ctrl, alt, shift, win))
                return (IntPtr)1;
        }

        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool ShouldBlockKey(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        if (BlockAllKeys) return true;
        if (!BlockSystemKeys) return false;

        if (vk == VK_LWIN || vk == VK_RWIN || vk == VK_APPS) return true;
        if (win) return true;
        if (alt && vk is VK_TAB or VK_ESCAPE or VK_F4 or VK_SPACE) return true;
        if (ctrl && vk == VK_ESCAPE) return true;
        if (ctrl && shift && vk == VK_ESCAPE) return true;
        return false;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && BlockMouse)
            return (IntPtr)1;
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void OnHoldElapsed()
    {
        if (!_comboDown) return;
        if (Interlocked.Exchange(ref _holdFired, 1) != 0) return;

        BeginUnlock();
    }

    private void BeginUnlock() => UnlockComboPressed?.Invoke();

    public void Dispose()
    {
        _comboDown = false;
        try { _holdTimer?.Dispose(); } catch { /* ignore */ }
        _holdTimer = null;

        if (_keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _keyboardProc = null;
        _mouseProc = null;
        _down.Clear();
        _started = false;
    }
}