using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrostLock.Services;
using QRCoder;

namespace FrostLock.Views;

public partial class MobilePairingQrWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Func<MobilePairingInvite> _createInvite;
    private readonly Action<string?> _revokeInvite;
    private readonly System.Windows.Threading.DispatcherTimer _expiryTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private MobilePairingInvite? _invite;

    public MobilePairingQrWindow(
        MobilePairingInvite invite,
        Func<MobilePairingInvite> createInvite,
        Action<string?> revokeInvite)
    {
        InitializeComponent();
        _createInvite = createInvite;
        _revokeInvite = revokeInvite;
        _expiryTimer.Tick += (_, _) => UpdateExpiry();
        Closed += (_, _) =>
        {
            _expiryTimer.Stop();
            _revokeInvite(_invite?.Token);
        };
        DisplayInvite(invite);
        _expiryTimer.Start();
    }

    public void RefreshCode()
    {
        var previousToken = _invite?.Token;
        _revokeInvite(previousToken);
        _invite = null;
        try
        {
            var nextInvite = _createInvite();
            DisplayInvite(nextInvite);
        }
        catch (Exception ex)
        {
            QrImage.Source = null;
            InviteCode.Clear();
            InviteEndpoint.Text = string.Empty;
            ExpiryLabel.Text = "Unavailable";
            StatusText.Text = $"Could not refresh the pairing code: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(190, 55, 55));
        }
    }

    private void DisplayInvite(MobilePairingInvite invite)
    {
        _invite = invite;
        var code = invite.ToCode();
        InviteCode.Text = code;
        InviteEndpoint.Text = invite.Endpoint;
        StatusText.Text = "This code works once and expires automatically. Refresh it if you need a new code.";
        StatusText.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(12);
        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
        }
        QrImage.Source = image;
        UpdateExpiry();
    }

    private void UpdateExpiry()
    {
        if (_invite is null) return;
        var remaining = DateTimeOffset.FromUnixTimeSeconds(_invite.ExpiresAt) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ExpiryLabel.Text = "Expired";
            StatusText.Text = "This code has expired. Refresh the QR code to pair a phone.";
            return;
        }

        ExpiryLabel.Text = $"Expires in {(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InviteCode.Text)) return;
        Clipboard.SetText(InviteCode.Text);
        StatusText.Text = "Encrypted pairing text copied to the clipboard.";
    }

    private void ToggleEncryptedText_Click(object sender, RoutedEventArgs e)
    {
        var show = EncryptedTextPanel.Visibility != Visibility.Visible;
        EncryptedTextPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleEncryptedTextButton.Content = show ? "Hide encrypted text  ▴" : "Show encrypted text  ▾";
        System.Windows.Automation.AutomationProperties.SetName(ToggleEncryptedTextButton,
            show ? "Hide encrypted pairing text" : "Show encrypted pairing text");
    }

    private void RefreshCode_Click(object sender, RoutedEventArgs e) => RefreshCode();
}
