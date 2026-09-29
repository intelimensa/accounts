using System.Security.Claims;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Api.Devices;

/// <summary>
/// BCI hardware registration -- called by AxoSync the first time it boots up with a given unit.
/// Validates the serial number against the production list (<see cref="BciDevice"/>) and links it
/// to the calling account (<see cref="AccountDevice"/>), idempotently: re-registering the same
/// account+unit pair returns the existing pairing rather than creating a duplicate. A unit may be
/// registered to multiple accounts at once (see CLAUDE.md) -- registration never unassigns another
/// account's pairing to the same serial.
/// </summary>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

        group.MapPost("/register", RegisterAsync);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Results.Unauthorized();

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        if (!AccountPolicy.IsUsable(account))
            return Results.Unauthorized();

        var serialNumber = request.SerialNumber.Trim();
        var bciDevice = await db.BciDevices.FirstOrDefaultAsync(d => d.SerialNumber == serialNumber);
        if (bciDevice is null)
            return Results.NotFound("Unknown device serial number.");

        var accountDevice = await db.AccountDevices
            .FirstOrDefaultAsync(ad => ad.AccountId == account!.Id && ad.BciDeviceId == bciDevice.Id);

        if (accountDevice is null)
        {
            accountDevice = new AccountDevice
            {
                AccountId = account!.Id,
                BciDeviceId = bciDevice.Id,
            };
            db.AccountDevices.Add(accountDevice);
            await db.SaveChangesAsync();
        }

        return Results.Ok(new RegisterDeviceResponse(
            accountDevice.Id,
            bciDevice.Id,
            bciDevice.SerialNumber,
            bciDevice.DeviceType,
            accountDevice.AssignedConfigId,
            accountDevice.RegisteredAt));
    }
}
