using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class AddBootloaderVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BootloaderVersion",
                table: "BciDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BootloaderVersionAfter",
                table: "BciDeviceEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BootloaderVersionBefore",
                table: "BciDeviceEvents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BootloaderVersion",
                table: "BciDevices");

            migrationBuilder.DropColumn(
                name: "BootloaderVersionAfter",
                table: "BciDeviceEvents");

            migrationBuilder.DropColumn(
                name: "BootloaderVersionBefore",
                table: "BciDeviceEvents");
        }
    }
}
