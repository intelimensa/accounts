using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using AccountEntity = Intelimensa.Accounts.Models.Account;

namespace Intelimensa.Accounts.Pages.Account;

/// <summary>
/// Opt-in research enrollment: join / edit / withdraw. See CLAUDE.md's study-participation design
/// -- joining requires every field (a "prefer not to say" answer still counts as filled in),
/// withdrawal is prospective (existing rows are kept, only future telemetry stops), and rejoining
/// re-stamps consent rather than silently resuming.
/// </summary>
public class StudyModel(UserManager<ApplicationUser> userManager, ApplicationDbContext db) : PageModel
{
    // Bump this whenever the consent text materially changes, independent of app releases.
    private const string CurrentConsentVersion = "2026-09-30";

    public StudyParticipation? Participation { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public Task<IActionResult> OnPostJoinAsync() => SaveAsync(isJoining: true);

    public Task<IActionResult> OnPostUpdateAsync() => SaveAsync(isJoining: false);

    public async Task<IActionResult> OnPostWithdrawAsync()
    {
        var account = await GetAccountAsync();
        var participation = account is null
            ? null
            : await db.StudyParticipations.FirstOrDefaultAsync(sp => sp.AccountId == account.Id);

        if (participation is { WithdrawnAt: null })
        {
            participation.WithdrawnAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        return RedirectToPage();
    }

    private async Task<IActionResult> SaveAsync(bool isJoining)
    {
        ValidateOtherDescriptions();

        if (isJoining && !Input.ConsentAcknowledged)
            ModelState.AddModelError(nameof(Input.ConsentAcknowledged), "You must give consent to join the study.");

        if (!ModelState.IsValid)
        {
            await LoadAsync(skipInputReset: true);
            return Page();
        }

        var account = await GetAccountAsync();
        if (account is null)
            return NotFound();

        var participation = await db.StudyParticipations.FirstOrDefaultAsync(sp => sp.AccountId == account.Id);
        if (participation is null)
        {
            participation = new StudyParticipation
            {
                AccountId = account.Id,
                ConsentVersion = CurrentConsentVersion,
                DateOfBirth = Input.DateOfBirth!.Value,
                Region = Input.Region,
            };
            db.StudyParticipations.Add(participation);
        }

        if (isJoining)
        {
            participation.ConsentGivenAt = DateTimeOffset.UtcNow;
            participation.ConsentVersion = CurrentConsentVersion;
            participation.WithdrawnAt = null;
        }

        participation.DateOfBirth = Input.DateOfBirth!.Value;
        participation.Gender = Input.Gender;
        participation.GenderOtherDescription = Input.Gender == Gender.Other ? Input.GenderOtherDescription : null;
        participation.Region = Input.Region;
        participation.Disability = Input.Disability;
        participation.DisabilityOtherDescription = Input.Disability == DisabilityCategory.Other ? Input.DisabilityOtherDescription : null;

        await db.SaveChangesAsync();
        StatusMessage = isJoining ? "You're enrolled in the study. Thank you." : "Your study profile was updated.";

        return RedirectToPage();
    }

    private void ValidateOtherDescriptions()
    {
        if (Input.Gender == Gender.Other && string.IsNullOrWhiteSpace(Input.GenderOtherDescription))
            ModelState.AddModelError(nameof(Input.GenderOtherDescription), "Please describe your gender.");

        if (Input.Disability == DisabilityCategory.Other && string.IsNullOrWhiteSpace(Input.DisabilityOtherDescription))
            ModelState.AddModelError(nameof(Input.DisabilityOtherDescription), "Please describe your disability.");
    }

    private async Task<AccountEntity?> GetAccountAsync()
    {
        var userId = userManager.GetUserId(User);
        return await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
    }

    private async Task LoadAsync(bool skipInputReset = false)
    {
        var account = await GetAccountAsync();
        Participation = account is null
            ? null
            : await db.StudyParticipations.FirstOrDefaultAsync(sp => sp.AccountId == account.Id);

        if (!skipInputReset && Participation is not null)
        {
            Input = new InputModel
            {
                ConsentAcknowledged = true,
                DateOfBirth = Participation.DateOfBirth,
                Gender = Participation.Gender,
                GenderOtherDescription = Participation.GenderOtherDescription,
                Region = Participation.Region,
                Disability = Participation.Disability,
                DisabilityOtherDescription = Participation.DisabilityOtherDescription,
            };
        }
    }

    public class InputModel
    {
        public bool ConsentAcknowledged { get; set; }

        [Required]
        [Display(Name = "Date of birth")]
        public DateOnly? DateOfBirth { get; set; }

        [Required]
        public Gender Gender { get; set; }

        [StringLength(200)]
        [Display(Name = "If \"Other\", please describe")]
        public string? GenderOtherDescription { get; set; }

        [Required, StringLength(100)]
        [Display(Name = "Region (e.g. country or state/province)")]
        public string Region { get; set; } = string.Empty;

        [Required]
        public DisabilityCategory Disability { get; set; }

        [StringLength(200)]
        [Display(Name = "If \"Other\", please describe")]
        public string? DisabilityOtherDescription { get; set; }
    }
}
