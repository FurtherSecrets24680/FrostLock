using System.Windows;
using FrostLock.Models;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FrostLock.Services;

/// <summary>
/// Applies the saved light/dark/system choice to WPF-UI's theme dictionary, and keeps a
/// "follow Windows" choice live by listening for system preference changes.
/// </summary>
public static class ThemeService
{
    private static AppTheme _current = AppTheme.System;
    private static bool _watching;

    /// <summary>Applies a theme now and remembers it for later system-change callbacks.</summary>
    public static void Apply(AppTheme theme)
    {
        _current = theme;
        ApplyNow(theme);
    }

    /// <summary>
    /// Starts listening for Windows theme changes. Safe to call once at startup; it is
    /// ignored when the user picked an explicit light or dark theme.
    /// </summary>
    public static void StartWatching()
    {
        if (_watching) return;

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _watching = true;
        }
        catch
        {
            // SystemEvents needs an interactive session; without it the app still themes once.
            _watching = false;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows reports light/dark switches as General (and colour changes as Color).
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
            return;

        if (_current != AppTheme.System) return;

        var app = Application.Current;
        if (app?.Dispatcher is not { } dispatcher || dispatcher.HasShutdownStarted) return;

        // The event can arrive on a non-UI thread; theme changes must run on the dispatcher.
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_current == AppTheme.System) ApplyNow(AppTheme.System);
        }));
    }

    private static void ApplyNow(AppTheme theme)
    {
        ApplicationTheme appTheme;

        switch (theme)
        {
            case AppTheme.Light:
                appTheme = ApplicationTheme.Light;
                ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, true);
                break;

            case AppTheme.Dark:
                appTheme = ApplicationTheme.Dark;
                ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, true);
                break;

            default:
                ApplicationThemeManager.ApplySystemTheme(true);
                appTheme = ApplicationThemeManager.GetAppTheme();
                break;
        }

        RefreshBackdrops(appTheme);
    }

    /// <summary>
    /// The theme dictionary updates brushes everywhere, but a Mica backdrop has to be told
    /// about the new light/dark state. Overlay windows are deliberately skipped: the lock
    /// screen is always dark.
    /// </summary>
    private static void RefreshBackdrops(ApplicationTheme theme)
    {
        if (Application.Current is null) return;

        foreach (Window window in Application.Current.Windows)
        {
            if (window is not FluentWindow) continue;

            try
            {
                WindowBackgroundManager.UpdateBackground(window, theme, WindowBackdropType.Mica);
            }
            catch
            {
                // A window without a handle yet will pick the backdrop up when shown.
            }
        }
    }
}
