using System.Net.Http.Headers;

namespace Axplayer.Audio;

/// <summary>Result of probing a stream URL before adding it to the station list.</summary>
public sealed class StreamInfo
{
    public required string Url { get; init; }
    public bool Ok { get; init; }
    public string? Name { get; init; }
    public string? Genre { get; init; }
    public string? Bitrate { get; init; }
    public string? ContentType { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Validates a stream URL and harvests the ICY/Shoutcast headers the server
/// advertises (station name, genre, bitrate). Uses a GET with headers-only
/// response handling so we never download the actual audio during a probe.
/// </summary>
public static class StreamProbe
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
    }

    public static async Task<StreamInfo> ProbeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", "Axplayer/1.0");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return new StreamInfo { Url = url, Ok = false, Error = $"HTTP {(int)response.StatusCode} {response.StatusCode}" };

            var headers = response.Headers;
            string? GetHeader(string name) =>
                headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

            return new StreamInfo
            {
                Url = url,
                Ok = true,
                Name = GetHeader("icy-name"),
                Genre = GetHeader("icy-genre"),
                Bitrate = GetHeader("icy-br"),
                ContentType = response.Content.Headers.ContentType?.MediaType,
            };
        }
        catch (OperationCanceledException)
        {
            return new StreamInfo { Url = url, Ok = false, Error = "Connection timed out." };
        }
        catch (Exception ex)
        {
            return new StreamInfo { Url = url, Ok = false, Error = ex.Message };
        }
    }
}
