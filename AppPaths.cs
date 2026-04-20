namespace Ps2TextureGrabber;

/// <summary>
/// All filesystem paths the app uses, computed from the exe location.
/// Keeps path logic in one place so nothing is ever guessed inline.
/// </summary>
public static class AppPaths
{
    // Root is wherever the exe lives (single-file publish places it in the user's chosen dir).
    public static readonly string AppDir  = AppContext.BaseDirectory;
    public static readonly string DataDir = Path.Combine(AppDir, "data");
    public static readonly string CacheDir      = Path.Combine(DataDir, "cache");
    public static readonly string WikiCacheDir        = Path.Combine(CacheDir, "wiki");
    public static readonly string GameDbCache         = Path.Combine(CacheDir, "gamedb.json");
    public static readonly string ArchiveIndexCache   = Path.Combine(CacheDir, "archive-index.json");
    public static readonly string JobsDir          = Path.Combine(DataDir, "jobs");
    public static readonly string SettingsFile     = Path.Combine(AppDir, ".settings");
    public static readonly string MissingLinksDir  = Path.Combine(CacheDir, "missing-links");

    public static string JobStateFile(string jobId) => Path.Combine(JobsDir, $"{jobId}.json");
    public static string JobLogFile(string jobId)   => Path.Combine(JobsDir, $"{jobId}.log");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WikiCacheDir);
        Directory.CreateDirectory(JobsDir);
        Directory.CreateDirectory(MissingLinksDir);
    }
}
