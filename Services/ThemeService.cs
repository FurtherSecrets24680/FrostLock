using System.Windows;
using FrostLock.Models;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace FrostLock.Services;

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
            _watching = false;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
            return;

        if (_current != AppTheme.System) return;

        var app = Application.Current;
        if (app?.Dispatcher is not { } dispatcher || dispatcher.HasShutdownStarted) return;

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
            }
        }
    }
}
