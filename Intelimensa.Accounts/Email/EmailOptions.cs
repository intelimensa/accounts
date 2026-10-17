namespace Intelimensa.Accounts.Email;

/// <summary>
/// Outgoing mail (<c>Email</c> section). With no <see cref="Host"/> nothing is sent: messages are
/// written to the log instead (<see cref="LoggingMailSender"/>), which is what development and the
/// test server use. The password is a secret: user-secrets in development, an environment variable
/// (<c>Email__Password</c>) in production.
/// </summary>
public class EmailOptions
{
    public string Host { get; set; } = "";

    /// <summary>587 = STARTTLS (the usual submission port); 465 = TLS from the first byte.</summary>
    public int Port { get; set; } = 587;

    public string User { get; set; } = "";

    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "Intelimensa";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Where the app is reachable from outside; used for links in emails (<c>App</c> section).</summary>
public class AppOptions
{
    /// <summary>
    /// e.g. <c>https://accounts.intelimensa.com</c>. Behind the reverse proxy the request itself says
    /// <c>http</c>, so production sets this; when empty, links use the current request's origin.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";
}

public class EmailOptionsValidator : Microsoft.Extensions.Options.IValidateOptions<EmailOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, EmailOptions o)
    {
        if (!o.IsConfigured) return Microsoft.Extensions.Options.ValidateOptionsResult.Success;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(o.User)) missing.Add("Email:User");
        if (string.IsNullOrWhiteSpace(o.Password)) missing.Add("Email:Password");
        if (string.IsNullOrWhiteSpace(o.FromAddress)) missing.Add("Email:FromAddress");
        return missing.Count == 0
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Success
            : Microsoft.Extensions.Options.ValidateOptionsResult.Fail(
                $"Email:Host is set but {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} missing.");
    }
}
