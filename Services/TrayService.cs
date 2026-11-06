using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using WinForms = System.Windows.Forms;

namespace FrostLock.Services;

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _freezeItem;
    private readonly MenuItem _quickFreezeItem;
    private readonly MenuItem _stopWatchingItem;
    private readonly MenuItem _exitItem;
    private bool _frozen;
    private bool _usbWatching;
    private IntPtr _mouseHook;
    private readonly LowLevelMouseProc _mouseHookProc;

    public event Action? FreezeRequested;
    public event Action<int>? QuickFreezeRequested;
    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? StopWatchingRequested;

    public TrayService()
    {
        _mouseHookProc = OnMouseHook;
        _freezeItem = CreateItem("Freeze input now", SymbolRegular.LockClosed24);
        _freezeItem.Click += (_, _) => FreezeRequested?.Invoke();

        _quickFreezeItem = CreateItem("Freeze for", SymbolRegular.Timer24);
        foreach (var (label, seconds) in new[]
                 {
                     ("5 minutes", 5 * 60),
                     ("15 minutes", 15 * 60),
                      ("30 minutes", 30 * 60),
                      ("1 hour", 60 * 60),
                      ("2 hours", 2 * 60 * 60),
                      ("4 hours", 4 * 60 * 60),
                      ("Until I unlock", 0)
                 })
        {
            var item = CreateItem(label, SymbolRegular.Timer24);
            item.Click += (_, _) => QuickFreezeRequested?.Invoke(seconds);
            _quickFreezeItem.Items.Add(item);
        }

        _stopWatchingItem = CreateItem("Stop USB watching", SymbolRegular.UsbStick24);
        _stopWatchingItem.IsEnabled = false;
        _stopWatchingItem.Click += (_, _) => StopWatchingRequested?.Invoke();

        var openItem = CreateItem("Open FrostLock", SymbolRegular.Apps24);
        openItem.Click += (_, _) => ShowRequested?.Invoke();

        var settingsItem = CreateItem("Settings", SymbolRegular.Settings24);
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = CreateItem("Exit", SymbolRegular.Power24);
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _exitItem = exitItem;

        _menu = new ContextMenu { StaysOpen = false };
        _menu.Closed += (_, _) => RemoveOutsideClickHook();
        _menu.Items.Add(_freezeItem);
        _menu.Items.Add(_quickFreezeItem);
        _menu.Items.Add(_stopWatchingItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(openItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = IconFactory.TrayIcon(),
            Text = "FrostLock: ready",
            Visible = true
        };

        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
                ShowMenu();
            else if (e.Button == WinForms.MouseButtons.Left && !_frozen)
                ShowRequested?.Invoke();
        };

        _icon.DoubleClick += (_, _) =>
        {
            if (!_frozen) ShowRequested?.Invoke();
        };
    }

    private static MenuItem CreateItem(string header, SymbolRegular symbol) => new()
    {
        Header = header,
        Icon = new SymbolIcon { Symbol = symbol, FontSize = 16, Margin = new Thickness(0, 0, 8, 0) }
    };

    private void ShowMenu()
    {
        if (_menu.IsOpen) _menu.IsOpen = false;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.PlacementTarget = null;
        _menu.IsOpen = true;
        _menu.Dispatcher.BeginInvoke(() => InstallOutsideClickHook(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void InstallOutsideClickHook()
    {
        RemoveOutsideClickHook();
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && _menu.IsOpen && IsButtonDown(message))
            {
                var click = Marshal.PtrToStructure<MouseHookData>(data);
                if (!IsInsideMenu(click.Point.X, click.Point.Y))
                    _menu.Dispatcher.BeginInvoke(() => _menu.IsOpen = false, System.Windows.Threading.DispatcherPriority.Input);
            }
        }
        catch { /* A transient popup teardown must not break the global mouse hook. */ }

        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private bool IsInsideMenu(int x, int y)
    {
        if (IsInsideSource(_menu, x, y)) return true;
        return OpenSubmenus(_menu.Items).Any(item => IsInsideSource(item, x, y));
    }

    private static IEnumerable<MenuItem> OpenSubmenus(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (!item.IsSubmenuOpen) continue;
            yield return item;
            foreach (var nested in OpenSubmenus(item.Items)) yield return nested;
        }
    }

    private static bool IsInsideSource(Visual visual, int x, int y)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source || !GetWindowRect(source.Handle, out var bounds))
            return false;
        return x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
    }

    private static bool IsButtonDown(IntPtr message)
    {
        var value = message.ToInt32();
        return value is WmLButtonDown or WmRButtonDown or WmMButtonDown;
    }

    private void RemoveOutsideClickHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    public void SetFrozen(bool frozen, bool canRelease = true, bool canExtend = false)
    {
        _frozen = frozen;
        _freezeItem.IsEnabled = !frozen && !_usbWatching;
        _quickFreezeItem.IsEnabled = !frozen && !_usbWatching;
        _exitItem.IsEnabled = !frozen || canRelease;
        _icon.Text = frozen ? "FrostLock: input frozen" : "FrostLock: ready";
    }

    public void SetTooltip(string text)
    {
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void SetUsbWatching(bool watching)
    {
        _usbWatching = watching;
        _freezeItem.IsEnabled = !_frozen && !watching;
        _quickFreezeItem.IsEnabled = !_frozen && !watching;
        _stopWatchingItem.IsEnabled = watching;
        _stopWatchingItem.Header = watching ? "Stop USB watching" : "USB mode is not watching";
    }

    public void ShowBalloon(string title, string body)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = body;
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _menu.IsOpen = false;
            RemoveOutsideClickHook();
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect bounds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
