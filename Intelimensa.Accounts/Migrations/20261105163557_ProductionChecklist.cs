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
                name: "CatalogItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    PartNo = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", nullable: true),
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
                    table.PrimaryKey("PK_CatalogItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    Field = table.Column<string>(type: "TEXT", nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogChanges_CatalogItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogChildren",
                columns: table => new
                {
                    AssemblyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChildId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogChildren", x => new { x.AssemblyId, x.ChildId });
                    table.ForeignKey(
                        name: "FK_CatalogChildren_CatalogItems_AssemblyId",
                        column: x => x.AssemblyId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogChildren_CatalogItems_ChildId",
                        column: x => x.ChildId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CatalogSupersessions",
                columns: table => new
                {
                    NewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogSupersessions", x => new { x.NewId, x.OldId });
                    table.ForeignKey(
                        name: "FK_CatalogSupersessions_CatalogItems_NewId",
                        column: x => x.NewId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatalogSupersessions_CatalogItems_OldId",
                        column: x => x.OldId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    AssemblyId = table.Column<Guid>(type: "TEXT", nullable: true),
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
                        name: "FK_ProductionSteps_CatalogItems_AssemblyId",
                        column: x => x.AssemblyId,
                        principalTable: "CatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogChanges_ItemId",
                table: "CatalogChanges",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogChildren_ChildId",
                table: "CatalogChildren",
                column: "ChildId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_PartNo",
                table: "CatalogItems",
                column: "PartNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogSupersessions_OldId",
                table: "CatalogSupersessions",
                column: "OldId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSteps_AssemblyId",
                table: "ProductionSteps",
                column: "AssemblyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionSteps_BciDeviceId_Step",
                table: "ProductionSteps",
                columns: new[] { "BciDeviceId", "Step" },
                unique: true);

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
                name: "CatalogChanges");

            migrationBuilder.DropTable(
                name: "CatalogChildren");

            migrationBuilder.DropTable(
                name: "CatalogSupersessions");

            migrationBuilder.DropTable(
                name: "ProductionSteps");

            migrationBuilder.DropTable(
                name: "CatalogItems");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "BciDeviceEvents");

            migrationBuilder.DropColumn(
                name: "WasPaired",
                table: "BciDeviceEvents");
        }
    }
}
