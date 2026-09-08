using System.Net;
using System.Text;
using System.Text.Json;
using IntegrationOps.Api.Data;
using IntegrationOps.Api.IntegrationRuns;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class IntegrationRunIntakeApiTests(PostgresFixture postgres)
{
    private const string Path = "/api/integration-runs";
    private const string Body = IntegrationRunIntakeInputTests.ValidBody;

    [Fact]
    public async Task Creation_persists_normalized_server_state_and_location_and_list_expose_only_the_public_contract()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        const string body = """{"partner":"  Cafe\u0301  ","operation":" Import "}""";
        var before = UtcTimestamp.NormalizeUtc(DateTime.UtcNow);
        using var response = await PostAsync(client, "create-key", body);
        var after = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(response.Headers.Location);
        var run = await ReadAsync(response);
        Assert.NotEqual(Guid.Empty, run.Id);
        Assert.Equal("Café", run.Partner);
        Assert.Equal("Import", run.Operation);
        Assert.Equal("Pending", run.Status);
        Assert.Equal(0, run.AttemptCount);
        Assert.Equal(0, run.RecordCount);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.LastError);
        Assert.InRange(run.ReceivedAt.UtcDateTime, before, after);
        Assert.Equal(0, run.ReceivedAt.Ticks % 10);
        Assert.Equal(TimeSpan.Zero, run.ReceivedAt.Offset);
        using var resource = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        Assert.Equal(run, await ReadAsync(resource));
        using var listed = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal(run.Id, Assert.Single(list.RootElement.EnumerateArray()).GetProperty("id").GetGuid());

        await using var reader = database.CreateContext();
        var stored = Assert.Single(await reader.IntegrationRuns.AsNoTracking().ToArrayAsync());
        var input = await InputAsync("create-key", body);
        Assert.Equal(input.KeyHash, stored.IdempotencyKeyHash);
        Assert.Equal(input.Fingerprint, stored.RequestFingerprint);
        Assert.Equal(32, stored.IdempotencyKeyHash!.Length);
        Assert.Equal(32, stored.RequestFingerprint!.Length);
        Assert.Equal(DateTimeKind.Utc, stored.ReceivedAt.Kind);
    }

    [Fact]
    public async Task Sequential_replay_uses_semantics_conflicts_do_not_overwrite_and_distinct_keys_create_distinct_runs()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using var first = await PostAsync(client, "key", """{"partner":"Café","operation":"Import"}""");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var run = await ReadAsync(first);
        using var replay = await PostAsync(client, "key", """{ "operation":" Import ", "partner":" Cafe\u0301 " }""");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(run, await ReadAsync(replay));
        using var conflict = await PostAsync(client, "key", Body);
        await IntegrationRunIntakeInputTests.AssertProblemAsync(conflict, HttpStatusCode.Conflict,
            "Idempotency key conflicts with an existing request.");
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
        using var another = await PostAsync(client, "KEY", """{"partner":"Café","operation":"Import"}""");
        Assert.Equal(HttpStatusCode.Created, another.StatusCode);
        Assert.NotEqual(run.Id, (await ReadAsync(another)).Id);
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Fact]
    public async Task Replay_returns_current_operational_state_after_host_replacement()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        Guid id;
        await using (var factory = new DatabaseApiFactory(database.ConnectionString))
        {
            using var client = factory.CreateHttpsClient();
            using var created = await PostAsync(client, "current");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await ReadAsync(created)).Id;
        }
        // Direct fixture setup isolates current-state replay; hosted processing has separate lifecycle coverage.
        await database.ExecuteAsync("UPDATE integration_runs SET status = 'Succeeded', attempt_count = 1, record_count = 7, completed_at = received_at");
        await using var replacement = new DatabaseApiFactory(database.ConnectionString);
        using var replacementClient = replacement.CreateHttpsClient();
        using var replay = await PostAsync(replacementClient, "current");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var run = await ReadAsync(replay);
        Assert.Equal(id, run.Id);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, run.AttemptCount);
        Assert.Equal(7, run.RecordCount);
        Assert.Equal(run.ReceivedAt, run.CompletedAt);
    }

    [Theory]
    [InlineData("UPDATE integration_runs SET partner = 'Different Synthetic'")]
    [InlineData("UPDATE integration_runs SET request_fingerprint = decode(repeat('ff',32),'hex')")]
    public async Task Replay_requires_both_fingerprint_and_ordinal_labels(string mutation)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using var created = await PostAsync(client, "comparison");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await database.ExecuteAsync(mutation);
        using var replay = await PostAsync(client, "comparison");
        await IntegrationRunIntakeInputTests.AssertProblemAsync(replay, HttpStatusCode.Conflict);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Fact]
    public async Task Committed_response_need_not_be_observed_for_safe_retry_through_a_new_host()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using (var factory = new DatabaseApiFactory(database.ConnectionString))
        {
            using var client = factory.CreateHttpsClient();
            using var ignored = await PostAsync(client, "ambiguous");
            // Deliberately do not rely on status/body. Prove the committed branch independently below.
        }
        var id = Assert.IsType<Guid>(await database.ScalarAsync("SELECT id FROM integration_runs"));
        await using var second = new DatabaseApiFactory(database.ConnectionString);
        using var secondClient = second.CreateHttpsClient();
        using var replay = await PostAsync(secondClient, "ambiguous");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(id, (await ReadAsync(replay)).Id);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_requests_across_hosts_are_arbitrated_by_postgresql(bool differentRequests)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var first = new DatabaseApiFactory(database.ConnectionString);
        await using var second = new DatabaseApiFactory(database.ConnectionString);
        using var firstClient = first.CreateHttpsClient();
        using var secondClient = second.CreateHttpsClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = differentRequests ? 2 : 8;
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            await gate.Task;
            return await PostAsync(index % 2 == 0 ? firstClient : secondClient, "race",
                differentRequests && index == 1 ? """{"partner":"Other Synthetic","operation":"Import"}""" : Body);
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(tasks);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            if (differentRequests)
            {
                var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
                await IntegrationRunIntakeInputTests.AssertProblemAsync(conflict, HttpStatusCode.Conflict);
            }
            else
            {
                Assert.Equal(count - 1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            }
            var successful = responses.Where(response => response.IsSuccessStatusCode).ToArray();
            var runs = await Task.WhenAll(successful.Select(ReadAsync));
            Assert.Single(runs.Select(run => run.Id).Distinct());
            Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
            Assert.Equal(runs[0].Partner, await database.ScalarAsync("SELECT partner FROM integration_runs"));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uncommitted_key_holder_controls_insert_outcome_without_a_preliminary_read(bool commitHolder)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var holder = database.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var input = await InputAsync("held");
        var row = Candidate(input);
        holder.Add(row);
        await holder.SaveChangesAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        var pending = PostAsync(client, "held");
        try
        {
            await WaitForBlockedInsertAsync(database, true);
            Assert.False(pending.IsCompleted);
            if (commitHolder) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            using var response = await pending;
            Assert.Equal(commitHolder ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
            var result = await ReadAsync(response);
            if (commitHolder) Assert.Equal(row.Id, result.Id);
            else Assert.NotEqual(row.Id, result.Id);
            Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
        }
        finally
        {
            // Disposal rolls back an unfinished holder even when a synchronization assertion fails.
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelling_a_database_blocked_insert_propagates_and_does_not_create_a_row()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var blocker = database.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE integration_runs IN ACCESS EXCLUSIVE MODE");
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = PostAsync(client, "cancelled", cancellationToken: cancellation.Token);
        try
        {
            await WaitForBlockedInsertAsync(database, true);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await pending; });
            await WaitForBlockedInsertAsync(database, false);
        }
        finally
        {
            await cancellation.CancelAsync();
            await transaction.RollbackAsync();
        }
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Fact]
    public async Task Binary_lookup_translates_to_value_equality_and_uses_a_separate_equal_array()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var input = await InputAsync("binary");
        var row = Candidate(input);
        await using (var writer = database.CreateContext()) { writer.Add(row); await writer.SaveChangesAsync(); }
        await using var reader = database.CreateContext();
        var keyHash = input.KeyHash.ToArray();
        Assert.NotSame(input.KeyHash, keyHash);
        var query = reader.IntegrationRuns.AsNoTracking().Where(run =>
            run.IdempotencyKeyHash != null && run.IdempotencyKeyHash.SequenceEqual(keyHash));
        var sql = query.ToQueryString();
        Assert.Matches(@"idempotency_key_hash\s*=\s*@", sql);
        Assert.Equal(row.Id, (await query.SingleAsync()).Id);
    }

    [Fact]
    public async Task Absent_id_is_404_and_missing_schema_is_safe_for_both_new_endpoints()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using var absent = await client.GetAsync(Path + "/" + Guid.NewGuid());
        await IntegrationRunIntakeInputTests.AssertProblemAsync(absent, HttpStatusCode.NotFound, "Integration run was not found.");
        await database.ExecuteAsync("ALTER TABLE integration_runs DROP COLUMN request_fingerprint");
        using var post = await PostAsync(client, "missing-schema");
        await IntegrationRunIntakeInputTests.AssertProblemAsync(post, HttpStatusCode.ServiceUnavailable, "Integration data is unavailable.");
        using var get = await client.GetAsync(Path + "/" + Guid.NewGuid());
        await IntegrationRunIntakeInputTests.AssertProblemAsync(get, HttpStatusCode.ServiceUnavailable, "Integration data is unavailable.");
    }

    [Fact]
    public async Task Database_outage_returns_safe_503_for_intake_and_by_id()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        try
        {
            await postgres.PauseAsync();
            using var post = await PostAsync(client, "outage");
            await IntegrationRunIntakeInputTests.AssertProblemAsync(post, HttpStatusCode.ServiceUnavailable, "Integration data is unavailable.");
            using var get = await client.GetAsync(Path + "/" + Guid.NewGuid());
            await IntegrationRunIntakeInputTests.AssertProblemAsync(get, HttpStatusCode.ServiceUnavailable, "Integration data is unavailable.");
        }
        finally { await postgres.UnpauseAsync(); }
    }

    [Fact]
    public async Task Unrelated_integrity_failure_remains_safe_500()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync("ALTER TABLE integration_runs ADD CONSTRAINT synthetic_test_rejection CHECK (partner <> 'Synthetic Partner')");
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using var response = await PostAsync(client, "integrity");
        await IntegrationRunIntakeInputTests.AssertProblemAsync(response, HttpStatusCode.InternalServerError, "An unexpected error occurred.");
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    internal static async Task<IntegrationRunIntakeInput> InputAsync(string key, string body = Body)
    {
        var context = IntegrationRunIntakeInputTests.Request(body);
        context.Request.Headers["Idempotency-Key"] = key;
        return (await IntegrationRunIntakeInput.ReadAsync(context.Request, default)).Input!;
    }

    internal static IntegrationRun Candidate(IntegrationRunIntakeInput input) => new()
    {
        Id = Guid.NewGuid(),
        Partner = input.Request.Partner,
        Operation = input.Request.Operation,
        Status = IntegrationRunStatus.Pending,
        AttemptCount = 0,
        RecordCount = 0,
        ReceivedAt = DateTime.UtcNow,
        IdempotencyKeyHash = input.KeyHash,
        RequestFingerprint = input.Fingerprint
    };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key, string body = Body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<WireRun> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(new[] { "attemptCount", "completedAt", "id", "lastError", "operation", "partner", "receivedAt", "recordCount", "status" },
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        foreach (var name in new[] { "id", "partner", "operation", "status", "receivedAt" }) Assert.Equal(JsonValueKind.String, root.GetProperty(name).ValueKind);
        foreach (var name in new[] { "attemptCount", "recordCount" }) Assert.Equal(JsonValueKind.Number, root.GetProperty(name).ValueKind);
        foreach (var name in new[] { "completedAt", "lastError" }) Assert.Contains(root.GetProperty(name).ValueKind, new[] { JsonValueKind.String, JsonValueKind.Null });
        return JsonSerializer.Deserialize<WireRun>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task WaitForBlockedInsertAsync(TestDatabase database, bool expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                AND wait_event_type = 'Lock' AND query LIKE '%INSERT INTO integration_runs%')
            """, connection);
        try
        {
            while ((bool)(await command.ExecuteScalarAsync(deadline.Token))! != expected)
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"Expected blocked PostgreSQL insert state {expected} was not observed within five seconds.", exception);
        }
    }

    private sealed record WireRun(Guid Id, string Partner, string Operation, string Status, int AttemptCount,
        int RecordCount, DateTimeOffset ReceivedAt, DateTimeOffset? CompletedAt, string? LastError);
}
