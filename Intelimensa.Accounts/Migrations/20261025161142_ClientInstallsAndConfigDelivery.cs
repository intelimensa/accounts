using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class ClientInstallsAndConfigDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Devices was the scaffold's never-written placeholder for client installs (no code ever added a row), so it
            // is replaced rather than renamed. CurrentVersion is dropped, not renamed to IsDefault as EF guessed: a
            // version number must not turn into "the default config".
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropColumn(
                name: "CurrentVersion",
                table: "Configs");

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfigDeliveryBlockedAt",
                table: "ReleaseArtifacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseSecretKekId",
                table: "ReleaseArtifacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrappedReleaseSecret",
                table: "ReleaseArtifacts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientInstallId",
                table: "RefreshTokens",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlobSha256",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BlobSize",
                table: "Configs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlobStorageKey",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KekId",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UploadedAt",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UploadedByUserId",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrappedCek",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxInstallsOverride",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineLeaseDaysOverride",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientInstalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    SigningKey = table.Column<string>(type: "TEXT", nullable: false),
                    KeyThumbprint = table.Column<string>(type: "TEXT", nullable: false),
                    AgreementKey = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", nullable: true),
                    Slot = table.Column<int>(type: "INTEGER", nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientInstalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientInstalls_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigKeyIssuances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientInstallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountDeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", nullable: false),
                    AppPlatform = table.Column<string>(type: "TEXT", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LeaseNotAfter = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigKeyIssuances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ClientInstallId",
                table: "RefreshTokens",
                column: "ClientInstallId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstalls_AccountId_KeyThumbprint",
                table: "ClientInstalls",
                columns: new[] { "AccountId", "KeyThumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigKeyIssuances_AccountId",
                table: "ConfigKeyIssuances",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigKeyIssuances_ClientInstallId",
                table: "ConfigKeyIssuances",
                column: "ClientInstallId");

            migrationBuilder.AddForeignKey(
                name: "FK_RefreshTokens_ClientInstalls_ClientInstallId",
                table: "RefreshTokens",
                column: "ClientInstallId",
                principalTable: "ClientInstalls",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RefreshTokens_ClientInstalls_ClientInstallId",
                table: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "ClientInstalls");

            migrationBuilder.DropTable(
                name: "ConfigKeyIssuances");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_ClientInstallId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "ConfigDeliveryBlockedAt",
                table: "ReleaseArtifacts");

            migrationBuilder.DropColumn(
                name: "ReleaseSecretKekId",
                table: "ReleaseArtifacts");

            migrationBuilder.DropColumn(
                name: "WrappedReleaseSecret",
                table: "ReleaseArtifacts");

            migrationBuilder.DropColumn(
                name: "ClientInstallId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "BlobSha256",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "BlobSize",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "BlobStorageKey",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "KekId",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UploadedAt",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "WrappedCek",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "MaxInstallsOverride",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OfflineLeaseDaysOverride",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "Configs");

            migrationBuilder.AddColumn<int>(
                name: "CurrentVersion",
                table: "Configs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Platform = table.Column<int>(type: "INTEGER", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_AccountId",
                table: "Devices",
                column: "AccountId");
        }
    }
}
