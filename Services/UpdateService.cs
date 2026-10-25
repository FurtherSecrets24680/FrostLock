using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace FrostLock.Services;

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool UpdateAvailable,
    string? LatestVersion,
    string? Error);

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

            return new UpdateCheckResult(true, latest > CurrentVersion, latest.ToString(3), null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, null, ex.Message);
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
