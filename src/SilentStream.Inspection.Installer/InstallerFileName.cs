using System.IO;
using System.Text.RegularExpressions;

namespace SilentStream.Inspection.Installer;

public static class InstallerFileName
{
    public static string? RoomFromPath(string? path)
    {
        // Browsers append a numbered suffix when a download already exists.
        var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        var match = Regex.Match(name,
            @"(?:install|setup)[-_](?<room>[a-zA-Z0-9_-]{1,64})(?:\s*\([0-9]+\))*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["room"].Value.ToLowerInvariant() : null;
    }
}
