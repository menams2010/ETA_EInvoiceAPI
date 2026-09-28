using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class SmtpProfilesController(AppDbContext ctx, EmailSender emailSender, GraphMailService graph, GraphAppRegistrationStore registrations) : Controller
{
    private readonly AppDbContext _ctx = ctx;
    private readonly EmailSender _emailSender = emailSender;

    [HttpGet]
    public IActionResult Index(string? message = null, string? error = null)
    {
        ViewBag.Message = message;
        ViewBag.Error = error;
        var items = _ctx.SmtpProfiles.FindAll().OrderBy(x => x.Id).ToList();
        return View(items);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return View(new SmtpProfile { EnableSsl = true, EncryptionMethod = SmtpProfile.EncryptionStartTls, Port = 587 });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(SmtpProfile model)
    {
        NormalizeEncryption(model);

        if (!ModelState.IsValid)
            return View(model);

        var nextId = _ctx.SmtpProfiles.Query().OrderByDescending(x => x.Id).Limit(1).ToList().FirstOrDefault()?.Id + 1 ?? 1;
        model.Id = nextId;

        if (model.IsDefault)
            ClearDefaultExcept(null);

        _ctx.SmtpProfiles.Insert(model);
        return RedirectToAction(nameof(Index), new { message = "SMTP profile created." });
    }

    [HttpGet]
    public IActionResult Edit(int id)
    {
        var item = _ctx.SmtpProfiles.FindById(id);
        if (item is null) return RedirectToAction(nameof(Index), new { error = "SMTP profile not found." });

        NormalizeEncryption(item);
        ViewBag.GraphMailbox = graph.GetConnectedMailbox(id);
        ViewBag.GraphClientId = registrations.Find(id)?.ClientId;
        ViewBag.GraphCredentialsSaved = graph.IsConfigured(id);

        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(SmtpProfile model)
    {
        var existing = _ctx.SmtpProfiles.FindById(model.Id);
        if (existing is null) return NotFound();
        ViewBag.GraphMailbox = graph.GetConnectedMailbox(model.Id);
        ViewBag.GraphClientId = registrations.Find(model.Id)?.ClientId;
        ViewBag.GraphCredentialsSaved = graph.IsConfigured(model.Id);
        NormalizeEncryption(model);

        if (!ModelState.IsValid)
            return View(model);

        if (model.IsDefault)
            ClearDefaultExcept(model.Id);

        _ctx.SmtpProfiles.Update(model);
        if (model.DeliveryProvider != SmtpProfile.ProviderGraph)
            await graph.DisconnectAsync(model.Id, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Index), new { message = "SMTP profile updated." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var existing = _ctx.SmtpProfiles.FindById(id);
        if (existing is null)
            return RedirectToAction(nameof(Index));

        if (existing.IsDefault)
            return RedirectToAction(nameof(Index), new { error = "Cannot delete the default SMTP profile." });

        // detach from rules
        foreach (var r in _ctx.AlertRules.Find(x => x.SmtpProfileId == id))
        {
            r.SmtpProfileId = null;
            _ctx.AlertRules.Update(r);
        }

        _ctx.SmtpProfiles.Delete(id);
        registrations.Delete(id);
        await graph.DisconnectAsync(id, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Index), new { message = "SMTP profile deleted." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ConnectMicrosoft(int id)
    {
        var profile = _ctx.SmtpProfiles.FindById(id);
        if (profile?.DeliveryProvider != SmtpProfile.ProviderGraph)
            return RedirectToAction(nameof(Index), new { error = "Save the profile with Microsoft Graph selected first." });
        if (!graph.IsConfigured(id))
            return RedirectToAction(nameof(Index), new { error = "Enter and save the Microsoft application ID and client secret on this profile first." });

        var properties = new AuthenticationProperties { RedirectUri = Url.Action(nameof(Edit), new { id }) };
        properties.Items["ProfileId"] = id.ToString();
        properties.Items["LocalUser"] = User.Identity?.Name;
        return Challenge(properties, GraphMailService.AuthenticationScheme);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult SaveMicrosoftRegistration(int id, string clientId, string? clientSecret)
    {
        var profile = _ctx.SmtpProfiles.FindById(id);
        if (profile?.DeliveryProvider != SmtpProfile.ProviderGraph)
            return BadRequest("Save the profile with Microsoft Graph selected first.");

        // Never re-render a posted secret, including through ModelState on validation failure.
        ModelState.Remove(nameof(clientSecret));
        try
        {
            registrations.Save(id, clientId, clientSecret);
            TempData["GraphRegistrationMessage"] = "Microsoft registration saved. Connect/reconnect the mailbox if the credentials changed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["GraphRegistrationError"] = ex.Message;
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMicrosoftRegistration(int id)
    {
        if (_ctx.SmtpProfiles.FindById(id) is null) return NotFound();
        registrations.Delete(id);
        await graph.DisconnectAsync(id, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectMicrosoft(int id)
    {
        await graph.DisconnectAsync(id, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Edit), new { id });
    }

    private void ClearDefaultExcept(int? id)
    {
        foreach (var p in _ctx.SmtpProfiles.Find(x => x.IsDefault))
        {
            if (id.HasValue && p.Id == id.Value) continue;
            p.IsDefault = false;
            _ctx.SmtpProfiles.Update(p);
        }
    }

    private void NormalizeEncryption(SmtpProfile model)
    {
        if (model.DeliveryProvider == SmtpProfile.ProviderGraph)
            return;
        if (model.DeliveryProvider != SmtpProfile.ProviderSmtp)
        {
            ModelState.AddModelError(nameof(SmtpProfile.DeliveryProvider), "Select SMTP or Microsoft Graph.");
            return;
        }

        if (string.IsNullOrWhiteSpace(model.Host))
            ModelState.AddModelError(nameof(SmtpProfile.Host), "SMTP host is required.");
        if (model.Port is < 1 or > 65535)
            ModelState.AddModelError(nameof(SmtpProfile.Port), "SMTP port must be between 1 and 65535.");

        var error = model.GetEncryptionValidationError();
        if (error is not null)
        {
            ModelState.AddModelError(nameof(SmtpProfile.EncryptionMethod), error);
            return;
        }

        model.EncryptionMethod = model.GetEffectiveEncryptionMethod();
        model.EnableSsl = model.EncryptionMethod == SmtpProfile.EncryptionStartTls;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection([FromBody] TestConnectionRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { success = false, message = "Invalid email test settings." });

        if (request.DeliveryProvider != SmtpProfile.ProviderSmtp && request.DeliveryProvider != SmtpProfile.ProviderGraph)
            return BadRequest(new { success = false, message = "Select SMTP or Microsoft Graph." });

        if (request.DeliveryProvider == SmtpProfile.ProviderSmtp && string.IsNullOrWhiteSpace(request.Host))
            return BadRequest(new { success = false, message = "SMTP host is required." });

        if (string.IsNullOrWhiteSpace(request.TestEmail))
            return BadRequest(new { success = false, message = "Test email address is required." });

        var profile = new SmtpProfile
        {
            Host = request.Host ?? string.Empty,
            Port = request.Port,
            EncryptionMethod = request.EncryptionMethod ?? string.Empty,
            EnableSsl = request.EnableSsl,
            Username = request.Username,
            Password = request.Password,
            FromEmail = request.FromEmail,
            FromName = request.FromName
        };

        if (request.DeliveryProvider == SmtpProfile.ProviderGraph)
        {
            var saved = _ctx.SmtpProfiles.FindById(request.ProfileId);
            if (saved?.DeliveryProvider != SmtpProfile.ProviderGraph)
                return BadRequest(new { success = false, message = "Save the profile with Microsoft Graph selected and connect a mailbox first." });
            profile = saved;
        }
        else
        {
            var encryptionError = profile.GetEncryptionValidationError();
            if (encryptionError is not null)
                return BadRequest(new { success = false, message = encryptionError });
        }

        try
        {
            await _emailSender.SendAsync(
                profile,
                [request.TestEmail],
                [],
                "Email Connection Test",
                "This is a test email to verify your email configuration is working correctly.",
                false,
                HttpContext.RequestAborted);

            return Json(new { success = true, message = profile.DeliveryProvider == SmtpProfile.ProviderGraph
                ? "Microsoft Graph accepted the test email. Check the recipient mailbox; acceptance does not guarantee delivery."
                : "Test email sent successfully!" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to send test email: {ex.Message}" });
        }
    }

    public class TestConnectionRequest
    {
        public int ProfileId { get; set; }
        public string DeliveryProvider { get; set; } = SmtpProfile.ProviderSmtp;
        public string? Host { get; set; }
        public int Port { get; set; } = 587;
        public string? EncryptionMethod { get; set; }
        public bool EnableSsl { get; set; } = true;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }
        public string TestEmail { get; set; } = string.Empty;
    }
}
