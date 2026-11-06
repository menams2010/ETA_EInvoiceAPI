using System.Security.Cryptography;
using LiteDB;

namespace InvoiceWebApp.Models;

/// <summary>
/// Settings of ETANotifications (IncomeTaxWebAPI) that are not per SAP endpoint. Stored in config.db,
/// collection "NotificationSettings", as a single record (Id 1). IncomeTaxWebAPI reads the same record.
/// </summary>
public class NotificationSettings
{
    public const int SingletonId = 1;
    public const int MinApiKeyLength = 8;
    public const int MaxApiKeyLength = 200;

    [BsonId]
    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// API key that ETA must send to ETANotifications, as the header <c>Authorization: ApiKey &lt;key&gt;</c>.
    /// Null means no key is set: ETANotifications then rejects every request.
    /// </summary>
    public string? ApiKey { get; set; }

    public DateTime? ApiKeyChangedUtc { get; set; }

    /// <summary>Returns an error message, or null when the key is acceptable.</summary>
    public static string? ValidateApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "Enter an API key.";
        if (key.Length is < MinApiKeyLength or > MaxApiKeyLength)
            return $"The API key must be {MinApiKeyLength} to {MaxApiKeyLength} characters.";
        // It travels in an HTTP header: visible ASCII only, no spaces.
        if (key.Any(c => c is < '!' or > '~'))
            return "The API key may contain only letters, digits and symbols (no spaces or non-English characters).";
        return null;
    }

    /// <summary>A random 32-character key (128 bits).</summary>
    public static string GenerateApiKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
