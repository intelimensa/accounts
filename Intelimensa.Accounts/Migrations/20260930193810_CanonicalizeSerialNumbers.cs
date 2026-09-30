using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelimensa.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalizeSerialNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Serials are now stored dashless (PPPPRVAAAAAC); dashes are display-only. Convert rows
            // already in the dashed form PPPP-RVAA-AAAC. Anything else (legacy free-form serials)
            // is left alone, and lookups still resolve either form.
            migrationBuilder.Sql(
                """
                UPDATE BciDevices
                SET SerialNumber = REPLACE(SerialNumber, '-', '')
                WHERE length(SerialNumber) = 14
                  AND substr(SerialNumber, 5, 1) = '-'
                  AND substr(SerialNumber, 10, 1) = '-';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: dashes are a display concern and can be re-derived from the serial.
        }
    }
}
