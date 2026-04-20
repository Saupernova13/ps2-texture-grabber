using System.Text.RegularExpressions;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads from MediaFire.
/// MediaFire serves a HTML page with a direct download button; we extract the
/// actual file URL from the page source and then stream it.
/// </summary>
public sealed partial class MediaFireDownloader : IDownloader
{
    private readonly Logger _log;
    public MediaFireDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                   url,
        string                   outFile,
        Action<long, long, int>? onProgress = null,
        CancellationToken        ct         = default)
    {
        _log.Info($"MediaFire download: {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        var page    = await client.GetStringAsync(url, ct).ConfigureAwait(false);
        var directM = DirectLinkRx().Match(page);
        if (!directM.Success)
            throw new InvalidOperationException(
                $"Could not find direct download link on MediaFire page: {url}");

        var directUrl = directM.Value;
        _log.Debug($"MediaFire direct URL: {directUrl}");

        using var resp = await client.GetAsync(
            directUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await HttpStreamer.StreamToFileAsync(resp, outFile, onProgress, ct).ConfigureAwait(false);
    }

    // MediaFire embeds the direct URL in the page as download.mediafire.com/...
    // Attribute order on #downloadButton changes; match the URL directly instead.
    [GeneratedRegex(@"https?://download\d*\.mediafire\.com/[A-Za-z0-9._/\-?=&%+]+")]
    private static partial Regex DirectLinkRx();
}
