using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class UploadQueueRemoteTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T01:00:00Z");
    private static UploadJob Job(string id, string status = "pending", long bytes = 1000) =>
        new(id, @"C:\private\recording.mp4", "1교시", new DateOnly(2026, 10, 17), 1, status, 0, null,
            FileSizeBytes: bytes);
    private static UploadJob[] Samples() => Enumerable.Range(0, 3).Select(i =>
        Job("done" + i, "completed") with { UploadDurationSeconds = 100 }).ToArray();

    [Fact]
    public void Serial_estimates_use_file_size_elapsed_time_and_prior_jobs()
    {
        var jobs = Samples().Concat([
            Job("active", "uploading", 2000) with { UploadStartedAtUtc = Now.AddSeconds(-50) },
            Job("next", bytes: 500)]).ToArray();
        var view = UploadQueueRemote.Build(jobs, Now);
        Assert.Equal(Now.AddSeconds(150), view.Items.Single(j => j.Id == "active").EstimatedCompletedAtUtc);
        Assert.Equal(Now.AddSeconds(200), view.Items.Single(j => j.Id == "next").EstimatedCompletedAtUtc);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("private", json);
        Assert.Equal("2026-10-17", view.Items[^1].Date);
    }

    [Fact]
    public void Insufficient_history_does_not_invent_estimates()
    {
        var view = UploadQueueRemote.Build([Samples()[0], Job("next")], Now);
        Assert.Null(view.Items.Single(j => j.Id == "next").EstimatedCompletedAtUtc);
    }

    [Fact]
    public void Quota_pause_moves_the_entire_queue_after_reset()
    {
        var reset = Now.AddHours(8);
        var view = UploadQueueRemote.Build(Samples().Concat([Job("a"), Job("b")]).ToArray(), Now, reset);
        Assert.Equal(reset, view.PausedUntilUtc);
        Assert.Equal(reset.AddSeconds(200), view.Items.Single(j => j.Id == "b").EstimatedCompletedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retrying_or_overdue_upload_suppresses_estimates_behind_it(bool retry)
    {
        var blocked = retry ? Job("a") with { Attempts = 1 } :
            Job("a", "uploading") with { UploadStartedAtUtc = Now.AddSeconds(-150) };
        var view = UploadQueueRemote.Build(Samples().Concat([blocked, Job("b")]).ToArray(), Now);
        Assert.Null(view.Items.Single(j => j.Id == "a").EstimatedCompletedAtUtc);
        Assert.Null(view.Items.Single(j => j.Id == "b").EstimatedCompletedAtUtc);
    }

    [Fact]
    public void All_active_jobs_survive_bounded_history()
    {
        var jobs = Enumerable.Range(0, 110).Select(i => Job("old" + i, "completed"))
            .Concat(Enumerable.Range(0, 25).Select(i => Job("pending" + i))).ToArray();
        var view = UploadQueueRemote.Build(jobs, Now);
        Assert.Equal(125, view.Items.Count);
        Assert.Equal(25, view.Pending);
    }

    [Fact]
    public void Lunch_identity_and_terminal_failure_are_preserved()
    {
        var job = Job("lunch", "failed") with { PeriodNumber = 0, SlotId = "lunch",
            SlotLabel = "점심시간", SlotOrder = 5, Attempts = 5 };
        var item = Assert.Single(UploadQueueRemote.Build([job], Now).Items);
        Assert.Equal("lunch", item.SlotId);
        Assert.Equal("점심시간", item.SlotLabel);
        Assert.Null(item.EstimatedCompletedAtUtc);
    }

    [Fact]
    public void Large_backlog_is_bounded_without_hiding_its_total_count()
    {
        var jobs = Enumerable.Range(0, 250).Select(i => Job("job" + i)).ToArray();
        var view = UploadQueueRemote.Build(jobs, Now);
        Assert.Equal(200, view.Items.Count);
        Assert.Equal(250, view.Pending);
        Assert.Equal(50, view.OmittedItems);
    }
}
