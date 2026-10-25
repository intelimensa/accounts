using System.Security.Cryptography;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.ConfigDelivery;

public record IssuedConfig(Config Config, WrappedCek WrappedCek, LeaseTerms Lease, string LeaseSignature);

public record IssueResult(IssuedConfig? Issued, int Status, string? Error)
{
    public static IssueResult Fail(int status, string error) => new(null, status, error);
}

/// <summary>
/// Delivers a unit's config key to one install, for one AxoSync build (docs/config-management.md, section 4):
/// checks the pairing, the assignment and the build, wraps the CEK to the install's agreement key and the build's
/// release secret (<see cref="CekWrap"/>), signs an offline lease and records the issuance. Error codes go to the
/// client as <c>{"error": code}</c>.
/// </summary>
public class ConfigIssuer(
    ApplicationDbContext db,
    KeyEncryption keys,
    IOptions<ConfigDeliveryOptions> options,
    ILogger<ConfigIssuer> logger)
{
    public async Task<IssueResult> IssueAsync(
        Account account, ClientInstall install, string? serial, string? version, string? platform, DateTimeOffset now)
    {
        if (!keys.IsAvailable)
            return IssueResult.Fail(StatusCodes.Status503ServiceUnavailable, "config_delivery_unavailable");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(platform))
            return IssueResult.Fail(StatusCodes.Status400BadRequest, "version_and_platform_required");

        var secret = await FindReleaseSecretAsync(version.Trim(), platform.Trim());
        if (secret is null)
            return IssueResult.Fail(StatusCodes.Status403Forbidden, "build_not_supported");

        try
        {
            // Unknown serial and "not this account's unit" are one answer, so this can't probe which serials exist.
            var bciDevice = await db.BciDevices.FindBySerialAsync(serial);
            var pairing = bciDevice is null
                ? null
                : await db.AccountDevices
                    .Include(ad => ad.AssignedConfig)
                    .FirstOrDefaultAsync(ad => ad.AccountId == account.Id && ad.BciDeviceId == bciDevice.Id && ad.UnassignedAt == null);
            if (pairing is null)
                return IssueResult.Fail(StatusCodes.Status404NotFound, "not_registered");

            var config = pairing.AssignedConfig;
            if (config is null || !config.IsDeliverable)
                return IssueResult.Fail(StatusCodes.Status404NotFound, "not_assigned");

            var cek = keys.Unwrap(KeyEncryption.ConfigCekPurpose(config.Key), config.WrappedCek!);
            WrappedCek wrapped;
            try
            {
                wrapped = CekWrap.Wrap(install.AgreementKey, secret, install.Id, config.Key, config.BlobSha256!, cek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cek);
            }

            var o = options.Value;
            var days = Math.Clamp(account.OfflineLeaseDaysOverride ?? o.OfflineLeaseDays, 0, o.MaxOfflineLeaseDays);
            var issuedAt = OfflineLease.ToSeconds(now);
            var notAfter = issuedAt.AddDays(days);
            if (account.ExpiresAt is { } expires && expires < notAfter)
                notAfter = OfflineLease.ToSeconds(expires);

            var lease = new LeaseTerms(account.Id, install.Id, bciDevice!.SerialNumber, config.Key, config.BlobSha256!, issuedAt, notAfter);
            var signature = OfflineLease.Sign(keys.LeaseKey, lease);

            db.ConfigKeyIssuances.Add(new ConfigKeyIssuance
            {
                AccountId = account.Id,
                ClientInstallId = install.Id,
                AccountDeviceId = pairing.Id,
                ConfigId = config.Id,
                BlobSha256 = config.BlobSha256!,
                AppVersion = version.Trim(),
                AppPlatform = platform.Trim(),
                IssuedAt = issuedAt,
                LeaseNotAfter = notAfter,
            });
            await db.SaveChangesAsync();

            logger.LogInformation("Issued config {Key} ({Sha256}) to install {InstallId} of account {AccountId} for {Version}/{Platform}, lease until {NotAfter}",
                config.Key, config.BlobSha256, install.Id, account.Id, version, platform, OfflineLease.FormatTime(notAfter));
            return new IssueResult(new IssuedConfig(config, wrapped, lease, signature), StatusCodes.Status200OK, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// The secret of a published build that may receive configs, or of Debug builds on a development server
    /// (<see cref="ConfigDeliveryOptions.DevelopmentReleaseSecret"/>); null otherwise.
    /// </summary>
    private async Task<byte[]?> FindReleaseSecretAsync(string version, string platform)
    {
        if (version == ConfigDeliveryOptions.DevelopmentVersion)
            return options.Value.DevelopmentReleaseSecret is { Length: > 0 } dev ? Convert.FromBase64String(dev) : null;

        if (!Enum.TryParse<ReleasePlatform>(platform, ignoreCase: false, out var releasePlatform) || !Enum.IsDefined(releasePlatform))
            return null;

        var artifact = await db.ReleaseArtifacts
            .Include(a => a.Release)
            .FirstOrDefaultAsync(a => a.Release!.Version == version && a.Release.Status == ReleaseStatus.Published &&
                                      a.Platform == releasePlatform);
        if (artifact is not { CanReceiveConfigs: true }) return null;

        return keys.Unwrap(KeyEncryption.ReleaseSecretPurpose(version, platform), artifact.WrappedReleaseSecret!);
    }
}
