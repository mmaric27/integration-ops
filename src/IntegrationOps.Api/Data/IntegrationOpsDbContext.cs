using Microsoft.EntityFrameworkCore;

namespace IntegrationOps.Api.Data;

public sealed class IntegrationOpsDbContext(DbContextOptions<IntegrationOpsDbContext> options)
    : DbContext(options)
{
    public DbSet<IntegrationRun> IntegrationRuns => Set<IntegrationRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<IntegrationRun>();
        run.ToTable("integration_runs", table =>
        {
            table.HasCheckConstraint("ck_integration_runs_lease_pair",
                "(lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");
            table.HasCheckConstraint("ck_integration_runs_lease_expires_at",
                $"lease_expires_at IS NULL OR ({TimestampBounds("lease_expires_at")})");
            table.HasCheckConstraint("ck_integration_runs_next_attempt_at",
                $"next_attempt_at IS NULL OR ({TimestampBounds("next_attempt_at")})");
            table.HasCheckConstraint("ck_integration_runs_processing_origin",
                "idempotency_key_hash IS NOT NULL OR (lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL)");
            table.HasCheckConstraint("ck_integration_runs_processing_state", ProcessingState);
            table.HasCheckConstraint("ck_integration_runs_partner", "char_length(partner) > 0");
            table.HasCheckConstraint("ck_integration_runs_operation", "char_length(operation) > 0");
            table.HasCheckConstraint("ck_integration_runs_status", "status IN ('Pending', 'Succeeded', 'Failed')");
            table.HasCheckConstraint("ck_integration_runs_attempt_count", "attempt_count >= 0");
            table.HasCheckConstraint("ck_integration_runs_intake_metadata",
                "(idempotency_key_hash IS NULL AND request_fingerprint IS NULL) OR (idempotency_key_hash IS NOT NULL AND request_fingerprint IS NOT NULL)");
            table.HasCheckConstraint("ck_integration_runs_idempotency_key_hash_length",
                "idempotency_key_hash IS NULL OR octet_length(idempotency_key_hash) = 32");
            table.HasCheckConstraint("ck_integration_runs_request_fingerprint_length",
                "request_fingerprint IS NULL OR octet_length(request_fingerprint) = 32");
            table.HasCheckConstraint("ck_integration_runs_record_count", "record_count >= 0");
            table.HasCheckConstraint("ck_integration_runs_received_at", TimestampBounds("received_at"));
            table.HasCheckConstraint("ck_integration_runs_completed_at",
                $"completed_at IS NULL OR ({TimestampBounds("completed_at")})");
            table.HasCheckConstraint("ck_integration_runs_completion_order",
                "completed_at IS NULL OR completed_at >= received_at");
        });

        run.HasKey(value => value.Id).HasName("pk_integration_runs");
        run.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        run.Property(value => value.Partner).HasColumnName("partner").HasMaxLength(160).IsRequired();
        run.Property(value => value.Operation).HasColumnName("operation").HasMaxLength(160).IsRequired();
        run.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        run.Property(value => value.AttemptCount).HasColumnName("attempt_count");
        run.Property(value => value.RecordCount).HasColumnName("record_count");
        run.Property(value => value.ReceivedAt).HasColumnName("received_at")
            .HasColumnType("timestamp(6) with time zone").UsePropertyAccessMode(PropertyAccessMode.Property);
        run.Property(value => value.CompletedAt).HasColumnName("completed_at")
            .HasColumnType("timestamp(6) with time zone").UsePropertyAccessMode(PropertyAccessMode.Property);
        run.Property(value => value.LeaseToken).HasColumnName("lease_token");
        run.Property(value => value.LeaseExpiresAt).HasColumnName("lease_expires_at")
            .HasColumnType("timestamp(6) with time zone").UsePropertyAccessMode(PropertyAccessMode.Property);
        run.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at")
            .HasColumnType("timestamp(6) with time zone").UsePropertyAccessMode(PropertyAccessMode.Property);
        run.HasIndex(value => new { value.LeaseExpiresAt, value.Id })
            .HasDatabaseName("ix_integration_runs_processing_expired")
            .HasFilter("idempotency_key_hash IS NOT NULL AND status = 'Pending' AND lease_token IS NOT NULL");
        // The ready COALESCE expression index is owned by the Phase 3 migration, not the EF model.
        run.Property(value => value.LastError).HasColumnName("last_error").HasMaxLength(1000);
        run.Property(value => value.IdempotencyKeyHash).HasColumnName("idempotency_key_hash").HasColumnType("bytea");
        run.Property(value => value.RequestFingerprint).HasColumnName("request_fingerprint").HasColumnType("bytea");
        run.HasIndex(value => value.IdempotencyKeyHash).IsUnique()
            .HasDatabaseName("ux_integration_runs_idempotency_key_hash")
            .HasFilter("idempotency_key_hash IS NOT NULL");
        run.HasIndex(value => new { value.ReceivedAt, value.Id })
            .HasDatabaseName("ix_integration_runs_received_at_id");
    }

    private const string ProcessingState = """
        idempotency_key_hash IS NULL OR (
            attempt_count BETWEEN 0 AND 3 AND (
                (status = 'Pending' AND record_count = 0 AND completed_at IS NULL AND (
                    (attempt_count = 0 AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL AND last_error IS NULL)
                    OR (attempt_count BETWEEN 1 AND 3 AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND next_attempt_at IS NULL AND last_error IS NULL)
                    OR (attempt_count BETWEEN 1 AND 2 AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NOT NULL
                        AND last_error IS NOT NULL AND last_error IN ('synthetic_retryable_failure', 'processing_deadline_exceeded', 'attempt_abandoned'))
                ))
                OR (status = 'Succeeded' AND attempt_count BETWEEN 1 AND 3 AND record_count >= 0 AND completed_at IS NOT NULL
                    AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL AND last_error IS NULL)
                OR (status = 'Failed' AND attempt_count BETWEEN 1 AND 3 AND record_count = 0 AND completed_at IS NOT NULL
                    AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL AND last_error IS NOT NULL
                    AND (last_error IN ('synthetic_permanent_failure', 'internal_processing_failure')
                        OR (last_error = 'attempt_exhausted' AND attempt_count = 3)))
            )
        )
        """;

    private static string TimestampBounds(string column) =>
        $"isfinite({column}) AND {column} BETWEEN TIMESTAMPTZ '0001-01-01 00:00:00+00' " +
        "AND TIMESTAMPTZ '9999-12-31 23:59:59.999999+00'";
}
