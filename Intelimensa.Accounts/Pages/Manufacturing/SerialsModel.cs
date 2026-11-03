using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Manufacturing;

public class SerialsModel(ApplicationDbContext db, UnitProvisioning provisioning, IOptions<ManufacturingOptions> options)
    : ManufacturingPageModel(db, provisioning, options)
{
    /// <summary>
    /// Allocates new serials and holds them, for casings that are 3D-printed with the serial before the
    /// boards are ready. No codes and no firmware: each gets its first code from a rekey at flash time.
    /// </summary>
    public async Task<IActionResult> OnPostAllocateSerialsAsync(string? deviceType, string? region, int count)
    {
        if (await Provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();
        if (count is < 1 or > MaxSerialBatch)
            return await FailAsync($"Count must be between 1 and {MaxSerialBatch}.");

        for (var i = 0; i < count; i++)
        {
            var result = await Provisioning.AllocateHeldSerialAsync(userId, deviceType, region);
            if (!result.IsOk)
            {
                // Serials already allocated in this batch stay held.
                ErrorMessage = Held.Count > 0
                    ? $"{result.Message} Allocated {Held.Count} of {count} before this failed."
                    : result.Message;
                break;
            }
            Held.Add(SerialNumber.ToDisplay(result.Value!.SerialNumber));
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>
    /// Holds serials that already exist outside the server (e.g. printed on casings): only the serial is
    /// reserved, with no code and no firmware, so it can't be issued to another unit. The first code
    /// comes from a rekey when the unit is flashed.
    /// </summary>
    public async Task<IActionResult> OnPostHoldSerialsAsync(string? serials)
    {
        if (await Provisioning.GetUsableUserIdAsync(User) is not { } userId)
            return Forbid();

        // One serial per line, but commas/spaces are tolerated too. Serials never contain whitespace.
        // De-duplicated on the normalized form so a repeated line isn't reported as "already issued".
        var tokens = (serials ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => SerialNumber.TryNormalize(t, out var canonical) ? canonical : t)
            .Distinct()
            .ToList();
        if (tokens.Count == 0)
            return await FailAsync("Enter at least one serial.");
        if (tokens.Count > MaxSerialBatch)
            return await FailAsync($"At most {MaxSerialBatch} serials at a time.");

        // Each serial stands alone: one bad serial doesn't stop the others.
        foreach (var token in tokens)
        {
            var result = await Provisioning.HoldSerialAsync(userId, token);
            if (result.IsOk)
                Held.Add(SerialNumber.ToDisplay(result.Value!.SerialNumber));
            else
                Failures.Add(new SerialFailure(SerialNumber.ToDisplay(token), result.Message ?? "Failed."));
        }

        await LoadAsync();
        return Page();
    }
}
