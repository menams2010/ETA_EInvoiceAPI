using System.Security.Claims;
using InvoiceWebApp.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace InvoiceWebApp.Services;

public sealed class AccountCookieEvents(AppDbContext context) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext cookie)
    {
        var id = cookie.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = cookie.Principal?.FindFirstValue(AccountSecurity.StampClaim);
        var user = int.TryParse(id, out var key) ? context.Logins.FindById(key) : null;
        if (user == null || string.IsNullOrEmpty(stamp) || user.SecurityStamp != stamp
            || user.u_name != cookie.Principal?.Identity?.Name)
        {
            cookie.RejectPrincipal();
            await cookie.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
