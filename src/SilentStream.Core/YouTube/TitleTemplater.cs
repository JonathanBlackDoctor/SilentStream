using System.Text.RegularExpressions;

namespace SilentStream.Core.YouTube;

/// <summary>
/// Expands the broadcast title template (plan §3.7): every "{...}" group is treated as a
/// .NET date format string, e.g. "라이브 - {yyyy-MM-dd HH:mm}" → "라이브 - 2026-06-12 09:30".
/// </summary>
public static partial class TitleTemplater
{
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenRegex();

    public static string Expand(string template, DateTime timestamp) =>
        TokenRegex().Replace(template, m =>
        {
            try
            {
                return timestamp.ToString(m.Groups[1].Value);
            }
            catch (FormatException)
            {
                return m.Value; // leave unknown tokens untouched
            }
        });
}
