using System.Threading.Channels;
using Microsoft.Playwright;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Uses a real Chromium browser (Edge when available, then Chrome, then bundled)
/// to bypass Cloudflare JS challenges natively.
///
/// A small pool of browser contexts is shared by every page fetch. Each context
/// keeps its own Cloudflare clearance cookie, so a warmed context loads pages
/// almost instantly - while parallel fetches use separate contexts instead of
/// hammering one session (Cloudflare serialises same-session requests).
///
/// Fetches are also paced: bursting more than a couple of requests per second at
/// the Cloudflare edge trips its bot detection and lands the session in an
/// unsolvable challenge loop. A 4s spacing keeps the whole scrape safe.
/// </summary>
public sealed class PlaywrightFetcher : IAsyncDisposable
{
    public sealed record PageResult(string FinalUrl, string Html);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";

    /// <summary>Contexts in the pool; each one keeps its own CF clearance cookie.</summary>
    private const int ContextPoolSize = 3;

    /// <summary>Minimum spacing between page-fetch starts, in milliseconds.
    /// Empirically, the Cloudflare edge in front of GBAtemp serves unsolvable
    /// "Just a moment" challenges at higher request rates (~0.5/s) but passes
    /// everything at the old ~0.25 requests/s the tool has always used.</summary>
    private const long MinFetchIntervalMs = 4000;

    private readonly Logger        _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _pacer    = new(1, 1);
    private long _lastFetchTick;

    private IPlaywright? _playwright;
    private IBrowser?    _browser;

    private readonly List<IBrowserContext> _contexts = [];
    private readonly Channel<IBrowserContext> _freeContexts =
        Channel.CreateBounded<IBrowserContext>(ContextPoolSize);

    public PlaywrightFetcher(Logger log) => _log = log;

    // -------------------------------------------------------------------------
    // Page fetch

    public async Task<PageResult> GetPageAsync(string url, int maxTimeoutMs = 30_000)
    {
        await PaceAsync().ConfigureAwait(false);

        _log.Debug($"Playwright -> GET {url}");
        var ctx  = await AcquireContextAsync().ConfigureAwait(false);
        var page = await ctx.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = maxTimeoutMs,
            }).ConfigureAwait(false);

            await WaitForCloudflareAsync(page, maxTimeoutMs).ConfigureAwait(false);
            return new PageResult(page.Url, await page.ContentAsync().ConfigureAwait(false));
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
            ReleaseContext(ctx);
        }
    }

    // -------------------------------------------------------------------------
    // Internals

    /// <summary>Spaces fetch starts at least MinFetchIntervalMs apart. The very first
    /// call passes immediately.</summary>
    private async Task PaceAsync()
    {
        await _pacer.WaitAsync().ConfigureAwait(false);
        try
        {
            var now  = Environment.TickCount64;
            var wait = MinFetchIntervalMs - (now - Interlocked.Read(ref _lastFetchTick));
            if (wait > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(wait)).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastFetchTick, Environment.TickCount64);
        }
        finally
        {
            _pacer.Release();
        }
    }

    /// <summary>Takes a free context, creating a new one (up to the pool limit) when
    /// none is free. Creation is serialised; callers only ever queue on the channel.</summary>
    private async Task<IBrowserContext> AcquireContextAsync()
    {
        if (_freeContexts.Reader.TryRead(out var free)) return free;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_freeContexts.Reader.TryRead(out free)) return free;

            if (_contexts.Count < ContextPoolSize)
            {
                var browser = _browser ?? await LaunchBrowserAsync().ConfigureAwait(false);
                var ctx = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent    = UserAgent,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                }).ConfigureAwait(false);

                // Suppress the webdriver flag that bot-detection scripts check.
                await ctx.AddInitScriptAsync(
                    "Object.defineProperty(navigator, 'webdriver', { get: () => undefined })")
                    .ConfigureAwait(false);

                _contexts.Add(ctx);
                return ctx;
            }
        }
        finally
        {
            _initLock.Release();
        }

        // Pool is full: wait for whichever context is returned first.
        return await _freeContexts.Reader.ReadAsync().ConfigureAwait(false);
    }

    private void ReleaseContext(IBrowserContext ctx) => _freeContexts.Writer.TryWrite(ctx);

    private async Task<IBrowser> LaunchBrowserAsync()
    {
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);

        var commonArgs = new[]
        {
            "--disable-blink-features=AutomationControlled",
            "--no-sandbox",
            "--disable-dev-shm-usage",
        };

        // Prefer Edge (pre-installed on Windows 11), then Chrome, then bundled Chromium.
        foreach (var (channel, label) in new[] { ("msedge", "Edge"), ("chrome", "Chrome") })
        {
            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel  = channel,
                    Headless = true,
                    Args     = commonArgs,
                }).ConfigureAwait(false);
                _log.Debug($"Playwright: using {label}");
                return _browser;
            }
            catch { }
        }

        _log.Warn("Edge/Chrome not found — falling back to bundled Chromium (may not bypass Cloudflare)");
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args     = commonArgs,
        }).ConfigureAwait(false);
        return _browser;
    }

    // Waits for Cloudflare's "Just a moment…" interstitial to resolve. Once the title
    // changes, the browser is either past the challenge or there never was one; a
    // DOMContentLoaded wait then covers the post-challenge reload.
    //
    // Deliberately no NetworkIdle wait: forum pages keep async requests alive for a
    // long time and would stall every fetch for the full idle timeout.
    private static async Task WaitForCloudflareAsync(IPage page, int maxTimeoutMs)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => !document.title.includes('Just a moment')",
                null,
                new PageWaitForFunctionOptions { Timeout = maxTimeoutMs })
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new PlaywrightFetcherException(
                $"Cloudflare challenge did not resolve within {maxTimeoutMs / 1000}s — the browser may have been detected.");
        }
        catch { /* No CF challenge — this is fine. */ }

        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = 10_000 })
                .ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts)
            try { await ctx.CloseAsync().ConfigureAwait(false); } catch { }
        if (_browser is not null)
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }

        _playwright?.Dispose();
    }
}

public sealed class PlaywrightFetcherException(string message) : Exception(message);
