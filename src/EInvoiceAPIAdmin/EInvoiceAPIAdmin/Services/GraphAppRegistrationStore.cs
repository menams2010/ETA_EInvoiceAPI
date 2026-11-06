using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;
using Microsoft.AspNetCore.DataProtection;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace InvoiceWebApp.Services;

// Kept separate from editable mail profiles so secrets cannot be overposted or rendered.
public sealed class GraphAppRegistrationStore(LiteDatabase db, IDataProtectionProvider protection)
{
    private readonly object _gate = new();
    private ILiteCollection<BsonDocument> Registrations => db.GetCollection("GraphAppRegistrations");

    public sealed record Registration(string ClientId, string ClientSecret, string Version);

    public Registration? Find(int profileId)
    {
        lock (_gate)
        {
            var document = Registrations.FindById(profileId);
            if (document is null) return null;
            try
            {
                return JsonSerializer.Deserialize<Registration>(Protector(profileId).Unprotect(document["ProtectedRegistration"].AsString));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                return null; // Show an unconfigured state; require re-entry after key loss.
            }
        }
    }

    public Registration Require(int profileId) => Find(profileId)
        ?? throw new InvalidOperationException("Enter the Microsoft application ID and client secret on this profile, save them, then connect the Microsoft account.");

    public void Save(int profileId, string clientId, string? newSecret)
    {
        if (!Guid.TryParse(clientId, out var parsedId))
            throw new InvalidOperationException("Enter a valid Application (client) ID (GUID).");

        lock (_gate)
        {
            var previous = Find(profileId);
            var normalizedId = parsedId.ToString();
            var secret = string.IsNullOrWhiteSpace(newSecret) ? null : newSecret;
            if (secret is null)
            {
                if (previous is null || previous.ClientId != normalizedId)
                    throw new InvalidOperationException("Enter a client secret when creating the registration or changing the application ID.");
                secret = previous.ClientSecret;
            }
            if (previous?.ClientId == normalizedId && previous.ClientSecret == secret)
                return;

            var registration = new Registration(normalizedId, secret, Guid.NewGuid().ToString("N"));
            Registrations.Upsert(new BsonDocument
            {
                ["_id"] = profileId,
                ["ProtectedRegistration"] = Protector(profileId).Protect(JsonSerializer.Serialize(registration))
            });
        }
    }

    public void Delete(int profileId)
    {
        lock (_gate) Registrations.Delete(profileId);
    }

    private IDataProtector Protector(int profileId) => protection.CreateProtector(
        "InvoiceWebApp.GraphMail.Registration.v1", profileId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
