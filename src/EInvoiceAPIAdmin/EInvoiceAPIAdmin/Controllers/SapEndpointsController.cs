using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers;

/// <summary>
/// SAP endpoints used by ETANotifications (IncomeTaxWebAPI) to forward ETA notifications.
/// The two records (Prod, Dev) are fixed; only their URL, credentials and timeout are editable.
/// </summary>
[Authorize]
[Route("[controller]/[action]")]
public class SapEndpointsController(AppDbContext ctx, AuditLogService audit) : Controller
{
    [HttpGet]
    public IActionResult Index(string? message = null, string? error = null)
    {
        ViewBag.Message = message;
        ViewBag.Error = error;
        return View(ctx.SapEndpoints.FindAll().OrderBy(x => x.Id).ToList());
    }

    [HttpGet]
    public IActionResult Edit(int id)
    {
        var item = ctx.SapEndpoints.FindById(id);
        if (item is null) return RedirectToAction(nameof(Index), new { error = "SAP endpoint not found." });
        ViewBag.PasswordSaved = !string.IsNullOrEmpty(item.Password);
        item.Password = null; // never send the saved password to the browser
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(int id, string url, string? username, string? password, bool clearPassword, int timeoutMs)
    {
        var existing = ctx.SapEndpoints.FindById(id);
        if (existing is null) return NotFound();

        var updated = new SapEndpoint
        {
            Id = existing.Id,
            Name = existing.Name, // fixed
            Url = (url ?? string.Empty).Trim(),
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            // Blank keeps the saved password; "clear" removes it.
            Password = clearPassword ? null : string.IsNullOrEmpty(password) ? existing.Password : password,
            TimeoutMs = timeoutMs
        };

        ModelState.Clear();
        if (!TryValidateModel(updated) ||
            !Uri.TryCreate(updated.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            if (ModelState.IsValid) ModelState.AddModelError(nameof(SapEndpoint.Url), "Enter an absolute http(s) URL.");
            ViewBag.PasswordSaved = !string.IsNullOrEmpty(existing.Password);
            updated.Password = null;
            return View(updated);
        }

        ctx.SapEndpoints.Update(updated);
        audit.Log(User.Identity?.Name ?? "Unknown", "Update", "SapEndpoint", updated.Id.ToString(),
            $"Updated SAP endpoint {updated.Name}" + (password is { Length: > 0 } || clearPassword ? " (password changed)" : string.Empty),
            Redacted(existing), Redacted(updated), HttpContext.Connection.RemoteIpAddress?.ToString());

        return RedirectToAction(nameof(Index), new { message = $"SAP endpoint {updated.Name} saved." });
    }

    // Audit entries record whether a password is set, never its value.
    private static object Redacted(SapEndpoint e) => new
    {
        e.Id, e.Name, e.Url, e.Username, PasswordSet = !string.IsNullOrEmpty(e.Password), e.TimeoutMs
    };
}
