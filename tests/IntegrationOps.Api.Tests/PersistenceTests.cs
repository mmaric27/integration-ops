using IntegrationOps.Api.Data;
using IntegrationOps.Api.IntegrationRuns;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class PersistenceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Fresh_migration_creates_history_without_samples_and_can_be_reapplied()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(3, applied.Length);
        Assert.EndsWith("_DurableIntegrationRunProcessing", applied[2]);
        Assert.EndsWith("_IdempotentIntegrationRunIntake", applied[1]);
        Assert.EndsWith("_InitialPersistence", applied[0]);
        Assert.Equal(3L, await database.ScalarAsync("""SELECT count(*) FROM "__EFMigrationsHistory" """));
        Assert.Empty(await context.IntegrationRuns.AsNoTracking().ToArrayAsync());

        await context.Database.MigrateAsync();
        Assert.Equal(applied, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(true, await database.ScalarAsync(
            "SELECT current_user = 'integration_ops' AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole " +
            "FROM pg_roles WHERE rolname = current_user"));
    }

    [Theory]
    [InlineData("partner", "''", "23514", "ck_integration_runs_partner")]
    [InlineData("operation", "''", "23514", "ck_integration_runs_operation")]
    [InlineData("status", "'Processing'", "23514", "ck_integration_runs_status")]
    [InlineData("attempt_count", "-1", "23514", "ck_integration_runs_attempt_count")]
    [InlineData("record_count", "-1", "23514", "ck_integration_runs_record_count")]
    [InlineData("received_at", "'infinity'", "23514", "ck_integration_runs_received_at")]
    [InlineData("received_at", "'-infinity'", "23514", "ck_integration_runs_received_at")]
    [InlineData("received_at", "'10000-01-01 00:00:00+00'", "23514", "ck_integration_runs_received_at")]
    [InlineData("received_at", "'0001-01-01 00:00:00+00 BC'", "23514", "ck_integration_runs_received_at")]
    [InlineData("completed_at", "'infinity'", "23514", "ck_integration_runs_completed_at")]
    [InlineData("completed_at", "'10000-01-01 00:00:00+00'", "23514", "ck_integration_runs_completed_at")]
    [InlineData("completed_at", "'2026-08-19 00:00:00+00'", "23514", "ck_integration_runs_completion_order")]
    [InlineData("partner", "NULL", "23502", null)]
    [InlineData("operation", "NULL", "23502", null)]
    [InlineData("status", "NULL", "23502", null)]
    [InlineData("received_at", "NULL", "23502", null)]
    [InlineData("attempt_count", "NULL", "23502", null)]
    [InlineData("record_count", "NULL", "23502", null)]
    [InlineData("partner", "repeat('x', 161)", "22001", null)]
    [InlineData("operation", "repeat('x', 161)", "22001", null)]
    [InlineData("last_error", "repeat('x', 1001)", "22001", null)]
    [InlineData("attempt_count", "2147483648", "22003", null)]
    [InlineData("record_count", "2147483648", "22003", null)]
    public async Task PostgreSql_rejects_invalid_durable_state(
        string column, string invalidSql, string sqlState, string? constraint)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var values = new Dictionary<string, string>
        {
            ["id"] = "'5c807324-7c84-41c3-86da-dc0e59fd49a5'",
            ["partner"] = "'Synthetic partner'",
            ["operation"] = "'Synthetic operation'",
            ["status"] = "'Pending'",
            ["attempt_count"] = "1",
            ["record_count"] = "0",
            ["received_at"] = "'2026-08-20 00:00:00+00'",
            ["completed_at"] = "NULL",
            ["last_error"] = "NULL"
        };
        // Only fixed test cases supply these SQL fragments; bypass the entity to exercise PostgreSQL.
        values[column] = invalidSql;
        var sql = $"INSERT INTO integration_runs ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Values)})";
        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));
        Assert.Equal(sqlState, exception.SqlState);
        if (constraint is not null)
        {
            Assert.Equal(constraint, exception.ConstraintName);
        }

        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Fact]
    public async Task PostgreSql_enforces_primary_key_uniqueness()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        const string sql = """
            INSERT INTO integration_runs (id, partner, operation, status, attempt_count, record_count, received_at)
            VALUES ('5c807324-7c84-41c3-86da-dc0e59fd49a5', 'Synthetic', 'Test', 'Pending', 1, 0, '2026-08-20 00:00:00+00')
            """;
        await database.ExecuteAsync(sql);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("pk_integration_runs", exception.ConstraintName);
        Assert.Equal(1L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Fact]
    public async Task Utc_microseconds_and_finite_boundaries_round_trip_independently_of_session_timezone()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        DateTime[] input =
        [
            new(0, DateTimeKind.Utc),
            new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(1234567),
            new(DateTime.MaxValue.Ticks, DateTimeKind.Utc)
        ];
        var runs = input.Select(value => new IntegrationRun
        {
            Id = Guid.NewGuid(),
            Partner = new string('p', 160),
            Operation = new string('o', 160),
            Status = IntegrationRunStatus.Succeeded,
            AttemptCount = int.MaxValue,
            RecordCount = int.MaxValue,
            ReceivedAt = value,
            CompletedAt = value,
            LastError = new string('e', 1000)
        }).ToArray();
        await using (var writer = database.CreateContext())
        {
            writer.AddRange(runs);
            await writer.SaveChangesAsync();
        }

        await using var reader = database.CreateContext();
        await reader.Database.OpenConnectionAsync();
        await reader.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Pacific/Auckland'");
        var stored = await reader.IntegrationRuns.AsNoTracking().OrderBy(run => run.ReceivedAt).ToArrayAsync();
        for (var index = 0; index < input.Length; index++)
        {
            Assert.Equal(input[index].Ticks - input[index].Ticks % 10, stored[index].ReceivedAt.Ticks);
            Assert.Equal(DateTimeKind.Utc, stored[index].ReceivedAt.Kind);
            Assert.Equal(stored[index].ReceivedAt, stored[index].CompletedAt);
            Assert.Equal(DateTimeKind.Utc, stored[index].CompletedAt!.Value.Kind);
            var response = IntegrationRunResponse.FromPersistence(stored[index]);
            Assert.Equal(TimeSpan.Zero, response.ReceivedAt.Offset);
            Assert.Equal(stored[index].ReceivedAt.Ticks, response.ReceivedAt.UtcTicks);
        }

        Assert.Equal(true, await database.ScalarAsync(
            "SELECT bool_and(isfinite(received_at) AND isfinite(completed_at)) FROM integration_runs"));
    }
}
