using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Intelimensa.Accounts.Email;

public class SmtpMailSender(IOptions<EmailOptions> options) : IMailSender
{
    public async Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken = default)
    {
        var o = options.Value;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = textBody };

        using var client = new SmtpClient { Timeout = 20_000 };
        await client.ConnectAsync(
            o.Host, o.Port,
            o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
            cancellationToken);
        await client.AuthenticateAsync(o.User, o.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
