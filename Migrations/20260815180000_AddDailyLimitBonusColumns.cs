using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyLimitBonusColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TodayBonusMinutes",
                table: "ManagedApps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BonusMinutesDate",
                table: "ManagedApps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TodayBonusMinutes",
                table: "ManagedApps");

            migrationBuilder.DropColumn(
                name: "BonusMinutesDate",
                table: "ManagedApps");
        }
    }
}
