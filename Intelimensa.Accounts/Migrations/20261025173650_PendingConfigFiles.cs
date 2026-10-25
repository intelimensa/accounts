using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class PendingConfigFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingBlobSha256",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PendingBlobSize",
                table: "Configs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingBlobStorageKey",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingKekId",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingUploadedAt",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingUploadedByUserId",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingWrappedCek",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "Configs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedByUserId",
                table: "Configs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingBlobSha256",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PendingBlobSize",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PendingBlobStorageKey",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PendingKekId",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PendingUploadedAt",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PendingUploadedByUserId",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PendingWrappedCek",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Configs");

            migrationBuilder.DropColumn(
                name: "PublishedByUserId",
                table: "Configs");
        }
    }
}
