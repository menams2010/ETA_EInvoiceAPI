using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class EmailTemplatesController(AppDbContext ctx) : Controller
{
    private readonly AppDbContext _ctx = ctx;

    [HttpGet]
    public IActionResult Index(string? message = null, string? error = null)
    {
        ViewBag.Message = message;
        ViewBag.Error = error;
        var items = _ctx.EmailTemplates.FindAll().OrderBy(x => x.Id).ToList();
        return View(items);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return View(new EmailTemplate { IsHtml = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(EmailTemplate model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var nextId = _ctx.EmailTemplates.Query().OrderByDescending(x => x.Id).Limit(1).ToList().FirstOrDefault()?.Id + 1 ?? 1;
        model.Id = nextId;

        _ctx.EmailTemplates.Insert(model);
        return RedirectToAction(nameof(Index), new { message = "Template created." });
    }

    [HttpGet]
    public IActionResult Edit(int id)
    {
        var item = _ctx.EmailTemplates.FindById(id);
        if (item is null) return RedirectToAction(nameof(Index), new { error = "Template not found." });
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(EmailTemplate model)
    {
        if (!ModelState.IsValid)
            return View(model);

        _ctx.EmailTemplates.Update(model);
        return RedirectToAction(nameof(Index), new { message = "Template updated." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(int id)
    {
        // detach from rules
        foreach (var r in _ctx.AlertRules.Find(x => x.EmailTemplateId == id))
        {
            r.EmailTemplateId = null;
            _ctx.AlertRules.Update(r);
        }

        _ctx.EmailTemplates.Delete(id);
        return RedirectToAction(nameof(Index), new { message = "Template deleted." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Preview([FromBody] PreviewRequest request)
    {
        if (string.IsNullOrEmpty(request.Subject) && string.IsNullOrEmpty(request.Body))
            return BadRequest(new { error = "Subject and Body are required." });

        var sampleValues = new Dictionary<string, string?>
        {
            ["Type"] = "Client/Secret",
            ["Days"] = "7",
            ["ExpirationDate"] = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd"),
            ["Entity"] = "Sample Company / Production",
            ["CompanyName"] = "Sample Company",
            ["Environment"] = "Production",
            ["RuleName"] = "7 Days Before Expiry",
            ["Severity"] = "Warning"
        };

        var renderedSubject = AlertTemplateRenderer.Render(request.Subject ?? "", sampleValues);
        var renderedBody = AlertTemplateRenderer.Render(request.Body ?? "", sampleValues);

        return Json(new { subject = renderedSubject, body = renderedBody, isHtml = request.IsHtml });
    }

    public class PreviewRequest
    {
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public bool IsHtml { get; set; }
    }
}
