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
                name: "Users");
        }
    }
}
