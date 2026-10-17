using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Admin;

public class RolesModel(UserManager<ApplicationUser> userManager) : PageModel
{
    /// <summary>The roles an Administrator may hand out. Administrator itself is excluded on purpose.</summary>
    public static readonly string[] Grantable = ["Staff", "Manufacturer", "Publisher"];

    public List<UserRow> Users { get; private set; } = [];

    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSetAsync(string userId, string role, bool enabled)
    {
        if (!Grantable.Contains(role))
            return BadRequest();

        if (userId == userManager.GetUserId(User))
        {
            ErrorMessage = "You can't change your own roles.";
            await LoadAsync();
            return Page();
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
            return NotFound();

        var has = await userManager.IsInRoleAsync(user, role);
        if (enabled != has)
        {
            var result = enabled
                ? await userManager.AddToRoleAsync(user, role)
                : await userManager.RemoveFromRoleAsync(user, role);
            if (!result.Succeeded)
            {
                ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
                await LoadAsync();
                return Page();
            }

            // Ends the user's web sessions (SecurityStampCheck); a JWT already issued
            // keeps its old roles until it expires (15 minutes by default).
            await userManager.UpdateSecurityStampAsync(user);
        }

        StatusMessage = $"{user.Email}: {role} {(enabled ? "granted" : "revoked")}.";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var users = await userManager.Users.OrderBy(u => u.Email).ToListAsync();
        var rows = new List<UserRow>();
        foreach (var user in users)
            rows.Add(new UserRow(user.Id, user.Email ?? "(no email)", (await userManager.GetRolesAsync(user)).ToHashSet()));
        Users = rows;
    }

    public record UserRow(string Id, string Email, HashSet<string> Roles);
}
