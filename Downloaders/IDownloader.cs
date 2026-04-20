namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Contract for a per-host download handler.
/// Implementations throw on failure so the caller can try the next link.
/// </summary>
public interface IDownloader
{
    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="outFile"/>.
    /// Reports progress via <paramref name="onProgress"/> (bytesRead, totalBytes, pct 0-100).
    /// Throws on any failure.
    /// </summary>
    Task DownloadAsync(
        string                             url,
        string                             outFile,
        Action<long, long, int>?           onProgress = null,
        CancellationToken                  ct         = default);
}
