using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <summary>
    /// Remembers which day a limit notification and a limit warning were last
    /// shown for each app. These were in-memory only, so restarting FastApp
    /// re-armed both and you got the same "almost at today's limit" warning
    /// twice in one day.
    ///
    /// Dates rather than booleans, following BonusMinutesDate: a stamp that is
    /// not today reads as "not yet notified", so the day rolls over on its own
    /// and no reset event has to fire for correctness.
    /// </summary>
    public partial class AddLimitNotificationDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LimitNotifiedDate",
                table: "ManagedApps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LimitWarnedDate",
                table: "ManagedApps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LimitNotifiedDate", table: "ManagedApps");
            migrationBuilder.DropColumn(name: "LimitWarnedDate", table: "ManagedApps");
        }
    }
}
