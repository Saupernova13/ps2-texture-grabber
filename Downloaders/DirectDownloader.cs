using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// HTTP streaming download with progress reporting.
/// Handles archive.org /details/ -> /download/ normalisation.
/// </summary>
public sealed class DirectDownloader : IDownloader
{
    private readonly Logger _log;
    public DirectDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                    url,
        string                    outFile,
        Action<long, long, int>?  onProgress = null,
        CancellationToken         ct         = default)
    {
        var finalUrl = url.Contains("archive.org/details/")
            ? url.Replace("/details/", "/download/")
            : url;

        _log.Info($"Direct HTTP download: {finalUrl}");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        using var client = new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        using var resp = await client.GetAsync(
            finalUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await HttpStreamer.StreamToFileAsync(resp, outFile, onProgress, ct).ConfigureAwait(false);

        if (!File.Exists(outFile) || new FileInfo(outFile).Length == 0)
            throw new InvalidOperationException($"Download produced empty file: {outFile}");
    }
}
