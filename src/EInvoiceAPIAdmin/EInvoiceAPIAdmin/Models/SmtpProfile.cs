using LiteDB;

namespace InvoiceWebApp.Models;

public class SmtpProfile
{
    public const string ProviderSmtp = "Smtp";
    public const string ProviderGraph = "MicrosoftGraph";
    public const string EncryptionNone = "None";
    public const string EncryptionStartTls = "StartTls";
    public const string EncryptionSslTls = "SslTls";

    [BsonId]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public string DeliveryProvider { get; set; } = ProviderSmtp;

    [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string EncryptionMethod { get; set; } = string.Empty;

    public bool EnableSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? FromEmail { get; set; }

    public string? FromName { get; set; }

    public string GetEffectiveEncryptionMethod() => string.IsNullOrWhiteSpace(EncryptionMethod)
        ? (EnableSsl ? EncryptionStartTls : EncryptionNone)
        : EncryptionMethod;

    public string? GetEncryptionValidationError() => GetEffectiveEncryptionMethod() switch
    {
        EncryptionNone or EncryptionStartTls => null,
        EncryptionSslTls => "Implicit SSL/TLS is not supported by System.Net.Mail. Use a server endpoint supporting STARTTLS or unencrypted SMTP.",
        _ => "Unsupported encryption method. Select None or STARTTLS."
    };
}
