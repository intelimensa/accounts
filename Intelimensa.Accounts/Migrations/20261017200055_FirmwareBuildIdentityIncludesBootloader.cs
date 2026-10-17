using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class FirmwareBuildIdentityIncludesBootloader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FirmwareBuilds_DeviceType_Kind_Version",
                table: "FirmwareBuilds");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareBuilds_DeviceType_Kind_Version",
                table: "FirmwareBuilds",
                columns: new[] { "DeviceType", "Kind", "Version" },
                unique: true,
                filter: "\"BootloaderVersion\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareBuilds_DeviceType_Kind_Version_BootloaderVersion",
                table: "FirmwareBuilds",
                columns: new[] { "DeviceType", "Kind", "Version", "BootloaderVersion" },
                unique: true,
                filter: "\"BootloaderVersion\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FirmwareBuilds_DeviceType_Kind_Version",
                table: "FirmwareBuilds");

            migrationBuilder.DropIndex(
                name: "IX_FirmwareBuilds_DeviceType_Kind_Version_BootloaderVersion",
                table: "FirmwareBuilds");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareBuilds_DeviceType_Kind_Version",
                table: "FirmwareBuilds",
                columns: new[] { "DeviceType", "Kind", "Version" },
                unique: true);
        }
    }
}
