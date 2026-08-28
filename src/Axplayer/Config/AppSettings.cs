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

    /// <summary>
    /// Minimum seconds of audio to accumulate in the buffer file before playback
    /// starts when a station begins (0 = start as soon as a minimum chunk lands).
    /// </summary>
    public int StartupBufferSeconds { get; set; } = 2;

    /// <summary>
    /// When true, the stream is downloaded to a local buffer file and playback
    /// runs from that file. Network blips then never interrupt playback (the file
    /// just stops growing and resumes), and reconnects are gap-free.
    /// </summary>
    public bool BufferModeEnabled { get; set; } = true;

    /// <summary>
    /// Minutes to keep retrying a lost feed before the station is declared dead
    /// (0 = give up on the first drop). Only applies in buffer mode.
    /// </summary>
    public int DropoutCoverMinutes { get; set; } = 5;

    /// <summary>
    /// Seconds with no incoming audio data before the feed is considered dropped
    /// (only applies in buffer mode).
    /// </summary>
    public int DropoutDetectSeconds { get; set; } = 2;

    /// <summary>Seconds of silence/stall before a stream is considered dead.</summary>
    public int DeadStreamTimeoutSeconds { get; set; } = 10;

    /// <summary>How many reconnect attempts before queue mode advances to the next station.</summary>
    public int MaxReconnectAttempts { get; set; } = 2;

    /// <summary>
    /// Queue mode: when a station fails to connect (after reconnect attempts are
    /// exhausted), automatically play the next station in the current list and
    /// keep trying until one connects or every station has been tried.
    /// </summary>
    public bool PlayQueueMode { get; set; } = true;

    /// <summary>Automatically record every station that starts playing.</summary>
    public bool AutoRecord { get; set; } = false;

    /// <summary>Maximum duration of each recording file; longer sessions continue in a new file.</summary>
    public int RecordingSegmentMinutes { get; set; } = 5;

    /// <summary>Whether the spectrum visualizer panel is rendered.</summary>
    public bool ShowVisualizer { get; set; } = true;

    /// <summary>Maximum number of spectrum bars (auto-shrinks to fit the terminal width).</summary>
    public int VisualizerBars { get; set; } = 48;

    /// <summary>Visualizer amplitude sensitivity 0-100.</summary>
    public int VisualizerSensitivity { get; set; } = 60;
}
