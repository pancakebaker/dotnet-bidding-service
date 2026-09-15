using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bidding_service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientCredentialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    public_key_pem = table.Column<string>(type: "text", nullable: false),
                    public_key_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_credentials", x => x.id);
                    table.CheckConstraint("CK_client_credentials_status", "status IN ('Active', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_client_credentials_client_applications_client_application_id",
                        column: x => x.client_application_id,
                        principalTable: "client_applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_credentials_client_application_id",
                table: "client_credentials",
                column: "client_application_id");

            migrationBuilder.CreateIndex(
                name: "ux_client_credentials_key_id",
                table: "client_credentials",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_client_credentials_public_key_fingerprint",
                table: "client_credentials",
                column: "public_key_fingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_credentials");
        }
    }
}
