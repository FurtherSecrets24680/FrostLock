using System.Drawing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrostLock.Services;

/// <summary>Loads the packaged .ico for the tray and window chrome, with a safe fallback.</summary>
public static class IconFactory
{
    // Assembly-qualified so the icon still resolves when FrostLock is loaded by another
    // host (design tools, tests) instead of being the entry assembly.
    private static readonly Uri IcoUri = new(
        $"pack://application:,,,/{typeof(IconFactory).Assembly.GetName().Name};component/Assets/frostlock.ico",
        UriKind.Absolute);

    private static System.Drawing.Icon? _trayIcon;

    public static System.Drawing.Icon TrayIcon()
    {
        if (_trayIcon is not null) return _trayIcon;
        try
        {
            var info = System.Windows.Application.GetResourceStream(IcoUri);
            if (info?.Stream is { } stream)
            {
                using var s = stream;
                _trayIcon = new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
                return _trayIcon;
            }
        }
        catch
        {
            // fall through to a system icon
        }

        _trayIcon = System.Drawing.SystemIcons.Shield;
        return _trayIcon;
    }

    public static ImageSource? WindowIconSource()
    {
        try
        {
            var decoder = BitmapDecoder.Create(IcoUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}