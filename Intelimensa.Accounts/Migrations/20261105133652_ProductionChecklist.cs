using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class ProductionChecklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Step",
                table: "BciDeviceEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasPaired",
                table: "BciDeviceEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ElectrodeSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastModifiedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    FrozenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RetiredByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectrodeSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MountingTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RetiredByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MountingTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ElectrodeAssemblies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ElectrodeSetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    PartNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectrodeAssemblies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectrodeAssemblies_ElectrodeSets_ElectrodeSetId",
                        column: x => x.ElectrodeSetId,
                        principalTable: "ElectrodeSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BciDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Step = table.Column<int>(type: "INTEGER", nullable: false),
                    PerformedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PerformedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    MountingTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    ElectrodeSetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionSteps_BciDevices_BciDeviceId",
                        column: x => x.BciDeviceId,
                        principalTable: "BciDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionSteps_ElectrodeSets_ElectrodeSetId",
                        column: x => x.ElectrodeSetId,
                        principalTable: "ElectrodeSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionSteps_MountingTypes_MountingTypeId",
                        column: x => x.MountingTypeId,
                        principalTable: "MountingTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ElectrodeParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ElectrodeAssemblyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    PartNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ElectrodeParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ElectrodeParts_ElectrodeAssemblies_ElectrodeAssemblyId",
                        column: x => x.ElectrodeAssemblyId,
                        principalTable: "ElectrodeAssemblies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectrodeAssemblies_ElectrodeSetId_Name_Version",
                table: "ElectrodeAssemblies",
                columns: new[] { "ElectrodeSetId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectrodeParts_ElectrodeAssemblyId_Name_Version",
                table: "ElectrodeParts",
                columns: new[] { "ElectrodeAssemblyId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectrodeSets_Name_Version",
                table: "ElectrodeSets",
                columns: new[] { "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ElectrodeSets_PreviousId",
                table: "ElectrodeSets",
                column: "PreviousId");

            migrationBuilder.CreateIndex(
                name: "IX_MountingTypes_Key",
                table: "MountingTypes",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSteps_BciDeviceId_Step",
                table: "ProductionSteps",
                columns: new[] { "BciDeviceId", "Step" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSteps_ElectrodeSetId",
                table: "ProductionSteps",
                column: "ElectrodeSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSteps_MountingTypeId",
                table: "ProductionSteps",
                column: "MountingTypeId");

            // Data. The two placeholder mounting clip types (the Manufacturer corrects or replaces them in the edit tool).
            migrationBuilder.Sql(
                "INSERT INTO MountingTypes (\"Key\", DisplayName) VALUES ('padded-clips', 'Padded clips'), ('bose-tight-clips', 'Bose tight clips');");

            // Data. Step 1 (electronics flashed, id 1) for every unit that has been confirmed: the time is its ManufacturedAt,
            // the account the first Confirmed event's. Source 2 = Backfill.
            migrationBuilder.Sql(
                """
                INSERT INTO ProductionSteps (Id, BciDeviceId, Step, PerformedAt, RecordedAt, PerformedByUserId, Source)
                SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-A' || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
                       d.Id, 1, d.ManufacturedAt, d.ManufacturedAt,
                       (SELECT e.UserId FROM BciDeviceEvents e WHERE e.BciDeviceId = d.Id AND e.Type = 1 ORDER BY e.OccurredAt LIMIT 1),
                       2
                FROM BciDevices d
                WHERE d.ManufacturedAt IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ElectrodeParts");

            migrationBuilder.DropTable(
                name: "ProductionSteps");

            migrationBuilder.DropTable(
                name: "ElectrodeAssemblies");

            migrationBuilder.DropTable(
                name: "MountingTypes");

            migrationBuilder.DropTable(
                name: "ElectrodeSets");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "BciDeviceEvents");

            migrationBuilder.DropColumn(
                name: "WasPaired",
                table: "BciDeviceEvents");
        }
    }
}
