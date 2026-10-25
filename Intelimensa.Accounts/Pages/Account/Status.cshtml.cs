using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AccountEntity = Intelimensa.Accounts.Models.Account;

namespace Intelimensa.Accounts.Pages.Account;

public class StatusModel(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db,
    EmailConfirmation emailConfirmation,
    Intelimensa.Accounts.Security.ClientInstallRegistry installs,
    ILogger<StatusModel> log) : PageModel
{
    /// <summary>
    /// Removes one of the user's own AxoSync installs: its tokens stop working at once, and it can only come back
    /// by signing in again (as a new install).
    /// </summary>
    public async Task<IActionResult> OnPostRemoveInstallAsync(Guid installId)
    {
        var userId = userManager.GetUserId(User);
        var install = await db.ClientInstalls
            .FirstOrDefaultAsync(i => i.Id == installId && i.Account!.UserId == userId && i.RevokedAt == null);
        if (install is not null)
        {
            await installs.RevokeAsync(install, DateTimeOffset.UtcNow);
            log.LogInformation("User {UserId} removed AxoSync install {InstallId}", userId, install.Id);
            Message = $"Removed {install.Name ?? install.Platform.ToString()}. It is signed out and can't fetch configs any more.";
        }
        return RedirectToPage();
    }

    public bool EmailConfirmed { get; private set; }

    public bool CanDownload => Intelimensa.Accounts.Security.AccountPolicy.IsUsable(Account);

    [TempData]
    public string? Message { get; set; }

    public async Task<IActionResult> OnPostResendAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
            return RedirectToPage();

        if (user.EmailConfirmed)
            Message = "Your email address is already confirmed.";
        else if (!emailConfirmation.TryBeginResend(user.Id, DateTimeOffset.UtcNow))
            Message = "A confirmation email was sent a moment ago. Check your inbox (and spam), or try again in a couple of minutes.";
        else
        {
            try
            {
                await emailConfirmation.SendAsync(user, $"{Request.Scheme}://{Request.Host}");
                Message = $"Confirmation email sent to {user.Email}.";
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not send the confirmation email to {Email}", user.Email);
                Message = "We couldn't send the email right now. Please try again later.";
            }
        }

        return RedirectToPage();
    }

    public string Email { get; private set; } = string.Empty;

    public AccountEntity? Account { get; private set; }

    public List<DeviceRow> Devices { get; private set; } = [];

    public List<ClientInstall> Installs { get; private set; } = [];

    public StudyParticipation? StudyParticipation { get; private set; }

    public async Task OnGetAsync()
    {
        var userId = userManager.GetUserId(User);
        Email = User.Identity?.Name ?? string.Empty;
        EmailConfirmed = (await userManager.GetUserAsync(User))?.EmailConfirmed ?? false;

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

            var now = DateTimeOffset.UtcNow;
            Installs = (await db.ClientInstalls.Where(i => i.AccountId == Account.Id && i.RevokedAt == null).ToListAsync())
                .Where(i => i.IsActive(now, installs.IdleWindow))
                .OrderByDescending(i => i.LastActivity)
                .ToList();

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
