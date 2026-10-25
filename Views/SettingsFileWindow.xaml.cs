using System.Diagnostics;
using System.IO;
using System.Windows;
using FrostLock.Models;
using FrostLock.Services;

namespace FrostLock.Views;

/// <summary>Editor for the persisted JSON settings.</summary>
public partial class SettingsFileWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings _settings;
    private readonly Func<AppSettings, string?> _apply;

    public SettingsFileWindow(AppSettings settings, Func<AppSettings, string?> apply)
    {
        InitializeComponent();

        _settings = settings;
        _apply = apply;

        RunPath.Text = SettingsService.SettingsFilePath;
        Loaded += (_, _) => ReloadFromDisk(showStatus: false);
    }

    private void ReloadFromDisk(bool showStatus = true)
    {
        try
        {
            var path = SettingsService.SettingsFilePath;
            JsonBox.Text = File.Exists(path)
                ? File.ReadAllText(path)
                : SettingsService.Serialize(_settings);

            if (showStatus)
                ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Success,
                    "Reloaded from disk", "The editor now shows the saved file.");
        }
        catch (Exception ex)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Could not read the file", ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsService.TryParse(JsonBox.Text, out var parsed, out var error))
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Not saved", error ?? "The settings could not be read.");
            return;
        }

        try
        {
            var hotkeyError = _apply(parsed);
            if (hotkeyError is not null)
            {
                ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    "Saved with a problem", hotkeyError);
                return;
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Could not apply the settings", ex.Message);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadFromDisk();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SettingsService.SettingsFilePath;
            var args = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path)}\"";

            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Could not open the folder", ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
