using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntegrationOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IdempotentIntegrationRunIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_attempt_count",
                table: "integration_runs");

            migrationBuilder.AddColumn<byte[]>(
                name: "idempotency_key_hash",
                table: "integration_runs",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "request_fingerprint",
                table: "integration_runs",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_integration_runs_idempotency_key_hash",
                table: "integration_runs",
                column: "idempotency_key_hash",
                unique: true,
                filter: "idempotency_key_hash IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_attempt_count",
                table: "integration_runs",
                sql: "attempt_count >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_idempotency_key_hash_length",
                table: "integration_runs",
                sql: "idempotency_key_hash IS NULL OR octet_length(idempotency_key_hash) = 32");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_intake_metadata",
                table: "integration_runs",
                sql: "(idempotency_key_hash IS NULL AND request_fingerprint IS NULL) OR (idempotency_key_hash IS NOT NULL AND request_fingerprint IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_request_fingerprint_length",
                table: "integration_runs",
                sql: "request_fingerprint IS NULL OR octet_length(request_fingerprint) = 32");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE integration_runs IN ACCESS EXCLUSIVE MODE;
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM integration_runs
                        WHERE idempotency_key_hash IS NOT NULL OR request_fingerprint IS NOT NULL OR attempt_count = 0) THEN
                        RAISE EXCEPTION 'Cannot downgrade while Phase 2 intake data exists.';
                    END IF;
                END;
                $guard$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_integration_runs_idempotency_key_hash",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_attempt_count",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_idempotency_key_hash_length",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_intake_metadata",
                table: "integration_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_integration_runs_request_fingerprint_length",
                table: "integration_runs");

            migrationBuilder.DropColumn(
                name: "idempotency_key_hash",
                table: "integration_runs");

            migrationBuilder.DropColumn(
                name: "request_fingerprint",
                table: "integration_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_integration_runs_attempt_count",
                table: "integration_runs",
                sql: "attempt_count >= 1");
        }
    }
}
