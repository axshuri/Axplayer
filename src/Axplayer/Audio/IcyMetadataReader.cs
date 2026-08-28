using System.Text;

namespace Axplayer.Audio;

/// <summary>
/// Dedicated connection that requests ICY metadata (Icy-MetaData: 1) and parses
/// the interleaved StreamTitle blocks so the UI can show the current song in
/// real time. Runs on its own background task; reconnects with exponential
/// backoff when the stream drops. If the server doesn't offer ICY metadata
/// (e.g. OGG/Vorbis streams), it exits quietly and the app falls back to the
/// backend's own metadata.
/// </summary>
public sealed class IcyMetadataReader : IDisposable
{
    /// <summary>Raised whenever a new song title is parsed.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Raised with (name, genre, bitrate) once the stream headers are seen.</summary>
    public event Action<string?, string?, string?>? StreamInfoReceived;

    private static readonly HttpClient Http = CreateClient();
    private CancellationTokenSource? _cts;

    /// <summary>True once the server confirmed it will send ICY metadata blocks.</summary>
    public bool HasIcyMetadata { get; private set; }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // stream never "times out"; we cancel explicitly
    }

    public void Start(string url)
    {
        Stop();
        HasIcyMetadata = false;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(url, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(string url, CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                request.Headers.TryAddWithoutValidation("User-Agent", "Axplayer/1.0");

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                var headers = response.Headers;
                string? GetHeader(string name) =>
                    headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

                // No metadata interval advertised -> no ICY data to parse.
                var metaintStr = GetHeader("icy-metaint");
                HasIcyMetadata = int.TryParse(metaintStr, out var metaint) && metaint > 0;
                if (!HasIcyMetadata)
                {
                    Logger.Info("Server does not provide ICY metadata; relying on backend metadata.");
                    StreamInfoReceived?.Invoke(GetHeader("icy-name"), GetHeader("icy-genre"), GetHeader("icy-br"));
                    return;
                }

                StreamInfoReceived?.Invoke(GetHeader("icy-name"), GetHeader("icy-genre"), GetHeader("icy-br"));

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var audioBuf = new byte[metaint];
                var metaBuf = new byte[1 + 16 * 255]; // max metadata block

                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactlyAsync(stream, audioBuf, metaint, ct))
                        break; // stream closed

                    int lenByte = stream.ReadByte();
                    if (lenByte < 0) break;

                    int metaLen = lenByte * 16;
                    int read = 0;
                    while (read < metaLen)
                    {
                        int n = await stream.ReadAsync(metaBuf.AsMemory(read, metaLen - read), ct);
                        if (n <= 0) break;
                        read += n;
                    }

                    var title = ParseTitle(metaBuf, metaLen);
                    if (!string.IsNullOrWhiteSpace(title))
                        TitleChanged?.Invoke(title);

                    attempt = 0; // healthy again
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn($"ICY metadata connection dropped: {ex.Message}");
            }

            if (ct.IsCancellationRequested) break;
            attempt++;
            await Task.Delay(BackoffDelay(attempt), ct);
        }
    }

    /// <summary>Exponential backoff: 1s, 2s, 4s, ... capped at 15s.</summary>
    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(15, 1 << Math.Min(attempt, 4)));

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n <= 0) return false;
            offset += n;
        }
        return true;
    }

    /// <summary>Extract "StreamTitle='...';" from an ICY metadata block (ISO-8859-1 encoded).</summary>
    private static string? ParseTitle(byte[] block, int length)
    {
        if (length <= 0) return null;
        // Only the first NUL-terminated region matters in practice.
        var text = Encoding.Latin1.GetString(block, 0, length);
        const string marker = "StreamTitle='";
        int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        int end = text.IndexOf('\'', start);
        if (end < 0) end = text.IndexOf(';', start);
        if (end <= start) return null;
        var title = text[start..end].Trim();
        return title.Length == 0 ? null : title;
    }

    public void Dispose() => Stop();
}
