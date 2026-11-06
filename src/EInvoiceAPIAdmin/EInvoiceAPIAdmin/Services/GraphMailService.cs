using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using InvoiceWebApp.Models;
using LiteDB;
using Microsoft.AspNetCore.DataProtection;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace InvoiceWebApp.Services;

public sealed class GraphMailService
{
    public const string AuthenticationScheme = "MicrosoftGraph";
    public const string Scopes = "https://graph.microsoft.com/Mail.Send https://graph.microsoft.com/User.Read offline_access";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private readonly LiteDatabase _db;
    private readonly IHttpClientFactory _clients;
    private readonly GraphAppRegistrationStore _registrations;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GraphMailService(LiteDatabase db, IHttpClientFactory clients, GraphAppRegistrationStore registrations, IDataProtectionProvider protection)
    {
        _db = db;
        _clients = clients;
        _registrations = registrations;
        _protector = protection.CreateProtector("InvoiceWebApp.GraphMail.Tokens.v1");
    }

    public bool IsConfigured(int profileId) => _registrations.Find(profileId) is not null;

    private ILiteCollection<BsonDocument> Connections => _db.GetCollection("GraphMailConnections");

    public string? GetConnectedMailbox(int profileId)
    {
        var document = Connections.FindById(profileId);
        if (document is null) return null;
        try
        {
            var tokens = ReadTokens(document);
            var registration = _registrations.Find(profileId);
            return registration is not null && tokens.ClientId == registration.ClientId
                && tokens.RegistrationVersion == registration.Version ? tokens.Mailbox : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    public async Task ConnectAsync(int profileId, string accessToken, string refreshToken, CancellationToken ct, string? registrationVersion = null)
    {
        var registration = _registrations.Require(profileId);
        if (registrationVersion is not null && registration.Version != registrationVersion)
            throw new InvalidOperationException("The Microsoft application registration changed. Reconnect the account.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _clients.CreateClient(AuthenticationScheme).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Microsoft mailbox details could not be read. Grant delegated User.Read and Mail.Send permissions and reconnect.");

        using var identity = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var mailbox = GetString(identity.RootElement, "mail") ?? GetString(identity.RootElement, "userPrincipalName");
        if (string.IsNullOrWhiteSpace(mailbox))
            throw new InvalidOperationException("The connected Microsoft account has no mailbox address.");

        await _gate.WaitAsync(ct);
        try
        {
            EnsureGraphProfile(profileId);
            if (_registrations.Require(profileId).Version != registration.Version)
                throw new InvalidOperationException("The Microsoft application registration changed. Reconnect the account.");
            SaveTokens(profileId, new TokenData
            {
                ClientId = registration.ClientId,
                RegistrationVersion = registration.Version,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                // Refresh before the first send rather than assuming the authorization token lifetime.
                ExpiresAtUtc = DateTimeOffset.MinValue,
                Mailbox = mailbox
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(int profileId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Connections.Delete(profileId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendAsync(SmtpProfile profile, IEnumerable<string> to, IEnumerable<string> cc,
        string subject, string body, bool isHtml, CancellationToken ct)
    {
        var toRecipients = CreateRecipients(to);
        var ccRecipients = CreateRecipients(cc);
        if (toRecipients.Length == 0 && ccRecipients.Length == 0)
            throw new InvalidOperationException("At least one email recipient is required.");

        var accessToken = await GetAccessTokenAsync(profile.Id, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            message = new
            {
                subject,
                body = new { contentType = isHtml ? "HTML" : "Text", content = body },
                toRecipients,
                ccRecipients
            },
            saveToSentItems = true
        });
        using var response = await _clients.CreateClient(AuthenticationScheme).SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Accepted) return;

        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Microsoft Graph rejected authorization. Reconnect the Microsoft account.",
            HttpStatusCode.Forbidden => "Microsoft Graph denied mail sending. Verify delegated Mail.Send consent, mailbox availability, and tenant policy, then reconnect.",
            HttpStatusCode.TooManyRequests => "Microsoft Graph throttled the request. Wait before trying again; the application did not retry automatically.",
            _ => $"Microsoft Graph did not accept the email (HTTP {(int)response.StatusCode}). Check mailbox availability and Microsoft service status before retrying."
        });
    }

    private async Task<string> GetAccessTokenAsync(int profileId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var registration = _registrations.Require(profileId);
            EnsureGraphProfile(profileId);
            var document = Connections.FindById(profileId)
                ?? throw new InvalidOperationException("Connect a Microsoft account on the saved profile before sending.");
            TokenData tokens;
            try
            {
                tokens = ReadTokens(document);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                throw new InvalidOperationException("The stored Microsoft connection cannot be decrypted. Reconnect the account.");
            }
            if (tokens.ClientId != registration.ClientId || tokens.RegistrationVersion != registration.Version)
                throw new InvalidOperationException("The Microsoft application registration changed. Reconnect the account.");
            if (tokens.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
                return tokens.AccessToken;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = tokens.ClientId,
                ["client_secret"] = registration.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
                ["scope"] = Scopes
            });
            using var response = await _clients.CreateClient(AuthenticationScheme).PostAsync(TokenEndpoint, content, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Microsoft authorization could not be renewed. Check the configured client secret and tenant policy, then reconnect the account.");

            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            tokens.AccessToken = GetString(result.RootElement, "access_token")
                ?? throw new InvalidOperationException("Microsoft returned no access token. Reconnect the account.");
            tokens.RefreshToken = GetString(result.RootElement, "refresh_token") ?? tokens.RefreshToken;
            var seconds = result.RootElement.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var value) ? value : 0;
            tokens.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds));
            SaveTokens(profileId, tokens);
            return tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureGraphProfile(int profileId)
    {
        var profile = _db.GetCollection<SmtpProfile>("SmtpProfiles").FindById(profileId);
        if (profile?.DeliveryProvider != SmtpProfile.ProviderGraph)
            throw new InvalidOperationException("Save this profile with Microsoft Graph selected before connecting or sending.");
    }

    private TokenData ReadTokens(BsonDocument document) =>
        JsonSerializer.Deserialize<TokenData>(_protector.Unprotect(document["ProtectedTokens"].AsString))
        ?? throw new JsonException("Invalid stored Microsoft connection.");

    private void SaveTokens(int profileId, TokenData tokens) => Connections.Upsert(new BsonDocument
    {
        ["_id"] = profileId,
        ["ProtectedTokens"] = _protector.Protect(JsonSerializer.Serialize(tokens))
    });

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static object[] CreateRecipients(IEnumerable<string> addresses) => addresses
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => (object)new { emailAddress = new { address = new MailAddress(x.Trim()).Address } })
        .ToArray();

    private sealed class TokenData
    {
        public string ClientId { get; set; } = string.Empty;
        public string? RegistrationVersion { get; set; }
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public string Mailbox { get; set; } = string.Empty;
    }
}
