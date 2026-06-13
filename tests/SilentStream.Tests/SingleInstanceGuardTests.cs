using SilentStream.Core.SingleInstance;
using Xunit;

namespace SilentStream.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void First_instance_is_primary_and_second_is_not()
    {
        var name = "sstream-test-" + Guid.NewGuid().ToString("N");

        using var first = new SingleInstanceGuard(name);
        Assert.True(first.IsPrimaryInstance);

        using var second = new SingleInstanceGuard(name);
        Assert.False(second.IsPrimaryInstance);
    }

    [Fact]
    public void Releasing_the_primary_lets_a_new_instance_become_primary()
    {
        var name = "sstream-test-" + Guid.NewGuid().ToString("N");

        var first = new SingleInstanceGuard(name);
        Assert.True(first.IsPrimaryInstance);
        first.Dispose();

        using var next = new SingleInstanceGuard(name);
        Assert.True(next.IsPrimaryInstance);
    }
}
