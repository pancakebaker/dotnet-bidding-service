using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bidding_service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "version",
                table: "tenants");
        }
    }
}
