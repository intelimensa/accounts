using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Importing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// The BCI hardware production/inventory list. New units are normally created by the manufacturing
/// station (<c>/api/manufacturing/units</c>), which issues each one a registration code. Rows added
/// on this page -- one at a time or by CSV -- are <em>backfill</em>: marked Manufactured but with no
/// registration code, so they can't be newly registered while code checking is on. Participants
/// link themselves to a row by serial number via <c>POST /api/devices/register</c>.
/// </summary>
public class DevicesModel(ApplicationDbContext db) : PageModel
{
    public List<DeviceRow> Devices { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public List<BciDeviceCsvParser.RowError> ImportErrors { get; private set; } = [];

    private const long MaxImportBytes = 1024 * 1024;

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

        var serialNumber = Input.SerialNumber.Trim();
        if (await db.BciDevices.AnyAsync(d => d.SerialNumber == serialNumber))
        {
            ErrorMessage = $"A device with serial number '{serialNumber}' already exists.";
            await LoadAsync();
            return Page();
        }

        db.BciDevices.Add(new BciDevice
        {
            SerialNumber = serialNumber,
            DeviceType = Input.DeviceType.Trim(),
            ProducedAt = Input.ProducedAt,
            CurrentFirmwareVersion = Input.CurrentFirmwareVersion.Trim(),
            Status = BciDeviceStatus.Manufactured,
            ManufacturedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? csvFile)
    {
        if (csvFile is null || csvFile.Length == 0)
        {
            ErrorMessage = "Choose a CSV file to import.";
            await LoadAsync();
            return Page();
        }

        if (csvFile.Length > MaxImportBytes)
        {
            ErrorMessage = "That file is too large (1 MB limit).";
            await LoadAsync();
            return Page();
        }

        BciDeviceCsvParser.Result result;
        using (var reader = new StreamReader(csvFile.OpenReadStream()))
            result = BciDeviceCsvParser.Parse(reader);

        // Serials already in the DB are row errors too, so the import stays all-or-nothing.
        var incoming = result.Devices.Select(d => d.SerialNumber).ToList();
        var existing = (await db.BciDevices
                .Where(d => incoming.Contains(d.SerialNumber))
                .Select(d => d.SerialNumber)
                .ToListAsync())
            .ToHashSet();

        var errors = result.Errors.ToList();
        if (existing.Count > 0)
        {
            errors.AddRange(existing.Order().Select(s =>
                new BciDeviceCsvParser.RowError(0, $"Serial '{s}' already exists.")));
        }

        if (errors.Count > 0)
        {
            ImportErrors = errors;
            ErrorMessage = "Nothing was imported -- fix the errors below and upload again.";
            await LoadAsync();
            return Page();
        }

        if (result.Devices.Count == 0)
        {
            ErrorMessage = "The file has a header but no device rows.";
            await LoadAsync();
            return Page();
        }

        foreach (var device in result.Devices)
        {
            device.Status = BciDeviceStatus.Manufactured;
            device.ManufacturedAt = DateTimeOffset.UtcNow;
        }

        db.BciDevices.AddRange(result.Devices);
        await db.SaveChangesAsync();

        StatusMessage = $"Imported {result.Devices.Count} device(s).";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        // SQLite's EF provider can't translate ORDER BY on DateTimeOffset -- sort client-side.
        var devices = await db.BciDevices
            .Include(d => d.AccountDevices)
            .ToListAsync();

        Devices = devices
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DeviceRow(
                d.SerialNumber,
                d.DeviceType,
                d.ProducedAt,
                d.CurrentFirmwareVersion,
                d.LastFirmwareUpdatedAt,
                d.AccountDevices.Count,
                d.Status,
                d.RegistrationCodeHash is not null))
            .ToList();
    }

    public class InputModel
    {
        [Required, StringLength(100)]
        [Display(Name = "Serial number")]
        public string SerialNumber { get; set; } = string.Empty;

        [Required, StringLength(100)]
        [Display(Name = "Device type (e.g. ms2)")]
        public string DeviceType { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Produced on")]
        public DateOnly ProducedAt { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

        [Required, StringLength(50)]
        [Display(Name = "Firmware version")]
        public string CurrentFirmwareVersion { get; set; } = string.Empty;
    }

    public record DeviceRow(
        string SerialNumber,
        string DeviceType,
        DateOnly ProducedAt,
        string CurrentFirmwareVersion,
        DateTimeOffset? LastFirmwareUpdatedAt,
        int RegistrationCount,
        BciDeviceStatus Status,
        bool HasRegistrationCode);
}
