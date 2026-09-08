using System.Net;
using System.Text.Json;
using IntegrationOps.Api.Data;
using IntegrationOps.Api.IntegrationRuns;
using Npgsql;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class PersistenceApiTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Empty_migrated_database_is_ready_and_returns_an_empty_array()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/api/integration-runs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Persisted_rows_have_deterministic_order_and_survive_a_new_application_host()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var early = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        var firstId = Guid.Parse("00000002-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var thirdId = Guid.Parse("00000001-0000-0000-0000-000000000001");
        await using (var writer = database.CreateContext())
        {
            writer.AddRange(Row(thirdId, early.AddDays(1)), Row(secondId, early), Row(firstId, early));
            await writer.SaveChangesAsync();
        }

        string firstBody;
        await using (var firstHost = new DatabaseApiFactory(database.ConnectionString))
        {
            using var client = firstHost.CreateHttpsClient();
            firstBody = await client.GetStringAsync("/api/integration-runs");
        }

        await using var secondHost = new DatabaseApiFactory(database.ConnectionString);
        using var secondClient = secondHost.CreateHttpsClient();
        var secondBody = await secondClient.GetStringAsync("/api/integration-runs");
        Assert.Equal(firstBody, secondBody);
        using var json = JsonDocument.Parse(secondBody);
        Assert.Equal(new[] { firstId, secondId, thirdId },
            json.RootElement.EnumerateArray().Select(run => run.GetProperty("id").GetGuid()).ToArray());
        Assert.All(json.RootElement.EnumerateArray(),
            run => Assert.Equal("Synthetic persisted partner", run.GetProperty("partner").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_table_or_required_column_fails_readiness_and_get_safely(bool dropColumn)
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: dropColumn);
        if (dropColumn)
        {
            await database.ExecuteAsync("ALTER TABLE integration_runs DROP COLUMN last_error");
        }

        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        await AssertUnavailableAsync(client);
    }

    [Fact]
    public async Task Database_outage_changes_readiness_but_not_liveness_and_recovers()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = Row(Guid.Parse("00000003-0000-0000-0000-000000000001"),
            new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc));
        await using (var writer = database.CreateContext())
        {
            writer.Add(row);
            await writer.SaveChangesAsync();
        }

        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        using (var ready = await client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        }

        var originalBody = await client.GetStringAsync("/api/integration-runs");
        using (var json = JsonDocument.Parse(originalBody))
        {
            Assert.Equal(row.Id, Assert.Single(json.RootElement.EnumerateArray()).GetProperty("id").GetGuid());
        }

        try
        {
            await postgres.PauseAsync();
            await AssertUnavailableAsync(client);
        }
        finally
        {
            await postgres.UnpauseAsync();
        }

        await WaitForReadinessRecoveryAsync(client);
        Assert.Equal(originalBody, await client.GetStringAsync("/api/integration-runs"));
    }

    [Fact]
    public async Task Persisted_row_survives_container_restart_with_a_fresh_connection_string()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var row = Row(Guid.Parse("00000004-0000-0000-0000-000000000001"),
            new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));
        row.Partner = "Synthetic restart durability partner";
        await using (var writer = database.CreateContext())
        {
            writer.Add(row);
            await writer.SaveChangesAsync();
        }

        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
        try
        {
            await postgres.StopAsync();
        }
        finally
        {
            await postgres.StartAsync();
        }

        // A restart can remap the host port. Reconnect to the existing database; do not recreate it.
        await using var connection = new NpgsqlConnection(postgres.GetApplicationConnectionString(database.Name));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT partner FROM integration_runs WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", row.Id);
        Assert.Equal(row.Partner, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Http_cancellation_cancels_a_query_waiting_inside_PostgreSql()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var factory = new DatabaseApiFactory(database.ConnectionString);
        using var client = factory.CreateHttpsClient();
        await using var blocker = new NpgsqlConnection(database.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE integration_runs IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync("/api/integration-runs", cancellation.Token);
        try
        {
            await WaitForBlockedQueryAsync(database, expected: true);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
            // Keep the blocking transaction open: disappearance must result from database cancellation.
            await WaitForBlockedQueryAsync(database, expected: false);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static async Task WaitForReadinessRecoveryAsync(
        HttpClient client, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        HttpStatusCode? lastStatus = null;
        try
        {
            while (true)
            {
                using (var response = await client.GetAsync("/health/ready", deadline.Token))
                {
                    lastStatus = response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(deadline.Token));
                        deadline.Token.ThrowIfCancellationRequested();
                        return;
                    }

                    Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            var observed = lastStatus.HasValue ? $"{(int)lastStatus.Value} ({lastStatus.Value})" : "none";
            throw new TimeoutException(
                $"Application readiness did not recover within 15 seconds. Last observed HTTP status: {observed}.",
                exception);
        }
    }

    private static async Task WaitForBlockedQueryAsync(TestDatabase database, bool expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var monitor = new NpgsqlConnection(database.ConnectionString);
        await monitor.OpenAsync(deadline.Token);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%integration_runs%'
                )
                """, monitor);
            var blocked = (bool)(await command.ExecuteScalarAsync(deadline.Token))!;
            if (blocked == expected)
            {
                return;
            }

            await Task.Delay(50, deadline.Token);
        }
    }

    private static async Task AssertUnavailableAsync(HttpClient client)
    {
        using var live = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        using var runs = await client.GetAsync("/api/integration-runs");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, runs.StatusCode);
        Assert.Equal("application/problem+json", runs.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", runs.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await runs.Content.ReadAsStringAsync());
        Assert.Equal(503, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Integration data is unavailable.", json.RootElement.GetProperty("title").GetString());
        Assert.All(json.RootElement.EnumerateObject(),
            property => Assert.Contains(property.Name, new[] { "type", "title", "status", "traceId" }));
    }

    private static IntegrationRun Row(Guid id, DateTime receivedAt) => new()
    {
        Id = id,
        Partner = "Synthetic persisted partner",
        Operation = "Synthetic operation",
        Status = IntegrationRunStatus.Pending,
        AttemptCount = 1,
        RecordCount = 0,
        ReceivedAt = receivedAt
    };
}
