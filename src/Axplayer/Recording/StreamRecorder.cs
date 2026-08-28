namespace Axplayer.Recording;

/// <summary>
/// Records the currently playing stream to rolling files. In buffer mode the
/// URL is the app's localhost buffered stream, so recording shares the same
/// network connection as playback. A recording can run indefinitely: each
/// segment is closed after the configured duration and the next segment starts
/// automatically. The recorder reconnects after a source dropout instead of
/// ending the recording.
/// </summary>
public sealed class StreamRecorder : IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private string? _url;
    private string? _directory;
    private bool _localBufferedSource;
    private string? _fileExtension;
    private string? _sessionDirectory;
    private int _segmentMinutes;
    private int _segmentNumber;

    public bool IsRecording { get; private set; }
    public string? FilePath { get; private set; }
    public string? SessionDirectory => _sessionDirectory;
    public int SegmentCount => _segmentNumber;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Start(string url, string directory, int segmentMinutes = 5, bool localBufferedSource = false, string? sourceExtension = null)
    {
        Stop();

        Directory.CreateDirectory(directory);
        _url = url;
        _directory = directory;
        _localBufferedSource = localBufferedSource;
        _fileExtension = sourceExtension ?? ExtForUrl(url);
        _sessionDirectory = CreateSessionDirectory(directory);
        _segmentMinutes = Math.Clamp(segmentMinutes, 1, 1440);
        _segmentNumber = 0;
        _cts = new CancellationTokenSource();
        FilePath = null;
        IsRecording = true;

        _task = Task.Run(() => RecordSessionAsync(_cts.Token));
        Logger.Info($"Recording session started: {url} (segments: {_segmentMinutes} min)");
    }

    private async Task RecordSessionAsync(CancellationToken ct)
    {
        int reconnectAttempt = 0;
        while (!ct.IsCancellationRequested && _url is not null && _directory is not null)
        {
            string path = NextPath(_directory, _url);
            FilePath = path;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Axplayer/1.0");
                if (_localBufferedSource)
                    request.Headers.TryAddWithoutValidation("X-Axplayer-Buffered-Source", "1");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                using var segmentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                segmentCts.CancelAfter(TimeSpan.FromMinutes(_segmentMinutes));

                await input.CopyToAsync(output, segmentCts.Token);
                reconnectAttempt = 0;
                if (!ct.IsCancellationRequested)
                    Logger.Info($"Recording segment finished: {path}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Segment duration elapsed. Start the next segment immediately.
                reconnectAttempt = 0;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Recording connection dropped: {ex.Message}");
                if (File.Exists(path))
                {
                    try
                    {
                        if (new FileInfo(path).Length == 0) File.Delete(path);
                    }
                    catch { /* best effort */ }
                }

                if (ct.IsCancellationRequested) break;
                reconnectAttempt++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, reconnectAttempt * 2)), ct);
            }
        }

        if (!ct.IsCancellationRequested)
            Logger.Info("Recording session ended.");
    }

    private string NextPath(string directory, string url)
    {
        _segmentNumber++;
        var ext = _fileExtension ?? ExtForUrl(url);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(_sessionDirectory ?? directory, $"rec_{stamp}_{_segmentNumber:000}{ext}");
    }

    public void Stop()
    {
        if (!IsRecording && _cts is null) return;
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
        _cts?.Dispose();
        _cts = null;
        _task = null;
        IsRecording = false;
        _url = null;
        _directory = null;
        _sessionDirectory = null;
        _localBufferedSource = false;
        _fileExtension = null;
    }

    private static string CreateSessionDirectory(string directory)
    {
        string baseName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}";
        string path = Path.Combine(directory, baseName);
        int suffix = 1;
        while (Directory.Exists(path))
            path = Path.Combine(directory, $"{baseName}_{suffix++:00}");
        Directory.CreateDirectory(path);
        return path;
    }

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
