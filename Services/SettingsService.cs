using System.IO;
using System.Text;
using System.Text.Json;
using FrostLock.Models;

namespace FrostLock.Services;

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrostLock");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly string BackupPath = Path.Combine(Dir, "settings.json.bak");
    private static readonly string TempPath = Path.Combine(Dir, "settings.json.tmp");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;

                var json = File.ReadAllText(path);
                if (TryParse(json, out var loaded, out _))
                    return loaded;
            }
            catch
            {
            }
        }

        return new AppSettings();
    }

    /// <summary>Writes settings through a temporary file and keeps a backup.</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);

            var json = Serialize(settings);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                try
                {
                    File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);
                }
                catch
                {
                    // Some filesystems do not support Replace; fall back to a copy.
                    File.Copy(TempPath, FilePath, overwrite: true);
                    try { File.Delete(TempPath); } catch { /* ignore */ }
                }
            }
            else
            {
                File.Move(TempPath, FilePath);
            }
        }
        catch
        {
            // Ignore persistence failures; the session keeps working with in-memory values.
        }
    }

    /// <summary>
    /// Serializes settings, optionally omitting PIN and emergency-phrase data.
    /// </summary>
    public static string Serialize(AppSettings settings, bool includeSecrets = true)
    {
        var copy = settings.Clone();

        if (includeSecrets)
        {
            copy.PinHash = PinService.Protect(copy.PinHash);
            copy.PanicHash = PinService.Protect(copy.PanicHash);
        }
        else
        {
            copy.ClearPin();
            copy.ClearPanicPhrase();
        }

        return JsonSerializer.Serialize(copy, Options);
    }

    /// <summary>
    /// Parses and normalizes JSON settings from disk, the editor, or an import file.
    /// </summary>
    public static bool TryParse(string json, out AppSettings settings, out string? error)
    {
        settings = new AppSettings();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The settings file is empty.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (parsed is null)
            {
                error = "The file did not contain a settings object.";
                return false;
            }

            Normalize(parsed);
            settings = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" at line {line + 1}" : string.Empty;
            error = $"Invalid JSON{where}: {ex.Message}";
            return false;
        }
    }

    private static void Normalize(AppSettings s)
    {
        // Version 1 always stored the 30-minute safety ceiling, and it was applied even to
        // "until I unlock" freezes. Version 2 makes the ceiling opt-in, so an old file's
        // implicit 30 minutes becomes a genuinely indefinite freeze.
        if (s.SettingsVersion < 2)
        {
            s.SafetyMaxSeconds = 0;
            s.SettingsVersion = 2;
        }

        if (s.DurationSeconds < 0) s.DurationSeconds = 0;
        if (s.ArmSeconds < 0) s.ArmSeconds = 0;
        if (s.SafetyMaxSeconds < 0) s.SafetyMaxSeconds = 0;
        if (s.FreezeCount < 0) s.FreezeCount = 0;
        if (s.TotalFrozenSeconds < 0) s.TotalFrozenSeconds = 0;
        if (s.PinFailures < 0) s.PinFailures = 0;
        if (s.PanicFailures < 0) s.PanicFailures = 0;
        if (s.UnlockHoldMs < 0) s.UnlockHoldMs = 0;
        if (s.UnlockHoldMs > 5000) s.UnlockHoldMs = 5000;

        s.PinLockoutLevel = Math.Clamp(s.PinLockoutLevel, 0, PinThrottle.MaxLockoutLevel);
        s.PanicLockoutLevel = Math.Clamp(s.PanicLockoutLevel, 0, PinThrottle.MaxLockoutLevel);

        s.PinLockedUntilUtc = NormalizeLockout(s.PinLockedUntilUtc, s.PinLockoutLevel);
        s.PanicLockedUntilUtc = NormalizeLockout(s.PanicLockedUntilUtc, s.PanicLockoutLevel);

        s.LockScreenMessage ??= string.Empty;
        s.LockScreenBackgroundPath = (s.LockScreenBackgroundPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s.UnlockHotkey)) s.UnlockHotkey = "Ctrl+Alt+U";
        if (string.IsNullOrWhiteSpace(s.StartHotkey)) s.StartHotkey = "Ctrl+Alt+L";

        (s.PinSalt, s.PinHash) = NormalizeSecret(s.PinSalt, s.PinHash);
        (s.PanicSalt, s.PanicHash) = NormalizeSecret(s.PanicSalt, s.PanicHash);

        s.SettingsVersion = 5;
    }

    private static DateTime? NormalizeLockout(DateTime? lockedUntil, int level)
    {
        if (lockedUntil is not { } value) return null;
        if (value <= DateTime.UtcNow) return null;

        var max = DateTime.UtcNow + PinThrottle.CooldownFor(level);
        return value > max ? max : value;
    }

    /// <summary>
    /// Drops a secret hash that cannot be unwrapped on this Windows account.
    /// </summary>
    private static (string? Salt, string? Hash) NormalizeSecret(string? salt, string? hash)
    {
        if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(hash))
            return (null, null);

        return PinService.Unprotect(hash) is null ? (null, null) : (salt, hash);
    }

    public static string SettingsDirectory => Dir;

    public static string SettingsFilePath => FilePath;
}
