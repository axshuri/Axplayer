using Axplayer.Audio;
using Axplayer.Data;
using Spectre.Console;

namespace Axplayer.UI;

/// <summary>
/// Immutable snapshot of everything the UI needs to draw one frame. The App
/// fills this each tick; <see cref="MainLayout"/> turns it into a single
/// ANSI string that is written to the console in one shot (cursor home +
/// erase-to-end-of-line per row) for a stable, flicker-free retro TUI.
/// </summary>
public sealed class UiSnapshot
{
    public required string Version { get; init; }
    public DateTime Now { get; init; }
    public string ConnectionLabel { get; init; } = "";
    public string ConnectionColor { get; init; } = "grey";
    public bool FavoritesView { get; init; }
    public bool Searching { get; init; }
    public IReadOnlyList<Station>? Stations { get; init; }
    public int Selected { get; init; }
    public int? PlayingIndex { get; init; }
    public PlaybackState State { get; init; }
    public string StationName { get; init; } = "";
    public string SongTitle { get; init; } = "";
    public int Volume { get; init; }
    public bool Muted { get; init; }
    public string Bitrate { get; init; } = "";
    public int BufferPct { get; init; } = -1;
    public bool Recording { get; init; }
    public string? RecFile { get; init; }
    public string Status { get; init; } = "";
    public bool StatusIsError { get; init; }
    public PromptSession? Prompt { get; init; }
    public bool PromptCursorOn { get; init; }
    public bool ShowInfo { get; init; }
    public IReadOnlyList<string> InfoLines { get; init; } = [];
    public int? SleepMinutesLeft { get; init; }
    public UiTheme Theme { get; init; } = UiTheme.Dark;
    public Visualizer? Visualizer { get; init; }
    public bool ShowVisualizer { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}    /// <summary>
    /// Composes the entire terminal frame: top bar, tabs, station list, now-playing
    /// bars, spectrum visualizer, status line and keyboard hint bar. Each row is
    /// written via a Spectre recorder console so markup colors are baked into the
    /// final ANSI string.
    /// </summary>
    public static class MainLayout
    {
        private static readonly StringWriter Writer = new();
        private static readonly IAnsiConsole Recorder = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Writer),
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
        });

        private const int MinWidth = 56;
        private const int MinHeight = 16;

        // Emitted before every frame so no stale rows survive a resize or a frame
        // that was taller than the console window (otherwise scrolling would leave
        // duplicated lines above the cursor home position).
        public const string FramePrefix = "\x1b[2J\x1b[H"; // CLEAR ENTIRE SCREEN, cursor home

        public static string Render(UiSnapshot s)
        {
            int w = s.Width, h = s.Height;
            var theme = s.Theme;

            // A recorder console has no terminal to measure, so its profile defaults
            // to 80 columns and Spectre would word-wrap our lines mid-frame. We do
            // our own truncation, so tell the profile to never wrap.
            Recorder.Profile.Width = 4096;
            Recorder.Profile.Height = 4096;

            Writer.GetStringBuilder().Clear();
        if (w < MinWidth || h < MinHeight)
        {
            Recorder.MarkupLine($"[{theme.Warn}]Axplayer needs a larger terminal.[/]");
            Recorder.MarkupLine($"[{theme.Dim}]Current size: {w}x{h}  (minimum {MinWidth}x{MinHeight})[/]");
            return Writer.ToString();
        }

        bool searchActive = s.Searching;
        bool secondHelp = h >= 22;
        int fixedRows = 12 + (searchActive ? 1 : 0) + (secondHelp ? 1 : 0);
        int listRows = Math.Max(4, h - fixedRows);
        int content = w - 4; // usable width inside the │ │ borders

        // --- Top bar ---------------------------------------------------------
        var top = new System.Text.StringBuilder($"┌ [bold {theme.Title}]Axplayer v{Markup.Escape(s.Version)}[/]");
        top.Append($"  [{theme.Dim}]{s.Now:HH:mm:ss}[/]");
        if (s.SleepMinutesLeft is { } sleepLeft)
            top.Append($"  [{theme.Warn}]sleep {sleepLeft}m[/]");
        top.Append($"  [{s.ConnectionColor}]{Markup.Escape($"[{s.ConnectionLabel}]")}[/]");
        Recorder.MarkupLine(PadToWidth(top.ToString(), w - 2) + "┐");

        Recorder.MarkupLine(Divider(w, theme));

        // --- Tabs --------------------------------------------------------------
        string allTab = s.FavoritesView
            ? $"[{theme.Dim}]{Markup.Escape("[ All Stations ]")}[/]"
            : $"[bold {theme.Accent}]{Markup.Escape("[ All Stations ]")}[/]";
        string favTab = s.FavoritesView
            ? $"[bold {theme.Accent}]{Markup.Escape("[ Favorites ]")}[/]"
            : $"[{theme.Dim}]{Markup.Escape("[ Favorites ]")}[/]";
        Recorder.MarkupLine(Row($"{allTab}  {favTab}", content));

        if (searchActive && s.Prompt is not null)
            Recorder.MarkupLine(Row($"[bold {theme.Accent}]/[/] {Markup.Escape(Truncate(s.Prompt.Buffer, content - 6))}", content));

        // --- Station list / info panel ----------------------------------------
        if (s.ShowInfo)
        {
            int shown = Math.Min(listRows, s.InfoLines.Count);
            for (int i = 0; i < shown; i++)
                Recorder.MarkupLine(Row(s.InfoLines[i], content));
            for (int i = shown; i < listRows; i++)
                Recorder.MarkupLine(EmptyRow(w));
        }
        else if (s.Stations is not null)
        {
            var rows = StationListView.BuildRows(s.Stations, s.Selected, listRows, content, s.PlayingIndex, s.State, theme);
            foreach (var row in rows)
                Recorder.MarkupLine(Row(row, content));
            for (int i = rows.Count; i < listRows; i++)
                Recorder.MarkupLine(EmptyRow(w));
        }
        else
        {
            for (int i = 0; i < listRows; i++)
                Recorder.MarkupLine(EmptyRow(w));
        }

        Recorder.MarkupLine(Divider(w, theme));

        // --- Now playing -------------------------------------------------------
        Recorder.MarkupLine(Row(NowPlayingBar.BuildTitleLine(s.StationName, s.SongTitle, s.State, content, theme), content));

        // --- Stats ---------------------------------------------------------------
        Recorder.MarkupLine(Row(NowPlayingBar.BuildStatsLine(s.Volume, s.Muted, s.Bitrate, s.BufferPct, s.Recording, s.RecFile, content, theme), content));

        // --- Visualizer -----------------------------------------------------------
        if (s.ShowVisualizer && s.Visualizer is not null)
        {
            var bars = s.Visualizer.Render(w - 9); // bars + 3 gap cols + 2-col VU == content width
            var vu = s.Visualizer.RenderVuMeter();
            for (int r = 0; r < 3; r++)
                Recorder.MarkupLine(Row($"{bars[r]}   {vu[r]}", content));
        }
        else
        {
            for (int r = 0; r < 3; r++)
                Recorder.MarkupLine(EmptyRow(w));
        }

        // --- Status / input line ----------------------------------------------------
        if (s.Prompt is not null)
        {
            string cursorChar = s.PromptCursorOn ? "[white]█[/]" : " ";
            string before = Markup.Escape(s.Prompt.Buffer[..Math.Min(s.Prompt.Cursor, s.Prompt.Buffer.Length)]);
            string after = Markup.Escape(s.Prompt.Buffer[Math.Min(s.Prompt.Cursor, s.Prompt.Buffer.Length)..]);
            Recorder.MarkupLine(Row($"[bold {theme.Accent}]{Markup.Escape(s.Prompt.Prompt)}[/] {before}{cursorChar}{after}", content));
        }
        else
        {
            string status = s.Status.Length == 0 ? " " : s.Status;
            string color = s.StatusIsError ? theme.Err : theme.Dim;
            Recorder.MarkupLine(Row($"[{color}]{Markup.Escape(Truncate(status, content))}[/]", content));
        }

        Recorder.MarkupLine(Divider(w, theme));

        // --- Help -----------------------------------------------------------------
        Recorder.MarkupLine(Row(HelpLine1(theme), content));
        if (secondHelp)
            Recorder.MarkupLine(Row(HelpLine2(theme), content));

        Recorder.MarkupLine($"└" + new string('─', w - 2) + "┘");

        return Writer.ToString();
    }

    /// <summary>Wrap content in a bordered row, padded to the exact content width.</summary>
    private static string Row(string content, int contentWidth)
    {
        int pad = Math.Max(0, contentWidth - Markup.Remove(content).Length);
        return $"│ {content}" + new string(' ', pad) + " │";
    }

    private static string EmptyRow(int width) => "│" + new string(' ', width - 1) + "│";

    private static string HelpLine1(UiTheme t) =>
        $"[{t.Dim}][[P]]lay [[S]]top [[F]]av [[+/-]]Vol [[M]]ute [[Tab]]view [[Enter]]play [[R]]ec [[I]]nfo [[Q]]uit[/]";

    private static string HelpLine2(UiTheme t) =>
        $"[{t.Dim}][[N]]ew [[E]]dit [[D]]el [[Ctrl+D]]elAll | /search | Ctrl+R refresh[/]";

    /// <summary>Append a fill character so the markup line reaches exactly `width` visible cells.</summary>
    private static string PadToWidth(string markup, int width)
    {
        string plain = Markup.Remove(markup);
        int fillCount = Math.Max(0, width - plain.Length);
        return markup + new string('─', fillCount);
    }

    private static string Divider(int width, UiTheme theme) => $"├[{theme.Frame}]{new string('─', width - 2)}[/]┤";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
}
