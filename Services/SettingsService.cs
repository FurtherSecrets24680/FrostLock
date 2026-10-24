using System.IO;
using System.Text.Json;
using FrostLock.Models;

namespace FrostLock.Services;

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrostLock");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt settings file must never stop the app from starting.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Ignore persistence failures; the session keeps working with in-memory values.
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
        if (s.SafetyMaxSeconds < 0) s.SafetyMaxSeconds = 0;
        if (string.IsNullOrWhiteSpace(s.UnlockHotkey)) s.UnlockHotkey = "Ctrl+Alt+U";
        if (string.IsNullOrWhiteSpace(s.StartHotkey)) s.StartHotkey = "Ctrl+Alt+L";

        if (string.IsNullOrWhiteSpace(s.PinSalt) || string.IsNullOrWhiteSpace(s.PinHash))
        {
            s.PinSalt = null;
            s.PinHash = null;
        }
    }

    public static string SettingsDirectory => Dir;
}