using System.Text.Json;
using System.Text.RegularExpressions;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Loads PCSX2's GameIndex.yaml (streaming line-by-line regex, no YAML lib needed),
/// caches the result as JSON, and resolves a user query to a GameEntry.
/// </summary>
public sealed partial class GameDbService
{
    private readonly Logger _log;
    public GameDbService(Logger log) => _log = log;

    // -------------------------------------------------------------------------
    // Load / cache

    public List<GameEntry> Load(string gameIndexPath, string cachePath)
    {
        if (File.Exists(cachePath) && File.Exists(gameIndexPath))
        {
            if (File.GetLastWriteTime(cachePath) >= File.GetLastWriteTime(gameIndexPath))
            {
                try
                {
                    _log.Debug($"Loading GameDB from cache: {cachePath}");
                    var cached = JsonSerializer.Deserialize<List<GameEntry>>(
                        File.ReadAllText(cachePath));
                    if (cached is { Count: > 0 }) return cached;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Cache read failed, rebuilding: {ex.Message}");
                }
            }
        }

        // A missing GameIndex.yaml is normal, not fatal. PCSX2's Linux AppImage keeps it
        // inside the read-only image and only populates resources/ once it has run, so a
        // fresh install legitimately has no copy on disk. Degrade instead of throwing:
        // --query still resolves serials through wiki.pcsx2.net, and --list still reports
        // the packs it finds, just without pretty game names. Throwing here surfaced as an
        // unhandled stack trace on the very first run of an otherwise working install.
        if (!File.Exists(gameIndexPath))
        {
            _log.Warn($"GameIndex.yaml not found: {gameIndexPath}");
            _log.Warn("Continuing without the local game database - names resolve via wiki.pcsx2.net instead.");
            _log.Warn("Launch PCSX2 once, or point --game-index at a copy, to enable offline serial lookup.");
            return new List<GameEntry>();
        }

        _log.Info("Parsing GameIndex.yaml (done once and cached)...");
        var entries = ParseYaml(gameIndexPath);
        _log.Success($"Parsed {entries.Count} GameDB entries");

        var cacheDir = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(entries));
        _log.Debug($"Cached GameDB to {cachePath}");

        return entries;
    }

    // -------------------------------------------------------------------------
    // YAML parsing  (regex-based; GameIndex.yaml has a predictable flat structure)

    private static List<GameEntry> ParseYaml(string path)
    {
        var entries = new List<GameEntry>();
        string? serial = null, name = null, nameEn = null, region = null;

        foreach (var line in File.ReadLines(path))
        {
            var sm = SerialLineRx().Match(line);
            if (sm.Success)
            {
                // Flush previous entry
                if (serial is not null)
                    entries.Add(new GameEntry(serial, name, nameEn, region));
                serial = sm.Groups[1].Value;
                name = nameEn = region = null;
                continue;
            }

            if (serial is null) continue;

            var fm = FieldLineRx().Match(line);
            if (!fm.Success) continue;

            switch (fm.Groups[1].Value)
            {
                case "name":    name   = fm.Groups[2].Value; break;
                case "name-en": nameEn = fm.Groups[2].Value; break;
                case "region":  region = fm.Groups[2].Value; break;
            }
        }

        if (serial is not null)
            entries.Add(new GameEntry(serial, name, nameEn, region));

        return entries;
    }

    // -------------------------------------------------------------------------
    // Resolve query -> GameEntry

    public GameEntry? Resolve(
        List<GameEntry> db,
        string          query,
        bool            interactive = false)
    {
        query = query.Trim();

        // Direct serial lookup
        if (SerialFormatRx().IsMatch(query))
        {
            var direct = db.FirstOrDefault(e => e.Serial == query);
            if (direct is null)
                _log.Warn($"Serial '{query}' not found in GameDB");
            return direct;
        }

        var qn      = Normalize(query);
        var qTokens = qn.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<(GameEntry Entry, int Score)>();
        foreach (var e in db)
        {
            int nameScore = 0;
            foreach (var c in new[] { e.Name, e.NameEn })
            {
                if (c is null) continue;
                var cn = Normalize(c);
                if      (cn == qn)                       nameScore = Math.Max(nameScore, 1000);
                else if (cn.StartsWith(qn + " "))        nameScore = Math.Max(nameScore, 700);
                else if (cn.Contains(" " + qn + " "))    nameScore = Math.Max(nameScore, 500);
                else if (cn.Contains(qn))                nameScore = Math.Max(nameScore, 300);
                else if (qTokens.Length > 0)
                {
                    var cTokens = cn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (qTokens.All(t => cTokens.Contains(t)))
                        nameScore = Math.Max(nameScore, 200);
                }
            }
            if (nameScore == 0) continue;

            int regionBonus = e.Region switch
            {
                "NTSC-U" => 30,
                "PAL"    => 20,
                "NTSC-J" => 10,
                _        => 0
            };

            // Penalise trial/demo/preview/kiosk/sample versions heavily so the
            // commercial release always wins over a trial with the same name.
            // Check both Name and NameEn — the marker may appear in either.
            bool isDemo = (e.Name   is not null && DemoTitleRx().IsMatch(e.Name))
                       || (e.NameEn is not null && DemoTitleRx().IsMatch(e.NameEn));
            int demopenalty = isDemo ? -500 : 0;

            scored.Add((e, nameScore + regionBonus + demopenalty));
        }

        if (scored.Count == 0) return null;
        scored.Sort((a, b) => b.Score - a.Score);

        if (interactive && scored.Count > 1 && scored[0].Score < 1000)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Multiple matches for '{query}':");
            Console.ResetColor();

            var top = scored.Take(10).ToList();
            for (int i = 0; i < top.Count; i++)
            {
                var e       = top[i].Entry;
                var display = e.NameEn is not null
                    ? $"{e.NameEn} / {e.Name}"
                    : e.Name ?? e.Serial;
                Console.WriteLine($"  [{i + 1}] {display} ({e.Serial}, {e.Region})");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"Select [1-{top.Count}] or 0 to cancel: ");
            Console.ResetColor();

            if (!int.TryParse(Console.ReadLine(), out int sel)
                || sel < 1 || sel > top.Count)
                return null;

            return top[sel - 1].Entry;
        }

        var best = scored[0];
        _log.Success(
            $"Resolved '{query}' -> {best.Entry.Serial} ({best.Entry.DisplayName}, {best.Entry.Region})");
        if (scored.Count > 1 && best.Score < 1000)
            _log.Warn($"  (ambiguous: {scored.Count} matches; use --interactive to pick)");

        return best.Entry;
    }

    public GameEntry? FindBySerial(List<GameEntry> db, string serial)
        => db.FirstOrDefault(e => e.Serial == serial);

    // -------------------------------------------------------------------------
    // Helpers

    private static string Normalize(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    // ---- compiled regexes ----
    [GeneratedRegex(@"^([A-Z]{4}-\d{5}):\s*$")]
    private static partial Regex SerialLineRx();

    [GeneratedRegex(@"^\s{2}([a-zA-Z\-]+):\s*""?([^""]*?)""?\s*$")]
    private static partial Regex FieldLineRx();

    [GeneratedRegex(@"^[A-Z]{4}-\d{5}$")]
    private static partial Regex SerialFormatRx();

    // Matches trial, demo, preview, sample, kiosk in any bracketing (case-insensitive)
    [GeneratedRegex(@"\b(trial|demo|preview|sample|kiosk)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DemoTitleRx();
}
