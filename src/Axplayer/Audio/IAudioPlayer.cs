namespace Axplayer.Audio;

/// <summary>High-level playback states surfaced to the UI.</summary>
public enum PlaybackState
{
    Stopped,
    Buffering,
    Playing,
    Paused,
    Error,
}

/// <summary>
/// Abstraction over the actual audio backend so the UI never depends on
/// LibVLC specifics. The only implementation is <see cref="LibVlcPlayer"/>.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Raised whenever the high-level state changes.</summary>
    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised with a human-readable message when the backend hits an error.</summary>
    event EventHandler<string>? ErrorOccurred;

    /// <summary>Raised with the stream time in ms whenever playback progresses (used for reconnection checks).</summary>
    event EventHandler<long>? TimeProgressed;

    PlaybackState State { get; }

    /// <summary>Volume 0-100.</summary>
    int Volume { get; set; }

    bool Muted { get; set; }

    /// <summary>Play a stream URL, replacing whatever was playing.</summary>
    void Play(string url);

    /// <summary>Force the reported playback state (buffer mode uses this to show "waiting for feed").</summary>
    void ReportState(PlaybackState state);

    void SetPause(bool paused);

    void Stop();

    /// <summary>Current stream position in milliseconds (advances while data flows).</summary>
    long TimeMs { get; }

    /// <summary>
    /// Best-effort "now playing" title reported by the backend itself
    /// (VLC parses ICY metadata). May be null/empty; the app prefers the
    /// dedicated ICY reader and falls back to this.
    /// </summary>
    string? BackendNowPlaying { get; }
}
