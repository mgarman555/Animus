namespace GameAssetExplorer.Core.Services;

/// <summary>
/// App-wide file logger. Writes to %AppData%\GameAssetExplorer\logs\app.log.
/// Thread-safe; new sessions append. Old sessions are demarcated with a banner.
/// </summary>
public static class Log
{
    private static readonly object _lock = new();
    private static readonly string _logDir;
    private static readonly string _logPath;

    static Log()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameAssetExplorer", "logs");
        Directory.CreateDirectory(_logDir);
        _logPath = Path.Combine(_logDir, "app.log");

        // Trim if log gets huge (>5 MB) — keep last ~1 MB
        try
        {
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024)
            {
                var keep = File.ReadAllText(_logPath);
                File.WriteAllText(_logPath, keep[(keep.Length - 1_000_000)..]);
            }
        }
        catch { }

        // Session banner
        WriteRaw($"\n========== Session start  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ==========\n");
    }

    /// <summary>Folder where log files live. Open this in Explorer with the toolbar button.</summary>
    public static string LogFolder => _logDir;

    /// <summary>Path to the current log file.</summary>
    public static string LogFile => _logPath;

    /// <summary>Fired for every line written. Subscribers must marshal to UI thread themselves.</summary>
    public static event Action<string, string>? OnLine;   // (level, formattedLine)

    public static void Info (string msg)            => Write("INFO ", msg);
    public static void Warn (string msg)            => Write("WARN ", msg);
    public static void Error(string msg)            => Write("ERROR", msg);
    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg}\n  {ex.GetType().Name}: {ex.Message}\n  {ex.StackTrace}");
    public static void Error(Exception ex)          => Write("ERROR", $"{ex.GetType().Name}: {ex.Message}\n  {ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        var bare = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        WriteRaw(bare + "\n");
        try { Console.WriteLine(bare); } catch { }
        try { OnLine?.Invoke(level.Trim(), bare); } catch { }
    }

    private static void WriteRaw(string text)
    {
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, text); } catch { /* don't crash on log failure */ }
        }
    }
}
