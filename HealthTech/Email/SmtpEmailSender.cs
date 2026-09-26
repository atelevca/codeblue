using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace HealthTech.Email;

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

public interface IEmailSender
{
    /// <summary>Returns only after the SMTP server has accepted the message; otherwise throws <see cref="EmailException"/>.</summary>
    Task SendAsync(IReadOnlyCollection<string> to, string subject, string textBody,
        EmailAttachment? attachment = null, CancellationToken ct = default);
}

/// <summary>
/// Plain SMTP to the local mail catcher. A new SmtpClient per call: MailKit's client is not
/// safe for concurrent sends. Logs recipients and outcome, never the body (it is the minutes).
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(IReadOnlyCollection<string> to, string subject, string textBody,
        EmailAttachment? attachment = null, CancellationToken ct = default)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        foreach (var address in to)
            message.To.Add(MailboxAddress.Parse(address));
        message.Subject = subject;
        var body = new BodyBuilder { TextBody = textBody };
        if (attachment != null)
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        message.Body = body.ToMessageBody();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        using var client = new SmtpClient { Timeout = o.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(o.Host, o.Port, SecureSocketOptions.None, timeout.Token);
            await client.SendAsync(message, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "E-mail to {Count} recipient(s) via {Host}:{Port} failed", to.Count, o.Host, o.Port);
            throw new EmailException(
                $"Serverul local de e-mail ({o.Host}:{o.Port}) nu este disponibil sau a refuzat mesajul. " +
                "Porniți Mailpit și încercați din nou.", ex);
        }

        logger.LogInformation("E-mail {MessageId} accepted by {Host}:{Port} for {Recipients}",
            message.MessageId, o.Host, o.Port, string.Join(", ", to));

        // The message is already accepted: a failed QUIT must not turn into an error (and a resend).
        try
        {
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP disconnect failed after e-mail {MessageId} was accepted", message.MessageId);
        }
    }
}
