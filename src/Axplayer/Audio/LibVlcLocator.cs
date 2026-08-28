namespace Axplayer.Audio;

/// <summary>
/// Finds the directory containing the native libvlc binaries. Straightforward
/// in a normal publish (DLLs sit in libvlc/win-x64 next to the exe); for
/// single-file self-contained publishes the runtime extracts the bundled
/// libvlc DLLs to %TEMP%/.net/&lt;exe&gt;/&lt;hash&gt;/, which we also search.
/// </summary>
internal static class LibVlcLocator
{
    public static string? FindDirectory()
    {
        var candidates = new List<string>();

        // 1. Single-file self-extract directories (covers PublishSingleFile=true).
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDirs)
        {
            foreach (var dir in nativeDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(dir);
        }

        // 2. Normal framework-dependent / folder publish layout.
        candidates.Add(AppContext.BaseDirectory);

        // 3. Directory of the running process (safety net).
        if (Environment.ProcessPath is string exe)
            candidates.Add(Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);

        // 4. VideoLAN.LibVLC.Windows layout: libvlc/win-<arch>/ next to the exe.
        foreach (var baseDir in candidates.ToList())
        {
            foreach (var arch in ArchNames)
                candidates.Add(Path.Combine(baseDir, "libvlc", arch));
        }

        // 5. Single-file self-extract layout: %TEMP%/.net/<exeName>/<hash>/libvlc/win-<arch>/.
        try
        {
            if (Environment.ProcessPath is string processPath)
            {
                var appName = Path.GetFileNameWithoutExtension(processPath);
                var extractRoot = Path.Combine(Path.GetTempPath(), ".net", appName);
                if (Directory.Exists(extractRoot))
                {
                    foreach (var hashDir in Directory.GetDirectories(extractRoot))
                        foreach (var arch in ArchNames)
                            candidates.Add(Path.Combine(hashDir, "libvlc", arch));
                }
            }
        }
        catch { /* temp dir unavailable */ }

        // Prefer the most recently touched candidate (newest extraction hash wins).
        var found = candidates
            .Where(dir => SafeExists(Path.Combine(dir, "libvlc.dll")))
            .OrderByDescending(dir => SafeLastWrite(Path.Combine(dir, "libvlc.dll")))
            .FirstOrDefault();

        return found;
    }

    private static readonly string[] ArchNames = { "win-x64", "win-x86", "win-arm64" };

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }
}
