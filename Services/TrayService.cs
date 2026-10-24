using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinForms = System.Windows.Forms;

namespace FrostLock.Services;

/// <summary>
/// Notification-area icon plus its context menu. The icon itself comes from WinForms
/// (there is no WPF equivalent), while the menu is a WPF one so it picks up the same
/// Fluent styling as the rest of the app.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly MenuItem _freezeItem;
    private readonly MenuItem _stopItem;
    private bool _frozen;

    public event Action? FreezeRequested;
    public event Action? UnlockRequested;
    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _freezeItem = new MenuItem { Header = "Freeze input now" };
        _freezeItem.Click += (_, _) => FreezeRequested?.Invoke();

        _stopItem = new MenuItem { Header = "Release freeze", IsEnabled = false };
        _stopItem.Click += (_, _) => UnlockRequested?.Invoke();

        var openItem = new MenuItem { Header = "Open FrostLock" };
        openItem.Click += (_, _) => ShowRequested?.Invoke();

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new ContextMenu();
        _menu.Items.Add(_freezeItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(openItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = IconFactory.TrayIcon(),
            Text = "FrostLock — ready",
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

    private void ShowMenu()
    {
        // A WPF ContextMenu closes on an outside click by itself; it only needs a nudge to
        // appear at the pointer, which is where the notification icon lives.
        _menu.Placement = PlacementMode.MousePoint;
        _menu.PlacementTarget = null;
        _menu.IsOpen = true;
    }

    public void SetFrozen(bool frozen)
    {
        _frozen = frozen;
        _freezeItem.IsEnabled = !frozen;
        _stopItem.IsEnabled = frozen;
        _icon.Text = frozen ? "FrostLock — input frozen" : "FrostLock — ready";
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text is capped at 63 characters.
        _icon.Text = text.Length > 63 ? text[..63] : text;
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
            // Balloons are best-effort.
        }
    }

    public void Dispose()
    {
        try
        {
            _menu.IsOpen = false;
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
