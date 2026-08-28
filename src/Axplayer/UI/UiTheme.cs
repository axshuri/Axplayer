namespace Axplayer.UI;

/// <summary>Color palette used by every rendered line. Two built-in themes.</summary>
public sealed class UiTheme
{
    public string Accent { get; init; } = "cyan";
    public string Highlight { get; init; } = "yellow";
    public string Dim { get; init; } = "grey";
    public string Ok { get; init; } = "green";
    public string Warn { get; init; } = "yellow";
    public string Err { get; init; } = "red";
    public string Playing { get; init; } = "green";
    public string Paused { get; init; } = "yellow";
    public string Title { get; init; } = "bold aqua";
    public string Frame { get; init; } = "grey";

    public static UiTheme Dark { get; } = new();
    public static UiTheme Light { get; } = new()
    {
        Accent = "blue",
        Highlight = "magenta",
        Dim = "grey",
        Ok = "green",
        Warn = "darkorange3",
        Err = "red",
        Playing = "green",
        Paused = "darkorange3",
        Title = "bold blue",
        Frame = "grey",
    };

    public static UiTheme For(string themeName) => themeName.Equals("light", StringComparison.OrdinalIgnoreCase) ? Light : Dark;
}
