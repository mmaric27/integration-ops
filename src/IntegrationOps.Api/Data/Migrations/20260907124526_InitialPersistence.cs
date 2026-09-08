using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntegrationOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    partner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    operation = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp(6) with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp(6) with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_runs", x => x.id);
                    table.CheckConstraint("ck_integration_runs_attempt_count", "attempt_count >= 1");
                    table.CheckConstraint("ck_integration_runs_completed_at", "completed_at IS NULL OR (isfinite(completed_at) AND completed_at BETWEEN TIMESTAMPTZ '0001-01-01 00:00:00+00' AND TIMESTAMPTZ '9999-12-31 23:59:59.999999+00')");
                    table.CheckConstraint("ck_integration_runs_completion_order", "completed_at IS NULL OR completed_at >= received_at");
                    table.CheckConstraint("ck_integration_runs_operation", "char_length(operation) > 0");
                    table.CheckConstraint("ck_integration_runs_partner", "char_length(partner) > 0");
                    table.CheckConstraint("ck_integration_runs_received_at", "isfinite(received_at) AND received_at BETWEEN TIMESTAMPTZ '0001-01-01 00:00:00+00' AND TIMESTAMPTZ '9999-12-31 23:59:59.999999+00'");
                    table.CheckConstraint("ck_integration_runs_record_count", "record_count >= 0");
                    table.CheckConstraint("ck_integration_runs_status", "status IN ('Pending', 'Succeeded', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_runs_received_at_id",
                table: "integration_runs",
                columns: new[] { "received_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_runs");
        }
    }
}
