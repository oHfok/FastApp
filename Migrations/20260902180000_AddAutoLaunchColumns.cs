using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <summary>
    /// Per-app auto-launch settings. Launch order is not stored here: the list
    /// is already drag-reorderable and OrderIndex carries that, so startup walks
    /// the apps in the order they appear on screen rather than in a second,
    /// invisible order the user would have to keep in sync.
    /// </summary>
    public partial class AddAutoLaunchColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LaunchArguments",
                table: "ManagedApps",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LaunchDelaySeconds",
                table: "ManagedApps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LaunchArguments", table: "ManagedApps");
            migrationBuilder.DropColumn(name: "LaunchDelaySeconds", table: "ManagedApps");
        }
    }
}
