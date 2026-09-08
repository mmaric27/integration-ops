using IntegrationOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class IntakeMigrationTests(PostgresFixture postgres)
{
    private const string Phase2 = "20260907183837_IdempotentIntegrationRunIntake";
    private const string Phase1 = "20260907124526_InitialPersistence";
    private const string LegacyInsert = """
        INSERT INTO integration_runs (id, partner, operation, status, attempt_count, record_count, received_at, completed_at, last_error) VALUES
        ('2b31a678-fcb8-4f8b-83d0-b22eaafc9fc7','Orion Booking','Reservation export','Succeeded',1,184,'2026-08-20 07:42:18+00','2026-08-20 07:42:51+00',NULL),
        ('14ac8db6-74c5-45fb-a022-5c06b113eb03','Harbor Payments','Settlement import','Pending',1,0,'2026-08-20 08:04:09+00',NULL,NULL),
        ('7c02116a-d6b7-41f3-8528-cbfc6813eb39','Atlas Inventory','Availability sync','Failed',3,0,'2026-08-20 08:11:32+00','2026-08-20 08:13:05+00','Partner endpoint returned HTTP 503 after three attempts.')
        """;
    private const string LegacySnapshot = """
        SELECT json_agg(r ORDER BY id)::text FROM
        (SELECT id, partner, operation, status, attempt_count, record_count, received_at, completed_at, last_error FROM integration_runs) r
        """;

    [Fact]
    public async Task Upgrade_preserves_every_canonical_field_and_leaves_metadata_null_then_legacy_downgrade_is_safe()
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Phase1);
        await database.ExecuteAsync(LegacyInsert); // Use only Phase 1 columns before the second migration.
        var original = await database.ScalarAsync(LegacySnapshot);
        await context.GetService<IMigrator>().MigrateAsync(Phase2);
        Assert.Equal(original, await database.ScalarAsync(LegacySnapshot));
        Assert.Equal(3L, await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE idempotency_key_hash IS NULL AND request_fingerprint IS NULL"));
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        await context.GetService<IMigrator>().MigrateAsync(Phase1);
        Assert.Equal(original, await database.ScalarAsync(LegacySnapshot));
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name='integration_runs' AND column_name IN ('idempotency_key_hash','request_fingerprint')"));
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE integration_runs SET attempt_count=0"));
        Assert.Equal("ck_integration_runs_attempt_count", error.ConstraintName);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task Downgrade_refuses_metadata_or_zero_attempts_without_changing_data_or_schema(bool metadata, int attempts)
    {
        await using var database = await postgres.CreateDatabaseAsync(migrate: false);
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Phase2);
        var hash = metadata ? "decode(repeat('01',32),'hex')" : "NULL";
        await database.ExecuteAsync(InsertSql(hash, hash));
        await database.ExecuteAsync($"UPDATE integration_runs SET attempt_count={attempts}");
        var before = await database.ScalarAsync("SELECT row_to_json(r)::text FROM integration_runs r");
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>().MigrateAsync(Phase1));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal("Cannot downgrade while Phase 2 intake data exists.", error.MessageText);
        Assert.Equal(before, await database.ScalarAsync("SELECT row_to_json(r)::text FROM integration_runs r"));
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(2L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name='integration_runs' AND column_name IN ('idempotency_key_hash','request_fingerprint')"));
        Assert.Equal(true, await database.ScalarAsync("SELECT to_regclass('ux_integration_runs_idempotency_key_hash') IS NOT NULL"));
        await context.GetService<IMigrator>().MigrateAsync(Phase2);
    }

    [Theory]
    [InlineData("NULL", "decode(repeat('01',32),'hex')", "ck_integration_runs_intake_metadata")]
    [InlineData("decode(repeat('01',32),'hex')", "NULL", "ck_integration_runs_intake_metadata")]
    [InlineData("decode(repeat('01',31),'hex')", "decode(repeat('02',32),'hex')", "ck_integration_runs_idempotency_key_hash_length")]
    [InlineData("decode(repeat('01',33),'hex')", "decode(repeat('02',32),'hex')", "ck_integration_runs_idempotency_key_hash_length")]
    [InlineData("decode(repeat('01',32),'hex')", "decode(repeat('02',31),'hex')", "ck_integration_runs_request_fingerprint_length")]
    [InlineData("decode(repeat('01',32),'hex')", "decode(repeat('02',33),'hex')", "ck_integration_runs_request_fingerprint_length")]
    public async Task Direct_sql_rejects_invalid_hash_pairs_and_lengths(string keySql, string fingerprintSql, string constraint)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(InsertSql(keySql, fingerprintSql)));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM integration_runs"));
    }

    [Fact]
    public async Task Direct_sql_allows_zero_and_multiple_null_pairs_but_enforces_binary_unique_index()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync(InsertSql("NULL", "NULL"));
        await database.ExecuteAsync(InsertSql("NULL", "NULL"));
        var sql = InsertSql("decode(repeat('01',32),'hex')", "decode(repeat('02',32),'hex')");
        await database.ExecuteAsync(sql);
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql));
        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_integration_runs_idempotency_key_hash", error.ConstraintName);
        Assert.Equal(3L, await database.ScalarAsync("SELECT count(*) FROM integration_runs WHERE attempt_count=0"));
        var negative = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync("UPDATE integration_runs SET attempt_count=-1"));
        Assert.Equal("ck_integration_runs_attempt_count", negative.ConstraintName);
    }

    // Fragments are fixed test data only. Each execution gets a distinct PostgreSQL-generated test ID.
    private static string InsertSql(string keySql, string fingerprintSql) => $"""
        INSERT INTO integration_runs (id, partner, operation, status, attempt_count, record_count, received_at, idempotency_key_hash, request_fingerprint)
        VALUES (gen_random_uuid(), 'Synthetic SQL', 'Import', 'Pending', 0, 0, '2026-09-07 00:00:00+00', {keySql}, {fingerprintSql})
        """;
}
