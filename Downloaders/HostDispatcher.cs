using Ps2TextureGrabber.Models;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Tries each <see cref="DownloadLink"/> in order until one succeeds.
/// Returns the host name that ultimately served the file.
/// </summary>
public sealed class HostDispatcher
{
    private readonly Logger _log;

    public HostDispatcher(Logger log) => _log = log;

    public async Task<string> DownloadAsync(
        IReadOnlyList<DownloadLink>  links,
        string                       outFile,
        Action<long, long, int, string>? onProgress = null,
        CancellationToken            ct             = default)
    {
        if (links.Count == 0)
            throw new ArgumentException("No download links provided");

        Exception? lastEx = null;
        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            _log.Info($"Trying {link.Host}: {link.Url}");

            var downloader = GetDownloader(link.Host);
            try
            {
                await downloader.DownloadAsync(
                    link.Url,
                    outFile,
                    onProgress is null ? null
                        : (b, t, p) => onProgress(b, t, p, link.Host),
                    ct).ConfigureAwait(false);

                _log.Success($"Download complete via {link.Host}");
                return link.Host;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"  {link.Host} failed: {ex.Message}");
                lastEx = ex;
            }
        }

        throw new InvalidOperationException(
            $"All {links.Count} download link(s) failed. Last error: {lastEx?.Message}");
    }

    private IDownloader GetDownloader(string host) => host switch
    {
        "MEGA"      => new MegaDownloader(_log),
        "GDrive"    => new GoogleDriveDownloader(_log),
        "MediaFire" => new MediaFireDownloader(_log),
        "Yandex"    => new YandexDownloader(_log),
        _           => new DirectDownloader(_log),  // Archive, GitHub, HTTP
    };
}
