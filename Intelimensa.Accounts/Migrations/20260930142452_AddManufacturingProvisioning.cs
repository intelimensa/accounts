using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class AddManufacturingProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ManufacturedAt",
                table: "BciDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationCodeHash",
                table: "BciDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservedAt",
                table: "BciDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservedByUserId",
                table: "BciDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "BciDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Everything already in the table predates the manufacturing flow (legacy/backfilled
            // units, some already registered) -- treat them as Manufactured, not Reserved.
            migrationBuilder.Sql("UPDATE BciDevices SET Status = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManufacturedAt",
                table: "BciDevices");

            migrationBuilder.DropColumn(
                name: "RegistrationCodeHash",
                table: "BciDevices");

            migrationBuilder.DropColumn(
                name: "ReservedAt",
                table: "BciDevices");

            migrationBuilder.DropColumn(
                name: "ReservedByUserId",
                table: "BciDevices");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BciDevices");
        }
    }
}
