using System.Net;
using System.Text;
using System.Text.Json;
using IntegrationOps.Api.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static IntegrationOps.Api.Tests.ProcessingPersistenceTests;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class ProcessingWorkerTests(PostgresFixture postgres)
{
    private const string Malicious = "secret-password SQL host=private stacktrace partner-body";

    [Fact]
    public async Task One_loop_recovers_one_then_claims_and_execution_has_no_open_database_transaction()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        for (var n = 0; n < 3; n++)
        {
            await InsertAsync(database); await ClaimAsync(database);
        }
        await database.ExecuteAsync("UPDATE integration_runs SET lease_expires_at=clock_timestamp()-interval '1 second'");
        var ready = await InsertAsync(database);
        var entered = new TaskCompletionSource<ProcessingAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DelegateProcessor(async (attempt, token) =>
        {
            // The processor sees the claim from a separate connection: allocation is committed.
            var durable = await ReadAsync(database, attempt.RunId);
            Assert.Equal(attempt.LeaseToken, durable.LeaseToken);
            entered.SetResult(attempt);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ProcessingResult(ProcessingOutcome.Success, 1);
        });
        await using var factory = new DatabaseApiFactory(database.ConnectionString, true, processor);
        using var client = factory.CreateHttpsClient();
        Assert.Equal(ready.Id, (await entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).RunId);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE last_error='attempt_abandoned'"));
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE lease_expires_at < clock_timestamp()"));
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND backend_type='client backend' AND pid<>pg_backend_pid()"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_or_invalid_processor_result_is_safe_terminal_failure(bool invalid)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = await InsertAsync(database);
        var processor = new DelegateProcessor((_, _) => invalid
            ? Task.FromResult(new ProcessingResult(ProcessingOutcome.Success, -1))
            : Task.FromException<ProcessingResult>(new InvalidOperationException(Malicious)));
        await using var factory = new DatabaseApiFactory(database.ConnectionString, true, processor);
        using var client = factory.CreateHttpsClient();
        await UntilAsync(async () => (await ReadAsync(database, row.Id)).Status == IntegrationOps.Api.IntegrationRuns.IntegrationRunStatus.Failed);
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal("internal_processing_failure", stored.LastError); Assert.Equal(1, stored.AttemptCount);
        using var response = await client.GetAsync($"/api/integration-runs/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Malicious, body); Assert.Contains("internal_processing_failure", body);
    }

    [Fact]
    public async Task Deadline_schedules_retry_but_shutdown_leaves_allocation_for_recovery()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new DelegateProcessor(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.TrySetResult(); }
            return new ProcessingResult(ProcessingOutcome.Success, 1);
        });
        await using (var factory = new DatabaseApiFactory(database.ConnectionString, true, blocking))
        {
            using var client = factory.CreateHttpsClient();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(async () => (await ReadAsync(database, row.Id)).LastError == "processing_deadline_exceeded", 14);
            Assert.True(stopped.Task.IsCompleted);
            var stored = await ReadAsync(database, row.Id);
            Assert.Equal(1, stored.AttemptCount); Assert.Null(stored.LeaseToken); Assert.NotNull(stored.NextAttemptAt);
        }

        await database.ExecuteAsync("UPDATE integration_runs SET next_attempt_at=clock_timestamp()-interval '1 second'");
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new DatabaseApiFactory(database.ConnectionString, true, blocking);
        using (var client = second.CreateHttpsClient())
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await second.DisposeAsync();
        }
        Assert.True(stopped.Task.IsCompleted);
        var abandoned = await ReadAsync(database, row.Id);
        Assert.Equal(2, abandoned.AttemptCount); Assert.NotNull(abandoned.LeaseToken); Assert.Null(abandoned.LastError);
        Assert.Null(abandoned.CompletedAt);
    }

    [Fact]
    public async Task Replacement_host_recovers_abandoned_allocation()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var original = new DatabaseApiFactory(database.ConnectionString, true, new DelegateProcessor(async (_, token) =>
        {
            entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new ProcessingResult(ProcessingOutcome.Success, 1);
        })))
        {
            using var client = original.CreateHttpsClient(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await database.ExecuteAsync("UPDATE integration_runs SET lease_expires_at=clock_timestamp()-interval '1 second'");
        await using var replacement = new DatabaseApiFactory(database.ConnectionString, true);
        using var replacementClient = replacement.CreateHttpsClient();
        await UntilAsync(async () => (await ReadAsync(database, row.Id)).LastError == "attempt_abandoned");
        await database.ExecuteAsync("UPDATE integration_runs SET next_attempt_at=clock_timestamp()-interval '1 second'");
        await UntilAsync(async () => (await ReadAsync(database, row.Id)).Status == IntegrationOps.Api.IntegrationRuns.IntegrationRunStatus.Succeeded);
        Assert.Equal(2, (await ReadAsync(database, row.Id)).AttemptCount);
    }

    [Fact]
    public async Task Multiple_hosts_process_distinct_allocations_and_preserve_terminal_results()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        for (var i = 0; i < 8; i++) { await InsertAsync(database); }
        var arrivals = new System.Collections.Concurrent.ConcurrentBag<ProcessingAttempt>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DelegateProcessor(async (attempt, token) =>
        {
            arrivals.Add(attempt); if (arrivals.Count >= 2) { both.TrySetResult(); }
            await both.Task.WaitAsync(token); return new ProcessingResult(ProcessingOutcome.Success, 1);
        });
        await using var first = new DatabaseApiFactory(database.ConnectionString, true, processor);
        await using var second = new DatabaseApiFactory(database.ConnectionString, true, processor);
        using var a = first.CreateHttpsClient(); using var b = second.CreateHttpsClient();
        await UntilAsync(async () => Equals(await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE status='Succeeded'"), 8L));
        Assert.Equal(8, arrivals.Count); Assert.Equal(8, arrivals.Select(x => (x.RunId, x.Number)).Distinct().Count());
        Assert.Equal(true, await database.ScalarAsync("SELECT bool_and(attempt_count=1 AND lease_token IS NULL AND record_count=1) FROM integration_runs"));
    }

    [Fact]
    public async Task Coordination_defect_stops_host_and_does_not_fail_a_run()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new DatabaseApiFactory(database.ConnectionString, true, new DelegateProcessor(async (_, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token); return new ProcessingResult(ProcessingOutcome.Success, 1);
        }));
        using var client = factory.CreateHttpsClient();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopped.TrySetResult());
        await database.ExecuteAsync("ALTER TABLE integration_runs ADD CONSTRAINT synthetic_reject_success CHECK (status <> 'Succeeded')");
        release.SetResult();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal(IntegrationOps.Api.IntegrationRuns.IntegrationRunStatus.Pending, stored.Status);
        Assert.Null(stored.LastError); Assert.NotNull(stored.LeaseToken);
    }

    [Fact]
    public async Task Rejected_claim_commit_never_invokes_processor_and_stops_host()
    {
        await using var database = await postgres.CreateDatabaseAsync(); await InsertAsync(database);
        await database.ExecuteAsync("""
            CREATE FUNCTION reject_claim_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                RAISE EXCEPTION 'synthetic commit rejection'; RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER reject_claim_commit AFTER UPDATE ON integration_runs
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION reject_claim_commit();
            """);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand("SELECT id FROM integration_runs FOR UPDATE", connection, transaction);
        await locked.ExecuteScalarAsync();
        var count = 0;
        await using var factory = new DatabaseApiFactory(database.ConnectionString, true, new DelegateProcessor((_, _) =>
        {
            Interlocked.Increment(ref count); return Task.FromResult(new ProcessingResult(ProcessingOutcome.Success, 1));
        }));
        using var client = factory.CreateHttpsClient();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        await transaction.RollbackAsync();
        await UntilAsync(() => Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested));
        Assert.Equal(0, count);
        Assert.Equal(true, await database.ScalarAsync("SELECT attempt_count=0 AND lease_token IS NULL FROM integration_runs"));
    }

    [Fact]
    public async Task Outage_before_claim_allocates_nothing_and_same_host_recovers_at_same_endpoint()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var locked = new NpgsqlCommand($"SELECT id FROM integration_runs WHERE id='{row.Id}' FOR UPDATE", connection, transaction);
        await locked.ExecuteScalarAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString, true);
        using var client = factory.CreateHttpsClient();
        var paused = false;
        try
        {
            await postgres.PauseAsync(); paused = true;
            using var live = await client.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => ClaimAsync(database));
            Assert.True(IntegrationRunProcessing.IsDatabaseUnavailable(error));
        }
        finally { if (paused) { await postgres.UnpauseAsync(); } }
        await UntilAsync(async () =>
        {
            try { return (await ReadAsync(database, row.Id)).AttemptCount == 0; }
            catch (Exception e) when (IntegrationRunProcessing.IsDatabaseUnavailable(e)) { return false; }
        });
        await transaction.RollbackAsync();
        await UntilAsync(async () => (await ReadAsync(database, row.Id)).Status == IntegrationOps.Api.IntegrationRuns.IntegrationRunStatus.Succeeded);
        Assert.False(factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task Database_outage_during_completion_does_not_create_a_run_failure()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var attempt = (await ClaimAsync(database))!;
        var paused = false;
        try
        {
            await postgres.PauseAsync(); paused = true;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => CompleteAsync(database, attempt, new ProcessingResult(ProcessingOutcome.Success, 7)));
            Assert.True(IntegrationRunProcessing.IsDatabaseUnavailable(error));
        }
        finally { if (paused) { await postgres.UnpauseAsync(); } }
        await UntilAsync(async () =>
        {
            try { return (await ReadAsync(database, row.Id)).LeaseToken == attempt.LeaseToken; }
            catch (Exception e) when (IntegrationRunProcessing.IsDatabaseUnavailable(e)) { return false; }
        });
        var stored = await ReadAsync(database, row.Id);
        Assert.Equal(IntegrationOps.Api.IntegrationRuns.IntegrationRunStatus.Pending, stored.Status);
        Assert.Equal(0, stored.RecordCount); Assert.Null(stored.LastError);
        await database.ExecuteAsync("UPDATE integration_runs SET lease_expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await RecoverAsync(database));
    }

    [Fact]
    public async Task Explicit_seed_with_processing_enabled_does_not_start_the_worker_or_http_host()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--environment"); start.ArgumentList.Add("Development"); start.ArgumentList.Add("--seed-synthetic");
        start.Environment["Processing__Enabled"] = "true";
        start.Environment["ConnectionStrings__IntegrationOps"] = database.ConnectionString;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Synthetic seed completed. Inserted 3 canonical rows.", await output);
        Assert.DoesNotContain("ProcessingClaimed", await output);
        Assert.DoesNotContain("Now listening", await output);
        Assert.Equal(string.Empty, await errors);
        Assert.Equal(0, (await ReadAsync(database, row.Id)).AttemptCount);
    }

    [Fact]
    public async Task Completion_result_not_relied_upon_is_safe_after_host_replacement()
    {
        await using var database = await postgres.CreateDatabaseAsync(); var row = await InsertAsync(database);
        var attempt = (await ClaimAsync(database))!;
        // Commit the outcome but intentionally do not rely on its returned acknowledgement.
        _ = await CompleteAsync(database, attempt, new ProcessingResult(ProcessingOutcome.Success, 9));
        await using var replacement = new DatabaseApiFactory(database.ConnectionString, true);
        using var client = replacement.CreateHttpsClient();
        Assert.Null(await ClaimAsync(database));
        Assert.False(await CompleteAsync(database, attempt, new ProcessingResult(ProcessingOutcome.Success, 999)));
        Assert.Equal(9, (await ReadAsync(database, row.Id)).RecordCount);
    }

    [Fact]
    public async Task Replay_returns_processed_current_state_and_readiness_requires_processing_columns()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString, true);
        using var client = factory.CreateHttpsClient();
        using (var ready = await client.GetAsync("/health/ready")) { Assert.Equal(HttpStatusCode.OK, ready.StatusCode); }
        async Task<HttpResponseMessage> Post()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integration-runs")
            { Content = new StringContent("""{"partner":"Synthetic","operation":"Ordinary"}""", Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", "processing-replay"); return await client.SendAsync(request);
        }
        using var created = await Post(); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var firstBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = firstBody.RootElement.GetProperty("id").GetGuid();
        await UntilAsync(async () => (await ReadAsync(database, id)).Status == IntegrationOps.Api.IntegrationRuns.IntegrationRunStatus.Succeeded);
        using var replay = await Post(); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.CacheControl?.NoStore == true);
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(9, body.RootElement.EnumerateObject().Count());
        // Stop the worker host before intentionally breaking schema; verify readiness with worker disabled.
        await factory.DisposeAsync();
        await database.ExecuteAsync("ALTER TABLE integration_runs DROP COLUMN next_attempt_at CASCADE");
        await using var reader = new DatabaseApiFactory(database.ConnectionString);
        using var readClient = reader.CreateHttpsClient();
        using var unhealthy = await readClient.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        using var live = await readClient.GetAsync("/health"); Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    internal static async Task UntilAsync(Func<Task<bool>> predicate, int seconds = 10)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (!await predicate().WaitAsync(deadline.Token)) { await Task.Delay(50, deadline.Token); }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("Expected synthetic processing state was not observed before the test deadline.", exception); }
    }

    internal sealed class DelegateProcessor(Func<ProcessingAttempt, CancellationToken, Task<ProcessingResult>> execute) : IIntegrationRunProcessor
    {
        public Task<ProcessingResult> ExecuteAsync(ProcessingAttempt attempt, CancellationToken cancellationToken) => execute(attempt, cancellationToken);
    }
}
