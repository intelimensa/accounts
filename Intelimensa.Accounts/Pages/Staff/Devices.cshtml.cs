using System.ComponentModel.DataAnnotations;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Staff;

/// <summary>
/// The BCI hardware production/inventory list -- entered here as units are manufactured, before
/// any are distributed to participants. See CLAUDE.md's device-registration design; participants
/// link themselves to a row here by serial number via <c>POST /api/devices/register</c>.
/// </summary>
public class DevicesModel(ApplicationDbContext db) : PageModel
{
    public List<DeviceRow> Devices { get; private set; } = [];

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
        });
        await db.SaveChangesAsync();

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
                d.AccountDevices.Count))
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
        int RegistrationCount);
}
