namespace Ps2TextureGrabber.Services;

/// <summary>
/// Reads key=value pairs from a .settings file.
/// Lines beginning with # are ignored.  Whitespace around = is stripped.
/// </summary>
public static class SettingsFile
{
    public static Dictionary<string, string> Load(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || !line.Contains('=')) continue;

            var sep = line.IndexOf('=');
            var key = line[..sep].Trim();
            var val = line[(sep + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
                d[key] = val;
        }
        return d;
    }
}
