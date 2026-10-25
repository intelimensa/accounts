using System.Security.Claims;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Api.ConfigDelivery;

/// <summary>
/// <c>/api/publishing/configs</c>, what ConfigGen's <c>publish</c> command calls. The <c>Publisher</c> role, as for
/// builds, and the same split: a Publisher uploads, the file waits as pending, Staff publish it on <c>/Staff/Configs</c>
/// (or the Publisher does, with <c>--auto-publish</c>). Rules in <see cref="ConfigPublisher"/>, shared with that page.
/// <list type="bullet">
/// <item><c>GET /{key}</c>: the config and its live and pending files.</item>
/// <item><c>PUT /{key}</c> <c>{displayName}</c>: creates the config, or renames it. Idempotent.</item>
/// <item><c>PUT /{key}/file</c>: the body is ConfigGen's v2 blob, the header <c>X-Config-Key</c> its key (base64). Becomes
/// the pending file.</item>
/// <item><c>POST /{key}/publish</c>: the pending file goes live.</item>
/// </list>
/// Errors are <c>{"error": message}</c> with 400, 404 or 409, as for releases.
/// </summary>
public static class ConfigPublishingEndpoints
{
    public static IEndpointRouteBuilder MapConfigPublishingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/publishing/configs").RequireAuthorization("Publisher");
        group.MapGet("/{key}", GetAsync);
        group.MapPut("/{key}", PutAsync);
        group.MapPut("/{key}/file", UploadAsync);
        group.MapPost("/{key}/publish", PublishAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(string key, ClaimsPrincipal user, ApplicationDbContext db)
    {
        if (await UserIdIfUsableAsync(user, db) is null) return Results.Unauthorized();
        var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == key);
        return config is null ? Results.NotFound(new { error = $"No config {key}." }) : Results.Ok(ToResponse(config));
    }

    private static async Task<IResult> PutAsync(
        string key, ConfigDraftRequest? body, ClaimsPrincipal user, ApplicationDbContext db, ConfigPublisher publisher)
    {
        if (await UserIdIfUsableAsync(user, db) is null) return Results.Unauthorized();
        var result = await publisher.CreateOrUpdateAsync(key, body?.DisplayName);
        return result.Ok ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result);
    }

    private static async Task<IResult> UploadAsync(
        string key, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, ConfigPublisher publisher, CancellationToken ct)
    {
        if (await UserIdIfUsableAsync(user, db) is not { } userId) return Results.Unauthorized();

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = ConfigPublisher.MaxBlobBytes + 1;

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > ConfigPublisher.MaxBlobBytes)
            return Results.BadRequest(new { error = "The config file is larger than 4 MB." });

        var result = await publisher.UploadAsync(key, buffer.ToArray(), http.Request.Headers["X-Config-Key"].ToString(), userId, ct);
        return result.Ok ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result);
    }

    private static async Task<IResult> PublishAsync(string key, ClaimsPrincipal user, ApplicationDbContext db, ConfigPublisher publisher)
    {
        if (await UserIdIfUsableAsync(user, db) is not { } userId) return Results.Unauthorized();
        var result = await publisher.PublishAsync(key, userId);
        return result.Ok ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result);
    }

    private static async Task<string?> UserIdIfUsableAsync(ClaimsPrincipal user, ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return null;
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account) ? userId : null;
    }

    private static IResult ToProblem<T>(PublishResult<T> result) => result.Error switch
    {
        PublishError.NotFound => Results.NotFound(new { error = result.Message }),
        PublishError.Conflict => Results.Conflict(new { error = result.Message }),
        _ => Results.BadRequest(new { error = result.Message }),
    };

    private static PublishedConfigResponse ToResponse(Config c) => new(
        c.Key, c.DisplayName, c.IsDefault, c.RetiredAt is not null,
        c.HasFile ? new ConfigFileResponse(c.BlobSha256!, c.BlobSize ?? 0, c.UploadedAt) : null,
        c.HasPendingFile ? new ConfigFileResponse(c.PendingBlobSha256!, c.PendingBlobSize ?? 0, c.PendingUploadedAt) : null);
}
