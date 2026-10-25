using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// Assigns a config to a specific account+hardware-unit pairing (<see cref="AccountDevice"/>), and
/// lets staff revoke a pairing (sets <see cref="AccountDevice.UnassignedAt"/>). Config assignment
/// moved here from <see cref="Account"/> because it's per participant/unit/calibration, not per
/// account as a whole -- see CLAUDE.md.
/// </summary>
public class DeviceAssignmentsModel(ApplicationDbContext db) : PageModel
{
    public List<PairingRow> Pairings { get; private set; } = [];

    public List<Config> Configs { get; private set; } = [];

    /// <summary>Pre-selected for a pairing with no config yet; staff still confirm it with Save.</summary>
    public int? DefaultConfigId => Configs.FirstOrDefault(c => c.IsDefault && c.RetiredAt is null)?.Id;

    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid accountDeviceId, int? assignedConfigId)
    {
        var pairing = await db.AccountDevices.FirstOrDefaultAsync(ad => ad.Id == accountDeviceId);
        if (pairing is not null)
        {
            // A retired config may stay on the pairings that have it, but isn't handed out anew.
            if (assignedConfigId is { } configId && configId != pairing.AssignedConfigId &&
                await db.Configs.AnyAsync(c => c.Id == configId && c.RetiredAt != null))
            {
                StatusMessage = "That config is retired and can't be newly assigned.";
                await LoadAsync();
                return Page();
            }
            pairing.AssignedConfigId = assignedConfigId;
            await db.SaveChangesAsync();
            StatusMessage = "Assignment updated.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid accountDeviceId)
    {
        var pairing = await db.AccountDevices.FirstOrDefaultAsync(ad => ad.Id == accountDeviceId);
        if (pairing is { UnassignedAt: null })
        {
            pairing.UnassignedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            StatusMessage = "Access revoked.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Configs = await db.Configs.OrderBy(c => c.DisplayName).ToListAsync();

        Pairings = await db.AccountDevices
            .Include(ad => ad.Account!.User)
            .Include(ad => ad.BciDevice)
            .OrderBy(ad => ad.Account!.User!.Email)
            .ThenBy(ad => ad.BciDevice!.SerialNumber)
            .Select(ad => new PairingRow(
                ad.Id,
                ad.Account!.User!.Email ?? "(no email)",
                ad.BciDevice!.SerialNumber,
                ad.BciDevice.DeviceType,
                ad.AssignedConfigId,
                ad.RegisteredAt,
                ad.UnassignedAt))
            .ToListAsync();
    }

    public record PairingRow(
        Guid AccountDeviceId,
        string Email,
        string SerialNumber,
        string DeviceType,
        int? AssignedConfigId,
        DateTimeOffset RegisteredAt,
        DateTimeOffset? UnassignedAt);
}
