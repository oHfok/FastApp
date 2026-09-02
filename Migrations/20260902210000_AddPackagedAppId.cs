using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FastApp.Migrations
{
    /// <summary>
    /// The AppUserModelID of a packaged (MSIX/Store) app, e.g.
    /// "Claude_pzs8sxrjxfjjc!Claude".
    ///
    /// The scanner resolved packaged apps down to a concrete executable path
    /// under Program Files\WindowsApps, and those paths carry the package
    /// version: Claude_1.40609.0.0_x64__pzs8sxrjxfjjc. The folder is replaced on
    /// every update, so the stored path pointed at a directory that no longer
    /// existed and launching failed with "not found" -- for every Store app,
    /// after every update. Tracking still worked, because that matches on
    /// process name rather than path.
    ///
    /// The AUMID does not carry a version, and activating through it also works
    /// for packages whose folders are ACL-locked against reading the exe.
    /// </summary>
    public partial class AddPackagedAppId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PackagedAppId",
                table: "ManagedApps",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PackagedAppId", table: "ManagedApps");
        }
    }
}
