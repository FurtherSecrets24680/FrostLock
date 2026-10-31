namespace FrostLock.Models;

public sealed class PairedMobileDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PublicKeyBase64 { get; set; } = string.Empty;
    public DateTime PairedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public bool Revoked { get; set; }

    public PairedMobileDevice Clone() => (PairedMobileDevice)MemberwiseClone();
}
