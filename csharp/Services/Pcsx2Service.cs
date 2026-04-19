using System.Text.RegularExpressions;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// PCSX2-specific operations:
///   • CRC resolution (from existing INI files, with wiki fallback)
///   • Writing the three texture-replacement flags to gamesettings INI
///   • Enumerating locally installed texture packs for --list
/// </summary>
public sealed partial class Pcsx2Service
{
    private readonly Logger _log;
    public Pcsx2Service(Logger log) => _log = log;

    // -------------------------------------------------------------------------
    // CRC resolution

    /// <summary>
    /// Looks for an existing {SERIAL}_*.ini in gamesettings and parses the CRC
    /// from its filename.  Returns null if no match found.
    /// </summary>
    public string? ResolveCrcFromIni(string serial, string gamesettingsPath)
    {
        var pattern = Path.Combine(gamesettingsPath, $"{serial}_*.ini");
        var matches = Directory.GetFiles(gamesettingsPath, $"{serial}_*.ini",
            SearchOption.TopDirectoryOnly);

        foreach (var file in matches)
        {
            var baseName = Path.GetFileNameWithoutExtension(file);
            var m = CrcFileNameRx().Match(baseName);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Returns the INI path for (serial, crc).  CRC may be null → unscoped form.</summary>
    public static string GetIniPath(string serial, string? crc, string gamesettingsPath)
    {
        var name = crc is not null
            ? $"{serial}_{crc.ToUpperInvariant()}.ini"
            : $"{serial}.ini";
        return Path.Combine(gamesettingsPath, name);
    }

    // -------------------------------------------------------------------------
    // INI management

    /// <summary>
    /// Ensures [EmuCore/GS] contains the three texture-replacement keys set to
    /// true.  Preserves all other sections and keys.  Returns true if the file
    /// was modified, false if it was already correct.
    /// </summary>
    public bool SetTextureIni(string iniPath)
    {
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LoadTextureReplacements"]         = "true",
            ["LoadTextureReplacementsAsync"]    = "true",
            ["PrecacheTextureReplacements"]     = "true",
        };

        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : [];

        if (!File.Exists(iniPath))
        {
            var dir = Path.GetDirectoryName(iniPath)!;
            Directory.CreateDirectory(dir);
        }

        // Parse all sections into an ordered structure.
        var sectionOrder = new List<string> { "" };       // "" = top-level (before first section)
        var sectionLines = new Dictionary<string, List<string>> { [""] = [] };
        var current      = "";

        foreach (var line in lines)
        {
            var sm = SectionHeaderRx().Match(line);
            if (sm.Success)
            {
                current = sm.Groups[1].Value;
                if (!sectionLines.ContainsKey(current))
                {
                    sectionOrder.Add(current);
                    sectionLines[current] = [];
                }
            }
            else
            {
                sectionLines[current].Add(line);
            }
        }

        // Ensure [EmuCore/GS] exists.
        if (!sectionLines.ContainsKey("EmuCore/GS"))
        {
            sectionOrder.Add("EmuCore/GS");
            sectionLines["EmuCore/GS"] = [];
        }

        // Set each required key in [EmuCore/GS].
        var gsLines = sectionLines["EmuCore/GS"];
        foreach (var (key, value) in required)
        {
            var desired = $"{key} = {value}";
            var idx     = gsLines.FindIndex(l =>
                KeyValueRx(key).IsMatch(l));
            if (idx >= 0)
                gsLines[idx] = desired;
            else
                gsLines.Add(desired);
        }

        // Rebuild file content.
        var outLines = new List<string>();
        foreach (var sec in sectionOrder)
        {
            if (sec != "")
            {
                if (outLines.Count > 0 && outLines[^1] != "")
                    outLines.Add("");
                outLines.Add($"[{sec}]");
            }
            outLines.AddRange(sectionLines[sec]);
        }

        // Trim trailing blank lines, add single trailing CRLF.
        while (outLines.Count > 0 && outLines[^1] == "")
            outLines.RemoveAt(outLines.Count - 1);

        var newContent = string.Join("\r\n", outLines) + "\r\n";
        var existing   = File.Exists(iniPath) ? File.ReadAllText(iniPath) : null;

        if (newContent == existing)
        {
            _log.Debug($"INI already configured: {iniPath}");
            return false;
        }

        File.WriteAllText(iniPath, newContent, System.Text.Encoding.UTF8);
        _log.Success($"Wrote texture replacement flags to {iniPath}");
        return true;
    }

    // -------------------------------------------------------------------------
    // --list support

    public List<InstalledPack> GetInstalledPacks(
        string            texturesPath,
        string            gamesettingsPath,
        List<GameEntry>   gameDb)
    {
        if (!Directory.Exists(texturesPath))
        {
            _log.Error($"Textures path not found: {texturesPath}");
            return [];
        }

        var results = new List<InstalledPack>();

        foreach (var dir in Directory.GetDirectories(texturesPath))
        {
            var serial = Path.GetFileName(dir);
            if (!SerialFormatRx().IsMatch(serial)) continue;

            var gameName = gameDb.FirstOrDefault(e => e.Serial == serial)?.DisplayName
                           ?? $"(unknown: {serial})";

            var replacements = Path.Combine(dir, "replacements");
            int pngCount     = 0;
            long totalBytes  = 0;
            if (Directory.Exists(replacements))
            {
                foreach (var f in Directory.EnumerateFiles(replacements, "*.png",
                    SearchOption.AllDirectories))
                {
                    pngCount++;
                    totalBytes += new FileInfo(f).Length;
                }
            }

            bool iniConfigured = false;
            foreach (var iniFile in Directory.GetFiles(gamesettingsPath, $"{serial}*.ini"))
            {
                var content = File.ReadAllText(iniFile);
                if (LoadTextureRx().IsMatch(content))
                {
                    iniConfigured = true;
                    break;
                }
            }

            results.Add(new InstalledPack(
                Serial:        serial,
                GameName:      gameName,
                TextureCount:  pngCount,
                SizeMb:        Math.Round(totalBytes / 1_048_576.0, 2),
                IniConfigured: iniConfigured));
        }

        results.Sort((a, b) => string.Compare(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    // ---- compiled regexes ----
    [GeneratedRegex(@"^[A-Z]{4}-\d{5}_([0-9A-Fa-f]{8})$")]
    private static partial Regex CrcFileNameRx();

    [GeneratedRegex(@"^\s*\[([^\]]+)\]\s*$")]
    private static partial Regex SectionHeaderRx();

    [GeneratedRegex(@"^[A-Z]{4}-\d{5}$")]
    private static partial Regex SerialFormatRx();

    [GeneratedRegex(@"(?m)^\s*LoadTextureReplacements\s*=\s*true")]
    private static partial Regex LoadTextureRx();

    private static Regex KeyValueRx(string key)
        => new($@"^\s*{Regex.Escape(key)}\s*=", RegexOptions.IgnoreCase);
}
