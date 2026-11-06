using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers;

/// <summary>
/// ETANotifications (IncomeTaxWebAPI) settings: the API key ETA must send, and the SAP endpoints that
/// notifications are forwarded to. The two SAP records (Prod, Dev) are fixed; only their URL, credentials
/// and timeout are editable.
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
        ViewBag.Notification = ctx.NotificationSettings.FindById(NotificationSettings.SingletonId) ?? new NotificationSettings();
        return View(ctx.SapEndpoints.FindAll().OrderBy(x => x.Id).ToList());
    }

    [HttpGet]
    public IActionResult ApiKey()
    {
        var current = ctx.NotificationSettings.FindById(NotificationSettings.SingletonId);
        ViewBag.KeySet = !string.IsNullOrEmpty(current?.ApiKey);
        ViewBag.SuggestedKey = NotificationSettings.GenerateApiKey();
        return View();
    }

    /// <param name="mode">"generate": save <paramref name="generatedKey"/> (shown on the form); "custom": save <paramref name="apiKey"/>.</param>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ApiKey(string mode, string? generatedKey, string? apiKey, string? confirmApiKey)
    {
        var key = (mode == "custom" ? apiKey : generatedKey)?.Trim();
        var error = NotificationSettings.ValidateApiKey(key);
        if (error is null && mode == "custom" && key != confirmApiKey?.Trim()) error = "The two API keys do not match.";
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            ViewBag.KeySet = !string.IsNullOrEmpty(ctx.NotificationSettings.FindById(NotificationSettings.SingletonId)?.ApiKey);
            ViewBag.SuggestedKey = NotificationSettings.GenerateApiKey();
            return View();
        }

        var existing = ctx.NotificationSettings.FindById(NotificationSettings.SingletonId);
        ctx.NotificationSettings.Upsert(new NotificationSettings { ApiKey = key, ApiKeyChangedUtc = DateTime.UtcNow });
        // The audit log records that the key changed, never the key itself.
        audit.Log(User.Identity?.Name ?? "Unknown", "Update", "NotificationSettings", NotificationSettings.SingletonId.ToString(),
            mode == "custom" ? "Set the ETA notifications API key" : "Generated a new ETA notifications API key",
            new { ApiKeySet = !string.IsNullOrEmpty(existing?.ApiKey), existing?.ApiKeyChangedUtc },
            new { ApiKeySet = true, ApiKeyChangedUtc = DateTime.UtcNow }, HttpContext.Connection.RemoteIpAddress?.ToString());

        return RedirectToAction(nameof(Index), new
        {
            message = "API key saved. ETANotifications uses it from the next request: update the API key in the ETA portal now."
        });
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
