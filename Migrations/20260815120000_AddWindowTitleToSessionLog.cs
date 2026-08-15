using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowTitleToSessionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WindowTitle",
                table: "SessionLogs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WindowTitle",
                table: "SessionLogs");
        }
    }
}
