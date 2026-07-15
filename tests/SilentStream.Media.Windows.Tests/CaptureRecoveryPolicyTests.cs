using Xunit;

namespace SilentStream.Media.Windows.Tests;

public sealed class CaptureRecoveryPolicyTests
{
    [Fact]
    public void Failed_initialization_is_released_and_can_be_retried()
    {
        var attempts = 0;
        var releases = 0;

        var first = CaptureRecoveryPolicy.TryInitialize(
            () => releases++,
            () =>
            {
                attempts++;
                throw new InvalidOperationException("display temporarily unavailable");
            },
            out var error);

        var second = CaptureRecoveryPolicy.TryInitialize(
            () => releases++,
            () => attempts++,
            out var secondError);

        Assert.False(first);
        Assert.IsType<InvalidOperationException>(error);
        Assert.True(second);
        Assert.Null(secondError);
        Assert.Equal(2, attempts);
        Assert.Equal(3, releases); // before each attempt, plus cleanup after the failed one
    }
}
