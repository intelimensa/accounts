using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmExistingEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only. Email confirmation is introduced here: everyone who registered before it
            // is a known account, so they must not be asked to confirm (or be locked out by it).
            migrationBuilder.Sql("UPDATE AspNetUsers SET EmailConfirmed = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
