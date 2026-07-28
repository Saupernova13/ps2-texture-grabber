namespace Ps2TextureGrabber;

/// <summary>
/// Where PCSX2 keeps the three directories this tool writes to, per platform.
///
/// The two layouts are genuinely different, not just differently rooted:
///
///   Windows  EmuDeck installs PCSX2 under %APPDATA%\EmuDeck\Emulators\PCSX2-Qt, and the
///            textures/gamesettings/resources folders live inside that.
///   Linux    PCSX2-Qt uses its own XDG data root, ~/.config/PCSX2. EmuDeck does not nest
///            it under an EmuDeck folder at all - it symlinks the heavy subdirectories out
///            to the SD card instead, so writing to ~/.config/PCSX2/textures lands on the
///            card without this tool needing to know where the card is.
///
/// Every value is overridable from .settings or the command line; these are only the
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

    public static string Textures     => Path.Combine(Root, "textures");
    public static string GameSettings => Path.Combine(Root, "gamesettings");

    /// <summary>
    /// GameIndex.yaml, used to resolve a game's serial. On Linux the AppImage keeps it
    /// inside the read-only image and only populates resources/ once PCSX2 has run, so
    /// this path legitimately may not exist yet - callers fall back to matching on title.
    /// </summary>
    public static string GameIndex => Path.Combine(Root, "resources", "GameIndex.yaml");
}
