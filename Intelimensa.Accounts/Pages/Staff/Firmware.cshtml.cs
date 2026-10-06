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
/// draft (its kind and versions are read from the file), then publish it; only Published builds are listed to (and downloadable by) Quarry.
/// </summary>
[RequestSizeLimit(MaxHexBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxHexBytes)]
public partial class FirmwareModel(
    ApplicationDbContext db, IFirmwareStorage storage, IOptions<ManufacturingOptions> manufacturing) : PageModel
{
    // A PIC32 hex is a few MB at most; the cap also bounds the buffered structural check.
    private const long MaxHexBytes = 16L * 1024 * 1024;

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

        if (!ModelState.IsValid)
            return await FailAsync(null);
        if (!manufacturing.Value.ProductCodes.ContainsKey(deviceType))
            return await FailAsync("Unknown device type.");
        if (Input.File is null || Input.File.Length == 0)
            return await FailAsync("Choose a hex file to upload.");

        // The name becomes a storage path segment.
        var fileName = Path.GetFileName(Input.File.FileName);
        if (!FileNamePattern().IsMatch(fileName) || fileName.Length > 200 || fileName.Contains(".."))
            return await FailAsync("File name may only contain letters, digits, '.', '_' and '-'.");

        // Read what the file is -- never typed: the kind and versions come from the image itself, so
        // the catalog can't disagree with what a unit would actually be flashed with.
        FirmwareDescription image;
        try
        {
            using var reader = new StreamReader(Input.File.OpenReadStream(), System.Text.Encoding.ASCII);
            image = FirmwareImage.Describe(await reader.ReadToEndAsync(ct));
        }
        catch (FirmwareImageException ex)
        {
            return await FailAsync(ex.Message);
        }

        if (ImageError(deviceType, image) is { } imageError)
            return await FailAsync(imageError);

        var kind = image.Kind == FirmwareImageKind.MsV3Factory ? FirmwareKind.Factory : FirmwareKind.Application;
        var version = image.Version!;

        if (await db.FirmwareBuilds.AnyAsync(b => b.DeviceType == deviceType && b.Kind == kind && b.Version == version, ct))
            return await FailAsync($"{deviceType.ToUpperInvariant()} {kind} {version} is already in the catalog. A changed build has a new version.");

        // Builds are immutable once uploaded (a different file is a new version), which is why
        // there's no replace: what a unit was flashed with must stay reconstructible.
        await using var stream = Input.File.OpenReadStream();
        var stored = await storage.SaveAsync(deviceType, kind, version, fileName, stream, ct);

        db.FirmwareBuilds.Add(new FirmwareBuild
        {
            DeviceType = deviceType,
            Kind = kind,
            Version = version,
            BootloaderVersion = image.BootloaderVersion,
            Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim(),
            FileName = fileName,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
        });
        await db.SaveChangesAsync(ct);

        StatusMessage = $"Read {Describe(kind, version, image.BootloaderVersion)} for {deviceType.ToUpperInvariant()} from {fileName}; added as a draft.";
        return RedirectToPage();
    }

    /// <summary>Whether the detected image belongs in the catalog for this product. Null if fine, else why not.</summary>
    private static string? ImageError(string deviceType, FirmwareDescription image)
    {
        if (image.Version is null)
            return "No firmware version found in the image (or it holds several different ones).";
        if (!FirmwareImage.IsRelease(image.Version))
            return $"{image.Version} isn't a release build (built from an unclean tree, or a test build). Only clean builds can be added.";

        if (deviceType == "msv3")
        {
            if (image.Kind == FirmwareImageKind.Single)
                return "This is a single image, not an MS-V3 one. MS-V3 takes a factory image (bootloader + app) or an app-only image.";
            if (image.Kind == FirmwareImageKind.MsV3Factory)
            {
                if (image.BootloaderVersion is null)
                    return "The factory image's bootloader has no version string (or several different ones).";
                if (!FirmwareImage.IsRelease(image.BootloaderVersion))
                    return $"The bootloader version {image.BootloaderVersion} isn't a release build.";
            }
        }
        else if (image.Kind != FirmwareImageKind.Single)
        {
            var what = image.Kind == FirmwareImageKind.MsV3Factory ? "factory" : "app";
            return $"This is an MS-V3 {what} image, but the device type is {deviceType.ToUpperInvariant()}.";
        }

        return null;
    }

    public static string Describe(FirmwareKind kind, string version, string? bootloaderVersion) =>
        kind == FirmwareKind.Factory && bootloaderVersion is not null
            ? $"factory image: app {version}, bootloader {bootloaderVersion}"
            : $"{kind.ToString().ToLowerInvariant()} {version}";

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

        [StringLength(4000)]
        public string? Notes { get; set; }

        [Required]
        public IFormFile? File { get; set; }
    }
}
