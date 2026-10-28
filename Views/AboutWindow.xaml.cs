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
    private UpdateCheckResult? _update;
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;

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
            if (e.Key == Key.Escape && !_downloading) Close();
        };

        Closing += (_, _) =>
        {
            try { _downloadCts?.Cancel(); } catch { /* ignore */ }
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
        if (_downloading) return;

        if (_update is { UpdateAvailable: true })
        {
            if (string.IsNullOrWhiteSpace(_update.DownloadUrl)
                || !UpdateService.TryNormalizeSha256(_update.Sha256, out _))
            {
                OpenReleases();
                return;
            }

            await DownloadAndInstallAsync(_update);
            return;
        }

        BtnCheckUpdates.IsEnabled = false;
        TxtUpdateStatus.Text = "Checking for updates…";
        TxtUpdateStatus.ToolTip = null;
        TxtUpdateStatus.Visibility = Visibility.Visible;

        var result = await UpdateService.CheckAsync();

        BtnCheckUpdates.IsEnabled = true;
        _update = result;

        if (!result.Succeeded)
        {
            TxtUpdateStatus.Text = "Could not check for updates.";
            TxtUpdateStatus.ToolTip = result.Error;
            return;
        }

        if (!result.UpdateAvailable)
        {
            TxtUpdateStatus.Text = $"You're up to date ({result.LatestVersion}).";
            SetUpdateButton("Check for updates", Wpf.Ui.Controls.SymbolRegular.ArrowSync24);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            TxtUpdateStatus.Text =
                $"Version {result.LatestVersion} is available, but this release has no installer.";
            SetUpdateButton("Open release page", Wpf.Ui.Controls.SymbolRegular.ArrowDownload24);
            OpenReleases();
            return;
        }

        if (!UpdateService.TryNormalizeSha256(result.Sha256, out _))
        {
            TxtUpdateStatus.Text =
                $"Version {result.LatestVersion} is available, but GitHub has not published a valid SHA-256 digest. Auto-install is disabled.";
            SetUpdateButton("Open release page", Wpf.Ui.Controls.SymbolRegular.ArrowDownload24);
            return;
        }

        TxtUpdateStatus.Text = $"Version {result.LatestVersion} is available.";
        SetUpdateButton("Download and install", Wpf.Ui.Controls.SymbolRegular.ArrowDownload24);
    }

    private void SetUpdateButton(string text, Wpf.Ui.Controls.SymbolRegular symbol)
    {
        BtnCheckUpdates.Content = text;

        if (BtnCheckUpdates.Icon is Wpf.Ui.Controls.SymbolIcon icon)
            icon.Symbol = symbol;
    }

    private async Task DownloadAndInstallAsync(UpdateCheckResult update)
    {
        if (update.DownloadUrl is not { Length: > 0 } url)
        {
            OpenReleases();
            return;
        }

        var fileName = Path.GetFileName(
            string.IsNullOrWhiteSpace(update.AssetName) ? "FrostLock-Setup.exe" : update.AssetName);

        if (string.IsNullOrWhiteSpace(fileName)) fileName = "FrostLock-Setup.exe";

        var directory = Path.Combine(Path.GetTempPath(), "FrostLock", "update");
        var destination = Path.Combine(directory, fileName);

        _downloading = true;
        BtnCheckUpdates.IsEnabled = false;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        TxtUpdateStatus.ToolTip = null;

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(value =>
        {
            UpdateProgress.Value = value;
            TxtUpdateStatus.Text = $"Downloading {fileName}… {value:P0}";
        });

        try
        {
            Directory.CreateDirectory(directory);

            await UpdateService.DownloadAsync(
                url, destination, update.AssetSize, update.Sha256, progress, _downloadCts.Token);

            UpdateProgress.Value = 1;
            TxtUpdateStatus.Text = "Download complete. Starting the installer…";

            StartInstaller(destination);
        }
        catch (OperationCanceledException)
        {
            TxtUpdateStatus.Text = "The update was cancelled.";
        }
        catch (Exception ex)
        {
            UpdateProgress.Visibility = Visibility.Hidden;
            TxtUpdateStatus.Text = "Could not download the update.";
            TxtUpdateStatus.ToolTip = ex.Message;
        }
        finally
        {
            _downloading = false;
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    /// <summary>
    /// Starts the downloaded installer and closes FrostLock so the installer can replace it.
    /// Inno Setup's CloseApplications knows how to wait for the mutex to go away.
    /// </summary>
    private void StartInstaller(string path)
    {
        // Exiting must never be a way past an active freeze, especially a PIN lock.
        if (System.Windows.Application.Current is App { Controller.IsActive: true })
        {
            UpdateProgress.Visibility = Visibility.Hidden;
            TxtUpdateStatus.Text = "Finish the current freeze before installing an update.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = "/CLOSEAPPLICATIONS /NORESTART"
            });

            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateProgress.Visibility = Visibility.Hidden;
            TxtUpdateStatus.Text = "Could not start the installer. The download was kept.";
            TxtUpdateStatus.ToolTip = $"{ex.Message}\n{path}";
        }
    }

    private void OpenReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch
        {
            TxtUpdateStatus.Text = "Could not open the browser.";
            TxtUpdateStatus.ToolTip = UpdateService.ReleasesPageUrl;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
