using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AccountEntity = Intelimensa.Accounts.Models.Account;

namespace Intelimensa.Accounts.Pages.Account;

public class RegisterModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ApplicationDbContext db) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var user = new ApplicationUser
        {
            UserName = Input.Email,
            Email = Input.Email,
        };

        var result = await userManager.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        db.Accounts.Add(new AccountEntity
        {
            UserId = user.Id,
            Status = AccountStatus.Active,
        });
        await db.SaveChangesAsync();

        await signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToPage("/Account/Status");
    }

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 12, ErrorMessage = "The {0} must be at least {2} characters long.")]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The password and confirmation password do not match.")]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
