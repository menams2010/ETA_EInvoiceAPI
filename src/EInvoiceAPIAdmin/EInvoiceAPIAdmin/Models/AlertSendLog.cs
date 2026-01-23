using LiteDB;

namespace InvoiceWebApp.Models;

public class AlertSendLog
{
    [BsonId]
    public int Id { get; set; }

    public int RuleId { get; set; }

    public AlertType Type { get; set; }

    /// <summary>
    /// A stable entity key, e.g. "CompEnv:{id}" or "Cert:{thumbprint}".
    /// </summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>
    /// UTC date (yyyy-MM-dd) the alert was sent.
    /// </summary>
    public string SentDate { get; set; } = string.Empty;

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}
