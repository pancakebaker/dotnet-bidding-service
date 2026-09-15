using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bidding_service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLifecycleHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_status_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tenant_version = table.Column<long>(type: "bigint", nullable: false),
                    changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_status_transitions", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_status_transitions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_status_transitions_tenant_version",
                table: "tenant_status_transitions",
                columns: new[] { "tenant_id", "tenant_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_status_transitions");
        }
    }
}
