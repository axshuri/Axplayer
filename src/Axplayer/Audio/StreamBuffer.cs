using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Axplayer.Audio;

/// <summary>
/// Downloads a radio stream to a local buffer file and serves that file over a
/// tiny localhost HTTP server, so playback is fully decoupled from the network.
///
/// Why the HTTP layer: VLC playing a growing local file is flaky — it sometimes
/// stops at the file's momentary EOF (EndReached). Served over HTTP with no
/// Content-Length, the stream never "ends": when the feed drops, the file stops
/// growing and VLC simply waits in a buffering state; when the feed returns the
/// file resumes and VLC continues — gap-free, with no seek/resume machinery.
///
/// The download connection retries with exponential backoff for a configurable
/// cover window; after that the station is declared dead. This class only does
/// I/O and exposes pollable state — the App reads <see cref="FeedAlive"/>,
/// <see cref="FileLengthBytes"/> and <see cref="IsDead"/> from its main loop.
/// </summary>
public sealed class StreamBuffer : IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private TcpListener? _tcp;
    private string? _filePath;
    private string? _streamUrl;
    private long _fileLength;
    private volatile bool _feedAlive;
    private volatile bool _isDead;
    private long _lastDataTicks;

    /// <summary>True while a download session is running (i.e. a station is buffered).</summary>
    public bool IsActive => _cts is not null;

    /// <summary>True when the HTTP connection is currently delivering audio data.</summary>
    public bool FeedAlive => _feedAlive;

    /// <summary>True once the cover window elapsed without the feed returning.</summary>
    public bool IsDead => _isDead;

    /// <summary>Local path of the buffer file (deleted on stop).</summary>
    public string? FilePath => _filePath;

    /// <summary>Localhost URL VLC should play to hear the (growing) buffer.</summary>
    public string? StreamUrl => _streamUrl;

    public long FileLengthBytes
    {
        get { lock (_gate) return _fileLength; }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Start (or restart) buffering <paramref name="url"/> into a fresh file and
    /// serving it on a random localhost port. Drops the previous session.
    /// </summary>
    public void Start(string url, string directory, int dropoutSeconds, int coverMinutes)
    {
        Stop();

        Directory.CreateDirectory(directory);
        var ext = ExtForUrl(url);
        var path = Path.Combine(directory, $"buffer_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");

        _filePath = path;
        _fileLength = 0;
        _isDead = false;
        _feedAlive = false;

        // Localhost HTTP server (TcpListener, not HttpListener: no URL ACL needed).
        _tcp = new TcpListener(IPAddress.Loopback, 0);
        _tcp.Start();
        var port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _streamUrl = $"http://127.0.0.1:{port}/stream";

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var tcp = _tcp;
        _task = Task.Run(() => RunAsync(url, path, Math.Max(1, dropoutSeconds), TimeSpan.FromMinutes(Math.Max(0, coverMinutes)), ct));
        _ = Task.Run(() => AcceptLoopAsync(tcp, path, ct));
        Logger.Info($"Buffer started: {path} → {_streamUrl}");
    }

    /// <summary>Stop the download and the local server, and delete the buffer file.</summary>
    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        var tcp = _tcp;
        _tcp = null;
        try { tcp?.Stop(); } catch { /* best effort */ }

        if (_task is not null)
        {
            try { _task.Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
            _task = null;
        }
        cts?.Dispose();

        _feedAlive = false;
        if (_filePath is not null)
        {
            try { File.Delete(_filePath); } catch { /* best effort */ }
            _filePath = null;
        }
    }

    // --- Download side ---------------------------------------------------------

    private async Task RunAsync(string url, string path, int dropoutSeconds, TimeSpan cover, CancellationToken ct)
    {
        int attempt = 0;
        bool firstConnect = true;
        DateTime? dropStarted = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Axplayer/1.0");

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sessionCts.Token);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync(sessionCts.Token);
                // First connection creates the file; reconnects APPEND so the local
                // server's read position stays valid (truncating would strand it).
                await using var output = new FileStream(path,
                    firstConnect ? FileMode.Create : FileMode.Append,
                    FileAccess.Write, FileShare.Read, 64 * 1024);
                firstConnect = false;

                Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
                _feedAlive = true;
                dropStarted = null; // a successful connection grants a fresh cover window
                attempt = 0;

                // Monitor: if no audio data arrives for dropoutSeconds, kill this
                // session so the retry loop can reconnect.
                using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                var monitor = MonitorAsync(sessionCts, monitorCts.Token, dropoutSeconds);

                var buf = new byte[64 * 1024];
                while (!sessionCts.IsCancellationRequested)
                {
                    int n = await input.ReadAsync(buf, sessionCts.Token);
                    if (n <= 0) break; // clean EOF from the server

                    await output.WriteAsync(buf.AsMemory(0, n), sessionCts.Token);
                    await output.FlushAsync(sessionCts.Token); // keep the file fresh for the local server
                    Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);
                    lock (_gate) _fileLength += n;
                }

                monitorCts.Cancel();
                try { await monitor; } catch { /* monitor exits on cancel */ }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Session cancelled by the dropout monitor (or a hung read).
            }
            catch (Exception ex)
            {
                Logger.Warn($"Buffer feed dropped: {ex.Message}");
            }

            _feedAlive = false;
            if (ct.IsCancellationRequested) break;

            dropStarted ??= DateTime.UtcNow;
            if (cover <= TimeSpan.Zero || DateTime.UtcNow - dropStarted.Value >= cover)
            {
                _isDead = true;
                Logger.Warn("Buffer feed dead — cover window exhausted.");
                break;
            }

            attempt++;
            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    /// <summary>Cancels the session when no data has arrived for `dropoutSeconds`.</summary>
    private async Task MonitorAsync(CancellationTokenSource sessionCts, CancellationToken ct, int dropoutSeconds)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                if ((DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastDataTicks)) > dropoutSeconds * TimeSpan.TicksPerSecond)
                {
                    sessionCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* session ended normally */ }
    }

    // --- Local HTTP server side ------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener tcp, string path, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await tcp.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }

            var contentType = ContentTypeFor(path);
            _ = Task.Run(() => ServeClientAsync(client, path, contentType, ct));
        }
    }

    /// <summary>
    /// Serve the growing file to one client: minimal HTTP/1.1 response with no
    /// Content-Length, then stream bytes as they appear in the file. The body is
    /// close-delimited, so the connection never "ends" while the session lives —
    /// VLC simply waits when the file stops growing.
    /// </summary>
    private async Task ServeClientAsync(TcpClient client, string path, string contentType, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                await using var ns = client.GetStream();

                // Read and discard the request headers (until the blank line).
                var sawLf = false;
                var sawCr = false;
                while (!ct.IsCancellationRequested)
                {
                    int b = ns.ReadByte();
                    if (b < 0) return;
                    if (b == '\n')
                    {
                        if (sawLf) break; // blank line ends the header block
                        sawLf = true;
                        sawCr = false;
                    }
                    else if (b == '\r') { sawCr = true; }
                    else
                    {
                        if (sawCr && sawLf) break; // \r\n\r\n case
                        sawLf = false;
                        sawCr = false;
                    }
                }

                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: " + contentType + "\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: close\r\n" +
                    "\r\n");
                await ns.WriteAsync(header, ct);

                var buf = new byte[64 * 1024];
                long pos = 0;
                // FileShare.Delete so Stop() can remove the file even while a client is reading.
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                while (!ct.IsCancellationRequested)
                {
                    long len = FileLengthBytes;
                    if (pos >= len)
                    {
                        await Task.Delay(150, ct);
                        continue;
                    }

                    fs.Seek(pos, SeekOrigin.Begin);
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        await Task.Delay(150, ct);
                        continue;
                    }

                    await ns.WriteAsync(buf.AsMemory(0, n), ct);
                    pos += n;
                }
            }
        }
        catch (OperationCanceledException) { /* session stopped */ }
        catch (Exception) { /* client disconnected or file gone */ }
    }

    private static string ContentTypeFor(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".ogg")) return "audio/ogg";
        if (lower.EndsWith(".aac")) return "audio/aac";
        if (lower.EndsWith(".flac")) return "audio/flac";
        return "audio/mpeg";
    }

    /// <summary>Exponential backoff: 1s, 2s, 4s, 8s, capped at 8s.</summary>
    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(8, 1 << Math.Min(attempt, 3)));

    private static string ExtForUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".ogg") || lower.Contains(".opus")) return ".ogg";
        if (lower.Contains(".aac")) return ".aac";
        if (lower.Contains(".flac")) return ".flac";
        return ".mp3";
    }

    public void Dispose() => Stop();
}
