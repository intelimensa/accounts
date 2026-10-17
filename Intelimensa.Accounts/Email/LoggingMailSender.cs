namespace Intelimensa.Accounts.Email;

/// <summary>Stand-in when no SMTP server is configured: the message, links included, goes to the log.</summary>
public class LoggingMailSender(ILogger<LoggingMailSender> log) : IMailSender
{
    public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken = default)
    {
        log.LogWarning("Email is not configured; not sending. To: {To}\nSubject: {Subject}\n{Body}", to, subject, textBody);
        return Task.CompletedTask;
    }
}
