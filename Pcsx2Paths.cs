namespace Ps2TextureGrabber;

/// <summary>
/// Where PCSX2 keeps the directories this tool writes to.
///
/// The data root differs by platform:
///
///   Windows  EmuDeck installs PCSX2 under %APPDATA%\EmuDeck\Emulators\PCSX2-Qt.
///   Linux    PCSX2-Qt uses ~/.config/PCSX2 (or the Flatpak's sandboxed copy).
///
/// **The subdirectories are not fixed.** PCSX2 lets the user move any of them, and records
/// the choice in the [Folders] section of its own PCSX2.ini. A relative value there is
/// resolved against the data root; an absolute one is used as-is. EmuDeck does exactly this
/// - a Steam Deck can easily end up with, say,
///
///     [Folders]
///     Textures = ../../Documents/pcsx2/textures
///
/// which puts the real texture folder nowhere near ~/.config/PCSX2. Assuming the default
/// subfolder there is not a cosmetic error: packs install successfully into a directory
/// PCSX2 never reads, so the game looks identical and nothing reports a failure.
///
/// So we read [Folders] the same way PCSX2 does, and only fall back to the default
/// subfolder name when the key (or the ini) is absent.
///
/// Every value is still overridable from .settings or the command line; these are only the
/// defaults for someone who has installed PCSX2 the ordinary way.
/// </summary>
public static class Pcsx2Paths
{
    // The Flatpak build sandboxes its config, so check that location too. A user with both
    // installed gets the native one, which is what EmuDeck sets up.
    private static readonly string[] LinuxRoots =
    {
        Path.Combine(Home, ".config", "PCSX2"),
        Path.Combine(Home, ".var", "app", "net.pcsx2.PCSX2", "config", "PCSX2"),
    };

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string WindowsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EmuDeck", "Emulators", "PCSX2-Qt");

    /// <summary>
    /// The PCSX2 data root. On Linux, the first candidate that already exists - falling
    /// back to the native path so a first run still produces a sensible default rather
    /// than an empty string.
    /// </summary>
    public static string Root
    {
        get
        {
            if (OperatingSystem.IsWindows()) return WindowsRoot;
            foreach (var root in LinuxRoots)
            {
                if (Directory.Exists(root)) return root;
            }
            return LinuxRoots[0];
        }
    }

    /// <summary>PCSX2's own settings file, which owns the [Folders] overrides.</summary>
    public static string ConfigIni => Path.Combine(Root, "inis", "PCSX2.ini");

    public static string Textures     => ResolveFolder("Textures", "textures");
    public static string GameSettings => ResolveFolder("GameSettings", "gamesettings");

    /// <summary>
    /// GameIndex.yaml, used to resolve a game's serial. On Linux the AppImage keeps it
    /// inside the read-only image and only populates resources/ once PCSX2 has run, so
    /// this path legitimately may not exist yet - callers fall back to matching on title.
    /// </summary>
    public static string GameIndex => Path.Combine(ResolveFolder("UserResources", "resources"), "GameIndex.yaml");

    // Parsed once: this is a CLI process, and PCSX2.ini does not change underneath a run.
    private static Dictionary<string, string>? _folders;

    private static Dictionary<string, string> Folders =>
        _folders ??= ReadFolders(ConfigIni);

    /// <summary>
    /// The [Folders] entry for <paramref name="key"/>, resolved the way PCSX2 resolves it:
    /// absolute values as given, relative values against the data root. Falls back to
    /// <paramref name="defaultSubdirectory"/> under the root when unset.
    /// </summary>
    private static string ResolveFolder(string key, string defaultSubdirectory)
    {
        if (!Folders.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Path.Combine(Root, defaultSubdirectory);
        }

        // PCSX2 writes native separators, so a config copied between platforms can carry
        // the other one. Normalise before combining or the whole thing becomes one segment.
        value = value.Replace('\\', Path.DirectorySeparatorChar)
                     .Replace('/', Path.DirectorySeparatorChar)
                     .Trim();

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(Root, value));
    }

    /// <summary>
    /// The [Folders] section of a PCSX2.ini, or an empty map if the file is missing or
    /// unreadable - PCSX2 has not necessarily run yet, which is not an error.
    /// </summary>
    private static Dictionary<string, string> ReadFolders(string iniPath)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(iniPath)) return folders;

        try
        {
            var inSection = false;
            foreach (var raw in File.ReadLines(iniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[')
                {
                    // Entering [Folders], or leaving it for the next section.
                    inSection = line.Equals("[Folders]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                folders[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            // Locked or vanished mid-read; the defaults are still a reasonable answer.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return folders;
    }
}
