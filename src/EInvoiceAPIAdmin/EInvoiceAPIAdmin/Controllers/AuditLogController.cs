using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class AuditLogController(AuditLogService auditService) : Controller
{
    private readonly AuditLogService _auditService = auditService;

    [HttpGet]
    public IActionResult Index(string? entityType = null, string? userName = null, int limit = 100)
    {
        ViewBag.EntityTypeFilter = entityType;
        ViewBag.UserNameFilter = userName;
        ViewBag.Limit = limit;

        var logs = _auditService.GetAll(limit);

        if (!string.IsNullOrEmpty(entityType))
            logs = logs.Where(x => x.EntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(userName))
            logs = logs.Where(x => x.UserName.Contains(userName, StringComparison.OrdinalIgnoreCase));

        return View(logs.ToList());
    }
}
