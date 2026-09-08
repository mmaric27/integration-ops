using IntegrationOps.Api.IntegrationRuns;

namespace IntegrationOps.Api.Data;

public sealed class IntegrationRun
{
    private DateTime _receivedAt;
    private DateTime? _completedAt;
    private DateTime? _leaseExpiresAt;
    private DateTime? _nextAttemptAt;

    public Guid Id { get; set; }
    public required string Partner { get; set; }
    public required string Operation { get; set; }
    public IntegrationRunStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public int RecordCount { get; set; }

    public required DateTime ReceivedAt
    {
        get => _receivedAt;
        set => _receivedAt = UtcTimestamp.NormalizeUtc(value);
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        set => _completedAt = value.HasValue ? UtcTimestamp.NormalizeUtc(value.Value) : null;
    }

    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAt
    {
        get => _leaseExpiresAt;
        set => _leaseExpiresAt = value.HasValue ? UtcTimestamp.NormalizeUtc(value.Value) : null;
    }
    public DateTime? NextAttemptAt
    {
        get => _nextAttemptAt;
        set => _nextAttemptAt = value.HasValue ? UtcTimestamp.NormalizeUtc(value.Value) : null;
    }

    public string? LastError { get; set; }
    public byte[]? IdempotencyKeyHash { get; set; }
    public byte[]? RequestFingerprint { get; set; }
}
