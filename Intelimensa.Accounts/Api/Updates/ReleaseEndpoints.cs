using System.Security.Claims;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.Security;
using Intelimensa.Accounts.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Intelimensa.Accounts.Api.Updates;

/// <summary>
/// Two faces of the release system, both JWT:
/// <list type="bullet">
/// <item><c>/api/releases</c> -- what AxoSync calls: <c>latest</c> (the update check) and the
/// installer download. Any account that is still usable (active, not expired), re-checked on every
/// call so revoking an account stops updates immediately.</item>
/// <item><c>/api/publishing</c> -- what the publish script calls, restricted to the
/// <c>Publisher</c> role: create a draft, upload a platform's installer with its signature, publish
/// or withdraw. Mirrors <c>/Staff/Releases</c> and enforces the same rules through
/// <see cref="ReleasePublisher"/>.</item>
/// </list>
/// </summary>
public static class ReleaseEndpoints
{
    // Same cap as the staff upload page: Kestrel's default (~30 MB) is far below an installer.
    private const long MaxArtifactBytes = 1L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        var releases = app.MapGroup("/api/releases").RequireAuthorization("ReleaseClient");
        releases.MapGet("/latest", CheckForUpdateAsync);
        releases.MapGet("/{version}/{platform}/file", DownloadAsync);

        var publishing = app.MapGroup("/api/publishing").RequireAuthorization("Publisher");
        publishing.MapGet("/releases/{version}", GetReleaseAsync);
        publishing.MapPut("/releases/{version}", PutDraftAsync);
        publishing.MapPut("/releases/{version}/artifacts/{platform}", UploadAsync);
        publishing.MapPost("/releases/{version}/publish", (string version, ClaimsPrincipal user, ApplicationDbContext db, ReleasePublisher publisher) =>
            SetStatusAsync(version, ReleaseStatus.Published, user, db, publisher));
        publishing.MapPost("/releases/{version}/withdraw", (string version, ClaimsPrincipal user, ApplicationDbContext db, ReleasePublisher publisher) =>
            SetStatusAsync(version, ReleaseStatus.Withdrawn, user, db, publisher));

        return app;
    }

    // ---- AxoSync: update check + download -------------------------------------------------------

    private static async Task<IResult> CheckForUpdateAsync(
        ReleasePlatform? platform,
        string? current,
        bool? includePrerelease,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        ReleasePublisher publisher)
    {
        if (!await IsUsableAsync(user, db)) return Results.Unauthorized();
        if (platform is null || !Enum.IsDefined(platform.Value))
            return Results.BadRequest(new { error = "platform is required (WindowsX64, MacOSArm64, MacOSX64, LinuxX64)." });

        var offered = await FindLatestAsync(db, platform.Value, includePrerelease ?? false, publisher.SignatureRequired);

        // A caller whose version we can't parse (or that sent none) is offered whatever is newest.
        var haveCurrent = ReleaseVersion.TryParse(current, allowBuildMetadata: true, out var currentVersion);
        if (offered is null || (haveCurrent && offered.Version <= currentVersion))
            return Results.Ok(new UpdateCheckResponse(false, false, null, null, null, null, null));

        var release = offered.Release;
        var artifact = release.Artifacts.First(a => a.Platform == platform.Value);
        var mandatory = haveCurrent &&
            ReleaseVersion.TryParse(release.MinimumVersion, out var minimum) &&
            currentVersion < minimum;

        return Results.Ok(new UpdateCheckResponse(
            true, mandatory, release.Version, release.Notes, release.MinimumVersion, release.PublishedAt,
            new UpdateArtifact(artifact.FileName, artifact.SizeBytes, artifact.Sha256, artifact.Signature,
                $"/api/releases/{release.Version}/{platform.Value}/file")));
    }

    private static async Task<IResult> DownloadAsync(
        string version, ReleasePlatform platform, ClaimsPrincipal user, ApplicationDbContext db, IReleaseStorage storage)
    {
        if (!await IsUsableAsync(user, db)) return Results.Unauthorized();

        var artifact = await db.ReleaseArtifacts
            .Include(a => a.Release)
            .FirstOrDefaultAsync(a =>
                a.Release!.Version == version &&
                a.Release.Status == ReleaseStatus.Published &&
                a.Platform == platform);
        if (artifact is null) return Results.NotFound();

        return Results.File(
            storage.OpenRead(artifact.StorageKey),
            "application/octet-stream",
            artifact.FileName,
            entityTag: new EntityTagHeaderValue($"\"{artifact.Sha256}\""),
            enableRangeProcessing: true);
    }

    private sealed record Offer(ReleaseVersion Version, Release Release);

    /// <summary>
    /// The highest published version that has a build for the platform. Unsigned builds are skipped
    /// while a signing key is configured, since no client would install them anyway. Releases are
    /// ordered by SemVer precedence, not publish date, so withdrawing a bad build is what falls back.
    /// </summary>
    private static async Task<Offer?> FindLatestAsync(
        ApplicationDbContext db, ReleasePlatform platform, bool includePrerelease, bool signatureRequired)
    {
        var published = await db.Releases
            .Include(r => r.Artifacts)
            .Where(r => r.Status == ReleaseStatus.Published)
            .ToListAsync();

        return published
            .Where(r => r.Artifacts.Any(a => a.Platform == platform && (!signatureRequired || a.Signature is not null)))
            .Select(r => ReleaseVersion.TryParse(r.Version, out var v) ? new Offer(v, r) : null)
            .OfType<Offer>()
            .Where(o => includePrerelease || !o.Version.IsPreRelease)
            .OrderByDescending(o => o.Version)
            .FirstOrDefault();
    }

    // ---- Publisher ------------------------------------------------------------------------------

    private static async Task<IResult> GetReleaseAsync(string version, ClaimsPrincipal user, ApplicationDbContext db)
    {
        if (!await IsUsableAsync(user, db)) return Results.Unauthorized();

        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Version == version);
        return release is null ? Results.NotFound() : Results.Ok(ToResponse(release));
    }

    private static async Task<IResult> PutDraftAsync(
        string version, DraftReleaseRequest? body, ClaimsPrincipal user, ApplicationDbContext db, ReleasePublisher publisher)
    {
        if (!await IsUsableAsync(user, db)) return Results.Unauthorized();

        var result = await publisher.CreateOrUpdateDraftAsync(version, body?.Notes, body?.MinimumVersion);
        return result.Ok ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result.Error!.Value, result.Message!);
    }

    /// <summary>
    /// Body is the raw installer; <c>fileName</c> is a query parameter and the signature goes in the
    /// <c>X-Release-Signature</c> header. Streamed to disk, never buffered.
    /// </summary>
    private static async Task<IResult> UploadAsync(
        string version,
        ReleasePlatform platform,
        string? fileName,
        HttpContext http,
        ClaimsPrincipal user,
        ApplicationDbContext db,
        ReleasePublisher publisher,
        CancellationToken ct)
    {
        if (!await IsUsableAsync(user, db)) return Results.Unauthorized();

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxArtifactBytes;

        var signature = http.Request.Headers["X-Release-Signature"].ToString();
        var result = await publisher.UploadArtifactAsync(version, platform, fileName, http.Request.Body, signature, ct);
        if (!result.Ok) return ToProblem(result.Error!.Value, result.Message!);

        var artifact = result.Value!;
        return Results.Ok(new PublishedArtifactResponse(
            artifact.Platform.ToString(), artifact.FileName, artifact.SizeBytes, artifact.Sha256, artifact.Signature is not null));
    }

    private static async Task<IResult> SetStatusAsync(
        string version, ReleaseStatus status, ClaimsPrincipal user, ApplicationDbContext db, ReleasePublisher publisher)
    {
        if (!await IsUsableAsync(user, db)) return Results.Unauthorized();

        var result = await publisher.SetStatusAsync(version, status);
        return result.Ok ? Results.Ok(ToResponse(result.Value!)) : ToProblem(result.Error!.Value, result.Message!);
    }

    // ---- shared ---------------------------------------------------------------------------------

    private static async Task<bool> IsUsableAsync(ClaimsPrincipal user, ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        return AccountPolicy.IsUsable(account);
    }

    private static IResult ToProblem(PublishError error, string message) => error switch
    {
        PublishError.NotFound => Results.NotFound(new { error = message }),
        PublishError.Conflict => Results.Conflict(new { error = message }),
        _ => Results.BadRequest(new { error = message }),
    };

    private static PublishedReleaseResponse ToResponse(Release release) => new(
        release.Version, release.Status.ToString(), release.Notes, release.MinimumVersion, release.PublishedAt,
        release.Artifacts
            .OrderBy(a => a.Platform)
            .Select(a => new PublishedArtifactResponse(a.Platform.ToString(), a.FileName, a.SizeBytes, a.Sha256, a.Signature is not null))
            .ToList());
}
