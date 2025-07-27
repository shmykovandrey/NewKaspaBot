using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaspaBot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderAmountModeAndDynamicCoef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Settings_DynamicOrderCoef",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Settings_OrderAmountMode",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Settings_DynamicOrderCoef",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_OrderAmountMode",
                table: "Users");
        }
    }
}
