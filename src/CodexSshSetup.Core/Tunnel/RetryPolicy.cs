namespace CodexSshSetup.Core.Tunnel;

public static class RetryPolicy
{
    private static readonly int[] DelaysInSeconds = [1, 2, 5, 10, 30];

    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        return TimeSpan.FromSeconds(DelaysInSeconds[Math.Min(attempt, DelaysInSeconds.Length - 1)]);
    }
}
