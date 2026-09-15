using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bidding_service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantPersistenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "auctions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                    table.CheckConstraint("CK_tenants_status", "status IN ('Active', 'Suspended', 'Disabled')");
                });

            migrationBuilder.Sql("""
                INSERT INTO tenants (id, name, status, created_at_utc, updated_at_utc)
                VALUES (
                    'aaaaaaaa-1111-4111-8111-111111111111',
                    'Local Demo Tenant',
                    'Active',
                    TIMESTAMPTZ '2026-09-13 00:00:00+00',
                    TIMESTAMPTZ '2026-09-13 00:00:00+00')
                ON CONFLICT (id) DO NOTHING;

                UPDATE auctions
                SET tenant_id = 'aaaaaaaa-1111-4111-8111-111111111111'
                WHERE tenant_id IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_auctions_tenant_id",
                table: "auctions",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_auctions_tenants_tenant_id",
                table: "auctions",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_auctions_tenants_tenant_id",
                table: "auctions");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropIndex(
                name: "IX_auctions_tenant_id",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "auctions");
        }
    }
}
