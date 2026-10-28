using System.IO;
using System.Text.Json;
using FrostLock.Services;

namespace FrostLock.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void FindsTheSetupExecutable()
    {
        const string json = """
            {
              "assets": [
                { "name": "FrostLock-1.3.0-Setup.exe", "browser_download_url": "https://example/Setup.exe", "size": 4217233, "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                { "name": "FrostLock-1.3.0.zip", "browser_download_url": "https://example/portable.zip", "size": 81000000 }
              ]
            }
            """;

        var asset = Find(json);

        Assert.NotNull(asset);
        Assert.Equal("FrostLock-1.3.0-Setup.exe", asset!.Value.Name);
        Assert.Equal("https://example/Setup.exe", asset.Value.Url);
        Assert.Equal(4217233L, asset.Value.Size);
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", asset.Value.Sha256);
    }

    [Fact]
    public void IgnoresAnExecutableThatIsNotASetup()
    {
        const string json = """
            { "assets": [ { "name": "FrostLock.exe", "browser_download_url": "https://example/app.exe", "size": 1 } ] }
            """;

        Assert.Null(Find(json));
    }

    [Fact]
    public void SkipsAssetsWithoutANameOrUrl()
    {
        const string json = """
            {
              "assets": [
                { "name": "FrostLock-1.3.0-Setup.exe", "size": 1 },
                { "browser_download_url": "https://example/Setup.exe", "size": 1 },
                { "name": "FrostLock-1.4.0-Setup.exe", "browser_download_url": "https://example/next.exe", "size": 2 }
              ]
            }
            """;

        var asset = Find(json);

        Assert.NotNull(asset);
        Assert.Equal("FrostLock-1.4.0-Setup.exe", asset.Value.Name);
    }

    [Fact]
    public void MissingAssetsReturnNull()
    {
        Assert.Null(Find("""{ "tag_name": "v1.3.0" }"""));
        Assert.Null(Find("""{ "assets": [] }"""));
    }

    [Theory]
    [InlineData("FrostLock-Setup-1.3.0.EXE", true)]
    [InlineData("frostlock-setup.exe", true)]
    [InlineData("FrostLock-Setup-1.3.0.zip", false)]
    [InlineData("Setup.msi", false)]
    public void AssetNamesAreMatchedCaseInsensitively(string name, bool expected)
    {
        var json = $$"""
            { "assets": [ { "name": "{{name}}", "browser_download_url": "https://example/file", "size": 1 } ] }
            """;

        Assert.Equal(expected, Find(json) is not null);
    }

    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("sha1:0123456789abcdef0123456789abcdef01234567", false)]
    [InlineData("sha256:abc", false)]
    [InlineData(null, false)]
    public void ValidatesSha256Digest(string? digest, bool expected)
    {
        Assert.Equal(expected, UpdateService.TryNormalizeSha256(digest, out _));
    }

    [Fact]
    public void MissingDigestIsRejectedBeforeDownload()
    {
        var error = Assert.Throws<InvalidDataException>(() => UpdateService.RequireSha256(null));

        Assert.Contains("not downloaded", error.Message);
    }

    private static (string Name, string Url, long Size, string? Sha256)? Find(string json)
    {
        using var document = JsonDocument.Parse(json);
        return UpdateService.FindInstallerAsset(document.RootElement);
    }
}
