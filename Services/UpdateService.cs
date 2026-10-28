using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FrostLock.Services;

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool UpdateAvailable,
    string? LatestVersion,
    string? Error,
    string? DownloadUrl = null,
    string? AssetName = null,
    long AssetSize = 0,
    string? Sha256 = null);

/// <summary>Asks GitHub for the latest published release and compares it to this build.</summary>
public static class UpdateService
{
    public const string ReleasesPageUrl = "https://github.com/FurtherSecrets24680/FrostLock/releases/latest";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/FurtherSecrets24680/FrostLock/releases/latest";

    public static Version CurrentVersion =>
        Normalize(Assembly.GetExecutingAssembly().GetName().Version);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // The GitHub API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FrostLock");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(LatestReleaseApi, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("tag_name", out var tagElement))
                return new UpdateCheckResult(false, false, null, "The release had no tag.");

            var latest = ParseTag(tagElement.GetString());
            if (latest is null)
                return new UpdateCheckResult(false, false, null, "The release tag was not a version.");

            var asset = FindInstallerAsset(doc.RootElement);

            return new UpdateCheckResult(
                true,
                latest > CurrentVersion,
                latest.ToString(3),
                null,
                asset?.Url,
                asset?.Name,
                asset?.Size ?? 0,
                asset?.Sha256);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads a release asset while computing its SHA-256, then checks the size and PE
    /// signature. A missing or malformed expected hash fails closed.
    /// </summary>
    public static async Task DownloadAsync(
        string url,
        string destination,
        long expectedSize,
        string? expectedSha256,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var expectedHash = RequireSha256(expectedSha256);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FrostLock");

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? expectedSize;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write,
                         FileShare.None, 81920, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;

                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), ct);
                    if (read <= 0) break;

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;

                    if (total > 0)
                        progress?.Report(Math.Clamp(received / (double)total, 0d, 1d));
                }

                await target.FlushAsync(ct);
                var actualHash = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash)))
                    throw new InvalidDataException("The installer SHA-256 does not match the digest published by GitHub.");
            }

            var info = new FileInfo(destination);
            if (expectedSize > 0 && info.Length != expectedSize)
                throw new InvalidDataException(
                    $"The download was {info.Length:N0} bytes; this release said {expectedSize:N0}.");

            if (!IsWindowsExecutable(destination))
                throw new InvalidDataException("The downloaded file is not a Windows installer.");
        }
        catch
        {
            try { File.Delete(destination); } catch { /* ignore cleanup failure */ }
            throw;
        }
    }

    internal static (string Name, string Url, long Size, string? Sha256)? FindInstallerAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            // The Inno Setup output is named FrostLock-<version>-Setup.exe.
            if (!name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                continue;

            var size = asset.TryGetProperty("size", out var sizeElement)
                       && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0;

            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;

            return (Path.GetFileName(name), url, size, digest);
        }

        return null;
    }

    internal static bool TryNormalizeSha256(string? digest, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(digest)) return false;

        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];

        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) return false;
        normalized = value.ToUpperInvariant();
        return true;
    }

    internal static string RequireSha256(string? digest)
    {
        if (TryNormalizeSha256(digest, out var normalized)) return normalized;
        throw new InvalidDataException("This release has no valid SHA-256 digest. The installer was not downloaded.");
    }

    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false; // "MZ"

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > stream.Length - 4) return false;

            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550; // "PE\0\0"
        }
        catch
        {
            return false;
        }
    }

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        return Version.TryParse(text, out var parsed) ? Normalize(parsed) : null;
    }

    private static Version Normalize(Version? version)
    {
        version ??= new Version(1, 0, 0);
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }
}
