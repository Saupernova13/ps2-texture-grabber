using System.Text.Json;
using Ps2TextureGrabber;
using Ps2TextureGrabber.Models;
using Ps2TextureGrabber.Services;
using Ps2TextureGrabber.Worker;

// ============================================================================
//  ps2tex — top-level argument dispatch
//
//  Usage:
//    ps2tex --query "Dragon Ball Z Budokai Tenkaichi 3" [options]
//    ps2tex --list [options]
//    ps2tex --status <jobId> [--json]
//    ps2tex worker --job-file <path>       ← internal; spawned by JobService
//
//  Options:
//    --textures-path <path>
//    --gamesettings-path <path>
//    --game-index <path>
//    --node-id <int>
//    --interactive
//    --json
// ============================================================================

AppPaths.EnsureAll();

var cli = Args.Parse(Environment.GetCommandLineArgs()[1..]);

// ---- worker subcommand (internal, no user-facing output) ----
if (cli.Command == "worker")
{
    if (string.IsNullOrEmpty(cli.JobFile))
    {
        Console.Error.WriteLine("worker: --job-file required");
        return 1;
    }
    await WorkerRunner.RunAsync(cli.JobFile);
    return 0;
}

// ---- load .settings; command-line args always win ----
var settings = SettingsFile.Load(AppPaths.SettingsFile);
string Get(string key, string? argVal, string? defaultVal = null)
    => argVal ?? settings.GetValueOrDefault(key) ?? defaultVal ?? string.Empty;

var texturesPath     = Get("TexturesPath",    cli.TexturesPath,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EmuDeck", "Emulators", "PCSX2-Qt", "textures"));
var gamesettingsPath = Get("GamesettingsPath", cli.GamesettingsPath,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EmuDeck", "Emulators", "PCSX2-Qt", "gamesettings"));
var gameIndexPath    = Get("GameIndexPath",    cli.GameIndexPath,
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EmuDeck", "Emulators", "PCSX2-Qt", "resources", "GameIndex.yaml"));
var nodeId           = cli.NodeId > 0 ? cli.NodeId
    : (settings.TryGetValue("GbatempNodeId", out var nStr) && int.TryParse(nStr, out var n) ? n : 549);

var log = new Logger();

// ============================================================================
//  --status
// ============================================================================
if (!string.IsNullOrEmpty(cli.Status))
{
    var jobs = new JobService(log, AppPaths.JobsDir);
    if (cli.Json)
        Console.WriteLine(jobs.ReadStatusJson(cli.Status));
    else
        jobs.PrintStatus(cli.Status);
    return 0;
}

// ============================================================================
//  --list
// ============================================================================
if (cli.List)
{
    var dbSvc = new GameDbService(log);
    var pcsx2 = new Pcsx2Service(log);
    var db    = dbSvc.Load(gameIndexPath, AppPaths.GameDbCache);
    var packs = pcsx2.GetInstalledPacks(texturesPath, gamesettingsPath, db);

    if (cli.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(packs,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (packs.Count == 0)
    {
        log.Warn($"No texture packs found under {texturesPath}");
        return 0;
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Installed PS2 texture packs:");
    Console.WriteLine();
    Console.ResetColor();

    const int w1 = 12, w2 = 40, w3 = 8, w4 = 8, w5 = 13;
    Console.WriteLine(
        $"{"Serial",-w1}  {"Game",-w2}  {"Textures",w3}  {"MB",w4}  {"INI OK?",w5}");
    Console.WriteLine(new string('-', w1 + w2 + w3 + w4 + w5 + 8));
    foreach (var p in packs)
    {
        Console.WriteLine(
            $"{p.Serial,-w1}  {p.GameName[..Math.Min(p.GameName.Length, w2)],-w2}  " +
            $"{p.TextureCount,w3}  {p.SizeMb,w4:F1}  {p.IniConfigured,w5}");
    }
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Total: {packs.Count} pack(s)");
    Console.ResetColor();
    return 0;
}

// ============================================================================
//  --query  (download)
// ============================================================================
if (string.IsNullOrEmpty(cli.Query))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine(
        "Missing argument.  Usage:\n" +
        "  ps2tex --query \"God of War\"\n" +
        "  ps2tex --list\n" +
        "  ps2tex --status <jobId>");
    Console.ResetColor();
    return 1;
}

log.Info("Starting texture grabber");
log.Info($"  Query:        {cli.Query}");
log.Debug($"  Textures:     {texturesPath}");
log.Debug($"  Gamesettings: {gamesettingsPath}");

// 1. Resolve name -> serial
var gameDbSvc = new GameDbService(log);
var gameDb    = gameDbSvc.Load(gameIndexPath, AppPaths.GameDbCache);

// 1a. Prefer locally installed game — check gamesettings INIs and texture folders
//     so that e.g. "The Sims 2" resolves to the user's PAL serial, not the US default.
var pcsx2Svc    = new Pcsx2Service(log);
var localSerials = pcsx2Svc.GetLocalGameSerials(gamesettingsPath, texturesPath);
GameEntry? entry = null;

if (localSerials.Count > 0)
{
    var localDb = gameDb.Where(e => localSerials.Contains(e.Serial)).ToList();
    if (localDb.Count > 0)
        entry = gameDbSvc.Resolve(localDb, cli.Query, cli.Interactive);
    if (entry is not null)
        log.Success($"[LOCAL] Matched to locally installed game {entry.Serial} ({entry.DisplayName}, {entry.Region})");
}

// 1b. Fall back to full GameIndex.yaml search (region-biased toward NTSC-U)
if (entry is null)
    entry = gameDbSvc.Resolve(gameDb, cli.Query, cli.Interactive);

if (entry is null)
{
    log.Warn($"No local GameDB match for '{cli.Query}' — trying wiki.pcsx2.net...");
    var wikiSerial = await new WikiService(log, AppPaths.WikiCacheDir)
        .GetSerialForNameAsync(cli.Query);
    if (wikiSerial is not null)
    {
        entry = gameDbSvc.FindBySerial(gameDb, wikiSerial)
            ?? new GameEntry(wikiSerial, cli.Query, cli.Query, null);
        log.Success($"[WIKI] Resolved '{cli.Query}' -> {wikiSerial}");
    }
}

if (entry is null)
{
    log.Error($"Could not resolve '{cli.Query}' to a PS2 serial");
    return 1;
}

var gameName = entry.DisplayName;

// 2. Check Archive.org index first (no browser required)
var archiveLink = await new ArchiveOrgIndexService(log, AppPaths.ArchiveIndexCache)
    .FindBySerialAsync(entry.Serial);

List<DownloadLink> links       = [];
string?            threadUrl   = null;
string?            threadTitle = null;

if (archiveLink is not null)
{
    links       = [archiveLink];
    threadUrl   = archiveLink.Url;
    threadTitle = $"[Archive.org] {gameName}";
}
else
{
    // 3. Initialise Playwright browser (solves Cloudflare challenges natively)
    await using var fetcher = new PlaywrightFetcher(log);

    // 4. Find thread candidates (search-first, score-ordered)
    var gbatemp    = new GbatempService(fetcher, log);
    var candidates = await gbatemp.FindThreadCandidatesAsync(
        entry.Serial, gameName, nodeId, userQuery: cli.Query);

    if (candidates.Count == 0)
    {
        log.Error($"No matching texture pack found for '{gameName}' [{entry.Serial}] — nothing installed.");
        log.Info($"Browse manually: https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/");
        return 1;
    }

    // 5. Extract download links — try each candidate in score order
    ForumThread? winnerThread = null;
    foreach (var candidate in candidates)
    {
        links = await gbatemp.GetDownloadLinksAsync(candidate.Url, candidate);
        if (links.Count > 0) { winnerThread = candidate; break; }
        if (candidate != candidates[^1])
            log.Info($"No links in \"{candidate.Title}\" — trying next candidate...");
    }

    if (links.Count == 0)
    {
        log.Error($"No usable download links in any matching thread for '{gameName}' [{entry.Serial}] — nothing installed.");
        return 1;
    }

    threadUrl   = winnerThread!.Url;
    threadTitle = winnerThread.Title;
}

// 6. Spawn job
var jobState = new JobState
{
    Query            = cli.Query,
    Serial           = entry.Serial,
    GameName         = gameName,
    Region           = entry.Region,
    ThreadUrl        = threadUrl,
    ThreadTitle      = threadTitle,
    DownloadLinks    = links,
    TexturesPath     = texturesPath,
    GamesettingsPath = gamesettingsPath,
};

var jobSvc = new JobService(log, AppPaths.JobsDir);
var result = jobSvc.Spawn(jobState);

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Download job spawned.  It will continue in the background.");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine($"  Job ID:   {result.JobId}");
Console.ResetColor();
Console.WriteLine($"  Log:      {result.LogFile}");
Console.WriteLine($"  Check:    ps2tex --status {result.JobId}");
Console.WriteLine();

if (cli.Json)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        jobId   = result.JobId,
        serial  = entry.Serial,
        game    = gameName,
        thread  = threadUrl,
        links   = links,
        logFile = result.LogFile,
    }, new JsonSerializerOptions { WriteIndented = true }));
}

return 0;

// ============================================================================
//  Argument model
// ============================================================================
internal sealed class Args
{
    public string  Command          { get; private set; } = "";
    public string? Query            { get; private set; }
    public bool    List             { get; private set; }
    public string? Status           { get; private set; }
    public bool    Interactive      { get; private set; }
    public bool    Json             { get; private set; }
    public string? TexturesPath     { get; private set; }
    public string? GamesettingsPath { get; private set; }
    public string? GameIndexPath    { get; private set; }
    public int     NodeId           { get; private set; }
    public string? JobFile          { get; private set; }

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "worker":                  a.Command          = "worker"; break;
                case "--query":       case "-q": a.Query           = Next(argv, ref i); break;
                case "--list":        case "-l": a.List            = true; break;
                case "--status":      case "-s": a.Status          = Next(argv, ref i); break;
                case "--interactive": case "-i": a.Interactive     = true; break;
                case "--json":                   a.Json            = true; break;
                case "--textures-path":          a.TexturesPath    = Next(argv, ref i); break;
                case "--gamesettings-path":      a.GamesettingsPath= Next(argv, ref i); break;
                case "--game-index":             a.GameIndexPath   = Next(argv, ref i); break;
                case "--node-id":
                    if (int.TryParse(Next(argv, ref i), out int nid)) a.NodeId = nid; break;
                case "--job-file":               a.JobFile         = Next(argv, ref i); break;
            }
        }
        return a;
    }

    private static string? Next(string[] argv, ref int i)
        => ++i < argv.Length ? argv[i] : null;
}
