using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Replaces FlareSolverrClient. Uses a real Chromium browser (Google Chrome when
/// available) to bypass Cloudflare JS challenges natively. Sessions map to
/// persistent browser contexts, so the cf_clearance cookie is reused across
/// requests in the same session just like FlareSolverr sessions did.
/// </summary>
public sealed class PlaywrightFetcher : IAsyncDisposable
{
    public sealed record PageResult(string FinalUrl, string Html);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";

    private readonly Logger _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser?    _browser;

    private readonly ConcurrentDictionary<string, IBrowserContext> _sessions = new();

    public PlaywrightFetcher(Logger log) => _log = log;

    // Always reachable — no external service required.
    public Task<bool> IsReachableAsync() => Task.FromResult(true);

    // -------------------------------------------------------------------------
    // Sessions  (persistent browser context = CF cookie cache, same as FlareSolverr)

    public async Task<string> CreateSessionAsync()
    {
        var ctx = await CreateContextAsync().ConfigureAwait(false);
        var id  = $"ps2tex-{Guid.NewGuid():N}"[..16];
        _sessions[id] = ctx;
        _log.Debug($"Playwright session created: {id}");
        return id;
    }

    public async Task DestroySessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var ctx))
        {
            try { await ctx.CloseAsync().ConfigureAwait(false); } catch { }
            _log.Debug($"Playwright session destroyed: {sessionId}");
        }
    }

    // -------------------------------------------------------------------------
    // Page fetch

    /// <summary>
    /// GET <paramref name="url"/> and extract the XenForo CSRF token from the
    /// <c>data-csrf</c> attribute on <c>&lt;html id="XF"&gt;</c>.
    /// XenForo 2.3+ stores the token there instead of in a hidden input.
    /// </summary>
    public async Task<(string CsrfToken, PageResult Page)> GetPageWithCsrfAsync(
        string  url,
        string? sessionId    = null,
        int     maxTimeoutMs = 60_000)
    {
        _log.Debug($"Playwright -> GET+CSRF {url}");
        var (ctx, owned) = await GetContextAsync(sessionId).ConfigureAwait(false);
        var page = await ctx.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = maxTimeoutMs,
            }).ConfigureAwait(false);

            await WaitForCloudflareAsync(page, maxTimeoutMs).ConfigureAwait(false);

            var csrf = await page.EvaluateAsync<string>(
                "() => document.getElementById('XF')?.dataset?.csrf ?? ''")
                .ConfigureAwait(false);

            var html = await page.ContentAsync().ConfigureAwait(false);
            return (csrf ?? "", new PageResult(page.Url, html));
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
            if (owned) await ctx.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<PageResult> GetPageAsync(
        string  url,
        string? sessionId    = null,
        int     maxTimeoutMs = 60_000)
    {
        _log.Debug($"Playwright -> GET {url}");
        var (ctx, owned) = await GetContextAsync(sessionId).ConfigureAwait(false);
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
            if (owned) await ctx.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// POST <paramref name="url"/> with form-urlencoded <paramref name="postData"/>.
    /// Runs fetch() inside the real browser page so it sends proper Origin/Referer
    /// headers that XenForo and Cloudflare expect from a same-origin request.
    /// </summary>
    public async Task<PageResult> PostPageAsync(
        string  url,
        string  postData,
        string? sessionId    = null,
        int     maxTimeoutMs = 60_000)
    {
        _log.Debug($"Playwright -> POST {url}");
        var (ctx, owned) = await GetContextAsync(sessionId).ConfigureAwait(false);
        var page = await ctx.NewPageAsync().ConfigureAwait(false);
        try
        {
            // Navigate to the domain so the page origin is gbatemp.net.
            // CF cookies are already in the context from the warm step, so
            // this navigation completes without triggering another challenge.
            await page.GotoAsync("https://gbatemp.net/search/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = maxTimeoutMs,
            }).ConfigureAwait(false);

            var json = await page.EvaluateAsync<string>(@"
                async ([targetUrl, body]) => {
                    const r = await fetch(targetUrl, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                        body,
                        redirect: 'follow',
                        credentials: 'include'
                    });
                    return JSON.stringify({ url: r.url, html: await r.text() });
                }", new object[] { url, postData }).ConfigureAwait(false);

            using var doc     = System.Text.Json.JsonDocument.Parse(json);
            var       root    = doc.RootElement;
            var       finalUrl = root.GetProperty("url").GetString() ?? url;
            var       html    = root.GetProperty("html").GetString() ?? "";
            return new PageResult(finalUrl, html);
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
            if (owned) await ctx.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Navigate to <paramref name="formPageUrl"/>, fill in the XenForo search
    /// form (including injecting any extra fields as hidden inputs), and press
    /// Enter so the browser submits the form natively.  The CSRF token is read
    /// from <c>data-csrf</c> on <c>&lt;html id="XF"&gt;</c> automatically.
    /// </summary>
    public async Task<PageResult> XFPostAsync(
        string                              formPageUrl,
        string                              postUrl,
        IReadOnlyDictionary<string, string> postFields,
        string?                             sessionId    = null,
        int                                 maxTimeoutMs = 60_000)
    {
        _log.Debug($"Playwright -> XFPost {formPageUrl}");
        var (ctx, owned) = await GetContextAsync(sessionId).ConfigureAwait(false);
        var page = await ctx.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.GotoAsync(formPageUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = maxTimeoutMs,
            }).ConfigureAwait(false);

            await WaitForCloudflareAsync(page, maxTimeoutMs).ConfigureAwait(false);

            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new() { Timeout = 15_000 }).ConfigureAwait(false);
            }
            catch { /* NetworkIdle not required — proceed. */ }

            // Wait for the main search input to be present (XenForo hydrates it via JS).
            await page.WaitForSelectorAsync("form input[name='keywords']",
                new PageWaitForSelectorOptions { Timeout = 30_000 }).ConfigureAwait(false);

            // Fill the form and call requestSubmit().  XenForo intercepts the submit
            // event in JS and does a window.location redirect (not a standard browser
            // form-POST navigation), so we use WaitForURLAsync which catches both full
            // navigations and history.pushState/location changes.
            var filled = await page.EvaluateAsync<bool>(@"
                (fields) => {
                    const allInputs = Array.from(document.querySelectorAll('input[name=""keywords""]'));
                    const input = allInputs.find(i => !i.closest('nav') && !i.closest('.p-nav') && !i.closest('.p-header'));
                    if (!input) return false;
                    input.value = fields.keywords;
                    input.dispatchEvent(new Event('input', {bubbles: true}));

                    const form = input.closest('form');
                    if (!form) return false;

                    for (const [k, v] of Object.entries(fields)) {
                        if (k === 'keywords') continue;
                        const inp  = document.createElement('input');
                        inp.type  = 'hidden';
                        inp.name  = k;
                        inp.value = v;
                        form.appendChild(inp);
                    }
                    form.requestSubmit();
                    return true;
                }", postFields).ConfigureAwait(false);

            if (!filled)
                throw new PlaywrightFetcherException("Could not locate the search form on the search page");

            // Wait for the URL to change from the search form to the results page.
            // GBAtemp search results live at /search/<id>/.
            await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex(@"/search/\d+/"),
                new PageWaitForURLOptions { Timeout = maxTimeoutMs }).ConfigureAwait(false);

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new() { Timeout = maxTimeoutMs }).ConfigureAwait(false);

            await WaitForCloudflareAsync(page, maxTimeoutMs).ConfigureAwait(false);

            return new PageResult(page.Url, await page.ContentAsync().ConfigureAwait(false));
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
            if (owned) await ctx.CloseAsync().ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // Internals

    private async Task<(IBrowserContext ctx, bool owned)> GetContextAsync(string? sessionId)
    {
        if (sessionId is not null && _sessions.TryGetValue(sessionId, out var named))
            return (named, false);

        // No session or unknown ID — spin up a temporary context.
        var temp = await CreateContextAsync().ConfigureAwait(false);
        return (temp, true);
    }

    private async Task<IBrowserContext> CreateContextAsync()
    {
        var browser = await GetBrowserAsync().ConfigureAwait(false);
        var ctx = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent    = UserAgent,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        }).ConfigureAwait(false);

        // Hide the webdriver flag that some bot-detection scripts check.
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

    // Wait for Cloudflare's "Just a moment…" interstitial to resolve.
    // With a real headed browser the JS challenge auto-solves in a few seconds.
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
        catch { /* Page had no CF challenge — this is fine. */ }

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
        foreach (var ctx in _sessions.Values)
            try { await ctx.CloseAsync().ConfigureAwait(false); } catch { }
        _sessions.Clear();

        if (_browser is not null)
            try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }

        _playwright?.Dispose();
    }
}

public sealed class PlaywrightFetcherException(string message) : Exception(message);
