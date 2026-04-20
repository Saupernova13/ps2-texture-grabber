using System.Diagnostics;
using System.IO.Compression;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Worker;

/// <summary>
/// Extracts archives to a target directory.
/// Supports .zip (built-in), .7z and .rar (via 7z.exe).
/// </summary>
public sealed class ArchiveExtractor
{
    private readonly Logger _log;
    public ArchiveExtractor(Logger log) => _log = log;

    public void Extract(string archivePath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        _log.Info($"Extracting ({ext}): {archivePath} -> {outDir}");

        if (ext == ".zip")
        {
            ZipFile.ExtractToDirectory(archivePath, outDir, overwriteFiles: true);
            return;
        }

        // .7z / .rar — delegate to 7z.exe
        var sevenZip = Find7z()
            ?? throw new FileNotFoundException(
                $"7-Zip (7z.exe) not found and archive is '{ext}'. " +
                "Install via: winget install 7zip.7zip");

        var psi = new ProcessStartInfo
        {
            FileName         = sevenZip,
            Arguments        = $"x \"-o{outDir}\" -y \"{archivePath}\"",
            CreateNoWindow   = true,
            UseShellExecute  = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 7z.exe");
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"7z.exe exited with code {proc.ExitCode}");
    }

    /// <summary>
    /// Detects the archive format from its file header magic bytes,
    /// and renames the file to the correct extension if needed.
    /// Returns the (possibly new) file path.
    /// </summary>
    public static string SniffAndRename(string path)
    {
        var bytes = new byte[6];
        using (var fs = File.OpenRead(path))
            _ = fs.Read(bytes, 0, 6);

        var sig = BitConverter.ToString(bytes).Replace("-", "");
        string ext;
        if      (sig.StartsWith("504B03"))     ext = ".zip";
        else if (sig.StartsWith("377ABCAF27")) ext = ".7z";
        else if (sig.StartsWith("526172211A")) ext = ".rar";
        else                                   ext = ".zip";  // best-guess fallback

        var current = Path.GetExtension(path);
        if (string.Equals(current, ext, StringComparison.OrdinalIgnoreCase))
            return path;

        var newPath = Path.ChangeExtension(path, ext);
        File.Move(path, newPath, overwrite: true);
        return newPath;
    }

    // -------------------------------------------------------------------------
    // Texture root detection

    /// <summary>
    /// Finds the folder inside <paramref name="extractedDir"/> whose contents
    /// should map to textures/{SERIAL}/replacements/.
    ///
    /// Priority:
    ///   1. A folder named "replacements" (any depth) — most common layout.
    ///   2. Longest common ancestor of all .png files.
    /// </summary>
    public static string FindTextureRoot(string extractedDir)
    {
        // Priority 1: explicit "replacements" folder
        var replace = Directory
            .EnumerateDirectories(extractedDir, "replacements",
                SearchOption.AllDirectories)
            .OrderBy(d => d.Length)
            .FirstOrDefault();
        if (replace is not null) return replace;

        // Priority 2: longest common path of all PNG files
        var pngs = Directory.EnumerateFiles(extractedDir, "*.png",
            SearchOption.AllDirectories).ToList();
        if (pngs.Count == 0)
            throw new InvalidOperationException(
                $"No PNG files found in extracted archive at: {extractedDir}");

        var dirs  = pngs.Select(p => Path.GetDirectoryName(p)!).Distinct().ToList();
        if (dirs.Count == 1) return dirs[0];

        return LongestCommonPath(dirs);
    }

    private static string LongestCommonPath(List<string> paths)
    {
        var split = paths.Select(p => p.Split(Path.DirectorySeparatorChar)).ToList();
        int min   = split.Min(s => s.Length);
        var common = new List<string>();
        for (int i = 0; i < min; i++)
        {
            var seg = split[0][i];
            if (split.All(s => s[i] == seg))
                common.Add(seg);
            else
                break;
        }
        return string.Join(Path.DirectorySeparatorChar, common);
    }

    // -------------------------------------------------------------------------
    // File installation

    /// <summary>
    /// Copies all files from <paramref name="sourceRoot"/> to <paramref name="targetRoot"/>,
    /// preserving relative paths.  Returns the file count.
    /// </summary>
    public int InstallFiles(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        int copied = 0;

        foreach (var srcFile in Directory.EnumerateFiles(sourceRoot,
            "*", SearchOption.AllDirectories))
        {
            var rel     = Path.GetRelativePath(sourceRoot, srcFile);
            var dst     = Path.Combine(targetRoot, rel);
            var dstDir  = Path.GetDirectoryName(dst)!;
            Directory.CreateDirectory(dstDir);
            File.Copy(srcFile, dst, overwrite: true);
            copied++;
        }

        _log.Success($"Copied {copied} file(s) to {targetRoot}");
        return copied;
    }

    // ---- helpers ----
    private static string? Find7z()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                            .Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, "7z.exe");
            if (File.Exists(full)) return full;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "7-Zip", "7z.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "7-Zip", "7z.exe"),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
