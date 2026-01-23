using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class AlertRulesController(AppDbContext ctx) : Controller
{
    private readonly AppDbContext _ctx = ctx;

    [HttpGet]
    public IActionResult Index(AlertType? type = null, string? message = null, string? error = null)
    {
        ViewBag.Message = message;
        ViewBag.Error = error;
        ViewBag.Type = type;

        var q = _ctx.AlertRules.FindAll().AsQueryable();
        if (type.HasValue)
            q = q.Where(x => x.Type == type.Value);

        return View(q.OrderBy(x => x.Type).ThenBy(x => x.DaysBeforeExpiration).ThenBy(x => x.Id).ToList());
    }

    [HttpGet]
    public IActionResult Create(AlertType? type = null)
    {
        var model = new AlertRule { Enabled = true, Type = type ?? AlertType.CertificateExpiration };
        PopulateLookups(model);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(AlertRule model)
    {
        if (!ModelState.IsValid)
        {
            PopulateLookups(model);
            return View(model);
        }

        var nextId = _ctx.AlertRules.Query().OrderByDescending(x => x.Id).Limit(1).ToList().FirstOrDefault()?.Id + 1 ?? 1;
        model.Id = nextId;

        _ctx.AlertRules.Insert(model);
        return RedirectToAction(nameof(Index), new { message = "Rule created.", type = model.Type });
    }

    [HttpGet]
    public IActionResult Edit(int id)
    {
        var item = _ctx.AlertRules.FindById(id);
        if (item is null) return RedirectToAction(nameof(Index), new { error = "Rule not found." });
        PopulateLookups(item);
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Edit(AlertRule model)
    {
        if (!ModelState.IsValid)
        {
            PopulateLookups(model);
            return View(model);
        }

        _ctx.AlertRules.Update(model);
        return RedirectToAction(nameof(Index), new { message = "Rule updated.", type = model.Type });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Delete(int id)
    {
        var existing = _ctx.AlertRules.FindById(id);
        if (existing is null)
            return RedirectToAction(nameof(Index));

        _ctx.AlertRules.Delete(id);
        return RedirectToAction(nameof(Index), new { message = "Rule deleted.", type = existing.Type });
    }

    private void PopulateLookups(AlertRule model)
    {
        var smtp = _ctx.SmtpProfiles.FindAll().OrderBy(x => x.Name).ToList();
        var templates = _ctx.EmailTemplates.FindAll().OrderBy(x => x.Name).ToList();

        ViewBag.SmtpProfiles = new SelectList(smtp, nameof(SmtpProfile.Id), nameof(SmtpProfile.Name), model.SmtpProfileId);
        ViewBag.EmailTemplates = new SelectList(templates, nameof(EmailTemplate.Id), nameof(EmailTemplate.Name), model.EmailTemplateId);
        ViewBag.AlertTypes = new SelectList(Enum.GetValues<AlertType>().Select(x => new { Id = (int)x, Name = x.ToString() }), "Id", "Name", (int)model.Type);

        // default if not set
        if (model.SmtpProfileId is null)
        {
            var def = smtp.FirstOrDefault(x => x.IsDefault);
            if (def is not null) model.SmtpProfileId = def.Id;
        }
        if (model.EmailTemplateId is null)
        {
            var first = templates.FirstOrDefault();
            if (first is not null) model.EmailTemplateId = first.Id;
        }
    }
}
