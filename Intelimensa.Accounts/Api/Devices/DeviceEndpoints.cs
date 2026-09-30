using System.Security.Claims;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Api.Devices;

/// <summary>
/// BCI hardware registration -- called by AxoSync the first time it boots up with a given unit.
/// Validates the serial number against the production list (<see cref="BciDevice"/>) and links it
/// to the calling account (<see cref="AccountDevice"/>), idempotently: re-registering the same
/// account+unit pair returns the existing pairing rather than creating a duplicate. A new pairing
/// also needs the unit's registration code while <see cref="DeviceRegistrationOptions.RequireRegistrationCode"/>
/// is on, and repeated failures are rate limited per user (<see cref="RegistrationFailureLimiter"/>). A unit may be
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
        HttpResponse response,
        ApplicationDbContext db,
        IOptionsMonitor<DeviceRegistrationOptions> options,
        RegistrationFailureLimiter limiter)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Results.Unauthorized();

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        if (!AccountPolicy.IsUsable(account))
            return Results.Unauthorized();

        var now = DateTimeOffset.UtcNow;
        if (limiter.RetryAfterSeconds(userId, now) is { } retryAfter)
        {
            response.Headers.RetryAfter = retryAfter.ToString();
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // One generic failure for every reason a *new* pairing can be refused (unknown serial, unit
        // not yet manufactured or voided, missing/wrong code, unit with no code on file), so the
        // response can't be used to probe which serials exist.
        IResult Refuse()
        {
            limiter.RecordFailure(userId, now);
            return Results.NotFound("Unknown device or invalid registration code.");
        }

        var bciDevice = await db.BciDevices.FindBySerialAsync(request.SerialNumber);
        if (bciDevice is null)
            return Refuse();

        // Re-registering an existing pairing is idempotent and needs no code -- and keeps working
        // even while the unit is awaiting a re-key/re-flash.
        var accountDevice = await db.AccountDevices
            .FirstOrDefaultAsync(ad => ad.AccountId == account!.Id && ad.BciDeviceId == bciDevice.Id);

        if (accountDevice is null)
        {
            if (bciDevice.Status != BciDeviceStatus.Manufactured)
                return Refuse();

            if (options.CurrentValue.RequireRegistrationCode &&
                !RegistrationCode.Verify(request.RegistrationCode, bciDevice.RegistrationCodeHash))
                return Refuse();

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
            SerialNumber.ToDisplay(bciDevice.SerialNumber),
            bciDevice.DeviceType,
            accountDevice.AssignedConfigId,
            accountDevice.RegisteredAt));
    }
}
