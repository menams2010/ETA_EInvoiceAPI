using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace InvoiceWebApp.Services;

// Options are shared across requests. Never mutate Options.ClientId/ClientSecret per profile.
public sealed class ProfileGraphOAuthHandler(
    IOptionsMonitor<OAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    GraphAppRegistrationStore registrations) : OAuthHandler<OAuthOptions>(options, logger, encoder)
{
    protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri)
    {
        var registration = GetRegistration(properties, requireVersion: false);
        properties.Items["RegistrationVersion"] = registration.Version;
        // The base handler supplies protected state, correlation and PKCE.
        var url = new Uri(base.BuildChallengeUrl(properties, redirectUri));
        var query = QueryHelpers.ParseQuery(url.Query);
        query["client_id"] = registration.ClientId;
        return url.GetLeftPart(UriPartial.Path) + QueryString.Create(query).ToUriComponent();
    }

    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        var local = await Context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!local.Succeeded || string.IsNullOrWhiteSpace(local.Principal?.Identity?.Name)
            || !context.Properties.Items.TryGetValue("LocalUser", out var user)
            || local.Principal.Identity.Name != user)
            return OAuthTokenResponse.Failed(new InvalidOperationException("Sign in locally and reconnect the Microsoft account."));

        var registration = GetRegistration(context.Properties, requireVersion: true);
        if (!context.Properties.Items.TryGetValue("code_verifier", out var verifier) || string.IsNullOrEmpty(verifier))
            return OAuthTokenResponse.Failed(new InvalidOperationException("Missing Microsoft sign-in verification. Reconnect."));

        using var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = registration.ClientId,
                ["client_secret"] = registration.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = context.Code,
                ["redirect_uri"] = context.RedirectUri,
                ["code_verifier"] = verifier
            })
        };
        using var response = await Backchannel.SendAsync(request, Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return OAuthTokenResponse.Failed(new InvalidOperationException("Microsoft authorization failed. Check this profile's application registration and reconnect."));
        return OAuthTokenResponse.Success(await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Context.RequestAborted), cancellationToken: Context.RequestAborted));
    }

    private GraphAppRegistrationStore.Registration GetRegistration(AuthenticationProperties properties, bool requireVersion)
    {
        if (!properties.Items.TryGetValue("ProfileId", out var value) || !int.TryParse(value, out var profileId))
            throw new InvalidOperationException("A saved Microsoft Graph profile is required.");
        var registration = registrations.Require(profileId);
        if (requireVersion && (!properties.Items.TryGetValue("RegistrationVersion", out var version) || version != registration.Version))
            throw new InvalidOperationException("The application registration changed during sign-in. Reconnect.");
        return registration;
    }
}
