using System.Text;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Email;

/// <summary>
/// The emails of the password-reset and email-change flows. Links are built like the confirmation link
/// (<see cref="EmailConfirmation.BuildLink"/>) and decoded with <see cref="EmailConfirmation.TryDecodeToken"/>.
/// </summary>
public class AccountMail(
    UserManager<ApplicationUser> userManager,
    IMailSender mail,
    IOptions<AppOptions> app,
    ConfirmationResendLimiter limiter)
{
    /// <summary>True if a reset email may go to this user now (and starts the cooldown).</summary>
    public bool TryBeginReset(string userId, DateTimeOffset now) => limiter.TryBegin("reset:" + userId, now);

    /// <summary>True if an email-change email may go to this user now (and starts the cooldown).</summary>
    public bool TryBeginEmailChange(string userId, DateTimeOffset now) => limiter.TryBegin("change:" + userId, now);

    public async Task SendPasswordResetAsync(ApplicationUser user, string requestBaseUrl)
    {
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var link = LinkFor(requestBaseUrl, "/Account/ResetPassword", ("userId", user.Id), ("code", Encode(token)));

        await mail.SendAsync(
            user.Email!,
            "Reset your Intelimensa account password",
            "Hello,\n\n" +
            "Someone asked to reset the password of your Intelimensa account. To choose a new one, open this link " +
            "within the next hour:\n\n" +
            $"{link}\n\n" +
            "If you didn't ask for this, ignore this message: your password has not changed.\n\n" +
            "Intelimensa\n");
    }

    /// <summary>Sends the confirmation link to the <em>new</em> address; nothing changes until it is opened.</summary>
    public async Task SendEmailChangeAsync(ApplicationUser user, string newEmail, string requestBaseUrl)
    {
        var token = await userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        var link = LinkFor(requestBaseUrl, "/Account/ConfirmEmailChange",
            ("userId", user.Id), ("email", newEmail), ("code", Encode(token)));

        await mail.SendAsync(
            newEmail,
            "Confirm your new Intelimensa account email",
            "Hello,\n\n" +
            $"Your Intelimensa account ({user.Email}) was asked to switch to this address. To confirm the change, " +
            "open this link:\n\n" +
            $"{link}\n\n" +
            "Afterwards you sign in with this address. If you didn't ask for this, ignore this message.\n\n" +
            "Intelimensa\n");
    }

    /// <summary>Tells the previous address that the sign-in address changed, so a hijacked account is noticed.</summary>
    public Task NotifyEmailChangedAsync(string oldEmail, string newEmail) =>
        mail.SendAsync(
            oldEmail,
            "Your Intelimensa account email was changed",
            "Hello,\n\n" +
            $"The email address of your Intelimensa account was changed to {newEmail}. " +
            "Sign-in now uses the new address.\n\n" +
            "If this wasn't you, contact Intelimensa right away.\n\n" +
            "Intelimensa\n");

    /// <summary>Tells the user that the password changed (by reset or from My Account).</summary>
    public Task NotifyPasswordChangedAsync(string email) =>
        mail.SendAsync(
            email,
            "Your Intelimensa account password was changed",
            "Hello,\n\n" +
            "The password of your Intelimensa account was just changed, and AxoSync installs were signed out.\n\n" +
            "If this wasn't you, contact Intelimensa right away.\n\n" +
            "Intelimensa\n");

    private string LinkFor(string requestBaseUrl, string path, params (string Key, string Value)[] query) =>
        BuildLink(string.IsNullOrWhiteSpace(app.Value.PublicBaseUrl) ? requestBaseUrl : app.Value.PublicBaseUrl, path, query);

    public static string BuildLink(string baseUrl, string path, params (string Key, string Value)[] query) =>
        $"{baseUrl.TrimEnd('/')}{path}?" + string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
}
