using Axplayer.Audio;
using Axplayer.Data;
using Spectre.Console;

namespace Axplayer.UI;

/// <summary>
/// Builds the visible station-list rows (with scrolling/pagination) as
/// Spectre markup strings. One row per station: cursor, number, name,
/// genre, favorite heart, play indicator and play-count.
/// </summary>
public static class StationListView
{
    public static List<string> BuildRows(
        IReadOnlyList<Station> stations,
        int selectedIndex,
        int maxRows,
        int contentWidth,
        int? playingIndex,
        PlaybackState playback,
        UiTheme theme)
    {
        var rows = new List<string>();
        if (stations.Count == 0)
        {
            rows.Add(Center($"No stations. Press [bold {theme.Accent}]N[/] to add one.", contentWidth));
            return rows;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, stations.Count - 1);

        // Scrolling window around the selection.
        int start = Math.Clamp(selectedIndex - maxRows / 2, 0, Math.Max(0, stations.Count - maxRows));
        int end = Math.Min(stations.Count, start + maxRows);

        for (int i = start; i < end; i++)
        {
            var station = stations[i];
            bool isSelected = i == selectedIndex;
            bool isPlaying = playingIndex == i && playback is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Paused;

            // Fixed columns: cursor+number+spacing (~10) + genre cell (18) + heart (2) + playing mark (3) + plays (6).
            int nameBudget = Math.Max(8, contentWidth - 39);
            string name = Truncate(station.Name, nameBudget);
            string genre = Truncate(station.Genre, 16).PadRight(16);

            string cursor = isSelected ? ">" : " ";
            string num = (i + 1).ToString().PadLeft(2);
            string fav = station.IsFavorite ? $"[red]*[/]" : " ";
            string playingMark = isPlaying ? $"[{theme.Playing}]+[/]" : " ";
            string plays = station.PlayCount > 0 ? $"[{theme.Dim}]{station.PlayCount}x[/]" : "";

            string color = isSelected ? theme.Highlight : theme.Dim;
            var row = $"{cursor} {num}. [bold {color}]{Markup.Escape(name)}[/]";
            row += $"[{theme.Dim}] {Markup.Escape(genre)}[/]";
            row += $" {fav} {playingMark} {plays}";

            rows.Add(row);
        }

        return rows;
    }

    private static string Center(string text, int width)
    {
        string plain = Markup.Remove(text);
        int pad = Math.Max(0, (width - plain.Length) / 2);
        return new string(' ', pad) + text;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
}
