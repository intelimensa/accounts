using System.Security.Claims;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Intelimensa.Accounts.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Intelimensa.Accounts.Api.ConfigDelivery;

/// <summary>
/// Config delivery to AxoSync (docs/config-management.md, section 4). Both endpoints need a token bound to a client
/// install (policy <c>InstallClient</c>) and that install's proof on the request (<see cref="InstallProofMiddleware"/>):
/// <list type="bullet">
/// <item><c>GET /api/devices/{serial}/config?version=&amp;platform=</c>: the unit's config key, wrapped for this
/// install and build, with a signed offline lease. <c>no-store</c>; every issuance is recorded. <c>version</c> and
/// <c>platform</c> name the build that will open it: the running one, or before an update the one being installed.</item>
/// <item><c>GET /api/configs/{key}/blob</c>: the encrypted blob, cacheable (ETag = SHA-256), for an account with a unit
/// assigned to that config.</item>
/// </list>
/// Errors are <c>{"error": code}</c>: 400 <c>version_and_platform_required</c>, 401 (account or install proof),
/// 403 <c>build_not_supported</c> (update AxoSync), 404 <c>not_registered</c> / <c>not_assigned</c> / <c>no_config</c>,
/// 503 <c>config_delivery_unavailable</c>.
/// </summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization("InstallClient");
        group.MapGet("/devices/{serial}/config", GetConfigAsync);
        group.MapGet("/configs/{key}/blob", GetBlobAsync);
        return app;
    }

    private static async Task<IResult> GetConfigAsync(
        string serial,
        string? version,
        string? platform,
        HttpContext http,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        ConfigIssuer issuer)
    {
        var account = await UsableAccountAsync(user, db);
        var install = http.GetClientInstall();
        if (account is null || install is null || install.AccountId != account.Id) return Results.Unauthorized();

        var result = await issuer.IssueAsync(account, install, serial, version, platform, DateTimeOffset.UtcNow);
        if (result.Issued is not { } issued)
            return Results.Json(new { error = result.Error }, statusCode: result.Status);

        http.Response.Headers.CacheControl = "no-store";
        var config = issued.Config;
        var lease = issued.Lease;
        var wrapped = issued.WrappedCek;
        return Results.Ok(new ConfigDeliveryResponse(
            config.Key, config.DisplayName, config.BlobSha256!, config.BlobSize ?? 0, $"/api/configs/{config.Key}/blob",
            new WrappedCekResponse(Convert.ToBase64String(wrapped.EphemeralKey), Convert.ToBase64String(wrapped.Nonce),
                Convert.ToBase64String(wrapped.Ciphertext), Convert.ToBase64String(wrapped.Tag)),
            new LeaseResponse(lease.AccountId, lease.ClientInstallId, lease.SerialNumber, lease.ConfigKey, lease.BlobSha256,
                OfflineLease.FormatTime(lease.IssuedAt), OfflineLease.FormatTime(lease.NotAfter)),
            issued.LeaseSignature));
    }

    private static async Task<IResult> GetBlobAsync(
        string key, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, IConfigStorage storage)
    {
        var account = await UsableAccountAsync(user, db);
        var install = http.GetClientInstall();
        if (account is null || install is null || install.AccountId != account.Id) return Results.Unauthorized();

        var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == key);
        var entitled = config is { IsDeliverable: true } && await db.AccountDevices.AnyAsync(ad =>
            ad.AccountId == account.Id && ad.UnassignedAt == null && ad.AssignedConfigId == config.Id);
        if (!entitled) return Results.Json(new { error = "no_config" }, statusCode: StatusCodes.Status404NotFound);

        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.File(storage.OpenRead(config!.BlobStorageKey!), "application/octet-stream", $"{config.Key}.cfg",
            entityTag: new EntityTagHeaderValue($"\"{config.BlobSha256}\""));
    }

    private static async Task<Account?> UsableAccountAsync(ClaimsPrincipal user, ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return null;
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account) ? account : null;
    }
}
