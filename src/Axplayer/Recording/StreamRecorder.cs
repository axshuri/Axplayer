namespace Axplayer.Recording;

/// <summary>
/// Records the currently playing stream to a file by opening its own plain
/// HTTP connection (without Icy-MetaData, so no metadata blocks are interleaved
/// into the saved bytes) and writing the raw audio to recordings/.
/// </summary>
public sealed class StreamRecorder : IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsRecording { get; private set; }
    public string? FilePath { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Start(string url, string directory)
    {
        Stop();

        Directory.CreateDirectory(directory);
        var ext = ExtForUrl(url);
        var fileName = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        var path = Path.Combine(directory, fileName);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        FilePath = path;
        _task = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Axplayer/1.0");

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
                await input.CopyToAsync(output, ct);

                if (!ct.IsCancellationRequested)
                    Logger.Info($"Recording finished: {path}");
            }
            catch (OperationCanceledException) { /* stopped by user */ }
            catch (Exception ex)
            {
                Logger.Error($"Recording failed: {ex.Message}");
                IsRecording = false;
            }
        });
        IsRecording = true;
        Logger.Info($"Recording started: {path}");
    }

    public void Stop()
    {
        if (!IsRecording) return;
        _cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
        _cts?.Dispose();
        _cts = null;
        IsRecording = false;
    }

    private static string ExtForUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".ogg") || lower.Contains(".opus")) return ".ogg";
        if (lower.Contains(".aac")) return ".aac";
        if (lower.Contains(".flac")) return ".flac";
        return ".mp3"; // Shoutcast/MP3 default
    }

    public void Dispose() => Stop();
}
