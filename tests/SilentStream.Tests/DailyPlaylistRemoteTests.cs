using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class DailyPlaylistRemoteTests
{
    private sealed class Playlists(params DailyPlaylistRecord[] items) : IYouTubePlaylistService
    {
        public bool HasRetryableFailures => false;
        public (string Room, DateOnly From, DateOnly To)? Query { get; private set; }
        public IReadOnlyList<DailyPlaylistRecord> Snapshot(string room, DateOnly from, DateOnly to)
        {
            Query = (room, from, to);
            return items;
        }
        public Task<DailyPlaylistRecord> EnsurePlaylistAsync(string room, DateOnly date, CancellationToken ct)
            => throw new Exception("Copy must not create a playlist");
        public Task AddVideoAsync(string room, DateOnly date, string videoId, CancellationToken ct)
            => throw new Exception("Copy must not modify a playlist");
    }

    private static AppConfig Config()
    {
        var config = AppConfig.CreateDefault();
        config.DeviceName = "display label";
        config.Provisioning.RoomId = "M112";
        return config;
    }

    private static DailyPlaylistRecord Item(string room, string date, string status = "ready", string? id = "PL_today")
        => new(room, DateOnly.Parse(date), $"{room} | {date}", id, "https://untrusted.invalid/", status, null, null);

    [Theory]
    [InlineData("2026-10-16T14:59:59Z", "2026-10-16")]
    [InlineData("2026-10-16T15:00:00Z", "2026-10-17")]
    public void Uses_korean_midnight_and_exact_room_date(string now, string expectedDate)
    {
        var playlists = new Playlists(Item("m111", expectedDate), Item("m112", "2026-10-15"), Item("m112", expectedDate));
        var result = DailyPlaylistRemote.Build(playlists, Config(), DateTimeOffset.Parse(now));
        Assert.Equal(expectedDate, result.Date);
        Assert.Equal("m112", result.Room);
        Assert.Equal("https://www.youtube.com/playlist?list=PL_today", result.Url);
        Assert.Equal(("m112", DateOnly.Parse(expectedDate), DateOnly.Parse(expectedDate)), playlists.Query);
    }

    [Theory]
    [InlineData("creating")]
    [InlineData("auth-required")]
    [InlineData("failed")]
    public void Not_ready_never_exposes_a_copy_link(string status)
    {
        var result = DailyPlaylistRemote.Build(new Playlists(Item("m112", "2026-10-17", status)), Config(),
            DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        Assert.Equal(status, result.Status);
        Assert.Null(result.Url);
    }

    [Fact]
    public void Missing_today_never_uses_yesterday_or_another_room()
    {
        var result = DailyPlaylistRemote.Build(new Playlists(Item("m111", "2026-10-17"), Item("m112", "2026-10-16")),
            Config(), DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        Assert.Equal("missing", result.Status);
        Assert.Null(result.Url);
    }

    [Fact]
    public void Ready_without_id_does_not_offer_a_link()
    {
        var result = DailyPlaylistRemote.Build(new Playlists(Item("m112", "2026-10-17", id: null)), Config(),
            DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        Assert.Null(result.Url);
    }
}
