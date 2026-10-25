using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// Config rows and their files: create a config, upload ConfigGen's <c>.cfg</c> + <c>.key</c> pair to it (replacing
/// a file rotates its key), mark the default the assignment form pre-selects, retire one. The upload rules live in
/// <see cref="ConfigUploader"/>.
/// </summary>
[RequestSizeLimit(ConfigUploader.MaxBlobBytes + 64 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = ConfigUploader.MaxBlobBytes + 64 * 1024)]
public class ConfigsModel(ApplicationDbContext db, ConfigUploader uploader, KeyEncryption keys) : PageModel
{
    public List<Config> Configs { get; private set; } = [];

    public bool DeliveryAvailable => keys.IsAvailable;

    public bool CanUpload => keys.CanWrap;

    /// <summary>What AxoSync embeds to check leases.</summary>
    public string? LeasePublicKey => keys.LeasePublicKey;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

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
            return await FailAsync($"A config with key '{key}' already exists.");

        db.Configs.Add(new Config
        {
            Key = key,
            DisplayName = Input.DisplayName.Trim(),
        });
        await db.SaveChangesAsync();

        StatusMessage = $"Created config {key}. Upload its file next.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadAsync(int configId, IFormFile? blob, IFormFile? key, CancellationToken ct)
    {
        if (blob is null || blob.Length == 0 || key is null || key.Length == 0)
            return await FailAsync("Choose both files: the .cfg and the .key ConfigGen wrote.");
        if (blob.Length > ConfigUploader.MaxBlobBytes || key.Length > 1024)
            return await FailAsync("Those files are too large to be a config and its key.");

        byte[] bytes;
        await using (var stream = blob.OpenReadStream())
        {
            bytes = new byte[blob.Length];
            await stream.ReadExactlyAsync(bytes, ct);
        }
        string keyText;
        using (var reader = new StreamReader(key.OpenReadStream()))
            keyText = await reader.ReadToEndAsync(ct);

        var error = await uploader.UploadAsync(configId, bytes, keyText, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
        if (error is not null) return await FailAsync(error);

        StatusMessage = "Config file uploaded. Delete the .key file from your computer now: the server keeps the only copy.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMakeDefaultAsync(int configId)
    {
        var configs = await db.Configs.ToListAsync();
        var config = configs.FirstOrDefault(c => c.Id == configId);
        if (config is null) return NotFound();
        if (config.RetiredAt is not null) return await FailAsync("A retired config can't be the default.");

        foreach (var c in configs) c.IsDefault = c.Id == configId;
        await db.SaveChangesAsync();
        StatusMessage = $"{config.DisplayName} is now the default.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetireAsync(int configId, bool retire)
    {
        var config = await db.Configs.FirstOrDefaultAsync(c => c.Id == configId);
        if (config is null) return NotFound();

        config.RetiredAt = retire ? DateTimeOffset.UtcNow : null;
        if (retire) config.IsDefault = false;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        StatusMessage = retire
            ? $"{config.DisplayName} is retired: it is no longer delivered or offered for new assignments."
            : $"{config.DisplayName} is back in use.";
        return RedirectToPage();
    }

    private async Task<IActionResult> FailAsync(string message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
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
