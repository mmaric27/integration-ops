using IntegrationOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationOps.Api.IntegrationRuns;

internal sealed class IntegrationRunIntake(IntegrationOpsDbContext database)
{
    internal const string KeyIndex = "ux_integration_runs_idempotency_key_hash";

    internal async Task<(IntegrationRunResponse? Run, int Status)> AcceptAsync(
        IntegrationRunIntakeInput input, CancellationToken cancellationToken)
    {
        var receivedAt = DateTime.UtcNow;
        var candidate = new IntegrationRun
        {
            Id = Guid.NewGuid(),
            Partner = input.Request.Partner,
            Operation = input.Request.Operation,
            Status = IntegrationRunStatus.Pending,
            AttemptCount = 0,
            RecordCount = 0,
            ReceivedAt = receivedAt,
            CompletedAt = null,
            LastError = null,
            IdempotencyKeyHash = input.KeyHash,
            RequestFingerprint = input.Fingerprint
        };
        database.IntegrationRuns.Add(candidate);
        try
        {
            // One row, one save. No enclosing transaction or execution-strategy retries.
            await database.SaveChangesAsync(cancellationToken);
            return (IntegrationRunResponse.FromPersistence(candidate), StatusCodes.Status201Created);
        }
        catch (DbUpdateException exception) when (IsKeyViolation(exception))
        {
            // SaveChanges has unwound its failed transaction before control reaches this handler.
            cancellationToken.ThrowIfCancellationRequested();
            database.Entry(candidate).State = EntityState.Detached;
        }

        var keyHash = input.KeyHash;
        var winner = await database.IntegrationRuns.AsNoTracking().SingleOrDefaultAsync(
            run => run.IdempotencyKeyHash != null && run.IdempotencyKeyHash.SequenceEqual(keyHash), cancellationToken);
        if (winner is null)
        {
            return (null, StatusCodes.Status503ServiceUnavailable);
        }

        var matches = winner.RequestFingerprint is not null && winner.RequestFingerprint.SequenceEqual(input.Fingerprint) &&
            string.Equals(winner.Partner, input.Request.Partner, StringComparison.Ordinal) &&
            string.Equals(winner.Operation, input.Request.Operation, StringComparison.Ordinal);
        return matches
            ? (IntegrationRunResponse.FromPersistence(winner), StatusCodes.Status200OK)
            : (null, StatusCodes.Status409Conflict);
    }

    internal static bool IsKeyViolation(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException postgres)
            {
                return postgres.SqlState == PostgresErrorCodes.UniqueViolation && postgres.ConstraintName == KeyIndex;
            }
        }

        return false;
    }

    // Server integrity errors are not availability errors. Do not reuse the broader read classifier for writes.
    internal static bool IsUnavailable(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState.StartsWith("08", StringComparison.Ordinal) ||
            postgres.SqlState is "42P01" or "42703" or "3D000" or "57P01" or "57P02" or "57P03" or "53300",
        NpgsqlException or TimeoutException => true,
        DbUpdateException { InnerException: { } inner } => IsUnavailable(inner),
        InvalidOperationException { InnerException: { } inner } => IsUnavailable(inner),
        _ => false
    };
}
