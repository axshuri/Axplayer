namespace Axplayer.UI;

/// <summary>
/// A modal text-entry session rendered on the status line. Supports typing,
/// backspace, arrow-key cursor movement, Home/End, Enter to commit and
/// Escape to cancel — all without leaving the custom TUI renderer.
/// </summary>
public sealed class PromptSession
{
    public string Prompt { get; }
    public string Buffer { get; private set; }
    public bool Finished { get; private set; }
    public bool Cancelled { get; private set; }
    public int Cursor { get; private set; }

    public PromptSession(string prompt, string initial = "")
    {
        Prompt = prompt;
        Buffer = initial;
        Cursor = initial.Length;
    }

    /// <summary>Feed one key. Returns true once the session is finished (committed or cancelled).</summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Finished = true;
                return true;

            case ConsoleKey.Escape:
                Cancelled = true;
                Finished = true;
                return true;

            case ConsoleKey.Backspace:
                if (Cursor > 0)
                {
                    Buffer = Buffer.Remove(Cursor - 1, 1);
                    Cursor--;
                }
                return false;

            case ConsoleKey.Delete:
                if (Cursor < Buffer.Length)
                    Buffer = Buffer.Remove(Cursor, 1);
                return false;

            case ConsoleKey.LeftArrow:
                Cursor = Math.Max(0, Cursor - 1);
                return false;

            case ConsoleKey.RightArrow:
                Cursor = Math.Min(Buffer.Length, Cursor + 1);
                return false;

            case ConsoleKey.Home:
                Cursor = 0;
                return false;

            case ConsoleKey.End:
                Cursor = Buffer.Length;
                return false;

            default:
                if (key.KeyChar >= 32 && key.KeyChar != 127)
                {
                    Buffer = Buffer.Insert(Cursor, key.KeyChar.ToString());
                    Cursor++;
                }
                return false;
        }
    }

    public string Result => Buffer.Trim();
}
