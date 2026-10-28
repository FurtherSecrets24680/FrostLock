using System.Windows;
using System.Windows.Interop;
using FrostLock.Interop;

namespace FrostLock.Services;

/// <summary>Registers global start and extend hotkeys on the main window's HWND.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int StartHotkeyId = 0xF10C;
    private const int ExtendHotkeyId = 0xF10D;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _startRegistered;
    private bool _extendRegistered;

    public event Action? StartPressed;
    public event Action? ExtendPressed;

    public string? LastError { get; private set; }

    public bool Attach(Window window)
    {
        try
        {
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            if (_source is null) return false;
            _source.AddHook(WndProc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool RegisterStart(HotkeyBinding binding)
    {
        Unregister(StartHotkeyId, ref _startRegistered);
        LastError = null;

        if (!binding.IsValid || _hwnd == IntPtr.Zero)
        {
            LastError = "Invalid hotkey.";
            return false;
        }

        if (Native.RegisterHotKey(_hwnd, StartHotkeyId, binding.Win32Modifiers | Native.MOD_NOREPEAT,
                (uint)binding.VirtualKey))
        {
            _startRegistered = true;
            return true;
        }

        LastError = $"'{binding.Display}' is already in use by another app.";
        return false;
    }

    public bool RegisterExtend(HotkeyBinding binding)
    {
        Unregister(ExtendHotkeyId, ref _extendRegistered);
        LastError = null;
        if (!binding.IsValid) return true;

        if (binding == HotkeyBinding.None || _hwnd == IntPtr.Zero)
        {
            LastError = "Invalid extend hotkey.";
            return false;
        }

        if (Native.RegisterHotKey(_hwnd, ExtendHotkeyId, binding.Win32Modifiers | Native.MOD_NOREPEAT,
                (uint)binding.VirtualKey))
        {
            _extendRegistered = true;
            return true;
        }

        LastError = $"'{binding.Display}' is already in use by another app.";
        return false;
    }

    public void UnregisterStart()
        => Unregister(StartHotkeyId, ref _startRegistered);

    public void UnregisterExtend()
        => Unregister(ExtendHotkeyId, ref _extendRegistered);

    private void Unregister(int id, ref bool registered)
    {
        if (registered && _hwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_hwnd, id);
            registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == StartHotkeyId)
        {
            StartPressed?.Invoke();
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == ExtendHotkeyId)
        {
            ExtendPressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterStart();
        UnregisterExtend();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}
