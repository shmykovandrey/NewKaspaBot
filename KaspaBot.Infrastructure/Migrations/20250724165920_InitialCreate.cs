using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaspaBot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderPairs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    BuyOrder_Id = table.Column<string>(type: "TEXT", nullable: false),
                    BuyOrder_Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    BuyOrder_Side = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyOrder_Type = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyOrder_Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    BuyOrder_Price = table.Column<decimal>(type: "TEXT", nullable: true),
                    BuyOrder_Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyOrder_CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BuyOrder_UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BuyOrder_QuantityFilled = table.Column<decimal>(type: "TEXT", nullable: false),
                    BuyOrder_QuoteQuantityFilled = table.Column<decimal>(type: "TEXT", nullable: false),
                    BuyOrder_Commission = table.Column<decimal>(type: "TEXT", nullable: false),
                    SellOrder_Id = table.Column<string>(type: "TEXT", nullable: false),
                    SellOrder_Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    SellOrder_Side = table.Column<int>(type: "INTEGER", nullable: false),
                    SellOrder_Type = table.Column<int>(type: "INTEGER", nullable: false),
                    SellOrder_Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    SellOrder_Price = table.Column<decimal>(type: "TEXT", nullable: true),
                    SellOrder_Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SellOrder_CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SellOrder_UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SellOrder_QuantityFilled = table.Column<decimal>(type: "TEXT", nullable: false),
                    SellOrder_QuoteQuantityFilled = table.Column<decimal>(type: "TEXT", nullable: false),
                    SellOrder_Commission = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Profit = table.Column<decimal>(type: "TEXT", nullable: true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderPairs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    RegistrationDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Settings_PercentPriceChange = table.Column<decimal>(type: "TEXT", nullable: false),
                    Settings_PercentProfit = table.Column<decimal>(type: "TEXT", nullable: false),
                    Settings_MaxUsdtUsing = table.Column<decimal>(type: "TEXT", nullable: false),
                    Settings_OrderAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Settings_EnableAutoTrading = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApiCredentials_ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    ApiCredentials_ApiSecret = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderPairs");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
