using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace IntegrationOps.Api.Tests;

[Collection(PostgresFixture.CollectionName)]
public sealed class ProcessingMigrationTests(PostgresFixture postgres)
{
    private const string Phase2 = "20260907183837_IdempotentIntegrationRunIntake";
    private const string InitialRows = """
        INSERT INTO integration_runs(id,partner,operation,status,attempt_count,record_count,received_at,idempotency_key_hash,request_fingerprint)
        VALUES ('00000000-0000-0000-0000-000000000001','Synthetic legacy','Snapshot','Pending',1,0,'2026-01-01 00:00:00+00',NULL,NULL),
        ('00000000-0000-0000-0000-000000000002','Synthetic intake','Ordinary','Pending',0,0,'2026-01-01 00:00:00+00',decode(repeat('01',32),'hex'),decode(repeat('02',32),'hex'));
        """;
    private const string Snapshot = """
        SELECT json_agg(r ORDER BY id)::text FROM
        (SELECT id,partner,operation,status,attempt_count,record_count,received_at,completed_at,last_error,idempotency_key_hash,request_fingerprint FROM integration_runs) r
        """;

    [Fact]
    public async Task Forward_migration_preserves_legacy_and_intake_and_compatible_downgrade_is_lossless()
    {
        await using var database = await postgres.CreateDatabaseAsync(false);
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(Phase2);
        await database.ExecuteAsync(InitialRows);
        var before = await database.ScalarAsync(Snapshot);
        await context.Database.MigrateAsync();
        Assert.Equal(3, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(before, await database.ScalarAsync(Snapshot));
        Assert.Equal(true, await database.ScalarAsync("SELECT bool_and(lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL) FROM integration_runs"));
        await context.GetService<IMigrator>().MigrateAsync(Phase2);
        Assert.Equal(before, await database.ScalarAsync(Snapshot));
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name='integration_runs' AND column_name IN ('lease_token','lease_expires_at','next_attempt_at')"));
        Assert.Equal(true, await database.ScalarAsync("SELECT to_regclass('ix_integration_runs_processing_ready') IS NULL"));
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task Ready_expression_index_has_exact_expression_key_order_predicate_and_properties()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT am.amname, i.indisunique, i.indisvalid, i.indisready, i.indnkeyatts,
                pg_get_indexdef(i.indexrelid,1,true), pg_get_indexdef(i.indexrelid,2,true), pg_get_indexdef(i.indexrelid,3,true),
                pg_get_expr(i.indpred,i.indrelid), i.indoption::text
            FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_am am ON am.oid=c.relam
            WHERE c.relname='ix_integration_runs_processing_ready'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("btree", reader.GetString(0)); Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2)); Assert.True(reader.GetBoolean(3)); Assert.Equal((short)3, reader.GetInt16(4));
        Assert.Equal("COALESCE(next_attempt_at, received_at)", reader.GetString(5));
        Assert.Equal("received_at", reader.GetString(6)); Assert.Equal("id", reader.GetString(7));
        // PostgreSQL deparses varchar equality using an explicit text cast.
        Assert.Equal("((idempotency_key_hash IS NOT NULL) AND ((status)::text = 'Pending'::text) AND (lease_token IS NULL))", reader.GetString(8));
        Assert.Equal("0 0 0", reader.GetString(9)); // ASC, default NULLS LAST for every key.
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("attempt_count=1")]
    [InlineData("last_error='private exception'")]
    [InlineData("status='Failed',attempt_count=1,completed_at=received_at,last_error='historical arbitrary error'")]
    public async Task Incompatible_upgrade_refuses_atomically_without_fabricating_state(string mutation)
    {
        await using var database = await postgres.CreateDatabaseAsync(false);
        await using var context = database.CreateContext(); await context.GetService<IMigrator>().MigrateAsync(Phase2);
        await database.ExecuteAsync(InitialRows);
        await database.ExecuteAsync("UPDATE integration_runs SET " + mutation + " WHERE idempotency_key_hash IS NOT NULL");
        var before = await database.ScalarAsync(Snapshot);
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal("Cannot enable Phase 3 with incompatible intake state.", error.MessageText);
        Assert.Equal(before, await database.ScalarAsync(Snapshot)); Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(0L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name='integration_runs' AND column_name='lease_token'"));
        Assert.Equal(true, await database.ScalarAsync("SELECT to_regclass('ix_integration_runs_processing_ready') IS NULL"));
    }

    [Theory]
    [InlineData("attempt_count=1,lease_token=gen_random_uuid(),lease_expires_at=clock_timestamp()+interval '60 seconds'")]
    [InlineData("attempt_count=1,next_attempt_at=clock_timestamp(),last_error='attempt_abandoned'")]
    [InlineData("status='Succeeded',attempt_count=1,record_count=1,completed_at=received_at")]
    [InlineData("status='Failed',attempt_count=3,completed_at=received_at,last_error='attempt_exhausted'")]
    public async Task Guarded_downgrade_preserves_schema_and_data_after_processing_begins(string mutation)
    {
        await using var database = await postgres.CreateDatabaseAsync();
        await database.ExecuteAsync(InitialRows);
        await database.ExecuteAsync("UPDATE integration_runs SET " + mutation + " WHERE idempotency_key_hash IS NOT NULL");
        var before = await database.ScalarAsync("SELECT json_agg(r ORDER BY id)::text FROM integration_runs r");
        await using var context = database.CreateContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>().MigrateAsync(Phase2));
        Assert.Equal("P0001", error.SqlState); Assert.Equal("Cannot downgrade while Phase 3 processing state exists.", error.MessageText);
        Assert.Equal(before, await database.ScalarAsync("SELECT json_agg(r ORDER BY id)::text FROM integration_runs r"));
        Assert.Equal(3, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(true, await database.ScalarAsync("SELECT to_regclass('ix_integration_runs_processing_ready') IS NOT NULL"));
        Assert.Equal(3L, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name='integration_runs' AND column_name IN ('lease_token','lease_expires_at','next_attempt_at')"));
    }
}
