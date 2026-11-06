using InvoiceWebApp.Models;

namespace InvoiceWebApp.Services;

public sealed class EmailSender(SmtpEmailSender smtp, GraphMailService graph)
{
    public Task SendAsync(SmtpProfile profile, IEnumerable<string> to, IEnumerable<string> cc,
        string subject, string body, bool isHtml, CancellationToken ct) => profile.DeliveryProvider switch
    {
        SmtpProfile.ProviderSmtp => smtp.SendAsync(profile, to, cc, subject, body, isHtml, ct),
        SmtpProfile.ProviderGraph => graph.SendAsync(profile, to, cc, subject, body, isHtml, ct),
        _ => throw new NotSupportedException("Unsupported email delivery provider. Select SMTP or Microsoft Graph.")
    };
}
