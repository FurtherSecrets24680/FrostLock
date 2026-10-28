using FrostLock.Models;
using FrostLock.Services;

namespace FrostLock.Tests;

public class CommandLineTests
{
    [Fact]
    public void FreezeCommandCarriesModeDurationArmAndSound()
    {
        var options = CliOptions.Parse(
            ["--freeze", "--mode", "strict", "--duration", "25m", "--arm", "10", "--mute"]);

        Assert.Equal("freeze", options.Command);
        Assert.Equal(FreezeMode.Strict, options.ModeOverride);
        Assert.Equal(1500, options.SecondsOverride!.Value);
        Assert.Equal(10, options.ArmOverride!.Value);
        Assert.False(options.SoundOverride);
        Assert.Null(options.Error);

        var command = options.ToCommand();
        Assert.Equal("freeze", command.Command);
        Assert.Equal("Strict", command.Mode);
        Assert.Equal(1500, command.DurationSeconds!.Value);
        Assert.Equal(10, command.ArmSeconds!.Value);
        Assert.False(command.Sound);
    }

    [Fact]
    public void UntilUnlockSetsAZeroDuration()
    {
        var options = CliOptions.Parse(["--freeze", "--until-unlock"]);

        Assert.Equal(0, options.SecondsOverride!.Value);
    }

    [Fact]
    public void FreezeUntilClockTimeBecomesANextOccurrenceDuration()
    {
        var options = CliOptions.Parse(["--freeze", "--until", "15:30"]);

        Assert.Equal(TimeSpan.FromHours(15.5), options.UntilTimeOfDay);
        Assert.Null(options.Error);
        Assert.InRange(options.ToCommand().DurationSeconds!.Value, 1, 24 * 60 * 60);
    }

    [Fact]
    public void ExtendDefaultsToFiveMinutesAndAcceptsCustomDuration()
    {
        var defaultExtend = CliOptions.Parse(["--extend"]);
        var customExtend = CliOptions.Parse(["--extend", "15m"]);

        Assert.Equal("extend", defaultExtend.ToCommand().Command);
        Assert.Equal(300, defaultExtend.ToCommand().DurationSeconds);
        Assert.Equal(900, customExtend.ToCommand().DurationSeconds);
        Assert.True(defaultExtend.WantsConsole);
    }

    [Fact]
    public void InvalidExtendDurationIsReported()
    {
        var options = CliOptions.Parse(["--extend", "later"]);

        Assert.Contains("extension", options.Error);
    }

    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("9:05", 9 * 60 + 5)]
    [InlineData("23:59", 23 * 60 + 59)]
    public void ClockTimesAreParsedIn24HourFormat(string text, int expectedMinutes)
    {
        Assert.True(CliOptions.TryParseClockTime(text, out var parsed));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), parsed);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("3pm")]
    [InlineData("")]
    public void InvalidClockTimesAreRejected(string text)
    {
        Assert.False(CliOptions.TryParseClockTime(text, out _));
    }

    [Fact]
    public void NoArgumentsMeansShow()
    {
        var options = CliOptions.Parse([]);

        Assert.Null(options.Command);
        Assert.Equal("show", options.ToCommand().Command);
    }

    [Fact]
    public void FirstCommandWins()
    {
        var options = CliOptions.Parse(["--exit", "--freeze"]);

        Assert.Equal("exit", options.Command);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("25m", 1500)]
    [InlineData("1h30m", 5400)]
    [InlineData("1h", 3600)]
    [InlineData("45", 45)]
    [InlineData("0", 0)]
    [InlineData("10M", 600)]
    public void DurationFormatsAreRead(string text, int expectedSeconds)
    {
        Assert.True(CliOptions.TryParseDuration(text, out var seconds));
        Assert.Equal(expectedSeconds, seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("5x")]
    [InlineData("-5")]
    [InlineData("h")]
    public void MalformedDurationsAreRejected(string text)
    {
        Assert.False(CliOptions.TryParseDuration(text, out _));
    }

    [Fact]
    public void DurationIsCappedAtOneDay()
    {
        var options = CliOptions.Parse(["--duration", "100h"]);

        Assert.Equal(24 * 60 * 60, options.SecondsOverride!.Value);
    }

    [Fact]
    public void SecondsAreCappedAtOneDay()
    {
        var options = CliOptions.Parse(["--seconds", "999999"]);

        Assert.Equal(24 * 60 * 60, options.SecondsOverride!.Value);
    }

    [Fact]
    public void ArmDelayIsCappedAtFiveMinutes()
    {
        var options = CliOptions.Parse(["--arm", "999"]);

        Assert.Equal(300, options.ArmOverride!.Value);
    }

    [Fact]
    public void NegativeArmDelayIsReported()
    {
        var options = CliOptions.Parse(["--arm", "-1"]);

        Assert.Null(options.ArmOverride);
        Assert.Contains("arm delay", options.Error);
    }

    [Fact]
    public void UnknownModeIsReported()
    {
        var options = CliOptions.Parse(["--mode", "kiosk"]);

        Assert.Null(options.ModeOverride);
        Assert.Contains("Unknown mode", options.Error);
    }

    [Fact]
    public void HelpAndVersionFlagsAreIndependentOfCommands()
    {
        var help = CliOptions.Parse(["--help"]);
        var version = CliOptions.Parse(["--version", "--freeze"]);

        Assert.True(help.Help);
        Assert.True(version.Version);
        Assert.True(help.WantsConsole);
        Assert.True(version.WantsConsole);
    }

    [Fact]
    public void ConsoleIsNotWantedForAFreeze()
    {
        Assert.False(CliOptions.Parse(["--freeze"]).WantsConsole);
        Assert.True(CliOptions.Parse(["--status"]).WantsConsole);
    }

    [Theory]
    [InlineData("--panic", "panic")]
    [InlineData("--lock-now", "lock")]
    [InlineData("--status", "status")]
    [InlineData("--exit", "exit")]
    [InlineData("--show", "show")]
    public void SimpleCommandsMapToInstanceRequests(string argument, string expected)
    {
        Assert.Equal(expected, CliOptions.Parse([argument]).ToCommand().Command);
    }
}
