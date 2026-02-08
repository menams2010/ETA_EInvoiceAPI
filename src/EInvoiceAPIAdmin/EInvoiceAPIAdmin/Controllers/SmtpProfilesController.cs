using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class SmtpProfilesController(AppDbContext ctx, SmtpEmailSender emailSender) : Controller
{
    private readonly AppDbContext _ctx = ctx;
    private readonly SmtpEmailSender _emailSender = emailSender;

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
        return View(new SmtpProfile { EnableSsl = true, Port = 587 });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(SmtpProfile model)
    {
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
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(SmtpProfile model)
    {
        if (!ModelState.IsValid)
            return View(model);

        if (model.IsDefault)
            ClearDefaultExcept(model.Id);

        _ctx.SmtpProfiles.Update(model);
        return RedirectToAction(nameof(Index), new { message = "SMTP profile updated." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(int id)
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
        return RedirectToAction(nameof(Index), new { message = "SMTP profile deleted." });
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

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection([FromBody] TestConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
            return BadRequest(new { success = false, message = "SMTP host is required." });

        if (string.IsNullOrWhiteSpace(request.TestEmail))
            return BadRequest(new { success = false, message = "Test email address is required." });

        var profile = new SmtpProfile
        {
            Host = request.Host,
            Port = request.Port,
            EnableSsl = request.EnableSsl,
            Username = request.Username,
            Password = request.Password,
            FromEmail = request.FromEmail,
            FromName = request.FromName
        };

        try
        {
            await _emailSender.SendAsync(
                profile,
                [request.TestEmail],
                [],
                "SMTP Connection Test",
                "This is a test email to verify your SMTP configuration is working correctly.",
                false,
                CancellationToken.None);

            return Json(new { success = true, message = "Test email sent successfully!" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to send test email: {ex.Message}" });
        }
    }

    public class TestConnectionRequest
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 587;
        public bool EnableSsl { get; set; } = true;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }
        public string TestEmail { get; set; } = string.Empty;
    }
}
