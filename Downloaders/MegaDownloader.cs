using CG.Web.MegaApiClient;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads from MEGA using MegaApiClient (no MEGAcmd required).
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
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        var client = new MegaApiClient();
        await client.LoginAnonymousAsync().ConfigureAwait(false);

        var node       = await client.GetNodeFromLinkAsync(new Uri(url)).ConfigureAwait(false);
        var totalBytes = node.Size;

        IProgress<double>? progress = onProgress is null ? null
            : new Progress<double>(d =>
            {
                var bytes = (long)(d * totalBytes);
                var pct   = Math.Min(100, (int)(d * 100));
                onProgress(bytes, totalBytes, pct);
            });

        _log.Debug($"MEGA node: {node.Name} ({totalBytes / 1_048_576.0:F1} MB)");
        await client.DownloadFileAsync(node, outFile, progress, ct).ConfigureAwait(false);

        _log.Success($"MEGA download complete: {outFile}");
    }
}
