using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Firmware;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// The catalog of production firmware the manufacturing station can flash. Staff upload a hex as a
/// draft, then publish it; only Published builds are listed to (and downloadable by) Quarry.
/// </summary>
[RequestSizeLimit(MaxHexBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxHexBytes)]
public partial class FirmwareModel(
    ApplicationDbContext db, IFirmwareStorage storage, IOptions<ManufacturingOptions> manufacturing) : PageModel
{
    // A PIC32 hex is a few MB at most; the cap also bounds the buffered structural check.
    private const long MaxHexBytes = 16L * 1024 * 1024;

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._+-]{0,49}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex FileNamePattern();

    public List<FirmwareBuild> Builds { get; private set; } = [];

    public List<string> DeviceTypes { get; private set; } = [];

    [BindProperty]
    public UploadInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        var deviceType = Input.DeviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        var version = Input.Version?.Trim() ?? string.Empty;

        if (!ModelState.IsValid)
            return await FailAsync(null);
        if (!manufacturing.Value.ProductCodes.ContainsKey(deviceType))
            return await FailAsync("Unknown device type.");
        if (!Enum.IsDefined(Input.Kind))
            return await FailAsync("Unknown kind.");
        if (Input.Kind == FirmwareKind.Bootloader && deviceType != "msv3")
            return await FailAsync("Only MS-V3 has a separate bootloader image.");
        if (!VersionPattern().IsMatch(version))
            return await FailAsync("Version may only contain letters, digits, '.', '_', '+' and '-' (max 50).");
        if (Input.File is null || Input.File.Length == 0)
            return await FailAsync("Choose a hex file to upload.");

        // The name becomes a storage path segment.
        var fileName = Path.GetFileName(Input.File.FileName);
        if (!FileNamePattern().IsMatch(fileName) || fileName.Length > 200 || fileName.Contains(".."))
            return await FailAsync("File name may only contain letters, digits, '.', '_' and '-'.");

        if (await db.FirmwareBuilds.AnyAsync(b => b.DeviceType == deviceType && b.Kind == Input.Kind && b.Version == version, ct))
            return await FailAsync($"{deviceType.ToUpperInvariant()} {Input.Kind} {version} already exists. Use a new version.");

        // Builds are immutable once uploaded (a different file is a new version), which is why
        // there's no replace: what a unit was flashed with must stay reconstructible.
        await using (var content = Input.File.OpenReadStream())
        {
            if (IntelHex.Validate(content) is { } hexError)
                return await FailAsync($"Not a valid Intel HEX file. {hexError}");
        }

        await using var stream = Input.File.OpenReadStream();
        var stored = await storage.SaveAsync(deviceType, Input.Kind, version, fileName, stream, ct);

        db.FirmwareBuilds.Add(new FirmwareBuild
        {
            DeviceType = deviceType,
            Kind = Input.Kind,
            Version = version,
            Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim(),
            FileName = fileName,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
        });
        await db.SaveChangesAsync(ct);

        StatusMessage = $"Uploaded {deviceType.ToUpperInvariant()} {Input.Kind} {version} as a draft.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(int buildId, FirmwareBuildStatus status)
    {
        var build = await db.FirmwareBuilds.FindAsync(buildId);
        if (build is null) return NotFound();

        if (!Enum.IsDefined(status))
            return await FailAsync("Unknown status.");
        if (status == FirmwareBuildStatus.Draft)
            return await FailAsync("A build can't be returned to draft.");

        build.Status = status;
        if (status == FirmwareBuildStatus.Published) build.PublishedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        StatusMessage = $"{build.DeviceType.ToUpperInvariant()} {build.Kind} {build.Version} is now {status}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int buildId)
    {
        var build = await db.FirmwareBuilds.FindAsync(buildId);
        if (build is null) return NotFound();

        // Only never-published drafts: anything that was offered to a station stays for history.
        if (build.Status != FirmwareBuildStatus.Draft || build.PublishedAt is not null)
            return await FailAsync("Only drafts can be deleted; withdraw a published build instead.");

        storage.Delete(build.StorageKey);
        db.FirmwareBuilds.Remove(build);
        await db.SaveChangesAsync();

        StatusMessage = $"Deleted draft {build.DeviceType.ToUpperInvariant()} {build.Kind} {build.Version}.";
        return RedirectToPage();
    }

    private async Task<IActionResult> FailAsync(string? message)
    {
        ErrorMessage = message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        DeviceTypes = manufacturing.Value.ProductCodes.Keys.Select(k => k.ToLowerInvariant()).Order().ToList();
        var builds = await db.FirmwareBuilds.ToListAsync();
        // SQLite's EF provider can't ORDER BY DateTimeOffset -- sort client-side.
        Builds = builds
            .OrderBy(b => b.DeviceType).ThenBy(b => b.Kind).ThenByDescending(b => b.CreatedAt)
            .ToList();
    }

    public class UploadInput
    {
        [Required]
        public string? DeviceType { get; set; }

        public FirmwareKind Kind { get; set; }

        [Required, StringLength(50)]
        public string? Version { get; set; }

        [StringLength(4000)]
        public string? Notes { get; set; }

        [Required]
        public IFormFile? File { get; set; }
    }
}
