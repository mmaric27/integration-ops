using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntegrationOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableIntegrationRunProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "lease_expires_at",
                table: "integration_runs",
                type: "timestamp(6) with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "integration_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "integration_runs",
                type: "timestamp(6) with time zone",
                nullable: true);

            // Frozen precondition: migration history must not depend on the current application model.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM integration_runs WHERE NOT (
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
                    )) THEN
                        RAISE EXCEPTION 'Cannot enable Phase 3 with incompatible intake state.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_integration_runs_processing_expired",
                table: "integration_runs",
                columns: new[] { "lease_expires_at", "id" },
                filter: "idempotency_key_hash IS NOT NULL AND status = 'Pending' AND lease_token IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_lease_expires_at",
                table: "integration_runs",
                sql: "lease_expires_at IS NULL OR (isfinite(lease_expires_at) AND lease_expires_at BETWEEN TIMESTAMPTZ '0001-01-01 00:00:00+00' AND TIMESTAMPTZ '9999-12-31 23:59:59.999999+00')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_lease_pair",
                table: "integration_runs",
                sql: "(lease_token IS NULL AND lease_expires_at IS NULL) OR (lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_next_attempt_at",
                table: "integration_runs",
                sql: "next_attempt_at IS NULL OR (isfinite(next_attempt_at) AND next_attempt_at BETWEEN TIMESTAMPTZ '0001-01-01 00:00:00+00' AND TIMESTAMPTZ '9999-12-31 23:59:59.999999+00')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_processing_origin",
                table: "integration_runs",
                sql: "idempotency_key_hash IS NOT NULL OR (lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_processing_state",
                table: "integration_runs",
                sql: "idempotency_key_hash IS NULL OR (\n    attempt_count BETWEEN 0 AND 3 AND (\n        (status = 'Pending' AND record_count = 0 AND completed_at IS NULL AND (\n            (attempt_count = 0 AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL AND last_error IS NULL)\n            OR (attempt_count BETWEEN 1 AND 3 AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL AND next_attempt_at IS NULL AND last_error IS NULL)\n            OR (attempt_count BETWEEN 1 AND 2 AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NOT NULL\n                AND last_error IS NOT NULL AND last_error IN ('synthetic_retryable_failure', 'processing_deadline_exceeded', 'attempt_abandoned'))\n        ))\n        OR (status = 'Succeeded' AND attempt_count BETWEEN 1 AND 3 AND record_count >= 0 AND completed_at IS NOT NULL\n            AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL AND last_error IS NULL)\n        OR (status = 'Failed' AND attempt_count BETWEEN 1 AND 3 AND record_count = 0 AND completed_at IS NOT NULL\n            AND lease_token IS NULL AND lease_expires_at IS NULL AND next_attempt_at IS NULL AND last_error IS NOT NULL\n            AND (last_error IN ('synthetic_permanent_failure', 'internal_processing_failure')\n                OR (last_error = 'attempt_exhausted' AND attempt_count = 3)))\n    )\n)");
            // Migration-owned expression index; database tests inspect its exact catalog definition.
            migrationBuilder.Sql("""
                CREATE INDEX ix_integration_runs_processing_ready
                ON integration_runs ((COALESCE(next_attempt_at, received_at)) ASC, received_at ASC, id ASC)
                WHERE idempotency_key_hash IS NOT NULL AND status = 'Pending' AND lease_token IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE integration_runs IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM integration_runs
                        WHERE lease_token IS NOT NULL OR lease_expires_at IS NOT NULL OR next_attempt_at IS NOT NULL
                            OR (idempotency_key_hash IS NOT NULL AND attempt_count > 0)) THEN
                        RAISE EXCEPTION 'Cannot downgrade while Phase 3 processing state exists.';
                    END IF;
                END $$;
                DROP INDEX ix_integration_runs_processing_ready;
                """);

            migrationBuilder.DropIndex(
                name: "ix_integration_runs_processing_expired",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_lease_expires_at",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_lease_pair",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_next_attempt_at",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_processing_origin",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_processing_state",
                table: "integration_runs");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "integration_runs");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "integration_runs");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "integration_runs");
        }
    }
}
