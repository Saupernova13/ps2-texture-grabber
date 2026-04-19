using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Thin async wrapper over the FlareSolverr HTTP API.
/// One instance per application lifetime; reuse the HttpClient.
///
/// Protocol:
///   POST /v1 with JSON body { cmd, url, maxTimeout, session? }
///   Response: { status: "ok"|"error", solution: { url, response, cookies, userAgent, status } }
/// </summary>
public sealed class FlareSolverrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;
    private readonly Logger     _log;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public FlareSolverrClient(string baseUrl, Logger log)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _log     = log;
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // -------------------------------------------------------------------------
    // Health check

    public async Task<bool> IsReachableAsync()
    {
        try
        {
            // The /v1 endpoint requires POST; probe the root instead.
            var root = _baseUrl.Replace("/v1", "/").TrimEnd('/') + "/";
            var resp = await _http.GetAsync(root).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // -------------------------------------------------------------------------
    // Sessions  (Cloudflare cookies are shared across requests within a session)

    public async Task<string> CreateSessionAsync()
    {
        var sessionId = $"ps2tex-{Guid.NewGuid():N}"[..16];
        try
        {
            var payload = Serialize(new { cmd = "sessions.create", session = sessionId });
            var node    = await PostRawAsync(payload).ConfigureAwait(false);
            if (node?["status"]?.GetValue<string>() == "ok")
                return sessionId;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not create FlareSolverr session: {ex.Message}");
        }
        return sessionId; // best-effort: proceed without a confirmed session
    }

    public async Task DestroySessionAsync(string sessionId)
    {
        try
        {
            var payload = Serialize(new { cmd = "sessions.destroy", session = sessionId });
            await PostRawAsync(payload).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    // -------------------------------------------------------------------------
    // Page fetch

    public sealed record PageResult(string FinalUrl, string Html);

    /// <summary>
    /// GET <paramref name="url"/> via FlareSolverr (optionally in a named session).
    /// Throws <see cref="FlareSolverrException"/> on API-level failures.
    /// </summary>
    public async Task<PageResult> GetPageAsync(
        string  url,
        string? sessionId      = null,
        int     maxTimeoutMs   = 60_000)
    {
        _log.Debug($"FlareSolverr -> GET {url}");

        var payload = Serialize(new
        {
            cmd        = "request.get",
            url,
            maxTimeout = maxTimeoutMs,
            session    = sessionId
        });

        var node = await PostRawAsync(payload).ConfigureAwait(false)
                   ?? throw new FlareSolverrException("FlareSolverr returned null response");

        var status = node["status"]?.GetValue<string>();
        if (status != "ok")
        {
            var msg = node["message"]?.GetValue<string>() ?? "unknown error";
            throw new FlareSolverrException($"FlareSolverr status '{status}': {msg}");
        }

        var solution = node["solution"]
            ?? throw new FlareSolverrException("FlareSolverr response is missing 'solution'");

        var html     = solution["response"]?.GetValue<string>()
            ?? throw new FlareSolverrException("FlareSolverr solution is missing 'response'");
        var finalUrl = solution["url"]?.GetValue<string>() ?? url;

        return new PageResult(finalUrl, html);
    }

    // -------------------------------------------------------------------------
    // Internals

    private async Task<JsonNode?> PostRawAsync(string jsonBody)
    {
        using var content = new StringContent(
            jsonBody, System.Text.Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(_baseUrl, content).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(body);
    }

    private static string Serialize(object obj)
        => JsonSerializer.Serialize(obj, _jsonOpts);

    public void Dispose() => _http.Dispose();
}

public sealed class FlareSolverrException(string message) : Exception(message);
