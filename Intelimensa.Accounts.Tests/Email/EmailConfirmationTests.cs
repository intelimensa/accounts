using Intelimensa.Accounts.Email;
using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Tests.Email;

public class EmailConfirmationTests
{
    [Fact]
    public void LinkCarriesTheTokenIntact()
    {
        // Identity tokens contain '+', '/' and '=' characters that a naive query string would mangle.
        const string token = "CfDJ8+abc/def==xyz+Ü";
        var link = EmailConfirmation.BuildLink("https://accounts.example.com/", "user id/1", token);

        var uri = new Uri(link);
        Assert.Equal("https://accounts.example.com/Account/ConfirmEmail", uri.GetLeftPart(UriPartial.Path));

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("user id/1", query["userId"]);
        Assert.True(EmailConfirmation.TryDecodeToken(query["code"]!, out var decoded));
        Assert.Equal(token, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!!")]
    public void GarbageCodeIsRejected(string code) =>
        Assert.False(EmailConfirmation.TryDecodeToken(code, out _));

    [Fact]
    public void ResendIsLimitedPerUser()
    {
        var limiter = new ConfirmationResendLimiter();
        var t0 = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryBegin("a", t0));
        Assert.False(limiter.TryBegin("a", t0.AddSeconds(30)));
        Assert.True(limiter.TryBegin("b", t0.AddSeconds(30)));
        Assert.True(limiter.TryBegin("a", t0 + ConfirmationResendLimiter.Cooldown));
    }

    [Fact]
    public void SmtpSettingsMustBeCompleteOnceAHostIsSet()
    {
        var validator = new EmailOptionsValidator();

        Assert.True(validator.Validate(null, new EmailOptions()).Succeeded);
        Assert.True(validator.Validate(null, new EmailOptions
            { Host = "mail.example.com", User = "u", Password = "p", FromAddress = "no-reply@example.com" }).Succeeded);

        var result = validator.Validate(null, new EmailOptions { Host = "mail.example.com", User = "u" });
        Assert.True(result.Failed);
        Assert.Contains("Email:Password", result.FailureMessage);
        Assert.Contains("Email:FromAddress", result.FailureMessage);
    }
}
