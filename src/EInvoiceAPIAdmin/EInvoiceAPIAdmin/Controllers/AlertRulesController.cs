using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class AlertRulesController(AppDbContext ctx, AlertRunner runner) : Controller
{
    private readonly AppDbContext _ctx = ctx;

    [HttpGet]
    public IActionResult Index(AlertType? type = null, string? message = null, string? error = null)
    {
        ViewBag.Message = message;
        ViewBag.Error = error;
        ViewBag.Type = type;
        ViewBag.SmtpProfiles = new SelectList(_ctx.SmtpProfiles.FindAll().OrderBy(x => x.Name),
            nameof(SmtpProfile.Id), nameof(SmtpProfile.Name));

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

    [HttpGet]
    public IActionResult Test(int id)
    {
        var rule = _ctx.AlertRules.FindById(id);
        if (rule is null) return RedirectToAction(nameof(Index), new { error = "Rule not found." });
        var model = new AlertRuleTestViewModel { Id = id, Rule = rule };
        PopulateTestEntities(model);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test([Bind("Id,DataSource,EntityKey,ConfirmSend")] AlertRuleTestViewModel model)
    {
        var rule = _ctx.AlertRules.FindById(model.Id);
        if (rule is null) return RedirectToAction(nameof(Index), new { error = "Rule not found." });
        model.Rule = rule;
        PopulateTestEntities(model);
        if (!model.ConfirmSend)
            ModelState.AddModelError(nameof(model.ConfirmSend), "Confirm that you want to send to the saved rule's To/CC recipients.");
        if (model.DataSource is not ("Sample" or "Real"))
            ModelState.AddModelError(nameof(model.DataSource), "Select sample data or one real record.");
        if (model.DataSource == "Real" && !model.Entities.Any(x => x.Key == model.EntityKey))
            ModelState.AddModelError(nameof(model.EntityKey), "Select an available real record.");
        if (!ModelState.IsValid) return View(model);

        try
        {
            await runner.SendTestAsync(model.Id, model.DataSource, model.EntityKey, HttpContext.RequestAborted);
            return RedirectToAction(nameof(Index), new
            {
                type = rule.Type,
                message = "Test alert submitted to the saved rule's To/CC recipients. Check the recipient mailbox; provider acceptance does not guarantee delivery. Scheduled alerts are unchanged."
            });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Failed to send test alert: {ex.Message}");
            return View(model);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult BatchEdit(AlertRuleBatchEdit model, [FromServices] LiteDB.LiteDatabase database)
    {
        IActionResult Failure(string error) => RedirectToAction(nameof(Index), new { type = model.Type, error });
        if (!ModelState.IsValid)
            return Failure("Invalid batch settings. No rules were changed.");
        var ids = (model.SelectedIds ?? []).Distinct().ToArray();
        if (ids.Length == 0)
            return Failure("Select at least one alert rule.");
        if (!model.ChangeTo && !model.ChangeCc && !model.ChangeEnabled && !model.ChangeSmtpProfile)
            return Failure("Select at least one field to change.");

        bool ValidRecipients(string? value) => AlertTemplateRenderer.SplitEmails(value)
            .All(address => System.Net.Mail.MailAddress.TryCreate(address, out _));
        if ((model.ChangeTo && !ValidRecipients(model.To)) || (model.ChangeCc && !ValidRecipients(model.Cc)))
            return Failure("To and CC must contain valid email addresses separated by commas or semicolons. No rules were changed.");

        if (!database.BeginTrans())
            return Failure("Unable to start the batch update. No rules were changed.");
        try
        {
            if (model.ChangeSmtpProfile && model.SmtpProfileId is int profileId
                && _ctx.SmtpProfiles.FindById(profileId) is null)
            {
                database.Rollback();
                return Failure("The selected mail profile no longer exists. No rules were changed.");
            }
            var rules = ids.Select(id => _ctx.AlertRules.FindById(id)).ToList();
            if (rules.Any(rule => rule is null))
            {
                database.Rollback();
                return Failure("One or more selected rules no longer exist. Refresh the list and try again. No rules were changed.");
            }
            foreach (var rule in rules)
            {
                if (model.ChangeTo) rule.To = string.IsNullOrWhiteSpace(model.To) ? null : model.To.Trim();
                if (model.ChangeCc) rule.Cc = string.IsNullOrWhiteSpace(model.Cc) ? null : model.Cc.Trim();
                if (model.ChangeEnabled) rule.Enabled = model.Enabled;
                if (model.ChangeSmtpProfile) rule.SmtpProfileId = model.SmtpProfileId;
                if (!_ctx.AlertRules.Update(rule))
                    throw new InvalidOperationException("A selected rule could not be updated.");
            }
            database.Commit();
            return RedirectToAction(nameof(Index), new { type = model.Type, message = $"Updated {rules.Count} selected alert rule(s)." });
        }
        catch
        {
            database.Rollback();
            return Failure("The batch update failed. No rules were changed. Refresh the list and try again.");
        }
    }

    private void PopulateTestEntities(AlertRuleTestViewModel model)
    {
        try
        {
            model.Entities = runner.GetTestEntities(model.Rule.Type);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }
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
