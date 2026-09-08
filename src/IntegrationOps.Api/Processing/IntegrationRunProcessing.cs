using System.Data;
using IntegrationOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace IntegrationOps.Api.Processing;

// PostgreSQL-specific coordination, not an execution strategy or a generic repository.
public sealed class IntegrationRunProcessing(IntegrationOpsDbContext database)
{
    public async Task<ProcessingAttempt?> ClaimAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockAsync("""
            SELECT id, partner, operation, attempt_count, lease_token FROM integration_runs
            WHERE idempotency_key_hash IS NOT NULL AND status = 'Pending' AND lease_token IS NULL
                AND attempt_count < 3
                AND ((attempt_count = 0 AND next_attempt_at IS NULL) OR next_attempt_at <= clock_timestamp())
            ORDER BY COALESCE(next_attempt_at, received_at) ASC, received_at ASC, id ASC
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """, cancellationToken);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var token = Guid.NewGuid();
        await using var command = Command("""
            WITH t AS MATERIALIZED (SELECT clock_timestamp() AS now)
            UPDATE integration_runs r SET attempt_count = r.attempt_count + 1,
                lease_token = @token, lease_expires_at = t.now + @lease,
                next_attempt_at = NULL, last_error = NULL
            FROM t WHERE r.id = @id AND r.idempotency_key_hash IS NOT NULL
                AND r.status = 'Pending' AND r.lease_token IS NULL AND r.lease_expires_at IS NULL
                AND r.attempt_count = @attempt AND r.attempt_count < 3
                AND ((r.attempt_count = 0 AND r.next_attempt_at IS NULL) OR r.next_attempt_at <= t.now)
            """);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("attempt", row.Attempt);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("lease", ProcessingPolicy.LeaseDuration);
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken));
        // If acknowledgement fails, no ProcessingAttempt escapes and execution must not start.
        await transaction.CommitAsync(cancellationToken);
        return new ProcessingAttempt(row.Id, row.Partner, row.Operation, row.Attempt + 1, token);
    }

    public async Task<bool> RecoverOneAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockAsync("""
            SELECT id, partner, operation, attempt_count, lease_token FROM integration_runs
            WHERE idempotency_key_hash IS NOT NULL AND status = 'Pending'
                AND lease_token IS NOT NULL AND lease_expires_at <= clock_timestamp()
            ORDER BY lease_expires_at ASC, id ASC LIMIT 1 FOR UPDATE SKIP LOCKED
            """, cancellationToken);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var exhausted = row.Attempt == ProcessingPolicy.MaximumAttempts;
        var delay = exhausted ? TimeSpan.Zero : ProcessingPolicy.RetryDelay(row.Attempt);
        await using var command = Command("""
            WITH t AS MATERIALIZED (SELECT clock_timestamp() AS now)
            UPDATE integration_runs r SET status = @status, record_count = 0,
                lease_token = NULL, lease_expires_at = NULL, last_error = @error,
                next_attempt_at = CASE WHEN @terminal THEN NULL ELSE t.now + @delay END,
                completed_at = CASE WHEN @terminal THEN GREATEST(t.now, r.received_at) ELSE NULL END
            FROM t WHERE r.id = @id AND r.idempotency_key_hash IS NOT NULL AND r.status = 'Pending'
                AND r.lease_token = @token AND r.attempt_count = @attempt AND r.lease_expires_at <= t.now
            """);
        AddOwnership(command, row.Id, row.Token!.Value, row.Attempt);
        command.Parameters.AddWithValue("status", exhausted ? "Failed" : "Pending");
        command.Parameters.AddWithValue("terminal", exhausted);
        command.Parameters.AddWithValue("delay", delay);
        command.Parameters.AddWithValue("error", ProcessingResult.ErrorCode(exhausted
            ? ProcessingError.AttemptExhausted : ProcessingError.AttemptAbandoned));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteAsync(ProcessingAttempt attempt, ProcessingResult result,
        CancellationToken cancellationToken = default)
    {
        if (!result.IsValidProcessorResult())
        {
            throw new InvalidOperationException("Coordination received an invalid processing result.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Lock before the mutation's fresh clock check; never rely on time captured before a lock wait.
        var row = await LockAsync("""
            SELECT id, partner, operation, attempt_count, lease_token FROM integration_runs
            WHERE id = @id FOR UPDATE
            """, cancellationToken, attempt.RunId);
        if (row is null || row.Token != attempt.LeaseToken || row.Attempt != attempt.Number)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var retry = result.Outcome == ProcessingOutcome.Retryable && attempt.Number < ProcessingPolicy.MaximumAttempts;
        var success = result.Outcome == ProcessingOutcome.Success;
        var code = success ? null : ProcessingResult.ErrorCode(
            result.Outcome == ProcessingOutcome.Retryable && !retry ? ProcessingError.AttemptExhausted : result.Error!.Value);
        await using var command = Command("""
            WITH t AS MATERIALIZED (SELECT clock_timestamp() AS now)
            UPDATE integration_runs r SET status = @status, record_count = @records,
                completed_at = CASE WHEN @retry THEN NULL ELSE GREATEST(t.now, r.received_at) END,
                next_attempt_at = CASE WHEN @retry THEN t.now + @delay ELSE NULL END,
                last_error = @error, lease_token = NULL, lease_expires_at = NULL
            FROM t WHERE r.id = @id AND r.idempotency_key_hash IS NOT NULL AND r.status = 'Pending'
                AND r.lease_token = @token AND r.attempt_count = @attempt AND r.lease_expires_at > t.now
            """);
        AddOwnership(command, attempt.RunId, attempt.LeaseToken, attempt.Number);
        command.Parameters.AddWithValue("status", success ? "Succeeded" : retry ? "Pending" : "Failed");
        command.Parameters.AddWithValue("records", success ? result.RecordCount : 0);
        command.Parameters.AddWithValue("retry", retry);
        command.Parameters.AddWithValue("delay", retry ? ProcessingPolicy.RetryDelay(attempt.Number) : TimeSpan.Zero);
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)code ?? DBNull.Value });
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed is < 0 or > 1)
        {
            throw new InvalidOperationException("Unexpected completion row count.");
        }

        // Do not automatically repeat this mutation if acknowledgement is lost.
        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    public static bool IsDatabaseUnavailable(Exception exception) => exception switch
    {
        // Classify server errors before the Npgsql base class. Schema/integrity/cancellation are not outages.
        PostgresException postgres => postgres.SqlState.StartsWith("08", StringComparison.Ordinal) ||
            postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300",
        OperationCanceledException => false,
        NpgsqlException { InnerException: PostgresException postgres } => IsDatabaseUnavailable(postgres),
        NpgsqlException { InnerException: OperationCanceledException } => false,
        NpgsqlException { InnerException: System.Net.Sockets.SocketException or System.IO.IOException or TimeoutException } => true,
        TimeoutException => true,
        InvalidOperationException { InnerException: { } inner } => IsDatabaseUnavailable(inner),
        _ => false
    };

    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)database.Database.GetDbConnection(),
        (NpgsqlTransaction?)database.Database.CurrentTransaction?.GetDbTransaction())
    { CommandTimeout = 10 };

    private async Task<LockedRun?> LockAsync(string sql, CancellationToken cancellationToken, Guid? id = null)
    {
        await using var command = Command(sql);
        if (id.HasValue) { command.Parameters.AddWithValue("id", id.Value); }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) { return null; }
        return new LockedRun(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4));
    }

    private static void AddOwnership(NpgsqlCommand command, Guid id, Guid token, int attempt)
    {
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("attempt", attempt);
    }

    private static void RequireOne(int count)
    {
        if (count != 1) { throw new InvalidOperationException("Processing state changed unexpectedly under its row lock."); }
    }

    private sealed record LockedRun(Guid Id, string Partner, string Operation, int Attempt, Guid? Token);
}
