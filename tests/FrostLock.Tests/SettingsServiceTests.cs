using System.IO;
using FrostLock.Models;
using FrostLock.Services;

namespace FrostLock.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void MissingFileReturnsDefaults()
    {
        var directory = NewDirectory();
        try
        {
            var settings = SettingsService.Load(directory);

            Assert.Equal(FreezeMode.Gentle, settings.Mode);
            Assert.False(settings.HasPin);
            Assert.Equal(5, settings.SettingsVersion);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var directory = NewDirectory();
        try
        {
            var original = new AppSettings
            {
                Mode = FreezeMode.Strict,
                DurationSeconds = 900,
                FreezeUntilTime = TimeSpan.FromHours(16.5),
                ExtendHotkey = "Ctrl+Alt+X",
                ArmSeconds = 0,
                LockScreenMessage = "Back soon"
            };

            SettingsService.Save(original, directory);
            var loaded = SettingsService.Load(directory);

            Assert.Equal(FreezeMode.Strict, loaded.Mode);
            Assert.Equal(900, loaded.DurationSeconds);
            Assert.Equal(TimeSpan.FromHours(16.5), loaded.FreezeUntilTime);
            Assert.Equal("Ctrl+Alt+X", loaded.ExtendHotkey);
            Assert.Equal(0, loaded.ArmSeconds);
            Assert.Equal("Back soon", loaded.LockScreenMessage);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void CorruptMainFileFallsBackToBackup()
    {
        var directory = NewDirectory();
        try
        {
            SettingsService.Save(new AppSettings { Mode = FreezeMode.Gentle, DurationSeconds = 111 }, directory);
            SettingsService.Save(new AppSettings { Mode = FreezeMode.Strict, DurationSeconds = 222 }, directory);

            File.WriteAllText(Path.Combine(directory, "settings.json"), "{ not json");

            var loaded = SettingsService.Load(directory);

            Assert.Equal(FreezeMode.Gentle, loaded.Mode);
            Assert.Equal(111, loaded.DurationSeconds);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void EmptyJsonIsRejected()
    {
        Assert.False(SettingsService.TryParse("   ", out _, out var error));
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidJsonReportsTheLine()
    {
        Assert.False(SettingsService.TryParse("{\n  \"Mode\": \n}", out _, out var error));
        Assert.StartsWith("Invalid JSON at line 3", error);
    }

    [Fact]
    public void CommentsTrailingCommasAndCaseAreAccepted()
    {
        const string json = """
            {
              // hand-edited settings
              "mode": "strict",
              "durationsEconds": 60,
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var settings, out var error), error);
        Assert.Equal(FreezeMode.Strict, settings.Mode);
        Assert.Equal(60, settings.DurationSeconds);
    }

    [Fact]
    public void LegacyVersionOneLosesItsImplicitSafetyCap()
    {
        const string json = """
            {
              "SettingsVersion": 1,
              "DurationSeconds": 0,
              "SafetyMaxSeconds": 1800
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var settings, out var error), error);
        Assert.Equal(0, settings.SafetyMaxSeconds);
        Assert.Equal(5, settings.SettingsVersion);
    }

    [Fact]
    public void NegativeAndOversizedValuesAreClamped()
    {
        const string json = """
            {
              "DurationSeconds": -5,
              "ArmSeconds": -1,
              "SafetyMaxSeconds": -10,
              "FreezeCount": -3,
              "UnlockHoldMs": 9000,
              "PinLockoutLevel": 99,
              "PanicLockoutLevel": -4
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var settings, out var error), error);
        Assert.Equal(0, settings.DurationSeconds);
        Assert.Equal(0, settings.ArmSeconds);
        Assert.Equal(0, settings.SafetyMaxSeconds);
        Assert.Equal(0, settings.FreezeCount);
        Assert.Equal(5000, settings.UnlockHoldMs);
        Assert.Equal(Services.PinThrottle.MaxLockoutLevel, settings.PinLockoutLevel);
        Assert.Equal(0, settings.PanicLockoutLevel);
    }

    [Fact]
    public void LockoutInTheFarFutureIsClamped()
    {
        var json = $$"""
            {
              "PinLockoutLevel": 0,
              "PinLockedUntilUtc": "{{DateTime.UtcNow.AddDays(1):o}}"
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var settings, out var error), error);
        Assert.NotNull(settings.PinLockedUntilUtc);
        Assert.True(settings.PinLockedUntilUtc <= DateTime.UtcNow.AddSeconds(32));
        Assert.True(settings.PinLockedUntilUtc > DateTime.UtcNow);
    }

    [Fact]
    public void PinSurvivesSerializationAndStillVerifies()
    {
        var original = new AppSettings();
        original.SetPin("4821");

        var json = SettingsService.Serialize(original);

        Assert.True(SettingsService.TryParse(json, out var loaded, out var error), error);
        Assert.True(loaded.HasPin);
        Assert.True(loaded.VerifyPin("4821"));
        Assert.False(loaded.VerifyPin("0000"));
    }

    [Fact]
    public void ExportOmitsSecrets()
    {
        var original = new AppSettings();
        original.SetPin("4821");
        original.SetPanicPhrase("a long way back in");

        var json = SettingsService.Serialize(original, includeSecrets: false);

        Assert.True(SettingsService.TryParse(json, out var loaded, out var error), error);
        Assert.False(loaded.HasPin);
        Assert.False(loaded.HasPanicPhrase);
        Assert.DoesNotContain("dpapi:", json);
    }

    [Fact]
    public void UnreadableProtectedHashIsDropped()
    {
        const string json = """
            {
              "PinSalt": "c2FsdA==",
              "PinHash": "dpapi:not-base64"
            }
            """;

        Assert.True(SettingsService.TryParse(json, out var settings, out var error), error);
        Assert.False(settings.HasPin);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "FrostLock.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
