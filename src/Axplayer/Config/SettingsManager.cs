using System.Text.Json;

namespace Axplayer.Config;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to disk. Load failures are never fatal:
/// we fall back to defaults and let the next Save() repair the file.
/// </summary>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public AppSettings Settings { get; }

    public SettingsManager(string dataDir)
    {
        _path = AppPaths.SettingsFile;
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read settings file ({ex.Message}); using defaults.");
        }

        var fresh = new AppSettings();
        Save();
        return fresh;
    }

    /// <summary>Persist the current settings to disk.</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }
}
