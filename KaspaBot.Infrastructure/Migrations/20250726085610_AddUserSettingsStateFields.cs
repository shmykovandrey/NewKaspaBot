using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KaspaBot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSettingsStateFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Settings_ConsecutiveFailures",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "Settings_DebounceStartTime",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Settings_IsInDebounce",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "Settings_LastBalanceCheck",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Settings_LastKnownBalance",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Settings_LastTradeTime",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Settings_ConsecutiveFailures",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_DebounceStartTime",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_IsInDebounce",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_LastBalanceCheck",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_LastKnownBalance",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_LastTradeTime",
                table: "Users");
        }
    }
}
