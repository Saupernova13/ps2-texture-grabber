namespace Ps2TextureGrabber.Downloaders;

internal static class HttpStreamer
{
    private const int BufferSize = 81_920;

    internal static async Task StreamToFileAsync(
        HttpResponseMessage      resp,
        string                   outFile,
        Action<long, long, int>? onProgress,
        CancellationToken        ct)
    {
        var total     = resp.Content.Headers.ContentLength ?? -1;
        long read     = 0;
        int  lastPct  = -1;
        long lastTick = Environment.TickCount64;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Open(outFile, FileMode.Create, FileAccess.Write);

        var buf = new byte[BufferSize];
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;

            if (onProgress is not null && total > 0)
            {
                int  pct = (int)(read * 100L / total);
                long now = Environment.TickCount64;
                if (pct != lastPct && now - lastTick >= 1000)
                {
                    onProgress(read, total, pct);
                    lastPct  = pct;
                    lastTick = now;
                }
            }
        }

        if (onProgress is not null && total > 0)
            onProgress(read, total, 100);
    }
}
