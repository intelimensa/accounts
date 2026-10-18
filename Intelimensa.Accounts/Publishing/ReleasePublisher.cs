using System.Text.RegularExpressions;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Publishing;

public class ReleasePublishingOptions
{
    /// <summary>
    /// Base64 SubjectPublicKeyInfo of an ECDSA P-256 key releases are signed with. When any key is
    /// configured, every uploaded artifact must carry a valid signature from one of them and a
    /// release can't be published while any artifact lacks one. None = signatures are stored if given
    /// but not required (development). Several keys may be given here separated by <c>;</c>, the form
    /// AxoSync's <c>UpdateSigningKeys.txt</c> uses; <see cref="SigningPublicKeys"/> is the list form.
    /// </summary>
    public string? SigningPublicKey { get; set; }

    /// <summary>
    /// More than one key, so a lost or retired signing key can be covered by a second one (set as
    /// <c>Releases__SigningPublicKeys__0</c>, <c>__1</c>, ... in the environment). A signature from
    /// any of them is accepted; they must match the keys compiled into AxoSync.
    /// </summary>
    public string[]? SigningPublicKeys { get; set; }

    /// <summary>Every configured key, from both settings, without blanks or duplicates.</summary>
    public IReadOnlyList<string> AllSigningPublicKeys =>
        (SigningPublicKey?.Split(';') ?? [])
            .Concat(SigningPublicKeys ?? [])
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();
}

public enum PublishError { NotFound, Invalid, Conflict }

public record PublishResult<T>(T? Value, PublishError? Error, string? Message)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The release-publishing rules, shared by the staff page (<c>/Staff/Releases</c>) and the
/// publisher API so both enforce the same things: version format, drafts-only uploads, signature
/// checks, and "can't publish an empty or unsigned release".
/// </summary>
public partial class ReleasePublisher(
    ApplicationDbContext db,
    IReleaseStorage storage,
    IOptions<ReleasePublishingOptions> options,
    ILogger<ReleasePublisher> logger)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._ -]*$")]
    private static partial Regex FileNamePattern();

    public bool SignatureRequired => options.Value.AllSigningPublicKeys.Count > 0;

    /// <summary>
    /// Creates the draft, or updates the notes / minimum version of an existing draft (so a script
    /// can be re-run). A release that is no longer a draft is never changed.
    /// </summary>
    public async Task<PublishResult<Release>> CreateOrUpdateDraftAsync(string version, string? notes, string? minimumVersion)
    {
        version = version.Trim();
        if (!ReleaseVersion.TryParse(version, out var parsed))
            return Fail<Release>(PublishError.Invalid, "Version must look like 1.3.0 or 1.4.0-beta.1.");

        minimumVersion = string.IsNullOrWhiteSpace(minimumVersion) ? null : minimumVersion.Trim();
        if (minimumVersion is not null)
        {
            if (!ReleaseVersion.TryParse(minimumVersion, out var min))
                return Fail<Release>(PublishError.Invalid, "Minimum version must look like 1.3.0 or 1.4.0-beta.1.");
            if (min > parsed)
                return Fail<Release>(PublishError.Invalid, "Minimum version can't be higher than the release's own version.");
        }

        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (notes is { Length: > 4000 })
            return Fail<Release>(PublishError.Invalid, "Notes are limited to 4000 characters.");

        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Version == version);
        if (release is null)
        {
            release = new Release { Version = version, Notes = notes, MinimumVersion = minimumVersion };
            db.Releases.Add(release);
        }
        else if (release.Status != ReleaseStatus.Draft)
        {
            return Fail<Release>(PublishError.Conflict, $"Release {version} already exists and is {release.Status}.");
        }
        else
        {
            release.Notes = notes;
            release.MinimumVersion = minimumVersion;
        }

        await db.SaveChangesAsync();
        return Ok(release);
    }

    /// <summary>Stores one platform's installer on a draft, replacing a previous upload for that platform.</summary>
    public async Task<PublishResult<ReleaseArtifact>> UploadArtifactAsync(
        string version, ReleasePlatform platform, string? fileName, Stream content, string? signature, CancellationToken ct)
    {
        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Version == version, ct);
        if (release is null)
            return Fail<ReleaseArtifact>(PublishError.NotFound, $"No release {version}.");
        if (release.Status != ReleaseStatus.Draft)
            return Fail<ReleaseArtifact>(PublishError.Conflict,
                $"Release {version} is {release.Status}; builds can only be uploaded to a draft.");
        if (!Enum.IsDefined(platform))
            return Fail<ReleaseArtifact>(PublishError.Invalid, "Unknown platform.");

        // The name becomes both a storage path segment and the browser's saved filename.
        fileName = fileName is null ? null : Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(fileName) || !FileNamePattern().IsMatch(fileName) || fileName.Length > 200 || fileName.Contains(".."))
            return Fail<ReleaseArtifact>(PublishError.Invalid,
                "File name may only contain letters, digits, spaces, '.', '_' and '-'.");

        signature = string.IsNullOrWhiteSpace(signature) ? null : signature.Trim();
        if (signature is null && SignatureRequired)
            return Fail<ReleaseArtifact>(PublishError.Invalid, "A signature is required.");

        var stored = await storage.SaveAsync(release.Version, platform, fileName, content, ct);
        if (stored.SizeBytes == 0)
        {
            storage.Delete(stored.StorageKey);
            return Fail<ReleaseArtifact>(PublishError.Invalid, "The file is empty.");
        }

        // One artifact per (release, platform): uploading again replaces the previous one.
        var existing = release.Artifacts.FirstOrDefault(a => a.Platform == platform);

        if (signature is not null && SignatureRequired &&
            !ReleaseSigning.VerifyAny(options.Value.AllSigningPublicKeys,
                ReleaseSigning.Statement(release.Version, platform, stored.Sha256, stored.SizeBytes), signature))
        {
            // The new file may have overwritten the old one at the same key; drop both rather than
            // leave a row whose hash no longer matches its file.
            storage.Delete(stored.StorageKey);
            if (existing is not null)
            {
                db.ReleaseArtifacts.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            return Fail<ReleaseArtifact>(PublishError.Invalid,
                "The signature doesn't match this file, version and platform for any configured public key.");
        }

        if (existing is not null)
        {
            if (existing.StorageKey != stored.StorageKey) storage.Delete(existing.StorageKey);
            existing.FileName = fileName;
            existing.SizeBytes = stored.SizeBytes;
            existing.Sha256 = stored.Sha256;
            existing.StorageKey = stored.StorageKey;
            existing.Signature = signature;
            existing.UploadedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            existing = new ReleaseArtifact
            {
                ReleaseId = release.Id,
                Platform = platform,
                FileName = fileName,
                SizeBytes = stored.SizeBytes,
                Sha256 = stored.Sha256,
                StorageKey = stored.StorageKey,
                Signature = signature,
            };
            db.ReleaseArtifacts.Add(existing);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Uploaded {File} for release {Version} ({Platform}, {Size} bytes, sha256 {Sha256}, signed: {Signed})",
            fileName, release.Version, platform, stored.SizeBytes, stored.Sha256, signature is not null);
        return Ok(existing);
    }

    public async Task<PublishResult<ReleaseArtifact>> RemoveArtifactAsync(int artifactId)
    {
        var artifact = await db.ReleaseArtifacts.Include(a => a.Release).FirstOrDefaultAsync(a => a.Id == artifactId);
        if (artifact is null)
            return Fail<ReleaseArtifact>(PublishError.NotFound, "No such artifact.");

        // Only drafts: removing a file from a published/withdrawn release would silently break
        // links and the audit trail of what participants could download.
        if (artifact.Release!.Status != ReleaseStatus.Draft)
            return Fail<ReleaseArtifact>(PublishError.Conflict, "Artifacts can only be removed from draft releases.");

        storage.Delete(artifact.StorageKey);
        db.ReleaseArtifacts.Remove(artifact);
        await db.SaveChangesAsync();
        return Ok(artifact);
    }

    public async Task<PublishResult<Release>> SetStatusAsync(string version, ReleaseStatus status)
    {
        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Version == version);
        if (release is null)
            return Fail<Release>(PublishError.NotFound, $"No release {version}.");
        return await SetStatusAsync(release, status);
    }

    public async Task<PublishResult<Release>> SetStatusAsync(int releaseId, ReleaseStatus status)
    {
        var release = await db.Releases.Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Id == releaseId);
        if (release is null)
            return Fail<Release>(PublishError.NotFound, "No such release.");
        return await SetStatusAsync(release, status);
    }

    private async Task<PublishResult<Release>> SetStatusAsync(Release release, ReleaseStatus status)
    {
        if (!Enum.IsDefined(status))
            return Fail<Release>(PublishError.Invalid, "Unknown status.");
        if (status == ReleaseStatus.Draft && release.PublishedAt is not null)
            return Fail<Release>(PublishError.Conflict, "A release that has been published can be withdrawn, not returned to draft.");
        if (status == ReleaseStatus.Published)
        {
            if (release.Artifacts.Count == 0)
                return Fail<Release>(PublishError.Invalid, "Upload at least one artifact before publishing.");
            if (SignatureRequired && release.Artifacts.Any(a => a.Signature is null))
                return Fail<Release>(PublishError.Invalid,
                    "Every artifact must be signed before publishing (a signing key is configured).");
        }

        release.Status = status;
        if (status == ReleaseStatus.Published) release.PublishedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Release {Version} is now {Status}", release.Version, status);
        return Ok(release);
    }

    private static PublishResult<T> Ok<T>(T value) => new(value, null, null);

    private static PublishResult<T> Fail<T>(PublishError error, string message) => new(default, error, message);
}
