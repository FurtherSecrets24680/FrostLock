using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using FrostLock.Services;

namespace FrostLock.Views;

/// <summary>Version and environment information for bug reports.</summary>
public partial class AboutWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string GitHubUrl = "https://github.com/FurtherSecrets24680/FrostLock";

    private readonly string _appVersion;
    private readonly string _wpfUiVersion;
    private readonly string _publisher;
    private readonly string _role;
    private bool _sizeLocked;
    private bool _updateAvailable;

    public AboutWindow()
    {
        InitializeComponent();

        var icon = IconFactory.WindowIconSource();
        Icon = icon;

        if (icon is not null)
        {
            AboutTitleBar.Icon = new Wpf.Ui.Controls.ImageIcon { Source = icon };
            AppIcon.Source = icon;
        }

        _appVersion = FormatVersion(typeof(AboutWindow).Assembly.GetName().Version, "1.0.0");
        _wpfUiVersion = FormatVersion(typeof(Wpf.Ui.Controls.FluentWindow).Assembly.GetName().Version, "unknown");
        _publisher = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
                     ?? "FurtherSecrets";
        _role = FreezeController.IsAdministrator() ? "Administrator" : "Standard user";

        TxtVersion.Text = $"Version {_appVersion}";
        TxtApp.Text = $"FrostLock {_appVersion}";
        TxtPublisher.Text = _publisher;
        TxtDotNet.Text = RuntimeInformation.FrameworkDescription;
        TxtWpfUi.Text = _wpfUiVersion;
        TxtWindows.Text = RuntimeInformation.OSDescription;
        TxtArch.Text = RuntimeInformation.ProcessArchitecture.ToString();
        TxtRole.Text = _role;
        TxtSettings.Text = Path.Combine(SettingsService.SettingsDirectory, "settings.json");

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        ContentRendered += (_, _) => LockSize();
    }

    /// <summary>
    /// <see cref="Window.ResizeMode"/> stops the user dragging the borders, but Windows can still
    /// maximize the window (Win+Up) or snap it via the shell. Clamp the size to whatever the
    /// content needed so those commands leave it alone.
    /// </summary>
    private void LockSize()
    {
        if (_sizeLocked) return;
        _sizeLocked = true;

        var width = ActualWidth;
        var height = ActualHeight;

        SizeToContent = SizeToContent.Manual;
        Width = MinWidth = MaxWidth = width;
        Height = MinHeight = MaxHeight = height;
    }

    private static string FormatVersion(Version? version, string fallback)
        => version is null ? fallback : version.ToString(3);

    private string BuildDetails()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FrostLock {_appVersion}");
        sb.AppendLine($"Publisher: {_publisher}");
        sb.AppendLine($"Source: {GitHubUrl}");
        sb.AppendLine($".NET runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"WPF-UI: {_wpfUiVersion}");
        sb.AppendLine($"Windows: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Running as: {_role}");
        sb.AppendLine($"Settings: {Path.Combine(SettingsService.SettingsDirectory, "settings.json")}");
        return sb.ToString().TrimEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(BuildDetails(), copy: true);
            TxtCopyStatus.Text = "Details copied to the clipboard.";
            TxtCopyStatus.Visibility = Visibility.Visible;
        }
        catch
        {
            TxtCopyStatus.Text = "The clipboard is busy. Try again.";
            TxtCopyStatus.Visibility = Visibility.Visible;
        }
    }

    private void Source_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Handled here so the window never tries to navigate itself to the URL.
        e.Handled = true;

        try
        {
            Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });
        }
        catch
        {
            TxtCopyStatus.Text = $"Could not open the browser. The source is at {GitHubUrl}";
            TxtCopyStatus.Visibility = Visibility.Visible;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateAvailable)
        {
            OpenReleases();
            return;
        }

        BtnCheckUpdates.IsEnabled = false;
        TxtUpdateStatus.Text = "Checking for updates…";
        TxtUpdateStatus.Visibility = Visibility.Visible;

        var result = await UpdateService.CheckAsync();

        BtnCheckUpdates.IsEnabled = true;

        if (!result.Succeeded)
        {
            TxtUpdateStatus.Text = "Could not check for updates.";
            return;
        }

        if (result.UpdateAvailable)
        {
            _updateAvailable = true;
            TxtUpdateStatus.Text = $"Version {result.LatestVersion} is available.";
            BtnCheckUpdates.Content = "Download";

            if (BtnCheckUpdates.Icon is Wpf.Ui.Controls.SymbolIcon icon)
                icon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
            return;
        }

        TxtUpdateStatus.Text = $"You're up to date ({result.LatestVersion}).";
    }

    private void OpenReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch
        {
            TxtUpdateStatus.Text = $"Could not open the browser. See {UpdateService.ReleasesPageUrl}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
