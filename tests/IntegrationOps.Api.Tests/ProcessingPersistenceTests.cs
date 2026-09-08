using System.Security.Cryptography;
using IntegrationOps.Api.Data;
using IntegrationOps.Api.IntegrationRuns;
using IntegrationOps.Api.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class ProcessingPersistenceTests(PostgresFixture postgres)
{
    internal static async Task<IntegrationRun> InsertAsync(TestDatabase database, string operation = "Ordinary")
    {
        var row = new IntegrationRun
        {
            Id = Guid.NewGuid(),
            Partner = "Synthetic Processing",
            Operation = operation,
            ReceivedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = IntegrationRunStatus.Pending,
            AttemptCount = 0,
            RecordCount = 0,
            IdempotencyKeyHash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
            RequestFingerprint = SHA256.HashData("synthetic test"u8)
        };
        await using var context = database.CreateContext();
        context.Add(row);
        await context.SaveChangesAsync();
        return row;
    }

    internal static async Task<ProcessingAttempt?> ClaimAsync(TestDatabase database, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateContext();
        return await new IntegrationRunProcessing(context).ClaimAsync(cancellationToken);
    }

    internal static async Task<bool> CompleteAsync(TestDatabase database, ProcessingAttempt attempt, ProcessingResult result)
    {
        await using var context = database.CreateContext();
        return await new IntegrationRunProcessing(context).CompleteAsync(attempt, result);
    }

    internal static async Task<bool> RecoverAsync(TestDatabase database)
    {
        await using var context = database.CreateContext();
        return await new IntegrationRunProcessing(context).RecoverOneAsync();
    }

    internal static async Task<IntegrationRun> ReadAsync(TestDatabase database, Guid id)
    {
        await using var context = database.CreateContext();
        return await context.IntegrationRuns.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task Original_snapshots_are_excluded_and_intake_is_eligible()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyntheticSeed>().RunAsync();
        }
        Assert.Null(await ClaimAsync(database));
        var row = await InsertAsync(database);
        Assert.Equal(row.Id, (await ClaimAsync(database))!.RunId);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE idempotency_key_hash IS NULL AND status='Pending' AND attempt_count=1 AND lease_token IS NULL"));
    }

    [Fact]
    public async Task Older_due_retry_precedes_later_new_intake_and_future_retry_is_excluded()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var retry = await InsertAsync(database);
        await database.ExecuteAsync($"UPDATE integration_runs SET attempt_count=1, last_error='attempt_abandoned', next_attempt_at='2026-02-01 00:00:00+00' WHERE id='{retry.Id}'");
        var later = new List<IntegrationRun>();
        for (var i = 0; i < 6; i++) { later.Add(await InsertAsync(database)); }
        await database.ExecuteAsync($"UPDATE integration_runs SET received_at='2026-03-01 00:00:00+00' WHERE id<>'{retry.Id}'");
        Assert.Equal(retry.Id, (await ClaimAsync(database))!.RunId);
        var future = await InsertAsync(database);
        await database.ExecuteAsync($"UPDATE integration_runs SET attempt_count=1, last_error='attempt_abandoned', next_attempt_at=clock_timestamp()+interval '1 day' WHERE id='{future.Id}'");
        var extra = await InsertAsync(database);
        await database.ExecuteAsync($"UPDATE integration_runs SET received_at='2026-04-01 00:00:00+00' WHERE id='{extra.Id}'");
        foreach (var expected in later.OrderBy(r => r.Id)) { Assert.Equal(expected.Id, (await ClaimAsync(database))!.RunId); }
        Assert.Equal(extra.Id, (await ClaimAsync(database))!.RunId);
        Assert.Null(await ClaimAsync(database));
    }

    [Fact]
    public async Task Effective_time_ties_use_received_time_then_id()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var retry = await InsertAsync(database);
        var initial = await InsertAsync(database);
        await database.ExecuteAsync($"UPDATE integration_runs SET attempt_count=1, last_error='attempt_abandoned', next_attempt_at='2026-03-01 00:00:00+00' WHERE id='{retry.Id}'");
        await database.ExecuteAsync($"UPDATE integration_runs SET received_at='2026-03-01 00:00:00+00' WHERE id='{initial.Id}'");
        Assert.Equal(retry.Id, (await ClaimAsync(database))!.RunId);
        Assert.Equal(initial.Id, (await ClaimAsync(database))!.RunId);
    }

    [Fact]
    public async Task Coordinated_claimers_allocate_one_attempt_and_skip_locked_rows()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var first = await InsertAsync(database);
        var second = await InsertAsync(database);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand($"SELECT id FROM integration_runs WHERE id='{first.Id}' FOR UPDATE", connection, transaction);
        await locked.ExecuteScalarAsync();
        Assert.Equal(second.Id, (await ClaimAsync(database))!.RunId);
        await transaction.RollbackAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 8).Select(async _ => { await start.Task; return await ClaimAsync(database); }).ToArray();
        start.SetResult();
        var claims = (await Task.WhenAll(calls)).Where(c => c is not null).ToArray();
        Assert.Equal(first.Id, Assert.Single(claims)!.RunId);
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE attempt_count=1 AND lease_token IS NOT NULL"));
    }

    [Theory]
    [InlineData("Ordinary", 1, IntegrationRunStatus.Succeeded, null)]
    [InlineData("Synthetic:RetryOnce", 2, IntegrationRunStatus.Succeeded, null)]
    [InlineData("Synthetic:Fail", 1, IntegrationRunStatus.Failed, "synthetic_permanent_failure")]
    [InlineData("Synthetic:AlwaysRetry", 3, IntegrationRunStatus.Failed, "attempt_exhausted")]
    public async Task Execution_lifecycle_preserves_attempts_and_terminal_invariants(
        string operation, int attempts, IntegrationRunStatus terminal, string? error)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = await InsertAsync(database, operation);
        for (var n = 1; n <= attempts; n++)
        {
            var claim = (await ClaimAsync(database))!;
            Assert.Equal(n, claim.Number);
            Assert.True(await CompleteAsync(database, claim, await new SyntheticIntegrationRunProcessor().ExecuteAsync(claim, default)));
            if (n < attempts)
            {
                Assert.Null(await ClaimAsync(database));
                Assert.Equal(true, await database.ScalarAsync($"SELECT next_attempt_at > clock_timestamp() AND next_attempt_at <= clock_timestamp()+interval '{(n == 1 ? 5 : 10)} seconds' FROM integration_runs WHERE id='{row.Id}'"));
                await database.ExecuteAsync("UPDATE integration_runs SET next_attempt_at=clock_timestamp()-interval '1 second'");
            }
        }
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal(terminal, stored.Status); Assert.Equal(attempts, stored.AttemptCount);
        Assert.Equal(terminal == IntegrationRunStatus.Succeeded ? 1 : 0, stored.RecordCount);
        Assert.NotNull(stored.CompletedAt); Assert.True(stored.CompletedAt >= stored.ReceivedAt);
        Assert.Null(stored.LeaseToken); Assert.Null(stored.LeaseExpiresAt); Assert.Null(stored.NextAttemptAt);
        Assert.Equal(error, stored.LastError); Assert.Null(await ClaimAsync(database));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Expired_allocations_are_recovered_once_without_incrementing_and_stale_completions_are_fenced(int attempt)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = await InsertAsync(database);
        var old = (await ClaimAsync(database))!;
        await database.ExecuteAsync($"UPDATE integration_runs SET attempt_count={attempt}, lease_expires_at=clock_timestamp()-interval '1 second'");
        old = old with { Number = attempt };
        Assert.False(await CompleteAsync(database, old, new ProcessingResult(ProcessingOutcome.Success, 1)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 2).Select(async _ => { await gate.Task; return await RecoverAsync(database); }).ToArray();
        gate.SetResult();
        Assert.Single(await Task.WhenAll(calls), x => x);
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal(attempt, stored.AttemptCount); Assert.Null(stored.LeaseToken);
        Assert.Equal(attempt == 3 ? "attempt_exhausted" : "attempt_abandoned", stored.LastError);
        Assert.Equal(attempt == 3 ? IntegrationRunStatus.Failed : IntegrationRunStatus.Pending, stored.Status);
        if (attempt < 3)
        {
            Assert.Null(stored.CompletedAt); Assert.NotNull(stored.NextAttemptAt);
            await database.ExecuteAsync("UPDATE integration_runs SET next_attempt_at=clock_timestamp()-interval '1 second'");
            var replacement = (await ClaimAsync(database))!;
            Assert.NotEqual(old.LeaseToken, replacement.LeaseToken);
            Assert.Equal(attempt + 1, replacement.Number);
            Assert.False(await CompleteAsync(database, old, new ProcessingResult(ProcessingOutcome.Success, 99)));
            Assert.True(await CompleteAsync(database, replacement, new ProcessingResult(ProcessingOutcome.Success, 1)));
        }
        else { Assert.NotNull(stored.CompletedAt); Assert.Null(stored.NextAttemptAt); }
    }

    [Fact]
    public async Task Duplicate_completion_and_retry_scheduling_cannot_overwrite_the_winner()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = await InsertAsync(database);
        var claim = (await ClaimAsync(database))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new[] { new ProcessingResult(ProcessingOutcome.Success, 7), new ProcessingResult(ProcessingOutcome.Retryable, Error: ProcessingError.SyntheticRetryableFailure) }
            .Select(async result => { await start.Task; return await CompleteAsync(database, claim, result); }).ToArray();
        start.SetResult(); Assert.Single(await Task.WhenAll(calls), x => x);
        var before = await database.ScalarAsync("SELECT row_to_json(r)::text FROM integration_runs r");
        Assert.False(await CompleteAsync(database, claim, new ProcessingResult(ProcessingOutcome.Success, 100)));
        Assert.Equal(before, await database.ScalarAsync("SELECT row_to_json(r)::text FROM integration_runs r"));
        Assert.Equal(1, (await ReadAsync(database, row.Id)).AttemptCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_completion_racing_recovery_cannot_commit_success(bool expireWhileWaiting)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = await InsertAsync(database);
        var claim = (await ClaimAsync(database))!;
        if (!expireWhileWaiting)
        {
            await database.ExecuteAsync("UPDATE integration_runs SET lease_expires_at=clock_timestamp()-interval '1 second'");
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var rowLock = new NpgsqlCommand("SELECT id FROM integration_runs WHERE id=@id FOR UPDATE", connection, transaction);
        rowLock.Parameters.AddWithValue("id", row.Id);
        await rowLock.ExecuteScalarAsync();
        await using var pidCommand = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
        var blockerPid = (int)(await pidCommand.ExecuteScalarAsync())!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var context = database.CreateContext();
        var completion = new IntegrationRunProcessing(context).CompleteAsync(
            claim, new ProcessingResult(ProcessingOutcome.Success, 1), deadline.Token);
        try
        {
            // Observe the actual database lock wait, not a scheduler delay or an assumed overlap.
            await ProcessingWorkerTests.UntilAsync(async () => Equals(await database.ScalarAsync($"""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname=current_database() AND wait_event_type='Lock'
                    AND {blockerPid}=ANY(pg_blocking_pids(pid)))
                """), true), seconds: 5);
            Assert.False(completion.IsCompleted);
            if (expireWhileWaiting)
            {
                // Expiry is set AFTER the completion transaction started and blocked.
                // A transaction-start/before-lock clock would incorrectly accept success.
                await using var expire = new NpgsqlCommand("""
                    UPDATE integration_runs SET lease_expires_at=clock_timestamp() WHERE id=@id
                    """, connection, transaction);
                expire.Parameters.AddWithValue("id", row.Id);
                await expire.ExecuteNonQueryAsync(deadline.Token);
            }

            // SKIP LOCKED must permit recovery to make no progress while this row is locked.
            Assert.False(await RecoverAsync(database));
            await transaction.CommitAsync(deadline.Token);
            Assert.False(await completion);
        }
        finally
        {
            await deadline.CancelAsync();
            // Commit above owns successful lock release and persists the expiry change.
            // await using disposes the transaction, rolling back only if it is still pending.
            // Observe cancellation/fault before disposing the context used by the pending operation.
            try { await completion; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        Assert.True(await RecoverAsync(database));
        var recovered = await ReadAsync(database, row.Id);
        Assert.Equal(IntegrationRunStatus.Pending, recovered.Status);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Equal(0, recovered.RecordCount);
        Assert.Equal("attempt_abandoned", recovered.LastError);
        Assert.Null(recovered.CompletedAt);
        Assert.Null(recovered.LeaseToken);
        Assert.NotNull(recovered.NextAttemptAt);
        Assert.False(await CompleteAsync(database, claim, new ProcessingResult(ProcessingOutcome.Success, 1)));
    }

    [Fact]
    public async Task Claim_failure_rolls_back_and_prevents_return_of_an_attempt()
    {
        await using var database = await postgres.CreateDatabaseAsync(); await InsertAsync(database);
        // A deferred constraint trigger rejects COMMIT, after allocation UPDATE has executed.
        await database.ExecuteAsync("""
            CREATE FUNCTION reject_claim() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                RAISE EXCEPTION 'synthetic commit rejection'; RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER reject_claim AFTER UPDATE ON integration_runs
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_claim();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => ClaimAsync(database));
        Assert.Equal(true, await database.ScalarAsync("SELECT attempt_count=0 AND lease_token IS NULL FROM integration_runs"));
    }

    [Fact]
    public async Task Cancellation_of_a_claim_blocked_in_postgresql_leaves_no_allocation()
    {
        await using var database = await postgres.CreateDatabaseAsync(); await InsertAsync(database);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand("LOCK TABLE integration_runs IN ACCESS EXCLUSIVE MODE", connection, transaction);
        await locked.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource();
        var claim = ClaimAsync(database, cancellation.Token);
        try
        {
            await ProcessingWorkerTests.UntilAsync(async () => Equals(await database.ScalarAsync(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%integration_runs%')"), true));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claim);
        }
        finally { await cancellation.CancelAsync(); await transaction.RollbackAsync(); }
        Assert.Equal(true, await database.ScalarAsync("SELECT attempt_count=0 AND lease_token IS NULL FROM integration_runs"));
    }

    [Fact]
    public async Task Lost_claim_acknowledgement_returns_no_attempt_but_durable_allocation_is_recoverable()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var interceptor = new LoseAcknowledgement();
        await using (var context = new IntegrationOpsDbContext(new DbContextOptionsBuilder<IntegrationOpsDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptor).Options))
        {
            await Assert.ThrowsAsync<NpgsqlException>(() => new IntegrationRunProcessing(context).ClaimAsync());
        }
        Assert.Equal(1, interceptor.Commits);
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal(1, stored.AttemptCount); Assert.NotNull(stored.LeaseToken);
        Assert.Null(await ClaimAsync(database));
        await database.ExecuteAsync("UPDATE integration_runs SET lease_expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await RecoverAsync(database));
        Assert.Equal("attempt_abandoned", (await ReadAsync(database, row.Id)).LastError);
    }

    [Fact]
    public async Task Lost_completion_acknowledgement_does_not_undo_or_repeat_committed_success()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var attempt = (await ClaimAsync(database))!;
        var interceptor = new LoseAcknowledgement();
        await using (var context = new IntegrationOpsDbContext(new DbContextOptionsBuilder<IntegrationOpsDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptor).Options))
        {
            await Assert.ThrowsAsync<NpgsqlException>(() => new IntegrationRunProcessing(context)
                .CompleteAsync(attempt, new ProcessingResult(ProcessingOutcome.Success, 7)));
        }
        Assert.Equal(1, interceptor.Commits);
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal(IntegrationRunStatus.Succeeded, stored.Status); Assert.Equal(7, stored.RecordCount);
        Assert.Null(await ClaimAsync(database)); Assert.False(await RecoverAsync(database));
        Assert.False(await CompleteAsync(database, attempt, new ProcessingResult(ProcessingOutcome.Success, 99)));
        Assert.Equal(7, (await ReadAsync(database, row.Id)).RecordCount);
    }

    // Deterministic fault after the real PostgreSQL commit, not a claim to reproduce every network timing.
    private sealed class LoseAcknowledgement : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new NpgsqlException("Synthetic lost acknowledgement", new TimeoutException());
        }
    }

    [Theory]
    [InlineData("attempt_count=4")]
    [InlineData("attempt_count=1")]
    [InlineData("record_count=1")]
    [InlineData("completed_at=received_at")]
    [InlineData("last_error='private exception'")]
    [InlineData("lease_token=gen_random_uuid()")]
    [InlineData("lease_expires_at=clock_timestamp()")]
    [InlineData("next_attempt_at=clock_timestamp()")]
    [InlineData("status='Succeeded'")]
    [InlineData("status='Failed'")]
    [InlineData("status='Failed', attempt_count=1, completed_at=received_at, last_error='attempt_exhausted'")]
    [InlineData("attempt_count=1, next_attempt_at=clock_timestamp(), last_error='synthetic_permanent_failure'")]
    [InlineData("attempt_count=3, next_attempt_at=clock_timestamp(), last_error='attempt_abandoned'")]
    [InlineData("attempt_count=1, lease_token=gen_random_uuid(), lease_expires_at=clock_timestamp(), last_error='attempt_abandoned'")]
    [InlineData("attempt_count=1, lease_token=gen_random_uuid(), lease_expires_at='infinity'")]
    [InlineData("attempt_count=1, next_attempt_at='-infinity', last_error='attempt_abandoned'")]
    [InlineData("attempt_count=1, next_attempt_at='10000-01-01 00:00:00+00', last_error='attempt_abandoned'")]
    public async Task PostgreSql_rejects_invalid_processing_shapes(string mutation)
    {
        await using var database = await postgres.CreateDatabaseAsync(); await InsertAsync(database);
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE integration_runs SET " + mutation));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(0, (await ReadAsync(database, (Guid)(await database.ScalarAsync("SELECT id FROM integration_runs"))!)).AttemptCount);
    }

    [Fact]
    public async Task Legacy_rows_cannot_acquire_processing_metadata()
    {
        await using var database = await postgres.CreateDatabaseAsync(); await InsertAsync(database);
        await database.ExecuteAsync("UPDATE integration_runs SET idempotency_key_hash=NULL, request_fingerprint=NULL");
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE integration_runs SET next_attempt_at=clock_timestamp()"));
        Assert.Equal("ck_integration_runs_processing_origin", error.ConstraintName);
    }

    [Fact]
    public async Task New_timestamps_round_trip_utc_microseconds_and_finite_extremes()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var values = new[] { new DateTime(0, DateTimeKind.Utc), DateTime.UtcNow.AddTicks(7), new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Utc) };
        foreach (var value in values)
        {
            await using (var context = database.CreateContext())
            {
                var stored = await context.IntegrationRuns.SingleAsync();
                stored.AttemptCount = 1; stored.LeaseToken = Guid.NewGuid(); stored.LeaseExpiresAt = value;
                await context.SaveChangesAsync();
            }
            var read = await ReadAsync(database, row.Id);
            Assert.Equal(value.Ticks - value.Ticks % 10, read.LeaseExpiresAt!.Value.Ticks);
            Assert.Equal(DateTimeKind.Utc, read.LeaseExpiresAt.Value.Kind);
            await using (var context = database.CreateContext())
            {
                var stored = await context.IntegrationRuns.SingleAsync();
                stored.LeaseToken = null; stored.LeaseExpiresAt = null; stored.NextAttemptAt = value; stored.LastError = "attempt_abandoned";
                await context.SaveChangesAsync();
            }
            read = await ReadAsync(database, row.Id);
            Assert.Equal(value.Ticks - value.Ticks % 10, read.NextAttemptAt!.Value.Ticks);
            Assert.Equal(DateTimeKind.Utc, read.NextAttemptAt.Value.Kind);
            await database.ExecuteAsync("UPDATE integration_runs SET next_attempt_at=NULL,last_error=NULL,attempt_count=0");
        }
    }
}
