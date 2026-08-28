using Axplayer.Audio;
using Spectre.Console;

namespace Axplayer.UI;

/// <summary>
/// Builds the "Now Playing" line and the stats line (volume / bitrate /
/// buffer / recording indicator) beneath the station list.
/// </summary>
public static class NowPlayingBar
{
    public static string BuildTitleLine(string stationName, string songTitle, PlaybackState state, int width, UiTheme theme)
    {
        string stateTag = state switch
        {
            PlaybackState.Playing => $"[{theme.Playing}][[PLAY]][/]",
            PlaybackState.Paused => $"[{theme.Paused}][[PAUSE]][/]",
            PlaybackState.Buffering => $"[yellow][[BUFFER]][/]",
            PlaybackState.Error => $"[{theme.Err}][[ERROR]][/]",
            _ => "[grey][[STOP]][/]",
        };

        string station = string.IsNullOrWhiteSpace(stationName) ? "unknown" : Markup.Escape(Truncate(stationName, 28));
        string title = string.IsNullOrWhiteSpace(songTitle)
            ? $"[{theme.Dim}](no metadata yet)[/]"
            : Markup.Escape(Truncate(songTitle, Math.Max(20, width - 52)));

        string line = $"{stateTag} [bold {theme.Accent}]{station}[/]";
        if (!string.IsNullOrWhiteSpace(songTitle))
            line += $"  {title}";

        return line;
    }

    public static string BuildStatsLine(
        int volume, bool muted, string bitrate, int bufferPct,
        bool recording, string? recFile, int width, UiTheme theme)
    {
        string volText = muted
            ? $"[{theme.Warn}]MUTED[/]"
            : $"[{theme.Accent}]Vol: {volume}%[/]";

        string bitrateText = string.IsNullOrWhiteSpace(bitrate)
            ? $"[{theme.Dim}]Bitrate: --[/]"
            : $"[{theme.Dim}]Bitrate: {Markup.Escape(bitrate)}[/]";

        string buffer = bufferPct switch
        {
            >= 100 => $"[{theme.Ok}]Buffer: 100%[/]",
            >= 0 => $"[{theme.Warn}]Buffer: {bufferPct}%[/]",
            _ => $"[{theme.Dim}]Buffer: --[/]",
        };

        string rec = recording
            ? $"[red]REC[/][{theme.Dim}] {Markup.Escape(Path.GetFileName(recFile ?? ""))}[/]"
            : "";

        return $"{volText} | {bitrateText} | {buffer} | {rec}".TrimEnd(' ', '|');
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
}
