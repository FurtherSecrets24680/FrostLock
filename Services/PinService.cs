using System.Security.Cryptography;
using System.Text;
using FrostLock.Interop;

namespace FrostLock.Services;

/// <summary>
/// Salts and hashes the PIN with PBKDF2-SHA256 so the settings file never holds the PIN
/// itself. The hash is additionally wrapped with DPAPI before it is written to disk.
/// Verification is constant-time.
/// </summary>
public static class PinService
{
    private const int Iterations = 120_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private const string DpapiPrefix = "dpapi:";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("FrostLock.Pin.v1");

    public static (string Salt, string Hash) Create(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Wraps a hash in DPAPI before persistence. Already-protected values pass through.</summary>
    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return value;

        try
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var protectedBytes = Native.DpapiProtect(plain, DpapiEntropy);
            return protectedBytes is null
                ? value
                : DpapiPrefix + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>Unwraps a DPAPI-protected hash. Plain values pass through; unreadable ones return null.</summary>
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return value;

        try
        {
            var protectedBytes = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            var plain = Native.DpapiUnprotect(protectedBytes, DpapiEntropy);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public static bool Verify(string pin, string? saltBase64, string? hashBase64)
    {
        if (string.IsNullOrEmpty(saltBase64) || string.IsNullOrEmpty(hashBase64)) return false;

        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var plainHash = Unprotect(hashBase64);
            if (plainHash is null) return false;

            var expected = Convert.FromBase64String(plainHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
