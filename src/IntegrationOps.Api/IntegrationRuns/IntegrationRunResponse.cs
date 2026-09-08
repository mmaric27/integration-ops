using IntegrationOps.Api.Data;

namespace IntegrationOps.Api.IntegrationRuns;

public sealed record IntegrationRunResponse(
    Guid Id,
    string Partner,
    string Operation,
    IntegrationRunStatus Status,
    int AttemptCount,
    int RecordCount,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    string? LastError)
{
    public static IntegrationRunResponse FromPersistence(IntegrationRun run) => new(
        run.Id, run.Partner, run.Operation, run.Status, run.AttemptCount, run.RecordCount,
        UtcTimestamp.ToResponse(run.ReceivedAt), UtcTimestamp.ToResponse(run.CompletedAt), run.LastError);
}
