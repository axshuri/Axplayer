using System.Runtime.InteropServices;
using System.Text;

namespace Axplayer.UI;

/// <summary>
/// Windows console plumbing: enables VT (ANSI) processing so escape sequences
/// work in legacy conhost, switches the output encoding to UTF-8 for Unicode
/// block characters, hides the cursor, and disables QuickEdit mode (which can
/// freeze the app when the user clicks the window).
/// </summary>
internal static class Terminal
{
    private const int StdOutputHandle = -11;
    private const int StdInputHandle = -10;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableQuickEditMode = 0x0040;

    public static bool IsInteractive =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static void Setup()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (IsInteractive)
        {
            EnableVtProcessing();
            DisableQuickEdit();
            Console.CursorVisible = false;
        }
    }

    public static void Restore()
    {
        try { Console.CursorVisible = true; } catch { /* redirected console */ }
    }

    private static void EnableVtProcessing()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (!GetConsoleMode(handle, out uint mode)) return;
            SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { /* non-Windows or no console; ignore */ }
    }

    private static void DisableQuickEdit()
    {
        try
        {
            var handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (!GetConsoleMode(handle, out uint mode)) return;
            // Keep extended flags but clear QuickEdit so mouse clicks don't pause the app.
            SetConsoleMode(handle, (mode & ~EnableQuickEditMode) | EnableExtendedFlags);
        }
        catch { /* ignore */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
