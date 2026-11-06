using LiteDB;

namespace InvoiceWebApp.Models;

public enum AlertType
{
    CertificateExpiration = 1,
    ClientSecretExpiration = 2
}

public class AlertRule
{
    [BsonId]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public AlertType Type { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Days before expiration that should trigger (0 = today).</summary>
    public int DaysBeforeExpiration { get; set; }

    /// <summary>Optional SMTP profile id used for this rule. If null, use default profile.</summary>
    public int? SmtpProfileId { get; set; }

    /// <summary>Email template id to use for this rule.</summary>
    public int? EmailTemplateId { get; set; }

    public string? To { get; set; }

    public string? Cc { get; set; }
}
