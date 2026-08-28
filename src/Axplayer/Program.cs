using Axplayer.Audio;
using Axplayer.Config;
using Axplayer.Data;
using Axplayer.UI;

namespace Axplayer;

/// <summary>Parsed command-line options.</summary>
public sealed record AppOptions
{
    public string? StationUrl { get; init; }
    public int? Volume { get; init; }
    public bool NoVisualizer { get; init; }
    public string? DataDir { get; init; }
    public string? ProbeUrl { get; init; }
    public string? PlayTestUrl { get; init; }
    public string? BufferTestUrl { get; init; }
    public int PlayTestSeconds { get; init; } = 15;
    public bool UiPreview { get; init; }
    public bool Check { get; init; }
    public bool RefreshCatalog { get; init; }
    public string CatalogBaseUrl { get; init; } = FmstreamCatalog.DefaultBaseUrl;
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
}

internal static class Program
{
    private const string Version = "1.0.0";

    private static int Main(string[] args)
    {
        // UTF-8 everywhere so box-drawing/block glyphs survive every code path.
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* redirected */ }

        var options = ParseArgs(args);

        if (options.ShowHelp) { PrintHelp(); return 0; }
        if (options.ShowVersion) { Console.WriteLine($"Axplayer {Version}"); return 0; }

        try
        {
            AppPaths.Initialize(options.DataDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cannot initialize data directory: {ex.Message}");
            return 1;
        }
        Logger.Initialize();
        Logger.Info($"Axplayer {Version} starting with args: {string.Join(' ', args)}");

        if (options.ProbeUrl is not null) return Probe(options.ProbeUrl);
        if (options.PlayTestUrl is not null) return PlayTest(options.PlayTestUrl, options.PlayTestSeconds);
        if (options.BufferTestUrl is not null) return BufferTest(options.BufferTestUrl, options.PlayTestSeconds);
        if (options.UiPreview) return UiPreview();
        if (options.RefreshCatalog) return RefreshCatalogAndExit(options.CatalogBaseUrl);
        if (options.Check) return SelfCheck();

        if (!Terminal.IsInteractive)
        {
            Console.WriteLine("Axplayer requires an interactive terminal (keyboard input).");
            Console.WriteLine("Run with --help to see command-line options, or use --probe <url> / --check to test streams.");
            return 2;
        }

        Terminal.Setup();
        try
        {
            using var app = new App(options);
            app.Run();
        }
        catch (Exception ex)
        {
            Logger.Error($"Fatal: {ex}");
            Console.WriteLine($"\nFatal error: {ex.Message}");
            return 1;
        }
        finally
        {
            Terminal.Restore();
        }

        return 0;
    }

    private static AppOptions ParseArgs(string[] args)
    {
        string? stationUrl = null, dataDir = null, probeUrl = null, playTestUrl = null, bufferTestUrl = null, catalogUrl = FmstreamCatalog.DefaultBaseUrl;
        int? volume = null;
        int playTestSeconds = 15;
        bool noVisualizer = false, check = false, help = false, version = false, uiPreview = false, refreshCatalog = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--station":
                    stationUrl = NextValue(args, ref i, "--station");
                    break;
                case "--volume":
                    if (int.TryParse(NextValue(args, ref i, "--volume"), out int v))
                        volume = v;
                    break;
                case "--no-visualizer":
                    noVisualizer = true;
                    break;
                case "--data-dir":
                    dataDir = NextValue(args, ref i, "--data-dir");
                    break;
                case "--probe":
                    probeUrl = NextValue(args, ref i, "--probe");
                    break;
                case "--play-test":
                    playTestUrl = NextValue(args, ref i, "--play-test");
                    break;
                case "--buffer-test":
                    bufferTestUrl = NextValue(args, ref i, "--buffer-test");
                    break;
                case "--seconds":
                    if (int.TryParse(NextValue(args, ref i, "--seconds"), out int sec))
                        playTestSeconds = Math.Clamp(sec, 3, 120);
                    break;
                case "--check":
                    check = true;
                    break;
                case "--ui-preview":
                    uiPreview = true;
                    break;
                case "--refresh-catalog":
                    refreshCatalog = true;
                    break;
                case "--catalog-url":
                    catalogUrl = NextValue(args, ref i, "--catalog-url");
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--version":
                case "-v":
                    version = true;
                    break;
                default:
                    Console.WriteLine($"Unknown argument: {args[i]} (use --help)");
                    break;
            }
        }

        return new AppOptions
        {
            StationUrl = stationUrl,
            Volume = volume,
            NoVisualizer = noVisualizer,
            DataDir = dataDir,
            ProbeUrl = probeUrl,
            PlayTestUrl = playTestUrl,
            BufferTestUrl = bufferTestUrl,
            PlayTestSeconds = playTestSeconds,
            UiPreview = uiPreview,
            Check = check,
            RefreshCatalog = refreshCatalog,
            CatalogBaseUrl = string.IsNullOrWhiteSpace(catalogUrl) ? FmstreamCatalog.DefaultBaseUrl : catalogUrl,
            ShowHelp = help,
            ShowVersion = version,
        };
    }

    private static string NextValue(string[] args, ref int i, string flag)
    {
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            return args[++i];
        Console.WriteLine($"Missing value for {flag}");
        return "";
    }

    /// <summary>Fetch the fmstream.org catalog into stations.json, then exit.</summary>
    private static int RefreshCatalogAndExit(string baseUrl)
    {
        Console.WriteLine($"Refreshing catalog from {baseUrl} …");
        try
        {
            var repo = new StationRepository(AppPaths.StationsFile, baseUrl);
            // A full categorized refresh involves many polite requests (delays + throttling
            // retries), so allow generous time.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var stations = repo.RefreshCatalogAsync(cts.Token).GetAwaiter().GetResult();
            Console.WriteLine($"  Categories: {FmstreamCatalog.Categories.Length}");
            Console.WriteLine($"  Stations:   {stations.Count}");
            Console.WriteLine($"  File:       {AppPaths.StationsFile}");

            // Show the breakdown by genre for confirmation.
            foreach (var group in stations.GroupBy(s => s.Genre).OrderBy(g => g.Key))
                Console.WriteLine($"    {group.Key,-22} {group.Count()}");

            Console.WriteLine("Catalog refreshed OK.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [ERROR] {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            Axplayer {Version} — retro terminal radio player for internet streams.

            USAGE:
              Axplayer.exe [options]

            OPTIONS:
              --station <url>     Play a stream URL immediately on startup
              --volume <0-100>    Override the default volume
              --no-visualizer     Disable the spectrum analyzer panel
              --data-dir <path>   Use a custom data directory (default: ./data next to the exe)
              --probe <url>       Validate a stream URL and print its ICY info, then exit
              --play-test <url>   Headless playback test for a few seconds, then exit
              --buffer-test <url> Buffer-mode smoke test (buffer + play the local file), then exit
              --refresh-catalog   Re-download the station catalog from fmstream.org, then exit
              --catalog-url <url> Override the fmstream.org directory base URL
              --check             Verify LibVLC/dependencies are working, then exit
              --ui-preview        Render one TUI frame and exit (dev tool)
              --help, -h          Show this help
              --version, -v       Show the version

            KEYS (in the TUI):
              Space/P  Play/pause      S     Stop              F     Toggle favorite
              +/-      Volume          M     Mute/unmute       Tab   All/Favorites view
              ↑/↓      Navigate        Enter Play             /     Search
              N        Add station     D     Delete            E     Edit station
              Ctrl+D   Delete all      S     Stop              /     Search
              R        Record stream   I     Station info      T     Sleep timer
              Ctrl+R   Refresh catalog Ctrl+D Delete all       X     Export favorites
              L        Import favorites C     Cycle theme       Q/Esc Quit
            """);
    }

    private static int Probe(string url)
    {
        Console.WriteLine($"Probing {url} …");
        var info = StreamProbe.ProbeAsync(url).GetAwaiter().GetResult();

        Console.WriteLine($"  Result:   {(info.Ok ? "OK ✓" : $"FAILED ✗")}");
        if (!info.Ok)
        {
            Console.WriteLine($"  Error:    {info.Error}");
            return 1;
        }
        Console.WriteLine($"  Name:     {info.Name ?? "(not advertised)"}");
        Console.WriteLine($"  Genre:    {info.Genre ?? "(not advertised)"}");
        Console.WriteLine($"  Bitrate:  {(string.IsNullOrWhiteSpace(info.Bitrate) ? "(not advertised)" : info.Bitrate + " kbps")}");
        Console.WriteLine($"  Format:   {info.ContentType ?? "(unknown)"}");
        return 0;
    }

    /// <summary>
    /// Renders one sample frame to stdout so the layout can be inspected
    /// without an interactive terminal.
    /// </summary>
    private static int UiPreview()
    {
        var stations = DefaultStations.Create().ToList();
        var viz = new Visualizer(48, 60);
        for (int i = 0; i < 40; i++) viz.Update(0.033, true); // warm up the spectrum

        var snapshot = new UiSnapshot
        {
            Version = "1.0",
            Now = DateTime.Now,
            ConnectionLabel = "PLAYING",
            ConnectionColor = "green",
            FavoritesView = false,
            Searching = false,
            Stations = stations,
            Selected = 2,
            PlayingIndex = 2,
            State = PlaybackState.Playing,
            StationName = "Radio Paradise Main",
            SongTitle = "Some Great Song - The Artist",
            Volume = 75,
            Muted = false,
            Bitrate = "128",
            BufferPct = 100,
            Recording = true,
            RecFile = "rec_20260828_120000.mp3",
            AutoRecord = true,
            QueueMode = true,
            QueuePosition = 3,
            QueueCount = stations.Count,
            Status = "Now playing a track",
            Theme = UiTheme.Dark,
            Visualizer = viz,
            ShowVisualizer = true,
            Width = 100,
            Height = 30,
        };

        var frame = MainLayout.Render(snapshot);
        // Strip ANSI codes so the preview is readable in a pipe.
        var plain = System.Text.RegularExpressions.Regex.Replace(frame, @"\x1b\[[0-9;]*m", "");
        Console.Write(plain);
        return 0;
    }

    /// <summary>
    /// Headless playback smoke test: plays a URL for a few seconds and reports
    /// the playback state and any ICY metadata received. Useful for debugging
    /// streams without launching the TUI.
    /// </summary>
    private static int PlayTest(string url, int seconds)
    {
        Console.WriteLine($"Playing {url} for up to {seconds}s (Ctrl+C to stop early) …");

        using var player = new LibVlcPlayer(1500);
        using var metadata = new IcyMetadataReader();

        var state = PlaybackState.Stopped;
        bool reachedPlaying = false;
        string title = "";
        string? bitrate = null;
        bool icy = false;

        player.StateChanged += (_, s) =>
        {
            state = s;
            if (s == PlaybackState.Playing) reachedPlaying = true;
            Console.WriteLine($"  [state]   {s}");
        };
        metadata.TitleChanged += t =>
        {
            if (t == title) return;
            title = t;
            Console.WriteLine($"  [title]   {t}");
        };
        metadata.StreamInfoReceived += (n, g, b) =>
        {
            bitrate = b;
            icy = metadata.HasIcyMetadata;
            Console.WriteLine($"  [icy]     name={n ?? "-"} genre={g ?? "-"} bitrate={b ?? "-"} icymetaint={(icy ? "yes" : "no")}");
        };

        player.Play(url);
        metadata.Start(url);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastPoll = sw.Elapsed.TotalSeconds;
        while (sw.Elapsed.TotalSeconds < seconds && state != PlaybackState.Error)
        {
            Thread.Sleep(500);
            if (sw.Elapsed.TotalSeconds - lastPoll >= 3)
            {
                lastPoll = sw.Elapsed.TotalSeconds;
                var backend = player.BackendNowPlaying;
                if (!string.IsNullOrWhiteSpace(backend))
                    Console.WriteLine($"  [vcl-meta] {backend}");
            }
        }

        player.Stop();
        Console.WriteLine($"  Result:   reachedPlaying={reachedPlaying} | title={(title.Length == 0 ? "(none)" : title)} | bitrate={bitrate ?? "?"} | icy={(icy ? "yes" : "no")}");
        return reachedPlaying ? 0 : 1;
    }

    /// <summary>
    /// Headless smoke test for buffer mode: starts a StreamBuffer (download + local
    /// HTTP server), waits for the startup pre-roll to fill, plays the local stream
    /// with LibVLC, and reports whether the feed is alive and playback advances.
    /// </summary>
    private static int BufferTest(string url, int seconds)
    {
        Console.WriteLine($"Buffering {url} for up to {seconds}s (buffer-mode smoke test) …");

        using var player = new LibVlcPlayer(1500);
        using var buffer = new StreamBuffer();

        var state = PlaybackState.Stopped;
        bool reachedPlaying = false;
        bool fileGrew = false;
        bool startedPlayback = false;
        long lastLen = -1;

        player.StateChanged += (_, s) =>
        {
            state = s;
            if (s == PlaybackState.Playing) reachedPlaying = true;
            Console.WriteLine($"  [state]   {s}");
        };

        buffer.Start(url, AppPaths.BufferDir, 2, 5);
        Console.WriteLine($"  [server]  {buffer.StreamUrl}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastPoll = sw.Elapsed.TotalSeconds;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(500);

            long len = buffer.FileLengthBytes;
            if (len > 0) fileGrew = true;
            if (len != lastLen)
            {
                lastLen = len;
                Console.WriteLine($"  [buffer]  {len / 1024} KB  feed={(buffer.FeedAlive ? "alive" : "down")}");
            }

            // Mimic the app: start playback once the 2s pre-roll (~32 KB) has filled.
            if (!startedPlayback && buffer.FeedAlive && len >= 32 * 1024)
            {
                startedPlayback = true;
                player.Play(buffer.StreamUrl!);
                Console.WriteLine("  [play]    started from local stream");
            }

            if (sw.Elapsed.TotalSeconds - lastPoll >= 5 && startedPlayback)
            {
                lastPoll = sw.Elapsed.TotalSeconds;
                Console.WriteLine($"  [pos]     time={player.TimeMs} ms");
            }
        }

        bool feedAliveAtEnd = buffer.FeedAlive;
        long buffered = lastLen;
        player.Stop();
        buffer.Stop();
        Console.WriteLine($"  Result:   reachedPlaying={reachedPlaying} | feedAlive={feedAliveAtEnd} | fileGrew={fileGrew} | buffered={buffered / 1024} KB | state={state}");
        return reachedPlaying && fileGrew ? 0 : 1;
    }

    private static int SelfCheck()
    {
        var failures = 0;

        try
        {
            Console.WriteLine($"Data dir:      {AppPaths.DataDir}");
            var settings = new SettingsManager(AppPaths.DataDir);
            Console.WriteLine($"Settings:      OK (default volume {settings.Settings.DefaultVolume}%)");
        }
        catch (Exception ex) { Console.WriteLine($"Settings:      FAILED ({ex.Message})"); failures++; }

        try
        {
            var repo = new StationRepository(AppPaths.StationsFile);
            Console.WriteLine($"Station list:  OK ({repo.All.Count} stations)");
        }
        catch (Exception ex) { Console.WriteLine($"Station list:  FAILED ({ex.Message})"); failures++; }

        var libVlcDir = LibVlcLocator.FindDirectory();
        Console.WriteLine($"libvlc native: {(libVlcDir is null ? "NOT FOUND" : libVlcDir)}");
        Console.WriteLine($"  base dir:    {AppContext.BaseDirectory}");
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nd)
            Console.WriteLine($"  native dirs: {nd.Replace(Path.PathSeparator, ';')}");

        try
        {
            using var player = new LibVlcPlayer(1500);
            Console.WriteLine("LibVLC init:   OK");
            Console.WriteLine("Self-check:    PASSED ✓");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LibVLC init:   FAILED ({ex.Message})");
            Console.WriteLine("Self-check:    FAILED ✗");
            return 1;
        }
    }
}
