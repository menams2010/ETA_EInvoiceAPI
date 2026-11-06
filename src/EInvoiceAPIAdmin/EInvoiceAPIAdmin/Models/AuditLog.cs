using LiteDB;

namespace InvoiceWebApp.Models;

public class AuditLog
{
    [BsonId]
    public int Id { get; set; }

    /// <summary>UTC timestamp when the action occurred.</summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Username who performed the action.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Action type (Create, Update, Delete, View, etc.).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Entity type (e.g., CompEnv, Company, SmtpProfile).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Entity identifier.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Optional description of what changed.</summary>
    public string? Description { get; set; }

    /// <summary>Optional JSON snapshot of old values (for updates).</summary>
    public string? OldValues { get; set; }

    /// <summary>Optional JSON snapshot of new values (for creates/updates).</summary>
    public string? NewValues { get; set; }

    /// <summary>IP address of the user (if available).</summary>
    public string? IpAddress { get; set; }
}
