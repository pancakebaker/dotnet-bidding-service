using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bidding_service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyNowDomainFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "buy_now_price",
                table: "auctions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "final_price",
                table: "auctions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "final_winner_id",
                table: "auctions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sale_mode",
                table: "auctions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "AuctionOnly");

            migrationBuilder.AddCheckConstraint(
                name: "CK_auctions_sale_mode_buy_now_price",
                table: "auctions",
                sql: "(sale_mode = 'AuctionOnly' AND buy_now_price IS NULL) OR (sale_mode = 'BuyNowOnly' AND buy_now_price IS NOT NULL) OR (sale_mode = 'AuctionAndBuyNow' AND buy_now_price IS NOT NULL AND starting_price < buy_now_price)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_auctions_sale_mode_buy_now_price",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "buy_now_price",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "final_price",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "final_winner_id",
                table: "auctions");

            migrationBuilder.DropColumn(
                name: "sale_mode",
                table: "auctions");
        }
    }
}
