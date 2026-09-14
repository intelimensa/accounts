using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

public class ConfigsModel(ApplicationDbContext db) : PageModel
{
    public List<Config> Configs { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var key = Input.Key.Trim();
        if (await db.Configs.AnyAsync(c => c.Key == key))
        {
            ErrorMessage = $"A config with key '{key}' already exists.";
            await LoadAsync();
            return Page();
        }

        db.Configs.Add(new Config
        {
            Key = key,
            DisplayName = Input.DisplayName.Trim(),
        });
        await db.SaveChangesAsync();

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Configs = await db.Configs.OrderBy(c => c.DisplayName).ToListAsync();
    }

    public class InputModel
    {
        [Required, StringLength(100)]
        public string Key { get; set; } = string.Empty;

        [Required, StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;
    }
}
