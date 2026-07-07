using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTicksColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppCategories",
                columns: table => new
                {
                    AppName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppCategories", x => x.AppName);
                });

            migrationBuilder.CreateTable(
                name: "DailyLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppName = table.Column<string>(type: "TEXT", nullable: false),
                    TimeSpent = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    AfkTimeSpent = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    TimeFocused = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    TimeSpentTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    AfkTimeSpentTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    TimeFocusedTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HiddenApps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenApps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MacroEventLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppName = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MacroEventLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagedApps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutablePath = table.Column<string>(type: "TEXT", nullable: false),
                    ActionType = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionPayload = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    CustomName = table.Column<string>(type: "TEXT", nullable: false),
                    LaunchOnStartup = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeRunning = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    HotkeyDisplayText = table.Column<string>(type: "TEXT", nullable: false),
                    HotkeyTriggerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HotkeySequence = table.Column<string>(type: "TEXT", nullable: false),
                    DailyLimitMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StrictFocusMode = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedApps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppName = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MacroEventLogs_Timestamp",
                table: "MacroEventLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SessionLogs_AppName_StartTime",
                table: "SessionLogs",
                columns: new[] { "AppName", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionLogs_StartTime",
                table: "SessionLogs",
                column: "StartTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppCategories");

            migrationBuilder.DropTable(
                name: "DailyLogs");

            migrationBuilder.DropTable(
                name: "HiddenApps");

            migrationBuilder.DropTable(
                name: "MacroEventLogs");

            migrationBuilder.DropTable(
                name: "ManagedApps");

            migrationBuilder.DropTable(
                name: "SessionLogs");
        }
    }
}
