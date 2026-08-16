using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyLogsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DailyLogs_AppName_Date",
                table: "DailyLogs",
                columns: new[] { "AppName", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyLogs_AppName_Date",
                table: "DailyLogs");
        }
    }
}
