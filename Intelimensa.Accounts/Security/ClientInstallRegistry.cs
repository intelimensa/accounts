using Intelimensa.Accounts.Api.Auth;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Security;

public enum InstallRegistrationError { Invalid, LimitReached }

public record InstallRegistration(ClientInstall? Install, bool IsNew, InstallRegistrationError? Error);

/// <summary>
/// The rules for client installs (docs/config-management.md, section 1): created only at a password login,
/// reused when the same signing key signs in again, limited per account (<c>Configs:MaxInstallsPerAccount</c>,
/// overridable per account), revoked after <c>Configs:InstallIdleDays</c> without use, and announced to the
/// participant by email.
/// </summary>
public class ClientInstallRegistry(
    ApplicationDbContext db,
    IOptions<ConfigDeliveryOptions> options,
    IMailSender mail,
    ILogger<ClientInstallRegistry> logger)
{
    // LastSeenAt is a "still in use" signal, not a log: writing it on every request would cost a write per call.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(10);

    public TimeSpan IdleWindow => TimeSpan.FromDays(options.Value.InstallIdleDays);

    public int LimitFor(Account account) => account.MaxInstallsOverride ?? options.Value.MaxInstallsPerAccount;

    public async Task<InstallRegistration> RegisterAsync(Account account, string email, ClientInstallRequest request, DateTimeOffset now)
    {
        if (!P256.IsPublicKey(request.SigningKey) || !P256.IsPublicKey(request.AgreementKey) ||
            request.SigningKey == request.AgreementKey ||
            !Enum.TryParse<ClientPlatform>(request.Platform, ignoreCase: false, out var platform) || !Enum.IsDefined(platform) ||
            request.Name is { Length: > 100 } || request.AppVersion is { Length: > 50 })
            return new InstallRegistration(null, false, InstallRegistrationError.Invalid);

        var thumbprint = P256.Thumbprint(request.SigningKey);
        var installs = await db.ClientInstalls.Where(i => i.AccountId == account.Id).ToListAsync();
        await RevokeIdleAsync(installs, now);

        // The same install signing in again (after signing out, or a lost refresh token): no new slot.
        var existing = installs.FirstOrDefault(i => i.KeyThumbprint == thumbprint && i.RevokedAt is null);
        if (existing is not null)
        {
            if (existing.AgreementKey != request.AgreementKey)
                return new InstallRegistration(null, false, InstallRegistrationError.Invalid);
            existing.Name = Clean(request.Name) ?? existing.Name;
            existing.AppVersion = Clean(request.AppVersion) ?? existing.AppVersion;
            existing.Platform = platform;
            existing.Slot = request.Slot;
            existing.LastSeenAt = now;
            await db.SaveChangesAsync();
            return new InstallRegistration(existing, false, null);
        }

        // A revoked install's key stays revoked: signing in again with it would undo a removal.
        if (installs.Any(i => i.KeyThumbprint == thumbprint))
            return new InstallRegistration(null, false, InstallRegistrationError.Invalid);

        var limit = LimitFor(account);
        if (installs.Count(i => i.IsActive(now, IdleWindow)) >= limit)
        {
            logger.LogInformation("Account {AccountId} is at its install limit ({Limit}); refused a new install", account.Id, limit);
            return new InstallRegistration(null, false, InstallRegistrationError.LimitReached);
        }

        var install = new ClientInstall
        {
            AccountId = account.Id,
            SigningKey = request.SigningKey,
            KeyThumbprint = thumbprint,
            AgreementKey = request.AgreementKey,
            Platform = platform,
            Name = Clean(request.Name),
            AppVersion = Clean(request.AppVersion),
            Slot = request.Slot,
            RegisteredAt = now,
            LastSeenAt = now,
        };
        db.ClientInstalls.Add(install);
        await db.SaveChangesAsync();

        var recent = installs.Count(i => now - i.RegisteredAt < TimeSpan.FromDays(1)) + 1;
        if (recent >= options.Value.InstallBurstWarning)
            logger.LogWarning("Account {AccountId} registered {Count} AxoSync installs within 24 hours: check for a scripted client",
                account.Id, recent);
        logger.LogInformation("Registered AxoSync install {InstallId} ({Platform}) for account {AccountId}", install.Id, platform, account.Id);

        await NotifyAsync(email, install);
        return new InstallRegistration(install, true, null);
    }

    /// <summary>
    /// The install a bound token belongs to, if it may still be used; records that it was seen. An install
    /// past the idle window is revoked here, so it fails from then on.
    /// </summary>
    public async Task<ClientInstall?> FindUsableAsync(Guid installId, DateTimeOffset now)
    {
        var install = await db.ClientInstalls.FirstOrDefaultAsync(i => i.Id == installId);
        if (install is null || install.RevokedAt is not null) return null;
        if (!install.IsActive(now, IdleWindow))
        {
            await RevokeAsync(install, now);
            return null;
        }

        if (install.LastSeenAt is null || now - install.LastSeenAt > TouchInterval)
        {
            install.LastSeenAt = now;
            await db.SaveChangesAsync();
        }
        return install;
    }

    /// <summary>Revokes an install and every refresh token bound to it.</summary>
    public async Task RevokeAsync(ClientInstall install, DateTimeOffset now)
    {
        install.RevokedAt ??= now;
        var tokens = await db.RefreshTokens.Where(t => t.ClientInstallId == install.Id && t.RevokedAt == null).ToListAsync();
        foreach (var token in tokens) token.RevokedAt = now;
        await db.SaveChangesAsync();
    }

    private async Task RevokeIdleAsync(List<ClientInstall> installs, DateTimeOffset now)
    {
        foreach (var install in installs.Where(i => i.RevokedAt is null && !i.IsActive(now, IdleWindow)))
            await RevokeAsync(install, now);
    }

    private async Task NotifyAsync(string email, ClientInstall install)
    {
        var name = install.Name is null ? install.Platform.ToString() : $"{install.Name} ({install.Platform})";
        try
        {
            await mail.SendAsync(email, "AxoSync was signed in on a new computer",
                $"""
                Your Intelimensa account was just used to sign in to AxoSync on a computer or device it hasn't been used on before:

                    {name}, {install.RegisteredAt:yyyy-MM-dd HH:mm} UTC

                If that was you, there is nothing to do. If it wasn't, change your password and remove the installation
                on your account page ("My Account", under "AxoSync installations").
                """);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not send the new-install notice for install {InstallId}", install.Id);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
