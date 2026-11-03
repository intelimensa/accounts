using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Email;

/// <summary>Options for <see cref="PasswordResetTokenProvider"/>: a reset link is short-lived (1 hour), unlike the 3 days of an email confirmation.</summary>
public class PasswordResetTokenOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenOptions()
    {
        Name = ProviderName;
        TokenLifespan = TimeSpan.FromHours(1);
    }

    public const string ProviderName = "PasswordReset";
}

/// <summary>
/// The token provider behind "forgot password". Its own name gives it its own data-protection purpose, so a confirmation
/// or email-change token can never be used as a reset token (or the other way round).
/// </summary>
public class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtection,
    IOptions<PasswordResetTokenOptions> options,
    ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
    : DataProtectorTokenProvider<ApplicationUser>(dataProtection, options, logger);
