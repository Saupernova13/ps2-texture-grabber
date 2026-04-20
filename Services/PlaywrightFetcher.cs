using Microsoft.Playwright;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Uses a real Chromium browser (Edge when available, then Chrome, then bundled)
/// to bypass Cloudflare JS challenges natively.
/// </summary>
public sealed class PlaywrightFetcher : IAsyncDisposable
{
    public sealed record PageResult(string FinalUrl, string Html);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";

    private readonly Logger      _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser?    _browser;

    public PlaywrightFetcher(Logger log) => _log = log;

    // -------------------------------------------------------------------------
    // Page fetch

    public async Task<PageResult> GetPageAsync(string url, int maxTimeoutMs = 60_000)
    {
        _log.Debug($"Playwright -> GET {url}");
        var ctx  = await CreateContextAsync().ConfigureAwait(false);
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
            await ctx.CloseAsync().ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // Internals

    private async Task<IBrowserContext> CreateContextAsync()
    {
        var browser = await GetBrowserAsync().ConfigureAwait(false);
        var ctx = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent    = UserAgent,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        }).ConfigureAwait(false);

        // Suppress the webdriver flag that bot-detection scripts check.
        await ctx.AddInitScriptAsync(
            "Object.defineProperty(navigator, 'webdriver', { get: () => undefined })")
            .ConfigureAwait(false);

        return ctx;
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null) return _browser;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null) return _browser;

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
        }
        finally
        {
            _initLock.Release();
        }

        return _browser;
    }

    // Waits for Cloudflare's "Just a moment…" interstitial to resolve.
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
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 10_000 })
                .ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }

        _playwright?.Dispose();
    }
}

public sealed class PlaywrightFetcherException(string message) : Exception(message);
