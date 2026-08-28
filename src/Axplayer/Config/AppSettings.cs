namespace Axplayer.Config;

/// <summary>
/// User-configurable application settings, persisted as JSON in settings.json.
/// All properties have sane defaults so a missing or corrupt file never blocks startup.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Volume applied when a station starts playing (0-100).</summary>
    public int DefaultVolume { get; set; } = 70;

    /// <summary>URL of the station that was playing when the app last exited.</summary>
    public string? LastStationUrl { get; set; }

    /// <summary>When true, automatically resume the last station on startup.</summary>
    public bool AutoPlayLastStation { get; set; } = true;

    /// <summary>UI color theme: "dark" or "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>VLC network cache in milliseconds (higher = more resilience to jitter).</summary>
    public int NetworkCachingMs { get; set; } = 1500;

    /// <summary>Seconds of silence/stall before a stream is considered dead.</summary>
    public int DeadStreamTimeoutSeconds { get; set; } = 10;

    /// <summary>How many reconnect attempts before auto-skipping to the next station.</summary>
    public int MaxReconnectAttempts { get; set; } = 2;

    /// <summary>Auto-skip to the next favorite when a stream dies and reconnects are exhausted.</summary>
    public bool AutoSkipOnDeadStream { get; set; } = true;

    /// <summary>Whether the spectrum visualizer panel is rendered.</summary>
    public bool ShowVisualizer { get; set; } = true;

    /// <summary>Maximum number of spectrum bars (auto-shrinks to fit the terminal width).</summary>
    public int VisualizerBars { get; set; } = 48;

    /// <summary>Visualizer amplitude sensitivity 0-100.</summary>
    public int VisualizerSensitivity { get; set; } = 60;
}
