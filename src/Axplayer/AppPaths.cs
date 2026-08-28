namespace Axplayer;

/// <summary>
/// Central definition of where Axplayer stores its data. Everything lives
/// under a single "data" directory next to the executable (or wherever the
/// user points --data-dir), which keeps the app portable and easy to back up.
/// </summary>
public static class AppPaths
{
    /// <summary>Root data directory (created on first use).</summary>
    public static string DataDir { get; private set; } = "";

    public static string StationsFile => Path.Combine(DataDir, "stations.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string RecordingsDir => Path.Combine(DataDir, "recordings");
    public static string BufferDir => Path.Combine(DataDir, "buffer");
    public static string FavoritesExportFile => Path.Combine(DataDir, "favorites.json");

    /// <summary>Resolve and create the data directory. Must be called before anything else touches disk.</summary>
    public static void Initialize(string? overrideDir)
    {
        DataDir = overrideDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RecordingsDir);
        Directory.CreateDirectory(BufferDir);
    }
}
