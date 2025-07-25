using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaspaBot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoTradeFieldsToUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Settings_IsAutoTradeEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "Settings_LastDcaBuyPrice",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Settings_IsAutoTradeEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_LastDcaBuyPrice",
                table: "Users");
        }
    }
}
