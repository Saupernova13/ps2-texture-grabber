using System.Diagnostics;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads via MEGAcmd's mega-get.bat.
/// Requires MEGAcmd installed at %ProgramFiles%\MEGAcmd or %LocalAppData%\MEGAcmd.
/// </summary>
public sealed class MegaDownloader : IDownloader
{
    private readonly Logger _log;
    public MegaDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                   url,
        string                   outFile,
        Action<long, long, int>? onProgress = null,
        CancellationToken        ct         = default)
    {
        _log.Info($"MEGA download: {url}");
        var dir = Path.GetDirectoryName(outFile)!;
        Directory.CreateDirectory(dir);

        var megaExe = FindMegaGet()
            ?? throw new FileNotFoundException(
                "MEGAcmd (mega-get.bat) not found. " +
                "Download from https://mega.io/cmd and install it.");

        var psi = new ProcessStartInfo
        {
            FileName         = megaExe,
            Arguments        = $"\"{url}\" \"{dir}\"",
            CreateNoWindow   = true,
            UseShellExecute  = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start mega-get process");

        // mega-get doesn't stream byte progress; just wait for completion.
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"mega-get exited with code {proc.ExitCode}");

        // mega-get names the file after the remote filename; find the newest file in dir.
        var newest = new DirectoryInfo(dir)
            .GetFiles()
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"MEGA download produced no file in {dir}");

        if (!string.Equals(newest.FullName, outFile, StringComparison.OrdinalIgnoreCase))
            File.Move(newest.FullName, outFile, overwrite: true);
    }

    private static string? FindMegaGet()
    {
        // Try PATH first
        foreach (var name in new[] { "mega-get.bat", "mega-get" })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                                .Split(Path.PathSeparator))
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }

        // Known install locations
        foreach (var candidate in new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "MEGAcmd", "mega-get.bat"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MEGAcmd", "mega-get.bat"),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
