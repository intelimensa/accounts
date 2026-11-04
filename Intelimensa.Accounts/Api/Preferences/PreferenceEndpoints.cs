using System.Security.Claims;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Preferences;
using Intelimensa.Accounts.Security;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Api.Preferences;

/// <summary>
/// AxoSync's preference slots (docs/preferences-and-game-results.md): five named profiles per account that follow the
/// user across machines. Any account that is still usable, with a bound or an older unbound token.
/// <list type="bullet">
/// <item><c>GET /api/preferences</c>: the five slots without payloads.</item>
/// <item><c>GET /api/preferences/{slot}</c>: one slot, <c>ETag</c> = its version; a never-written slot is version 0 and empty.</item>
/// <item><c>PUT /api/preferences/{slot}</c>, <c>PATCH .../name</c>, <c>DELETE</c>: need <c>If-Match</c> (the version read last,
/// <c>"0"</c> = no row yet). A stale version is 412 with the slot as it is now.</item>
/// </list>
/// Errors are <c>{"error": code}</c>: 400 <c>invalid_preferences</c> (with <c>detail</c>) / <c>invalid_if_match</c>, 401,
/// 404 <c>unknown_slot</c>, 412 <c>version_conflict</c>, 428 <c>if_match_required</c>.
/// </summary>
public static class PreferenceEndpoints
{
    public static IEndpointRouteBuilder MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").RequireAuthorization("AccountClient");
        group.MapGet("", ListAsync);
        group.MapGet("/{slot:int}", GetAsync);
        group.MapPut("/{slot:int}", PutAsync);
        group.MapPatch("/{slot:int}/name", RenameAsync);
        group.MapDelete("/{slot:int}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, ApplicationDbContext db, PreferenceSlotService slots)
    {
        if (await UsableAccountIdAsync(user, db) is not { } accountId) return Results.Unauthorized();
        return Results.Ok(new PreferenceSlotListResponse(await slots.ListAsync(accountId)));
    }

    private static async Task<IResult> GetAsync(
        int slot, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, PreferenceSlotService slots)
    {
        if (await UsableAccountIdAsync(user, db) is not { } accountId) return Results.Unauthorized();
        if (!PreferenceLimits.IsSlot(slot)) return UnknownSlot();

        return Respond(http, await slots.GetAsync(accountId, slot));
    }

    private static async Task<IResult> PutAsync(
        int slot, PutPreferenceSlotRequest? request, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, PreferenceSlotService slots)
    {
        if (await UsableAccountIdAsync(user, db) is not { } accountId) return Results.Unauthorized();
        if (!PreferenceLimits.IsSlot(slot)) return UnknownSlot();
        var (expected, refusal) = ExpectedVersion(http);
        if (refusal is not null) return refusal;

        return Respond(http, await slots.PutAsync(accountId, slot, expected, request?.Name, request?.Payload));
    }

    private static async Task<IResult> RenameAsync(
        int slot, RenamePreferenceSlotRequest? request, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, PreferenceSlotService slots)
    {
        if (await UsableAccountIdAsync(user, db) is not { } accountId) return Results.Unauthorized();
        if (!PreferenceLimits.IsSlot(slot)) return UnknownSlot();
        var (expected, refusal) = ExpectedVersion(http);
        if (refusal is not null) return refusal;

        return Respond(http, await slots.RenameAsync(accountId, slot, expected, request?.Name));
    }

    private static async Task<IResult> DeleteAsync(
        int slot, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, PreferenceSlotService slots)
    {
        if (await UsableAccountIdAsync(user, db) is not { } accountId) return Results.Unauthorized();
        if (!PreferenceLimits.IsSlot(slot)) return UnknownSlot();
        var (expected, refusal) = ExpectedVersion(http);
        if (refusal is not null) return refusal;

        var result = await slots.DeleteAsync(accountId, slot, expected);
        return result.Outcome == SlotOutcome.Ok ? Results.NoContent() : Respond(http, result);
    }

    private static IResult Respond(HttpContext http, PreferenceSlotResponse slot)
    {
        http.Response.Headers.ETag = $"\"{slot.Version}\"";
        return Results.Ok(slot);
    }

    private static IResult Respond(HttpContext http, SlotResult result)
    {
        switch (result.Outcome)
        {
            case SlotOutcome.Ok:
                return Respond(http, result.Slot!);
            case SlotOutcome.Invalid:
                return Results.Json(new { error = "invalid_preferences", detail = result.Error }, statusCode: StatusCodes.Status400BadRequest);
            default:
                http.Response.Headers.ETag = $"\"{result.Slot!.Version}\"";
                return Results.Json(new PreferenceSlotConflictResponse("version_conflict", result.Slot),
                    statusCode: StatusCodes.Status412PreconditionFailed);
        }
    }

    private static IResult UnknownSlot() =>
        Results.Json(new { error = "unknown_slot" }, statusCode: StatusCodes.Status404NotFound);

    /// <summary><c>If-Match: "3"</c> (the quotes are optional) as 3, or the refusal to send when it is missing or not a count.</summary>
    private static (long Version, IResult? Refusal) ExpectedVersion(HttpContext http)
    {
        var header = http.Request.Headers.IfMatch.ToString().Trim().Trim('"');
        if (header.Length == 0)
            return (0, Results.Json(new { error = "if_match_required" }, statusCode: StatusCodes.Status428PreconditionRequired));
        return long.TryParse(header, out var version) && version >= 0
            ? (version, null)
            : (0, Results.Json(new { error = "invalid_if_match" }, statusCode: StatusCodes.Status400BadRequest));
    }

    private static async Task<int?> UsableAccountIdAsync(ClaimsPrincipal user, ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return null;
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account) ? account!.Id : null;
    }
}
