using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class IntroduceBciDevicesAndAccountDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_Configs_AssignedConfigId",
                table: "Accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_TelemetryEvents_Devices_DeviceId",
                table: "TelemetryEvents");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_AssignedConfigId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AssignedConfigId",
                table: "Accounts");

            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "TelemetryEvents",
                newName: "AccountDeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_TelemetryEvents_DeviceId",
                table: "TelemetryEvents",
                newName: "IX_TelemetryEvents_AccountDeviceId");

            migrationBuilder.CreateTable(
                name: "BciDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceType = table.Column<string>(type: "TEXT", nullable: false),
                    ProducedAt = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CurrentFirmwareVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LastFirmwareUpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BciDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    BciDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedConfigId = table.Column<int>(type: "INTEGER", nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UnassignedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountDevices_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountDevices_BciDevices_BciDeviceId",
                        column: x => x.BciDeviceId,
                        principalTable: "BciDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountDevices_Configs_AssignedConfigId",
                        column: x => x.AssignedConfigId,
                        principalTable: "Configs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountDevices_AccountId_BciDeviceId",
                table: "AccountDevices",
                columns: new[] { "AccountId", "BciDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountDevices_AssignedConfigId",
                table: "AccountDevices",
                column: "AssignedConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountDevices_BciDeviceId",
                table: "AccountDevices",
                column: "BciDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_BciDevices_SerialNumber",
                table: "BciDevices",
                column: "SerialNumber",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TelemetryEvents_AccountDevices_AccountDeviceId",
                table: "TelemetryEvents",
                column: "AccountDeviceId",
                principalTable: "AccountDevices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TelemetryEvents_AccountDevices_AccountDeviceId",
                table: "TelemetryEvents");

            migrationBuilder.DropTable(
                name: "AccountDevices");

            migrationBuilder.DropTable(
                name: "BciDevices");

            migrationBuilder.RenameColumn(
                name: "AccountDeviceId",
                table: "TelemetryEvents",
                newName: "DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_TelemetryEvents_AccountDeviceId",
                table: "TelemetryEvents",
                newName: "IX_TelemetryEvents_DeviceId");

            migrationBuilder.AddColumn<int>(
                name: "AssignedConfigId",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_AssignedConfigId",
                table: "Accounts",
                column: "AssignedConfigId");

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Configs_AssignedConfigId",
                table: "Accounts",
                column: "AssignedConfigId",
                principalTable: "Configs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TelemetryEvents_Devices_DeviceId",
                table: "TelemetryEvents",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
