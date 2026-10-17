using System.Collections.Concurrent;
using System.Text;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Email;

/// <summary>Builds the confirmation link, sends it, and checks it when it comes back.</summary>
public class EmailConfirmation(
    UserManager<ApplicationUser> userManager,
    IMailSender mail,
    IOptions<AppOptions> app,
    ConfirmationResendLimiter limiter)
{
    /// <param name="requestBaseUrl">Origin of the current request, used only when <c>App:PublicBaseUrl</c> is unset.</param>
    public async Task SendAsync(ApplicationUser user, string requestBaseUrl)
    {
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var baseUrl = string.IsNullOrWhiteSpace(app.Value.PublicBaseUrl) ? requestBaseUrl : app.Value.PublicBaseUrl;
        var link = BuildLink(baseUrl, user.Id, token);

        await mail.SendAsync(
            user.Email!,
            "Confirm your Intelimensa account email",
            "Hello,\n\n" +
            "Confirm your email address to continue setting up your Intelimensa account:\n\n" +
            $"{link}\n\n" +
            "After that, our team reviews new accounts before AxoSync access is switched on. " +
            "If you didn't sign up, ignore this message.\n\n" +
            "Intelimensa\n");
    }

    public async Task<IdentityResult> ConfirmAsync(string userId, string code)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null || !TryDecodeToken(code, out var token))
            return IdentityResult.Failed(new IdentityError { Description = "Invalid confirmation link." });
        return await userManager.ConfirmEmailAsync(user, token);
    }

    /// <summary>True if a resend is allowed now (and starts the cooldown).</summary>
    public bool TryBeginResend(string userId, DateTimeOffset now) => limiter.TryBegin(userId, now);

    public static string BuildLink(string baseUrl, string userId, string token) =>
        $"{baseUrl.TrimEnd('/')}/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId)}" +
        $"&code={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))}";

    public static bool TryDecodeToken(string code, out string token)
    {
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            return token.Length > 0;
        }
        catch (FormatException)
        {
            token = "";
            return false;
        }
    }
}

/// <summary>One confirmation email per user per cooldown, so "resend" can't be used to spam an address. In-memory, like the other limiters.</summary>
public class ConfirmationResendLimiter
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();

    public bool TryBegin(string userId, DateTimeOffset now)
    {
        var allowed = false;
        _last.AddOrUpdate(
            userId,
            _ => { allowed = true; return now; },
            (_, previous) =>
            {
                allowed = now - previous >= Cooldown;
                return allowed ? now : previous;
            });

        if (_last.Count > 1000)
            foreach (var (key, at) in _last)
                if (now - at >= Cooldown)
                    _last.TryRemove(key, out _);

        return allowed;
    }
}
