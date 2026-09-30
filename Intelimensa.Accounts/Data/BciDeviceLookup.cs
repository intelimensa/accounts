using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Data;

public static class BciDeviceLookup
{
    /// <summary>
    /// Finds a unit by serial. Tries the input as given (so legacy and staff-entered, free-form
    /// serials keep working), then falls back to its canonical form, so a serial typed from a label
    /// (<c>MSV2-G01S-ATCF</c>), rewritten by an OS (<c>MSV2_G01S_ATCF</c>) or in lowercase still
    /// resolves to the stored dashless serial.
    /// </summary>
    public static async Task<BciDevice?> FindBySerialAsync(this DbSet<BciDevice> devices, string? input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return null;

        var exact = await devices.FirstOrDefaultAsync(d => d.SerialNumber == trimmed);
        if (exact is not null)
            return exact;

        return SerialNumber.TryNormalize(trimmed, out var canonical) && canonical != trimmed
            ? await devices.FirstOrDefaultAsync(d => d.SerialNumber == canonical)
            : null;
    }
}
