using System.Globalization;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Remote;

/// <summary>Read-only link for the selected room's Korean calendar date. Never creates a playlist.</summary>
public static class DailyPlaylistRemote
{
    public sealed record TodayDto(string Room, string Date, string Status, string? Url);

    public static TodayDto Build(IYouTubePlaylistService playlists, AppConfig config, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(9)).DateTime);
        var room = TitleTemplater.ResolveRoomName(config);
        if (string.IsNullOrWhiteSpace(room)) room = Environment.MachineName.ToLowerInvariant();
        var item = playlists.Snapshot(room, date, date).FirstOrDefault(item =>
            item.Date == date && string.Equals(item.Room, room, StringComparison.OrdinalIgnoreCase));
        var url = item?.Status == DailyPlaylistStatus.Ready && !string.IsNullOrWhiteSpace(item.PlaylistId)
            ? "https://www.youtube.com/playlist?list=" + Uri.EscapeDataString(item.PlaylistId)
            : null;
        return new TodayDto(room, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            item?.Status ?? "missing", url);
    }
}
