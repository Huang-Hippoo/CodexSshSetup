using CodexSshSetup.Core.Tunnel;

namespace CodexSshSetup.Core.Tests;

public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(4, 30)]
    [InlineData(99, 30)]
    public void Retry_delays_follow_the_bounded_schedule(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.DelayForAttempt(attempt));
    }

    [Fact]
    public void Negative_attempt_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayForAttempt(-1));
    }
}
