namespace Ps2TextureGrabber.Services;

/// <summary>
/// Structured console logger.  Optionally mirrors output to a log file.
/// Thread-safe for the file-append path (best-effort; no lock needed since
/// File.AppendAllText is atomic at the OS level for short lines).
/// </summary>
public sealed class Logger
{
    private string? _logFile;

    public void SetLogFile(string path)
    {
        _logFile = path;
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
    }

    public void Log(string message, string level = "INFO")
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line      = $"[{timestamp}] [{level}] {message}";

        var prev  = Console.ForegroundColor;
        Console.ForegroundColor = level switch
        {
            "SUCCESS" => ConsoleColor.Green,
            "WARN"    => ConsoleColor.Yellow,
            "ERROR"   => ConsoleColor.Red,
            "DEBUG"   => ConsoleColor.DarkGray,
            _         => ConsoleColor.Cyan,  // INFO
        };
        Console.WriteLine(line);
        Console.ForegroundColor = prev;

        if (_logFile is not null)
        {
            try { File.AppendAllText(_logFile, line + Environment.NewLine); }
            catch { /* logging must not throw */ }
        }
    }

    public void Info(string msg)    => Log(msg, "INFO");
    public void Success(string msg) => Log(msg, "SUCCESS");
    public void Warn(string msg)    => Log(msg, "WARN");
    public void Error(string msg)   => Log(msg, "ERROR");
    public void Debug(string msg)   => Log(msg, "DEBUG");
}
