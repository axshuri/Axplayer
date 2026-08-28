using LibVLCSharp.Shared;

namespace Axplayer.Audio;

/// <summary>
/// LibVLC-backed audio player. LibVLC is used because it is by far the most
/// robust option for internet radio: it natively handles MP3/AAC/OGG streams,
/// ICY (Shoutcast) metadata, buffering, and network dropouts, and it works
/// headless from a console app with no window or WebView2 runtime.
/// </summary>
public sealed class LibVlcPlayer : IAudioPlayer
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _media;
    private PlaybackState _state = PlaybackState.Stopped;

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<long>? TimeProgressed;

    public LibVlcPlayer(int networkCachingMs)
    {
        var libVlcDir = LibVlcLocator.FindDirectory();
        if (libVlcDir is null)
            throw new InvalidOperationException(
                "libvlc native binaries were not found. Make sure the VideoLAN.LibVLC.Windows package is restored and the " +
                "libvlc.dll files are next to the executable (or use a normal folder publish).");

        Core.Initialize(libVlcDir);

        _libVlc = new LibVLC(
            "--no-video",                       // radio only
            $"--network-caching={networkCachingMs}",
            "--live-caching=1500",
            "--verbose=0");

        _mediaPlayer = new MediaPlayer(_libVlc);
        WireEvents();
    }

    private void WireEvents()
    {
        _mediaPlayer.Playing += (_, _) => SetState(PlaybackState.Playing);
        _mediaPlayer.Paused += (_, _) => SetState(PlaybackState.Paused);
        _mediaPlayer.Stopped += (_, _) => SetState(PlaybackState.Stopped);
        _mediaPlayer.EncounteredError += (_, _) =>
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, "Playback error (stream may be dead or unreachable).");
        };
        _mediaPlayer.EndReached += (_, _) => SetState(PlaybackState.Stopped);
        _mediaPlayer.TimeChanged += (_, e) => TimeProgressed?.Invoke(this, e.Time);
    }

    private void SetState(PlaybackState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, newState);
    }

    public PlaybackState State => _state;

    public int Volume
    {
        get => _mediaPlayer.Volume;
        set
        {
            try { _mediaPlayer.Volume = Math.Clamp(value, 0, 100); }
            catch (Exception ex) { Logger.Warn($"Volume set failed: {ex.Message}"); }
        }
    }

    public bool Muted
    {
        get => _mediaPlayer.Mute;
        set
        {
            try { _mediaPlayer.Mute = value; }
            catch (Exception ex) { Logger.Warn($"Mute set failed: {ex.Message}"); }
        }
    }

    public long TimeMs
    {
        get
        {
            try { return _mediaPlayer.Time; }
            catch { return 0; }
        }
    }

    public string? BackendNowPlaying
    {
        get
        {
            try
            {
                var media = _mediaPlayer.Media;
                if (media is null) return null;
                var meta = media.Meta(MetadataType.NowPlaying);
                return string.IsNullOrWhiteSpace(meta) ? null : meta;
            }
            catch { return null; }
        }
    }

    public void Play(string url)
    {
        try
        {
            StopInternal();

            _media = new Media(_libVlc, new Uri(url));
            _mediaPlayer.Play(_media);
            SetState(PlaybackState.Buffering);
            Logger.Info($"Playing {url}");
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, $"Could not start playback: {ex.Message}");
        }
    }

    public void SetPause(bool paused)
    {
        try
        {
            if (paused && _state == PlaybackState.Playing)
                _mediaPlayer.SetPause(true);
            else if (!paused && _state == PlaybackState.Paused)
                _mediaPlayer.SetPause(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Pause failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        StopInternal();
        SetState(PlaybackState.Stopped);
    }

    private void StopInternal()
    {
        try
        {
            _mediaPlayer.Stop();
            _media?.Dispose();
            _media = null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Stop failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            StopInternal();
            _mediaPlayer.Dispose();
            _libVlc.Dispose();
        }
        catch { /* best-effort cleanup on exit */ }
    }
}
