using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AccountEntity = Intelimensa.Accounts.Models.Account;

namespace Intelimensa.Accounts.Pages.Account;

public class StatusModel(UserManager<ApplicationUser> userManager, ApplicationDbContext db) : PageModel
{
    public string Email { get; private set; } = string.Empty;

    public AccountEntity? Account { get; private set; }

    public List<DeviceRow> Devices { get; private set; } = [];

    public StudyParticipation? StudyParticipation { get; private set; }

    public async Task OnGetAsync()
    {
        var userId = userManager.GetUserId(User);
        Email = User.Identity?.Name ?? string.Empty;

        Account = await db.Accounts
            .Include(a => a.StudyParticipation)
            .FirstOrDefaultAsync(a => a.UserId == userId);

        StudyParticipation = Account?.StudyParticipation;

        if (Account is not null)
        {
            // SQLite's EF provider can't translate ORDER BY on DateTimeOffset -- sort client-side.
            var accountDevices = await db.AccountDevices
                .Where(ad => ad.AccountId == Account.Id)
                .Include(ad => ad.BciDevice)
                .Include(ad => ad.AssignedConfig)
                .ToListAsync();

            Devices = accountDevices
                .OrderBy(ad => ad.RegisteredAt)
                .Select(ad => new DeviceRow(
                    ad.BciDevice!.SerialNumber,
                    ad.BciDevice.DeviceType,
                    ad.AssignedConfig?.DisplayName,
                    ad.UnassignedAt))
                .ToList();
        }
    }

    public record DeviceRow(string SerialNumber, string DeviceType, string? AssignedConfigName, DateTimeOffset? UnassignedAt);
}
