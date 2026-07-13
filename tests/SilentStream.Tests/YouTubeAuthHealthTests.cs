using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class YouTubeAuthHealthTests
{
    private DateTime _now = new(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc);

    private (YouTubeAuthHealth Health, List<YouTubeAuthHealthStatus> Changes) Create()
    {
        var changes = new List<YouTubeAuthHealthStatus>();
        var health = new YouTubeAuthHealth(() => _now);
        health.StatusChanged += (_, status) => changes.Add(status);
        return (health, changes);
    }

    [Fact]
    public void Warning_starts_at_the_ten_minute_boundary_once_and_clears_after_a_fresh_token()
    {
        var (health, changes) = Create();

        health.ObserveAccessTokenExpiry(_now.AddMinutes(10).AddSeconds(1));
        Assert.Empty(changes);

        _now = _now.AddSeconds(1);
        health.Evaluate();
        var expiring = Assert.Single(changes);
        Assert.Equal(YouTubeAuthHealthState.Expiring, expiring.State);

        health.Evaluate();
        Assert.Single(changes); // the 5-second health poll cannot spam the same condition

        health.ObserveAccessTokenExpiry(_now.AddHours(1));
        Assert.Equal(2, changes.Count);
        var recovered = changes[1];
        Assert.Equal(YouTubeAuthHealthState.Healthy, recovered.State);
    }

    [Theory]
    [InlineData(YouTubeAuthFailureKind.MissingClientSecret)]
    [InlineData(YouTubeAuthFailureKind.TokenRejected)]
    public void Permanent_oauth_failure_is_critical_until_a_successful_credential_is_observed(
        YouTubeAuthFailureKind failure)
    {
        var (health, changes) = Create();

        health.ReportPermanentFailure(failure);
        var critical = Assert.Single(changes);
        Assert.Equal(YouTubeAuthHealthState.ActionRequired, critical.State);

        health.ReportPermanentFailure(failure);
        Assert.Single(changes); // repeat failures do not create a storm of health events

        health.ObserveAccessTokenExpiry(_now.AddHours(1));
        Assert.Equal(2, changes.Count);
        var recovered = changes[1];
        Assert.Equal(YouTubeAuthHealthState.Healthy, recovered.State);
    }
}
