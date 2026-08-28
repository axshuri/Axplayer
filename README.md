# Axplayer

A retro terminal radio player for internet streams (Icecast / Shoutcast). Keyboard-first
TUI in the spirit of `cmus` / `ncmpcpp` — box-drawn panels, a live ASCII spectrum
analyzer, real ICY song metadata, favorites, and resilient reconnection.

Preview of the TUI frame (see [`docs/preview.txt`](docs/preview.txt) for the rendered output):

## Features

- **Audio playback** via **LibVLC** (LibVLCSharp) — plays MP3, AAC, OGG/Opus, FLAC
  streams headless from the console; no WebView2 runtime required.
- **Retro TUI** built with Spectre.Console: top bar (title, clock, connection status),
  station list, now-playing bar, spectrum visualizer, status line and shortcut hints.
- **Live song metadata** — a dedicated ICY metadata reader parses `StreamTitle` blocks
  in real time (with reconnection/backoff); falls back to VLC's own metadata for
  streams without ICY (e.g. OGG).
- **Categorized stations from [fmstream.org](http://nossl.fmstream.org)** — the
  master list is fetched by category (Jazz, Rock, Classical, Electronic, Hip-Hop/R&B,
  Country, News, Talk, Oldies, Chill/Lounge, Alternative/Metal, World) on first run and
  cached to `data/stations.json`. Refresh any time with `Ctrl+R` or
  `--refresh-catalog`. If the directory can't be reached, a small built-in fallback
  list is used.
- **Favorites** — saved to `data/stations.json`, preserved across catalog refreshes
  (matched by stream URL), with import/export.
- **Resilience** — buffering indicators, stall watchdog, exponential-backoff reconnect,
  and auto-skip to the next station when a stream is dead. The catalog fetcher is
  polite to fmstream.org (rate-gated, retries 429s with backoff).
- **Recording** — `R` saves the raw stream to `data/recordings/`.
- **Settings** — `data/settings.json`: default volume, last station, autoplay, theme,
  buffer, visualizer tuning.

## Build & run

Prerequisites: [.NET SDK](https://dotnet.microsoft.com/download) 8 or later
(developed on .NET 10). Windows 10+ (Windows Terminal recommended).

### One-click build (Windows)

```bat
build.bat              rem build the single-file .exe (auto-detects x64/arm64)
build.bat -r           rem clean + rebuild
```

This publishes the self-contained single-file exe to
`src/Axplayer/bin/Release/net10.0/<rid>/publish/Axplayer.exe`, prints the size, and
optionally adds it to your user PATH (see below).

### To call `axplayer` from any Command Prompt

```bat
add-to-path.bat        rem add the default x64 publish folder to your user PATH
add-to-path.bat "C:\path\to\publish"
```

Then open a **new** terminal window and run:

```bat
axplayer
```

### Manual builds

```bash
# Build and run (framework-dependent)
dotnet run --project src/Axplayer

# Self-contained single-file .exe (win-x64)
dotnet publish src/Axplayer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# → src/Axplayer/bin/Release/net10.0/win-x64/publish/Axplayer.exe
```

The single-file exe embeds the libvlc runtime (x64) and self-extracts it to a temp
directory on first run. The `data/` folder (settings, stations, logs, recordings)
is created next to the executable.

## Controls

| Key            | Action                              | Key            | Action                    |
|----------------|-------------------------------------|----------------|---------------------------|
| `Space` / `P`  | Play / pause selected station       | `S`            | Stop                      |
| `+` / `-`      | Volume up / down                    | `M`            | Mute / unmute             |
| `F`            | Toggle favorite                     | `Tab`          | All stations / Favorites  |
| `↑` / `↓`      | Navigate list                       | `Enter`        | Play selected             |
| `N`            | Add station (URL validated)         | `D`            | Delete selected           |
| `E`            | Edit station (name/URL/genre)       | `Ctrl+D`       | Delete all stations       |
| `/`            | Search by name / genre              |                |                           |
| `R`            | Record / stop recording             | `I`            | Station info + history    |
| `Ctrl+R`       | Refresh catalog from fmstream.org   |                |                           |
| `T`            | Sleep timer (minutes)               | `X` / `L`      | Export / import favorites |
| `C`            | Cycle color theme                   | `Q` / `Esc`    | Quit                      |

## Command line

```
Axplayer.exe --station <url>        Play a stream URL on startup
Axplayer.exe --volume 50            Override default volume (0-100)
Axplayer.exe --no-visualizer        Disable the spectrum analyzer
Axplayer.exe --data-dir <path>      Custom data directory
Axplayer.exe --probe <url>          Validate a URL and print its ICY info
Axplayer.exe --play-test <url>      Headless playback test (15s) for debugging
Axplayer.exe --refresh-catalog      Re-download the categorized catalog, then exit
Axplayer.exe --catalog-url <url>    Override the fmstream.org directory base URL
Axplayer.exe --check                Verify LibVLC and dependencies
Axplayer.exe --ui-preview           Render one TUI frame and exit (dev tool)
```

## Project structure

```
src/Axplayer/
├── Program.cs              # Entry point, CLI args, self-check/probe/test modes
├── App.cs                  # Main loop, input handling, playback control, resilience
├── AppPaths.cs             # Data directory layout
├── Logger.cs               # Timestamped file logging (data/logs/)
├── Config/
│   ├── AppSettings.cs      # Settings model with defaults
│   └── SettingsManager.cs  # settings.json persistence
├── Data/
│   ├── Station.cs          # Station model
│   ├── StationRepository.cs# stations.json persistence, favorites, import/export, catalog refresh
│   ├── FmstreamCatalog.cs  # fmstream.org directory loader (search → expand → filter)
│   ├── FavoritesManager.cs # Favorites facade
│   └── DefaultStations.cs  # Offline fallback station list (few picks)
├── Audio/
│   ├── IAudioPlayer.cs     # Playback abstraction + state enum
│   ├── LibVlcPlayer.cs     # LibVLC backend
│   ├── LibVlcLocator.cs    # Finds libvlc binaries (normal & single-file publish)
│   ├── StreamProbe.cs      # URL validation / ICY header discovery
│   └── IcyMetadataReader.cs# Real-time StreamTitle parsing
├── Recording/
│   └── StreamRecorder.cs   # Raw stream → file
└── UI/
    ├── MainLayout.cs       # Frame composition (UiSnapshot → ANSI string)
    ├── StationListView.cs  # Scrolling station rows
    ├── NowPlayingBar.cs    # Title + stats lines
    ├── Visualizer.cs       # Simulated spectrum analyzer + VU meter
    ├── PromptSession.cs    # In-TUI text input
    ├── Terminal.cs         # VT/UTF-8 console setup
    └── UiTheme.cs          # Dark / light palettes
```

## Notes & limitations

- **Catalog freshness & rate limits.** axplayer fetches the station catalog from
  fmstream.org on first run and only re-fetches on `Ctrl+R` / `--refresh-catalog`.
  The fetcher spaces out requests and retries `429`/`5xx` with backoff out of respect
  for the directory. Fast, repeated refreshes may still be throttled briefly; the
  previous cached list is always kept on failure.
- The spectrum analyzer uses a **procedural simulation** driven by the play state:
  console apps don't get decoded PCM from LibVLC, so the bars are "inspired by"
  the music rather than measured. It reacts to play/pause and intensity settings.
- ICY metadata requires the server to honor `Icy-MetaData: 1`; some servers
  (e.g. certain Zeno.fm frontends) don't, and the app then falls back to VLC's
  metadata or shows "(no metadata yet)".
- Mouse interaction is not supported; the UI is keyboard-first.
- The recorder saves the raw stream bytes (no transcoding), so files are named
  by the stream's likely format (`.mp3`, `.aac`, `.ogg`).
