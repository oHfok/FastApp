using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMacroEventOutcomeColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Blocked",
                table: "MacroEventLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "MacroEventLogs",
                type: "TEXT",
                nullable: true);

            // true, not the bare-convention false: every row that already
            // existed before this column did was logged at a point where a
            // row only ever got written on success (see ProcessTriggersAsync's
            // history) -- so "unknown" for old data means "succeeded", not
            // "failed".
            migrationBuilder.AddColumn<bool>(
                name: "Success",
                table: "MacroEventLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Blocked",
                table: "MacroEventLogs");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "MacroEventLogs");

            migrationBuilder.DropColumn(
                name: "Success",
                table: "MacroEventLogs");
        }
    }
}
