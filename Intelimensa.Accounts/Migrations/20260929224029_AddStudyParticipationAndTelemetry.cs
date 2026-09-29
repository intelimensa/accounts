using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class AddStudyParticipationAndTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudyParticipations",
                columns: table => new
                {
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsentGivenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsentVersion = table.Column<string>(type: "TEXT", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Gender = table.Column<int>(type: "INTEGER", nullable: false),
                    GenderOtherDescription = table.Column<string>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    Disability = table.Column<int>(type: "INTEGER", nullable: false),
                    DisabilityOtherDescription = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyParticipations", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_StudyParticipations_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SessionEndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DifficultyLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    Accuracy = table.Column<double>(type: "REAL", nullable: false),
                    SuccessfulCommands = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelemetryEvents_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TelemetryEvents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_AccountId",
                table: "TelemetryEvents",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryEvents_DeviceId",
                table: "TelemetryEvents",
                column: "DeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudyParticipations");

            migrationBuilder.DropTable(
                name: "TelemetryEvents");
        }
    }
}
