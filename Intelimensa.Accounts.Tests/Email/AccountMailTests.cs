using Intelimensa.Accounts.Email;

namespace Intelimensa.Accounts.Tests.Email;

public class AccountMailTests
{
    [Fact]
    public void LinkEscapesEveryValueAndKeepsTheTokenIntact()
    {
        const string token = "CfDJ8+abc/def==xyz";
        var code = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));
        var link = AccountMail.BuildLink("https://accounts.example.com/", "/Account/ConfirmEmailChange",
            ("userId", "u/1"), ("email", "a+b@example.com"), ("code", code));

        var uri = new Uri(link);
        Assert.Equal("https://accounts.example.com/Account/ConfirmEmailChange", uri.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("u/1", query["userId"]);
        Assert.Equal("a+b@example.com", query["email"]);
        Assert.True(EmailConfirmation.TryDecodeToken(query["code"]!, out var decoded));
        Assert.Equal(token, decoded);
    }

    [Fact]
    public void ResetLinksLastAnHourAndHaveTheirOwnProviderName()
    {
        var options = new PasswordResetTokenOptions();
        Assert.Equal(TimeSpan.FromHours(1), options.TokenLifespan);
        Assert.Equal(PasswordResetTokenOptions.ProviderName, options.Name);
    }

    [Fact]
    public void ResetAndEmailChangeCooldownsAreIndependentOfConfirmation()
    {
        var limiter = new ConfirmationResendLimiter();
        var mail = new AccountMail(null!, null!, null!, limiter);
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(mail.TryBeginReset("u", t0));
        Assert.False(mail.TryBeginReset("u", t0.AddSeconds(10)));
        Assert.True(mail.TryBeginEmailChange("u", t0.AddSeconds(10)));
        Assert.True(limiter.TryBegin("u", t0.AddSeconds(10)));
    }
}
