using System.Net;
using System.Windows;
using System.Windows.Controls;
using FrostLock.Models;
using FrostLock.Services;

namespace FrostLock.Views;

public partial class MobileUnlockWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings _settings;
    private readonly MobileControlService _mobile;
    private readonly Action _saveAndApply;
    private bool _loading;
    private MobilePairingQrWindow? _pairingQrWindow;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    public MobileUnlockWindow(AppSettings settings, MobileControlService mobile, Action saveAndApply)
    {
        InitializeComponent();
        _settings = settings;
        _mobile = mobile;
        _saveAndApply = saveAndApply;
        _loading = true;
        EnableMobile.IsChecked = settings.MobileControlEnabled;
        AllowPinRelease.IsChecked = settings.MobilePinReleaseAllowed;
        AllowPublicTesting.IsChecked = settings.AllowMobileOnPublicProfileForTesting;
        _loading = false;
        RefreshState();
        _mobile.NetworkChanged += Mobile_NetworkChanged;
        _mobile.PairingCompleted += Mobile_PairingCompleted;
        _statusTimer.Tick += (_, _) => UpdateStatusText();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
        Closed += (_, _) => RevokeDisplayedInvite();
        Closed += (_, _) => _mobile.NetworkChanged -= Mobile_NetworkChanged;
        Closed += (_, _) => _mobile.PairingCompleted -= Mobile_PairingCompleted;
    }

    private void Mobile_NetworkChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RevokeDisplayedInvite();
            RefreshState();
        });
    }

    private void Mobile_PairingCompleted(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RevokeDisplayedInvite();
            RefreshState();
        });
    }

    private void RefreshState()
    {
        var listening = _mobile.ListeningAddresses;
        AddressBox.ItemsSource = listening.Select(address => address.ToString()).ToArray();
        if (AddressBox.SelectedIndex < 0 && AddressBox.Items.Count > 0) AddressBox.SelectedIndex = 0;
        CreateInviteButton.IsEnabled = EnableMobile.IsChecked == true && listening.Count > 0;
        AddressBox.IsEnabled = EnableMobile.IsChecked == true;
        LanInviteLabel.IsEnabled = EnableMobile.IsChecked == true;
        UpdateStatusText();

        var devices = _mobile.Devices;
        DevicesList.ItemsSource = devices;
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatusText()
    {
        var networkStatus = !_settings.MobileControlEnabled
            ? "LAN: off."
            : _mobile.IsRunning
                ? _settings.AllowMobileOnPublicProfileForTesting
                    ? $"LAN: development override active on private IPv4, port {MobileControlService.Port}."
                    : $"LAN: listening on private IPv4, port {MobileControlService.Port}. Windows Firewall may need a private-profile rule."
                : $"LAN: not listening. {_mobile.LastError ?? "No private IPv4 interface is available."}";
        ListenerStatus.Text = networkStatus;
    }

    private async void EnableMobile_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (EnableMobile.IsChecked == true)
        {
            var answer = AppDialog.Show(this,
                "Enable a local HTTPS listener for explicitly paired phones on private IPv4 networks? Do not enable this on an untrusted network or forward its port from your router.",
                "Enable Mobile Unlock", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                _loading = true;
                EnableMobile.IsChecked = false;
                _loading = false;
                return;
            }
            _settings.MobileControlEnabled = true;
        }
        else
        {
            _settings.MobileControlEnabled = false;
            RevokeDisplayedInvite();
        }
        _saveAndApply();
        await _mobile.StartAsync();
        RefreshState();
    }

    private async void AllowPublicTesting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AllowPublicTesting.IsChecked == true
            && AppDialog.Show(this,
                "This allows FrostLock to accept phone connections while Windows is using a Public network profile. Other devices on that network may be able to reach FrostLock. Continue only on a trusted test network.",
                "Unsafe development override", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loading = true;
            AllowPublicTesting.IsChecked = false;
            _loading = false;
            return;
        }

        _settings.AllowMobileOnPublicProfileForTesting = AllowPublicTesting.IsChecked == true;
        _saveAndApply();
        await _mobile.StopAsync();
        await _mobile.StartAsync();
        RefreshState();
    }

    private void AllowPinRelease_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AllowPinRelease.IsChecked == true && !_settings.MobilePinReleaseAllowed)
        {
            _loading = true;
            AllowPinRelease.IsChecked = false;
            _loading = false;
            PinReleaseConfirmation.Visibility = Visibility.Visible;
            PinReleaseConfirmation.BringIntoView();
            return;
        }

        _settings.MobilePinReleaseAllowed = AllowPinRelease.IsChecked == true;
        PinReleaseConfirmation.Visibility = Visibility.Collapsed;
        _saveAndApply();
    }

    private void ConfirmPinRelease_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        AllowPinRelease.IsChecked = true;
        _loading = false;
        PinReleaseConfirmation.Visibility = Visibility.Collapsed;
        _settings.MobilePinReleaseAllowed = true;
        _saveAndApply();
    }

    private void CancelPinRelease_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        AllowPinRelease.IsChecked = false;
        _loading = false;
        PinReleaseConfirmation.Visibility = Visibility.Collapsed;
    }

    private void CreateInvite_Click(object sender, RoutedEventArgs e)
    {
        if (_pairingQrWindow is { IsVisible: true })
        {
            _pairingQrWindow.Activate();
            _pairingQrWindow.RefreshCode();
            return;
        }

        if (AddressBox.SelectedItem is not string addressText || !IPAddress.TryParse(addressText, out var address))
        {
            RefreshState();
            return;
        }

        try
        {
            var invite = _mobile.CreateInvite(address);
            var window = new MobilePairingQrWindow(
                invite,
                () => _mobile.CreateInvite(address),
                _mobile.RevokeInvite)
            {
                Owner = this
            };
            _pairingQrWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_pairingQrWindow, window)) _pairingQrWindow = null;
            };
            window.Show();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Could not create pairing code", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshState();
        }
    }

    private void RevokeDisplayedInvite()
    {
        _pairingQrWindow?.Close();
        _pairingQrWindow = null;
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PairedMobileDevice device) return;
        if (AppDialog.Show(this, $"Remove {device.Name} from this PC? It will no longer be able to connect to FrostLock here.",
                "Revoke paired phone", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _mobile.RevokeDevice(device.DeviceId);
        RefreshState();
    }
}
