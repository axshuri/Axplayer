using System.Diagnostics;
using Axplayer.Audio;
using Axplayer.Config;
using Axplayer.Data;
using Axplayer.Recording;
using Axplayer.UI;
using Spectre.Console;

namespace Axplayer;

/// <summary>
/// The application core: owns all state, wires the audio backend to the UI,
/// handles keyboard input, and runs the frame loop. Designed to be driven
/// from Program.cs with a parsed <see cref="AppOptions"/>.
/// </summary>
public sealed class App : IDisposable
{
    private const string AppVersion = "1.0";
    private const int FrameDelayMs = 33;

    // --- Dependencies -------------------------------------------------------
    private readonly SettingsManager _settingsManager;
    private readonly StationRepository _repo;
    private readonly FavoritesManager _favorites;
    private readonly string _catalogBaseUrl;
    private readonly IAudioPlayer _player;
    private readonly IcyMetadataReader _metadata;
    private readonly StreamRecorder _recorder;
    private readonly UiTheme _theme;
    private readonly Visualizer _visualizer;
    private readonly bool _noVisualizer;

    // --- UI state ------------------------------------------------------------
    private readonly List<Station> _filtered = [];
    private int _selected;
    private bool _favoritesView;
    private string _search = "";
    private bool _searching;
    private PlaybackState _playback = PlaybackState.Stopped;
    private Station? _currentStation;
    private string _songTitle = "";
    private string _bitrate = "";
    private int _bufferPct = -1;
    private string _status = "";
    private bool _statusIsError;
    private int _volume;
    private bool _muted;
    private bool _showInfo;
    private PromptSession? _prompt;
    private PendingPrompt _pending;
    private readonly Queue<string> _history = new();
    private DateTime? _sleepUntil;

    // --- Runtime / resilience -------------------------------------------------
    private readonly CancellationTokenSource _cts = new();
    private bool _running = true;
    private bool _refreshingCatalog;
    private int _reconnectAttempts;
    private Station? _pendingReconnect;
    private DateTime _reconnectAt;

    // --- Buffer mode (play through a local stream buffer) ----------------------
    private StreamBuffer? _buffer;
    private bool _bufferModeActive;
    private bool _bufferPlayStarted;
    private DateTime _bufferStartedAt;
    private bool _prevFeedAlive;
    private long _lastTimeMs;
    private DateTime _lastTimeProgress = DateTime.Now;
    private DateTime _lastBufferStatusAt = DateTime.MinValue;
    private DateTime _bufferStartedAtUtc;
    private bool _bufferFallbackAttempted;
    private bool _promptCursorOn;

    private enum PendingPrompt
    {
        None,
        AddName, AddUrl, AddGenre,
        EditName, EditUrl, EditGenre,
        Search,
        ExportPath, ImportPath,
        SleepMinutes,
        BufferSeconds,
        ConfirmDelete,
        ConfirmDeleteAll,
    }

    private Station? _editTarget;
    private string _addName = "";
    private string _addUrl = "";
    private string _addGenre = "";

    public App(AppOptions options)
    {
        _settingsManager = new SettingsManager(AppPaths.DataDir);
        _catalogBaseUrl = options.CatalogBaseUrl;
        _repo = new StationRepository(AppPaths.StationsFile, _catalogBaseUrl);
        _favorites = new FavoritesManager(_repo);
        _theme = UiTheme.For(_settingsManager.Settings.Theme);
        _player = new LibVlcPlayer(_settingsManager.Settings.NetworkCachingMs);
        _metadata = new IcyMetadataReader();
        _recorder = new StreamRecorder();
        _noVisualizer = options.NoVisualizer || !_settingsManager.Settings.ShowVisualizer;
        _visualizer = new Visualizer(_settingsManager.Settings.VisualizerBars, _settingsManager.Settings.VisualizerSensitivity);

        _volume = Math.Clamp(options.Volume ?? _settingsManager.Settings.DefaultVolume, 0, 100);
        _player.Volume = _volume;

        _player.StateChanged += OnPlayerStateChanged;
        _player.ErrorOccurred += OnPlayerError;
        _metadata.TitleChanged += OnTitleChanged;
        _metadata.StreamInfoReceived += OnStreamInfo;

        RebuildFilter();

        if (!string.IsNullOrEmpty(options.StationUrl))
        {
            var cliStation = new Station { Name = "CLI Station", Url = options.StationUrl, Genre = "CLI", DateAdded = DateTime.Now };
            _repo.Add(cliStation);
            _filtered.Clear();
            _filtered.Add(cliStation);
            _selected = 0;
            PlayStation(cliStation);
        }
        else if (_settingsManager.Settings.AutoPlayLastStation
                 && !string.IsNullOrEmpty(_settingsManager.Settings.LastStationUrl))
        {
            var last = _repo.FindByUrl(_settingsManager.Settings.LastStationUrl);
            if (last is not null)
            {
                _selected = Math.Max(0, _filtered.IndexOf(last));
                PlayStation(last);
            }
        }

        // First run: replace the fallback seed with the real categorized catalog
        // from fmstream.org (background fetch, non-blocking).
        if (_repo.IsFirstRun)
            RefreshCatalogNow();
    }

    /// <summary>The main loop: drain input, tick timers, update the visualizer, redraw.</summary>
    public void Run()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };
        Console.Clear();

        var lastFrame = Stopwatch.GetTimestamp();
        var lastTick = lastFrame;
        var lastMetaPoll = lastFrame;

        while (_running && !_cts.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - lastFrame) / (double)Stopwatch.Frequency;
            lastFrame = now;

            DrainInput();

            // ~2 Hz housekeeping (watchdog + sleep timer + buffer resume).
            if ((now - lastTick) / (double)Stopwatch.Frequency >= 0.5)
            {
                lastTick = now;
                WatchdogTick();
                BufferTick();
                CheckSleepTimer();
                ProcessPendingReconnect();
            }

            // ~every 3 s poll the backend for its own metadata (fallback).
            if ((now - lastMetaPoll) / (double)Stopwatch.Frequency >= 3.0)
            {
                lastMetaPoll = now;
                PollBackendMetadata();
            }

            _visualizer.Update(Math.Min(dt, 0.1), _playback == PlaybackState.Playing);
            _promptCursorOn = ((long)(now / (double)Stopwatch.Frequency * 2.0)) % 2 == 0;

            RenderFrame();
            Thread.Sleep(FrameDelayMs);
        }
    }

    // ------------------------------------------------------------------
    //  Input
    // ------------------------------------------------------------------

    private void DrainInput()
    {
        while (_running && Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);

            if (_prompt is not null)
            {
                if (_prompt.HandleKey(key))
                {
                    var finished = _prompt;
                    _prompt = null;
                    var completedKind = _pending;
                    HandlePromptResult(completedKind, finished);
                    // HandlePromptResult may advance a multi-step prompt (add/edit).
                    // Only clear the pending kind when no replacement prompt was started.
                    if (_prompt is null)
                        _pending = PendingPrompt.None;
                }
            }
            else
            {
                HandleKey(key);
            }
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q:
            case ConsoleKey.Escape:
                _running = false;
                break;

            case ConsoleKey.Spacebar:
            case ConsoleKey.P:
                TogglePlayPause();
                break;

            case ConsoleKey.S:
                StopPlayback();
                break;

            case ConsoleKey.OemPlus:
            case ConsoleKey.Add:
                ChangeVolume(5);
                break;

            case ConsoleKey.OemMinus:
            case ConsoleKey.Subtract:
                ChangeVolume(-5);
                break;

            case ConsoleKey.F:
                ToggleFavorite();
                break;

            case ConsoleKey.N:
                _showInfo = false;
                BeginPrompt(PendingPrompt.AddName, "Station name:", "");
                break;

            case ConsoleKey.D:
                ConfirmDelete();
                break;

            case ConsoleKey.E:
                BeginEdit();
                break;

            case ConsoleKey.Tab:
                ToggleView();
                break;

            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                break;

            case ConsoleKey.DownArrow:
                MoveSelection(1);
                break;

            case ConsoleKey.PageUp:
                MoveSelection(-5);
                break;

            case ConsoleKey.PageDown:
                MoveSelection(5);
                break;

            case ConsoleKey.Home:
                _selected = 0;
                break;

            case ConsoleKey.End:
                _selected = Math.Max(0, _filtered.Count - 1);
                break;

            case ConsoleKey.Enter:
                PlaySelected();
                break;

            case ConsoleKey.M:
                ToggleMute();
                break;

            case ConsoleKey.R:
                ToggleRecord();
                break;

            case ConsoleKey.G:
                ToggleAutoRecord();
                break;

            case ConsoleKey.I:
                _showInfo = !_showInfo;
                break;

            case ConsoleKey.T:
                BeginPrompt(PendingPrompt.SleepMinutes, "Sleep timer minutes (0=cancel):", "");
                break;

            case ConsoleKey.X:
                BeginPrompt(PendingPrompt.ExportPath, "Export favorites to:", AppPaths.FavoritesExportFile);
                break;

            case ConsoleKey.L:
                BeginPrompt(PendingPrompt.ImportPath, "Import favorites from:", "");
                break;

            case ConsoleKey.C:
                CycleTheme();
                break;

            case ConsoleKey.Oem2: // '/' (US layout)
                BeginPrompt(PendingPrompt.Search, "Search:", _search);
                break;

            case ConsoleKey.A:
                ToggleQueueMode();
                break;

            case ConsoleKey.J:
                SkipToNextManually();
                break;

            case ConsoleKey.B:
                BeginPrompt(PendingPrompt.BufferSeconds, "Startup buffer seconds (0=off):",
                    _settingsManager.Settings.StartupBufferSeconds.ToString());
                break;

            default:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    if (key.Key == ConsoleKey.E) BeginEdit();
                    else if (key.Key == ConsoleKey.R) RefreshCatalogNow();
                    else if (key.Key == ConsoleKey.D) ConfirmDeleteAll();
                }
                break;
        }
    }

    private void BeginPrompt(PendingPrompt kind, string prompt, string initial)
    {
        _pending = kind;
        _prompt = new PromptSession(prompt, initial);
        if (kind == PendingPrompt.Search) _searching = true;
    }

    private void HandlePromptResult(PendingPrompt kind, PromptSession session)
    {
        if (session.Cancelled)
        {
            _editTarget = null;
            _addName = "";
            _addUrl = "";
            _addGenre = "";
            if (kind == PendingPrompt.Search)
            {
                _searching = !string.IsNullOrEmpty(_search);
                RebuildFilter();
            }
            return;
        }

        var value = session.Result;

        switch (kind)
        {
            case PendingPrompt.AddName:
                if (value.Length == 0) { SetStatus("Add cancelled — name required.", true); break; }
                _addName = value;
                BeginPrompt(PendingPrompt.AddUrl, "Stream URL:", "");
                break;

            case PendingPrompt.AddUrl:
                if (!IsHttpUrl(value)) { SetStatus("Invalid URL — must start with http:// or https://", true); break; }
                _addUrl = value;
                BeginPrompt(PendingPrompt.AddGenre, "Genre (optional):", "");
                break;

            case PendingPrompt.AddGenre:
                _addGenre = value;
                SetStatus("Validating stream URL…");
                _ = ValidateAndAddAsync(_addName, _addUrl, _addGenre);
                break;

            case PendingPrompt.EditName:
                if (_editTarget is null) break;
                if (value.Length == 0) { SetStatus("Edit cancelled — name required.", true); break; }
                _editTarget.Name = value;
                BeginPrompt(PendingPrompt.EditUrl, "Stream URL:", _editTarget.Url);
                break;

            case PendingPrompt.EditUrl:
                if (_editTarget is null) break;
                if (!IsHttpUrl(value)) { SetStatus("Invalid URL — must start with http:// or https://", true); break; }
                if (value != _editTarget.Url)
                {
                    _editTarget.Url = value;
                    _repo.Update(_editTarget);
                    SetStatus("Validating updated URL…");
                    _ = ValidateEditAsync(_editTarget);
                }
                BeginPrompt(PendingPrompt.EditGenre, "Genre:", _editTarget.Genre);
                break;

            case PendingPrompt.EditGenre:
                if (_editTarget is null) break;
                _editTarget.Genre = value;
                _repo.Update(_editTarget);
                RebuildFilter();
                SetStatus($"Updated {_editTarget.Name} ✓");
                _editTarget = null;
                break;

            case PendingPrompt.Search:
                _search = value;
                _searching = value.Length > 0;
                RebuildFilter();
                break;

            case PendingPrompt.ExportPath:
                if (value.Length == 0) break;
                try
                {
                    int n = _favorites.Export(value);
                    SetStatus($"Exported {n} favorite(s) → {value}");
                }
                catch (Exception ex) { SetStatus($"Export failed: {ex.Message}", true); }
                break;

            case PendingPrompt.ImportPath:
                if (value.Length == 0) break;
                if (!File.Exists(value)) { SetStatus($"File not found: {value}", true); break; }
                try
                {
                    int added = _favorites.Import(value);
                    RebuildFilter();
                    SetStatus($"Imported {added} new station(s) from {value}");
                }
                catch (Exception ex) { SetStatus($"Import failed: {ex.Message}", true); }
                break;

            case PendingPrompt.SleepMinutes:
                if (int.TryParse(value, out int minutes) && minutes > 0)
                {
                    _sleepUntil = DateTime.Now.AddMinutes(minutes);
                    SetStatus($"Sleep timer: stop in {minutes} minute(s)");
                }
                else
                {
                    _sleepUntil = null;
                    SetStatus("Sleep timer cancelled");
                }
                break;

            case PendingPrompt.BufferSeconds:
                if (int.TryParse(value, out int sec) && sec is >= 0 and <= 120)
                {
                    _settingsManager.Settings.StartupBufferSeconds = sec;
                    _settingsManager.Save();
                    SetStatus(sec > 0
                        ? $"Startup buffer: {sec}s silent pre-roll when a station starts"
                        : "Startup buffer: off — stations start immediately");
                }
                else
                {
                    SetStatus("Invalid buffer — enter whole seconds between 0 and 120", true);
                }
                break;

            case PendingPrompt.ConfirmDelete:
                if (value.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    DeleteSelected();
                else
                    SetStatus("Delete cancelled");
                break;

            case PendingPrompt.ConfirmDeleteAll:
                if (value.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    DeleteAllStations();
                else
                    SetStatus("Delete all cancelled");
                break;
        }
    }

    // ------------------------------------------------------------------
    //  Playback control
    // ------------------------------------------------------------------

    private void PlaySelected()
    {
        if (_filtered.Count == 0) return;
        PlayStation(_filtered[_selected]);
    }

    /// <summary>Manually resume/skip to the next station in the active queue.</summary>
    private void SkipToNextManually()
    {
        if (!_settingsManager.Settings.PlayQueueMode)
        {
            SetStatus("Queue mode is off — press A to enable it", true);
            return;
        }
        if (_filtered.Count == 0)
        {
            SetStatus("Queue is empty", true);
            return;
        }

        SkipToNext(play: true);
        SetStatus($"Queue: trying {_currentStation?.Name}…");
    }

    private void PlayStation(Station station)
    {
        bool wasRecording = _recorder.IsRecording;
        _recorder.Stop();
        _pendingReconnect = null;
        _reconnectAttempts = 0;
        _currentStation = station;
        _songTitle = "";
        _bitrate = "";
        _bufferPct = -1;
        _lastTimeMs = 0;
        _lastTimeProgress = DateTime.Now;
        _showInfo = false;

        _repo.IncrementPlayCount(station);
        _settingsManager.Settings.LastStationUrl = station.Url;
        _settingsManager.Save();

        bool bufferMode = _settingsManager.Settings.BufferModeEnabled;
        _bufferModeActive = bufferMode;
        _bufferPlayStarted = false;
        _bufferStartedAt = DateTime.UtcNow;
        _bufferStartedAtUtc = DateTime.UtcNow;
        _lastBufferStatusAt = DateTime.MinValue;
        _bufferFallbackAttempted = false;
        _prevFeedAlive = false;

        if (bufferMode)
        {
            // Playback runs from a local buffer file: the stream is downloaded by
            // StreamBuffer and VLC reads the file, so network blips never produce
            // player errors and reconnects are gap-free.
            _buffer?.Stop();
            _buffer = new StreamBuffer();
            _buffer.Start(station.Url, AppPaths.BufferDir,
                _settingsManager.Settings.DropoutDetectSeconds,
                _settingsManager.Settings.DropoutCoverMinutes);
            _player.Stop();
            SetStatus($"Buffering {station.Name} — connecting…");
        }
        else
        {
            _player.Play(station.Url);
            SetStatus($"Tuning {station.Name} …");
        }

        _metadata.Start(station.Url);
        if (_settingsManager.Settings.AutoRecord || wasRecording)
        {
            bool useBufferedSource = _bufferModeActive && _buffer?.StreamUrl is not null;
            string recordingUrl = useBufferedSource ? _buffer!.StreamUrl! : station.Url;
            _recorder.Start(recordingUrl, AppPaths.RecordingsDir,
                _settingsManager.Settings.RecordingSegmentMinutes,
                localBufferedSource: useBufferedSource,
                sourceExtension: RecordingExtension(station.Url));
        }
        Logger.Info($"Playing station: {station.Name} ({station.Url})");
    }

    /// <summary>
    /// Drives buffer mode from the main loop (~2 Hz): waits for the initial fill,
    /// keeps the UI honest while the feed is down, and hands off to the queue/stop
    /// logic when the cover window is exhausted. VLC resumes on its own when the
    /// local stream starts flowing again, so there is no resume/seek machinery.
    /// </summary>
    private void BufferTick()
    {
        if (!_bufferModeActive || _buffer is not { IsActive: true }) return;

        // Never leave startup stuck indefinitely. If the feed does not produce
        // enough data within the startup grace period, fall back to direct VLC
        // playback for this station rather than waiting forever at one percent.
        if (!_bufferPlayStarted && !_bufferFallbackAttempted &&
            DateTime.UtcNow - _bufferStartedAtUtc >= BufferStartupTimeout())
        {
            DisableBufferForCurrentStation("Startup buffer timed out — buffer disabled for this station; trying direct playback…");
            return;
        }

        if (_buffer.IsDead)
        {
            HandleFeedDead();
            return;
        }

        if (!_bufferPlayStarted)
        {
            // Initial fill: hold off until the startup pre-roll has accumulated.
            // A stream can be delivered in chunks larger than the final poll,
            // so use the same threshold for both readiness and displayed progress.
            if (_buffer.FeedAlive && _buffer.FileLengthBytes >= InitialFillBytes())
                StartBufferPlayback();
            else if (_buffer.FeedAlive && DateTime.UtcNow - _lastBufferStatusAt >= TimeSpan.FromSeconds(1))
            {
                _lastBufferStatusAt = DateTime.UtcNow;
                long required = InitialFillBytes();
                long buffered = _buffer.FileLengthBytes;
                int remaining = StartupBufferRemaining();
                int percent = required <= 0 ? 100 : (int)Math.Clamp(buffered * 100L / required, 0, 99);
                SetStatus(remaining > 0
                    ? $"{_currentStation?.Name} — startup buffer: {percent}% ({remaining}s remaining)…"
                    : $"{_currentStation?.Name} — startup buffer: {percent}% (almost ready…)");
            }
            else if (!_buffer.FeedAlive && _prevFeedAlive)
                SetStatus($"{_currentStation?.Name} — connecting…", true);
            _prevFeedAlive = _buffer.FeedAlive;
            return;
        }

        // Feed down: VLC plays its buffered audio, then waits at the edge on its
        // own (the local stream never "ends"); we only keep the UI honest.
        if (!_buffer.FeedAlive)
        {
            if (_playback is not (PlaybackState.Playing or PlaybackState.Paused))
            {
                if (_playback != PlaybackState.Buffering)
                {
                    _player.ReportState(PlaybackState.Buffering);
                    SetStatus($"{_currentStation?.Name} — feed lost, waiting…", true);
                }
            }
            else if (_playback == PlaybackState.Playing && _prevFeedAlive)
            {
                SetStatus($"{_currentStation?.Name} — feed lost, playing from buffer…", true);
            }
        }
        // Feed alive: nothing to do — VLC resumes automatically when bytes flow.

        _prevFeedAlive = _buffer.FeedAlive;
    }

    private void StartBufferPlayback()
    {
        _bufferPlayStarted = true;
        _player.Play(_buffer!.StreamUrl!);
    }

    private TimeSpan BufferStartupTimeout()
    {
        int seconds = Math.Max(1, _settingsManager.Settings.StartupBufferSeconds);
        return TimeSpan.FromSeconds(Math.Max(15, seconds * 5));
    }

    private void DisableBufferForCurrentStation(string message)
    {
        if (_currentStation is null) return;

        _bufferFallbackAttempted = true;
        _bufferModeActive = false;
        _bufferPlayStarted = false;
        _buffer?.Stop();
        _player.Stop();
        SetStatus(message, true);
        _player.Play(_currentStation.Url);
        _metadata.Start(_currentStation.Url);
        Logger.Warn($"Buffer startup failed for {_currentStation.Name}; disabled for current playback.");
    }

    private void HandleFeedDead()
    {
        Logger.Warn($"Feed dead for {_currentStation?.Name} — cover window exhausted.");
        _bufferModeActive = false;
        _bufferPlayStarted = false;
        if (_settingsManager.Settings.PlayQueueMode)
        {
            SetStatus($"{_currentStation?.Name} is offline — trying next…", true);
            SkipToNext(play: true);
        }
        else
        {
            _buffer?.Stop();
            _player.Stop();
            SetStatus($"{_currentStation?.Name} is offline — feed lost. Select another station or press S.", true);
        }
    }

    /// <summary>Bytes that must accumulate before playback starts (startup pre-roll).</summary>
    private long InitialFillBytes() =>
        Math.Max(8 * 1024, _settingsManager.Settings.StartupBufferSeconds * 16 * 1024L);

    private int StartupBufferRemaining()
    {
        int total = Math.Max(0, _settingsManager.Settings.StartupBufferSeconds);
        return Math.Max(0, total - (int)Math.Floor((DateTime.UtcNow - _bufferStartedAt).TotalSeconds));
    }

    private void TogglePlayPause()
    {
        if (_filtered.Count == 0) return;
        var target = _filtered[_selected];

        if (_playback == PlaybackState.Playing && ReferenceEquals(_currentStation, target))
            _player.SetPause(true);
        else if (_playback == PlaybackState.Paused && ReferenceEquals(_currentStation, target))
            _player.SetPause(false);
        else
            PlaySelected();
    }

    private void StopPlayback()
    {
        _player.Stop();
        _metadata.Stop();
        _recorder.Stop();
        _pendingReconnect = null;
        _buffer?.Stop();
        _bufferModeActive = false;
        _bufferPlayStarted = false;
        _songTitle = "";
        _bitrate = "";
        _bufferPct = -1;
        SetStatus("Stopped");
    }

    /// <summary>Toggle queue mode: on failure, auto-play the next station in the list.</summary>
    private void ToggleQueueMode()
    {
        _settingsManager.Settings.PlayQueueMode = !_settingsManager.Settings.PlayQueueMode;
        _settingsManager.Save();
        SetStatus(_settingsManager.Settings.PlayQueueMode
            ? "Queue mode ON — will autoplay the next station if this one fails to connect"
            : "Queue mode OFF — failed stations stay stopped");
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        _player.Muted = _muted;
        SetStatus(_muted ? "Muted (M to unmute)" : $"Volume {_volume}%");
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");

    private void ChangeVolume(int delta)
    {
        _volume = Math.Clamp(_volume + delta, 0, 100);
        _player.Volume = _volume;
        if (_volume > 0 && _muted)
        {
            _muted = false;
            _player.Muted = false;
        }
        _settingsManager.Settings.DefaultVolume = _volume;
        _settingsManager.Save();
        SetStatus($"Volume {_volume}%");
    }

    private static string RecordingExtension(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".ogg") || lower.Contains(".opus")) return ".ogg";
        if (lower.Contains(".aac")) return ".aac";
        if (lower.Contains(".flac")) return ".flac";
        return ".mp3";
    }

    private void ToggleAutoRecord()
    {
        _settingsManager.Settings.AutoRecord = !_settingsManager.Settings.AutoRecord;
        _settingsManager.Save();
        SetStatus(_settingsManager.Settings.AutoRecord
            ? "Auto-record ON — every station starts a rolling recording"
            : "Auto-record OFF");
    }

    private void ToggleRecord()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            SetStatus("Recording stopped");
            return;
        }
        if (_currentStation is null || _playback == PlaybackState.Stopped)
        {
            SetStatus("Nothing playing to record", true);
            return;
        }
        bool useBufferedSource = _bufferModeActive && _buffer?.StreamUrl is not null;
        string recordingUrl = useBufferedSource ? _buffer!.StreamUrl! : _currentStation.Url;
        _recorder.Start(recordingUrl, AppPaths.RecordingsDir,
            _settingsManager.Settings.RecordingSegmentMinutes,
            localBufferedSource: useBufferedSource);
        SetStatus($"Recording {_currentStation.Name} → {_recorder.SessionDirectory}");
    }

    // ------------------------------------------------------------------
    //  Stations / favorites
    // ------------------------------------------------------------------

    private void RebuildFilter()
    {
        var source = _favoritesView ? _repo.GetFavorites() : _repo.All;
        _filtered.Clear();
        if (string.IsNullOrWhiteSpace(_search))
        {
            _filtered.AddRange(source);
        }
        else
        {
            _filtered.AddRange(source.Where(s =>
                s.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                s.Genre.Contains(_search, StringComparison.OrdinalIgnoreCase)));
        }
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _filtered.Count - 1));
    }

    private void ToggleView()
    {
        _favoritesView = !_favoritesView;
        RebuildFilter();
        SetStatus(_favoritesView ? "Favorites view" : "All stations view");
    }

    private void MoveSelection(int delta)
    {
        if (_filtered.Count == 0) return;
        _selected = Math.Clamp(_selected + delta, 0, _filtered.Count - 1);
    }

    private void ToggleFavorite()
    {
        if (_filtered.Count == 0) return;
        var station = _filtered[_selected];
        bool nowFavorite = _favorites.ToggleFavorite(station);
        SetStatus(nowFavorite ? $"* {station.Name} added to Favorites" : $"{station.Name} removed from Favorites");
        if (_favoritesView && !nowFavorite)
        {
            RebuildFilter();
            _selected = Math.Clamp(_selected, 0, Math.Max(0, _filtered.Count - 1));
        }
    }

    private void DeleteSelected()
    {
        if (_filtered.Count == 0) return;
        var station = _filtered[_selected];
        _repo.Remove(station);
        if (ReferenceEquals(_currentStation, station))
            StopPlayback();
        RebuildFilter();
        SetStatus($"Deleted {station.Name}");
    }

    private void ConfirmDelete()
    {
        if (_filtered.Count == 0) return;
        var station = _filtered[_selected];
        BeginPrompt(PendingPrompt.ConfirmDelete, $"Delete '{station.Name}'? (y/N):", "");
    }

    private void ConfirmDeleteAll()
    {
        if (_repo.All.Count == 0)
        {
            SetStatus("No stations to delete");
            return;
        }
        BeginPrompt(PendingPrompt.ConfirmDeleteAll,
            $"Delete ALL {_repo.All.Count} stations? This cannot be undone! (y/N):", "");
    }

    private void DeleteAllStations()
    {
        bool wasPlaying = _playback != PlaybackState.Stopped;
        int n = _repo.DeleteAll();
        if (wasPlaying) StopPlayback();
        _filtered.Clear();
        _selected = 0;
        SetStatus($"Deleted all {n} stations.");
        Logger.Info($"Deleted all {n} stations.");
    }

    private void BeginEdit()
    {
        if (_filtered.Count == 0) return;
        _editTarget = _filtered[_selected];
        BeginPrompt(PendingPrompt.EditName, "Name:", _editTarget.Name);
    }

    /// <summary>
    /// Kick off a background refresh of the fmstream.org catalog. The list is
    /// rebuilt in place when the fetch completes, so favorites/play-counts are
    /// preserved (they are keyed by URL).
    /// </summary>
    private void RefreshCatalogNow()
    {
        if (_refreshingCatalog)
        {
            SetStatus("Catalog refresh already in progress…");
            return;
        }

        _refreshingCatalog = true;
        SetStatus("Refreshing catalog from fmstream.org…");
        Logger.Info("Catalog refresh requested.");

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromMinutes(6));
                var stations = await _repo.RefreshCatalogAsync(cts.Token, cat =>
                {
                    // Live progress on the status line while fetching each category.
                    _status = $"Fetching category: {cat}…";
                    _statusIsError = false;
                });
                int count = stations.Count;
                RebuildFilter();
                if (count > 0)
                    SetStatus($"Catalog refreshed: {count} stations across {_repo.GroupByGenre().Count} categories");
                else
                    SetStatus("Catalog refresh returned nothing; keeping existing list", true);
            }
            catch (Exception ex)
            {
                Logger.Error($"Catalog refresh failed: {ex.Message}");
                SetStatus($"Catalog refresh failed: {ex.Message}", true);
            }
            finally
            {
                _refreshingCatalog = false;
            }
        });
    }

    private void CycleTheme()
    {
        _settingsManager.Settings.Theme = _settingsManager.Settings.Theme == "light" ? "dark" : "light";
        _settingsManager.Save();
        SetStatus($"Theme: {_settingsManager.Settings.Theme}");
    }

    private async Task ValidateAndAddAsync(string name, string url, string genre)
    {
        var info = await StreamProbe.ProbeAsync(url, _cts.Token);
        if (!info.Ok)
        {
            SetStatus($"Cannot add station: {info.Error}", true);
            return;
        }

        var station = new Station
        {
            Name = name,
            Url = url,
            Genre = string.IsNullOrWhiteSpace(genre) ? (info.Genre ?? "") : genre,
            DateAdded = DateTime.Now,
            IsFavorite = true,
        };
        _repo.Add(station);
        _search = "";
        _searching = false;
        RebuildFilter();
        _selected = Math.Max(0, _filtered.IndexOf(station));
        SetStatus($"Added {station.Name} ✓  (press Enter to play)");
        Logger.Info($"Added station {station.Name} ({url})");
    }

    private async Task ValidateEditAsync(Station station)
    {
        var info = await StreamProbe.ProbeAsync(station.Url, _cts.Token);
        if (!info.Ok)
            SetStatus($"Warning: updated URL looks unreachable ({info.Error})", true);
        else
            SetStatus($"URL validated ✓");
    }

    // ------------------------------------------------------------------
    //  Metadata & playback events
    // ------------------------------------------------------------------

    private void OnPlayerStateChanged(object? sender, PlaybackState state)
    {
        _playback = state;
        switch (state)
        {
            case PlaybackState.Buffering:
                _bufferPct = 0;
                _lastTimeProgress = DateTime.Now;
                break;

            case PlaybackState.Playing:
                _bufferPct = 100;
                _lastTimeProgress = DateTime.Now;
                SetStatus($"Now playing: {_currentStation?.Name}");
                break;

            case PlaybackState.Paused:
                SetStatus("Paused");
                break;

            case PlaybackState.Error:
                ScheduleReconnect();
                break;
        }
    }

    private void OnPlayerError(object? sender, string message)
    {
        SetStatus(message, true);
        Logger.Warn($"Player error: {message}");
    }

    private void OnTitleChanged(string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title == _songTitle) return;
        _songTitle = title;
        PushHistory(title);
        SetStatus(title);
    }

    private void OnStreamInfo(string? name, string? genre, string? bitrate)
    {
        if (!string.IsNullOrWhiteSpace(bitrate)) _bitrate = bitrate;

        if (!string.IsNullOrWhiteSpace(name) && _currentStation is { } station &&
            (string.IsNullOrWhiteSpace(station.Name) || station.Name == "CLI Station"))
        {
            station.Name = name;
            _repo.Update(station);
        }

        if (!string.IsNullOrWhiteSpace(genre) && _currentStation is { } st2 && string.IsNullOrWhiteSpace(st2.Genre))
        {
            st2.Genre = genre;
            _repo.Update(st2);
        }
    }

    private void PushHistory(string title)
    {
        if (_history.Count > 0 && _history.Peek() == title) return;
        _history.Enqueue(title);
        while (_history.Count > 10) _history.Dequeue();
    }

    private void PollBackendMetadata()
    {
        if (_playback != PlaybackState.Playing) return;
        if (_metadata.HasIcyMetadata) return; // ICY reader is authoritative
        var meta = _player.BackendNowPlaying;
        if (!string.IsNullOrWhiteSpace(meta)) OnTitleChanged(meta);
    }

    // ------------------------------------------------------------------
    //  Resilience: watchdog, reconnects, queue mode
    // ------------------------------------------------------------------

    private void WatchdogTick()
    {
        if (_bufferModeActive) return; // buffer mode has its own resilience
        if (_currentStation is null) return;
        if (_playback is not (PlaybackState.Playing or PlaybackState.Buffering)) return;

        long time = _player.TimeMs;
        if (time != _lastTimeMs)
        {
            _lastTimeMs = time;
            _lastTimeProgress = DateTime.Now;
            return;
        }

        var stalledFor = DateTime.Now - _lastTimeProgress;
        if (stalledFor.TotalSeconds >= _settingsManager.Settings.DeadStreamTimeoutSeconds)
        {
            Logger.Warn($"Stream stalled for {stalledFor.TotalSeconds:0}s — scheduling reconnect.");
            ScheduleReconnect();
        }
    }

    /// <summary>Back off and retry; after MaxReconnectAttempts, advance the queue or give up.</summary>
    private void ScheduleReconnect()
    {
        if (_currentStation is null || _pendingReconnect is not null) return;

        _reconnectAttempts++;
        int max = _settingsManager.Settings.MaxReconnectAttempts;

        if (_reconnectAttempts <= max)
        {
            int delaySec = Math.Min(8, 1 << _reconnectAttempts);
            _pendingReconnect = _currentStation;
            _reconnectAt = DateTime.Now.AddSeconds(delaySec);
            _player.Stop();
            _metadata.Stop();
            SetStatus($"Stream error — retrying in {delaySec}s (attempt {_reconnectAttempts}/{max})", true);
        }
        else
        {
            _player.Stop();
            _metadata.Stop();
            Logger.Warn($"Giving up on {_currentStation.Name} after {_reconnectAttempts} attempts.");

            if (_settingsManager.Settings.PlayQueueMode)
                AdvanceQueue();
            else
                SetStatus($"{_currentStation.Name} is offline. Select another station or press S.", true);
        }
    }

    /// <summary>
    /// Queue mode: play the next station in the current list, cycling forever
    /// until one connects. The counter shown is the next station's position in
    /// the list, so it wraps instead of growing unbounded.
    /// </summary>
    private void AdvanceQueue()
    {
        if (_filtered.Count == 0)
        {
            StopPlayback();
            return;
        }

        int idx = _currentStation is null ? -1 : _filtered.IndexOf(_currentStation);
        int next = (idx + 1) % _filtered.Count;
        SetStatus($"{_currentStation?.Name} is offline — trying next ({next + 1}/{_filtered.Count})…", true);
        SkipToNext(play: true);
    }

    private void ProcessPendingReconnect()
    {
        if (_pendingReconnect is null) return;
        if (DateTime.Now < _reconnectAt) return;

        var station = _pendingReconnect;
        _pendingReconnect = null;

        if (ReferenceEquals(station, _currentStation))
            PlayStation(station); // resets attempt counter and restarts playback
    }

    private void SkipToNext(bool play)
    {
        if (_filtered.Count == 0) { StopPlayback(); return; }

        int idx = _currentStation is null ? -1 : _filtered.IndexOf(_currentStation);
        _selected = (idx + 1) % _filtered.Count;
        if (play) PlaySelected();
    }

    private void CheckSleepTimer()
    {
        if (_sleepUntil is null) return;
        if (DateTime.Now >= _sleepUntil.Value)
        {
            _sleepUntil = null;
            _player.Stop();
            _metadata.Stop();
            _recorder.Stop();
            SetStatus("Sleep timer fired — stopped.");
            Logger.Info("Sleep timer fired.");
        }
    }

    // ------------------------------------------------------------------
    //  Rendering
    // ------------------------------------------------------------------

    private void RenderFrame()
    {
        try
        {
            // Hide the cursor while drawing (restored on exit).
            Console.CursorVisible = false;
        }
        catch { /* redirected console */ }

        int w, h;
        try { w = Console.WindowWidth; h = Console.WindowHeight; }
        catch { w = 120; h = 30; }

        int playingIndex = _currentStation is null ? -1 : _filtered.IndexOf(_currentStation);
        int? queuePosition = playingIndex >= 0 ? playingIndex + 1 : null;
        var (connLabel, connColor) = Connection();

        var snapshot = new UiSnapshot
        {
            Version = AppVersion,
            Now = DateTime.Now,
            ConnectionLabel = connLabel,
            ConnectionColor = connColor,
            FavoritesView = _favoritesView,
            Searching = _searching,
            Stations = _filtered,
            Selected = _selected,
            PlayingIndex = playingIndex,
            State = _playback,
            StationName = _currentStation?.Name ?? "",
            SongTitle = _songTitle,
            Volume = _muted ? 0 : _volume,
            Muted = _muted,
            Bitrate = _bitrate,
            BufferPct = _bufferPct,
            Recording = _recorder.IsRecording,
            RecFile = _recorder.FilePath,
            AutoRecord = _settingsManager.Settings.AutoRecord,
            Status = _status,
            StatusIsError = _statusIsError,
            QueueMode = _settingsManager.Settings.PlayQueueMode,
            QueuePosition = queuePosition,
            QueueCount = _filtered.Count,
            Prompt = _prompt,
            PromptCursorOn = _promptCursorOn,
            ShowInfo = _showInfo,
            InfoLines = _showInfo ? BuildInfoLines() : [],
            SleepMinutesLeft = _sleepUntil is { } d ? (int)Math.Ceiling((d - DateTime.Now).TotalMinutes) : null,
            Theme = _theme,
            Visualizer = _visualizer,
            ShowVisualizer = !_noVisualizer,
            Width = w,
            Height = h,
        };

        var frame = MainLayout.Render(snapshot);
        try
        {
            // Redraw IN PLACE instead of relying on ANSI clearing (\x1b[2J) which some
            // terminals/pipelines ignore — that would append frames forever. We home the
            // cursor natively and overwrite each row, padding leftover cells with spaces.
            Console.SetCursorPosition(0, 0);
            WriteFrameInPlace(frame, w);
            Console.Out.Flush();
        }
        catch { /* console gone (e.g. closed window) */ }
    }

    /// <summary>
    /// Write a fully-rendered frame at the current cursor position, row by row,
    /// without emitting any newline. Each row is padded to the console width so
    /// leftover cells from an earlier, longer frame are overwritten. Relying on
    /// \x1b[2J/hide or WriteLine scrolling is what caused frames to APPEND.
    /// </summary>
    private static void WriteFrameInPlace(string frame, int consoleWidth)
    {
        int row = 0;
        foreach (var rawLine in frame.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                // Erase any stale content on rows that are now blank.
                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', consoleWidth));
                row++;
                continue;
            }

            Console.SetCursorPosition(0, row);
            Console.Write(line);

            // Pad the remainder of the row with spaces so stale text is erased.
            int visible = VisibleWidth(line);
            int pad = consoleWidth - visible;
            if (pad > 0) Console.Write(new string(' ', pad));

            row++;
        }
    }

    /// <summary>Approximate visible width of a line that may contain ANSI escape codes.</summary>
    private static int VisibleWidth(string line)
    {
        // 27 = ESC, [0..;]*: parameter bytes, m = final byte (SGR).
        int width = 0, i = 0;
        while (i < line.Length)
        {
            if (line[i] == (char)27)
            {
                // Skip an ESC[...] sequence.
                if (i + 1 < line.Length && line[i + 1] == '[')
                {
                    i += 2;
                    while (i < line.Length && line[i] != 'm' && line[i] != 'H') i++;
                    i++; // skip the final byte
                    continue;
                }
            }
            width++;
            i++;
        }
        return width;
    }

    private (string Label, string Color) Connection() => _playback switch
    {
        PlaybackState.Playing => ("PLAYING", _theme.Playing),
        PlaybackState.Paused => ("PAUSED", _theme.Paused),
        PlaybackState.Buffering => ("BUFFERING", "yellow"),
        PlaybackState.Error => ("ERROR", _theme.Err),
        _ => _currentStation is null ? ("IDLE", "grey") : ("STOPPED", "grey"),
    };

    private List<string> BuildInfoLines()
    {
        var lines = new List<string>();
        if (_currentStation is null)
        {
            lines.Add($"[{_theme.Dim}]Nothing playing.[/]");
            return lines;
        }
        var s = _currentStation;
        var t = _theme;
        lines.Add($"[bold {t.Accent}]Station info — {Markup.Escape(s.Name)}[/]");
        lines.Add($"  URL:      [{t.Dim}]{Markup.Escape(s.Url)}[/]");
        lines.Add($"  Genre:    {Markup.Escape(s.Genre.PadRight(20))}  Favorites: {(s.IsFavorite ? "[red]* yes[/]" : "[grey]no[/]")}");
        lines.Add($"  Bitrate:  {(string.IsNullOrWhiteSpace(_bitrate) ? "[grey]--[/]" : _bitrate)}   Plays: {s.PlayCount}");
        int prerollSec = _settingsManager.Settings.StartupBufferSeconds;
        string preroll = prerollSec > 0
            ? $"[{t.Accent}]{prerollSec}s initial buffer[/]"
            : "[grey]off[/]";
        lines.Add($"  Pre-roll: {preroll}");
        if (_bufferModeActive && _buffer is { IsActive: true })
        {
            string feed = _buffer.IsDead ? $"[{t.Err}]lost[/]"
                : _buffer.FeedAlive ? $"[{t.Ok}]live[/]"
                : $"[{t.Warn}]reconnecting…[/]";
            lines.Add($"  Feed:     {feed}   buffered: {_buffer.FileLengthBytes / 1024} KB");
        }
        lines.Add($"  State:    [{Connection().Color}]{Connection().Label}[/]");
        lines.Add($"  Now:      {Markup.Escape(_songTitle.Length == 0 ? "(no metadata)" : _songTitle)}");
        lines.Add("");
        lines.Add($"[bold {t.Accent}]Recently played[/]");
        if (_history.Count == 0) lines.Add($"  [{t.Dim}](nothing yet)[/]");
        foreach (var title in _history.Reverse().Take(5))
            lines.Add($"  - {Markup.Escape(title)}");
        return lines;
    }

    private void SetStatus(string message, bool isError = false)
    {
        _status = message;
        _statusIsError = isError;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _recorder.Stop();
        _buffer?.Stop();
        _metadata.Stop();
        if (_currentStation is not null)
            _settingsManager.Settings.LastStationUrl = _currentStation.Url;
        _settingsManager.Save();
        _player.Dispose();
        Logger.Info("Axplayer exiting.");
    }
}
