namespace IntegrationOps.Api.Processing;

public static class ProcessingPolicy
{
    public const int MaximumAttempts = 3;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DatabaseDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    public static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(10),
        _ => throw new ArgumentOutOfRangeException(nameof(attempt))
    };
}
