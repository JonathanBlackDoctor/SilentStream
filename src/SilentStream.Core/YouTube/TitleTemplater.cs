using System.Text.RegularExpressions;

namespace SilentStream.Core.YouTube;

/// <summary>
/// Expands the broadcast title template (plan §3.7): every "{...}" group is treated as a
/// .NET date format string, e.g. "라이브 - {yyyy-MM-dd HH:mm}" → "라이브 - 2026-06-12 09:30".
/// The optional {호실} token (확장계획서 D12) is substituted first with the per-device room name
/// (호실명) so several PCs sharing one channel produce distinguishable titles; when no room name is
/// given the bracketed "[{호실}] " prefix collapses to nothing instead of rendering "[] 라이브".
/// </summary>
public static partial class TitleTemplater
{
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TokenRegex();

    // Empty-room collapse: drop the bracketed prefix "[{호실}] " (with any surrounding whitespace)
    // first, then any bare "{호실}" plus its trailing space — so an unset room never leaves "[] "
    // or a stray leading space behind.
    [GeneratedRegex(@"\[\s*\{호실\}\s*\]\s*")]
    private static partial Regex EmptyRoomBracketRegex();

    [GeneratedRegex(@"\{호실\}\s*")]
    private static partial Regex EmptyRoomBareRegex();

    public static string Expand(string template, DateTime timestamp, string? roomName = null) =>
        TokenRegex().Replace(ApplyRoom(template, roomName), m =>
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

    /// <summary>
    /// Period-aware overload (확장계획서 §5, D6): the {교시} token (or {교시:00} with a numeric
    /// format) is replaced with <paramref name="periodNumber"/>; the optional {호실} token with
    /// <paramref name="roomName"/>; every other "{...}" group is a date format applied to
    /// <paramref name="timestamp"/>.
    /// e.g. "[{호실}] {교시}교시 - {yyyy-MM-dd}" + (2026-06-14, 1, "201호") → "[201호] 1교시 - 2026-06-14".
    /// </summary>
    public static string Expand(string template, DateTime timestamp, int periodNumber, string? roomName = null) =>
        TokenRegex().Replace(ApplyRoom(template, roomName), m =>
        {
            var token = m.Groups[1].Value;
            if (token == "교시")
            {
                return periodNumber.ToString();
            }
            if (token.StartsWith("교시:", StringComparison.Ordinal))
            {
                try
                {
                    return periodNumber.ToString(token["교시:".Length..]);
                }
                catch (FormatException)
                {
                    return m.Value;
                }
            }
            try
            {
                return timestamp.ToString(token);
            }
            catch (FormatException)
            {
                return m.Value; // leave unknown tokens untouched
            }
        });

    /// <summary>
    /// Resolves the {호실} room token BEFORE the date/{교시} pass so the room name is never parsed as
    /// a date format. A non-empty name is brace-stripped first so an operator-typed "{교시}" inside
    /// the room name can't smuggle a token into the second pass; an empty/whitespace name removes the
    /// token (and its bracket wrapper) entirely.
    /// </summary>
    private static string ApplyRoom(string template, string? roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            template = EmptyRoomBracketRegex().Replace(template, string.Empty);
            return EmptyRoomBareRegex().Replace(template, string.Empty);
        }

        var safe = roomName.Trim().Replace("{", string.Empty).Replace("}", string.Empty);
        return template.Replace("{호실}", safe);
    }
}
