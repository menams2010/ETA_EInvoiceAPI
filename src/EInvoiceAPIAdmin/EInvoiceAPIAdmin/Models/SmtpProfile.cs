using LiteDB;

namespace InvoiceWebApp.Models;

public class SmtpProfile
{
    [BsonId]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? FromEmail { get; set; }

    public string? FromName { get; set; }
}
