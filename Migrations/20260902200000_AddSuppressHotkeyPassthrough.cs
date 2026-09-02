using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <summary>
    /// Per-app opt-in for swallowing a hotkey instead of letting it through to
    /// the focused application. Off by default, so no existing binding changes
    /// behaviour: it exists for combinations that collide with a shortcut the
    /// target app also uses, such as a Ctrl+Shift+V paste macro that currently
    /// fires the app's own paste as well.
    /// </summary>
    public partial class AddSuppressHotkeyPassthrough : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SuppressHotkeyPassthrough",
                table: "ManagedApps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SuppressHotkeyPassthrough", table: "ManagedApps");
        }
    }
}
