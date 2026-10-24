using System.Windows;
using System.Windows.Interop;
using FrostLock.Interop;

namespace FrostLock.Services;

/// <summary>Registers the "start freeze" global hotkey on the main window's HWND.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int StartHotkeyId = 0xF10C;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    public event Action? StartPressed;

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
        UnregisterStart();
        LastError = null;

        if (!binding.IsValid || _hwnd == IntPtr.Zero)
        {
            LastError = "Invalid hotkey.";
            return false;
        }

        if (Native.RegisterHotKey(_hwnd, StartHotkeyId, binding.Win32Modifiers | Native.MOD_NOREPEAT,
                (uint)binding.VirtualKey))
        {
            _registered = true;
            return true;
        }

        LastError = $"'{binding.Display}' is already in use by another app.";
        return false;
    }

    public void UnregisterStart()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_hwnd, StartHotkeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == StartHotkeyId)
        {
            StartPressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterStart();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}