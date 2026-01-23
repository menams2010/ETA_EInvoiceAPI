using System.Net;
using System.Net.Mail;
using InvoiceWebApp.Models;

namespace InvoiceWebApp.Services;

public class SmtpEmailSender
{
    public async Task SendAsync(SmtpProfile profile, IEnumerable<string> to, IEnumerable<string> cc, string subject, string body, bool isHtml, CancellationToken ct)
    {
        using var msg = new MailMessage();

        foreach (var addr in to.Where(x => !string.IsNullOrWhiteSpace(x)))
            msg.To.Add(addr.Trim());

        foreach (var addr in cc.Where(x => !string.IsNullOrWhiteSpace(x)))
            msg.CC.Add(addr.Trim());

        msg.Subject = subject;
        msg.Body = body;
        msg.IsBodyHtml = isHtml;

        if (!string.IsNullOrWhiteSpace(profile.FromEmail))
        {
            msg.From = new MailAddress(profile.FromEmail, profile.FromName);
        }

        using var client = new SmtpClient(profile.Host, profile.Port)
        {
            EnableSsl = profile.EnableSsl,
        };

        if (!string.IsNullOrWhiteSpace(profile.Username))
            client.Credentials = new NetworkCredential(profile.Username, profile.Password);
        else
            client.UseDefaultCredentials = true;

        ct.ThrowIfCancellationRequested();
        await client.SendMailAsync(msg, ct);
    }
}
