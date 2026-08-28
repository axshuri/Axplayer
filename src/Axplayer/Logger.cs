namespace Axplayer;

/// <summary>
/// Tiny file logger. Writes timestamped lines to logs/axplayer_YYYY-MM-DD.txt.
/// Used for debugging stream/network issues without cluttering the UI.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string _file = "";

    /// <summary>Ensure the log file exists (called once at startup).</summary>
    public static void Initialize()
    {
        lock (Gate)
        {
            _file = Path.Combine(AppPaths.LogsDir, $"axplayer_{DateTime.Now:yyyy-MM-dd}.txt");
            try { File.AppendAllText(_file, $"[{DateTime.Now:HH:mm:ss}] --- Axplayer session started ---{Environment.NewLine}"); }
            catch { /* logging must never crash the app */ }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(_file)) return;
            try
            {
                File.AppendAllText(_file, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }
    }
}
