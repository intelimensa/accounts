namespace Intelimensa.Accounts.Email;

public interface IMailSender
{
    /// <summary>Sends a plain-text message. Throws if the message could not be handed to the mail server.</summary>
    Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken = default);
}
