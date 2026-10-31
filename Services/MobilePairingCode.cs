using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrostLock.Services;

/// <summary>Encrypts compact pairing payloads for the FrostLock Remote Android app.</summary>
internal static class MobilePairingCode
{
    private const string Prefix = "FL1.";
    private const int MaxRsaOaepSha256Bytes = 318;
    private const string AndroidPublicKeyBase64 =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA60eqLy3qBp8iGGZbjdYL" +
        "5q5qlj24dPxSi+mWUzr+ufKySIUmEjxH4ZaYgD0Cm1dLONLcywjvCQVW05vyIVFU" +
        "BNdSox6PFEqLjy6GM/AJWjc64/GG1nDXER+bXPAJKQY3mt2407Y7AF7I+txhl+d9" +
        "qWGUrt146TGaGJdxJDjEO0/KXzeP3/c0q5CSWw+GtDuUsaymS9uhsqEN7v651I9v" +
        "FLu4xCZEPc/9hGmEPzZz0o87wVIROuZxVEQYqFBHaYxIn9Yb0quEeRI2LeGu33Pi" +
        "lVS7VafkfDD5kvrlXqn/hXV4aOCq0fFb7DjVexA15XOlcEuIA58J41PAEo0gE+sM" +
        "E0cPqzPffOpeRuRa3E0HiQdFBmmtOCvRTTKCsND4nfhCGrHedrxBydZqdgGSxCf2" +
        "C9iRP49xvOVJpKLrNwMgLgQsPLPoYHkQZLHoMpa1SMFjuX9lDNzsS5D9WawABA4k" +
        "r6Na2m+dXbcsYnLecRu43PbQD0lMnIdqhsHE1cafCMOhAgMBAAE=";

    public static string Encrypt<T>(T payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (plaintext.Length > MaxRsaOaepSha256Bytes)
            throw new InvalidOperationException("Pairing data is too large to encode safely.");

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(AndroidPublicKeyBase64), out _);
        var ciphertext = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA256);
        return Prefix + Convert.ToBase64String(ciphertext).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
