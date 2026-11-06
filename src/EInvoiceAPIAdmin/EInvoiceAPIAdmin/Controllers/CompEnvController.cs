using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class CompEnvController : Controller
    {
        private readonly AppDbContext _context;
        private readonly AuditLogService _audit;

        public CompEnvController(AppDbContext context, AuditLogService audit)
        {
            _context = context;
            _audit = audit;
        }

        public IActionResult Index()
        {
            ViewBag.Companies = _context.Companies.FindAll().ToList();
            ViewBag.Envs = _context.EInvoiceEnvs.FindAll().ToList();
            return View(_context.CompEnvs.FindAll().ToList());
        }

        [HttpPost]
        public IActionResult Create(CompEnv compEnv)
        {
            if (ModelState.IsValid)
            {
                _context.CompEnvs.Upsert(compEnv);
                _audit.Log(
                    User.Identity?.Name ?? "Unknown",
                    "Create",
                    "CompEnv",
                    compEnv.Id.ToString(),
                    $"Created CompEnv for Company {compEnv.CompanyId}, Env {compEnv.EnvId}",
                    null,
                    compEnv,
                    HttpContext.Connection.RemoteIpAddress?.ToString());
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(CompEnv compEnv)
        {
            if (ModelState.IsValid)
            {
                var oldValue = _context.CompEnvs.FindById(compEnv.Id);
                _context.CompEnvs.Update(compEnv);
                _audit.Log(
                    User.Identity?.Name ?? "Unknown",
                    "Update",
                    "CompEnv",
                    compEnv.Id.ToString(),
                    $"Updated CompEnv for Company {compEnv.CompanyId}, Env {compEnv.EnvId}",
                    oldValue,
                    compEnv,
                    HttpContext.Connection.RemoteIpAddress?.ToString());
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            var oldValue = _context.CompEnvs.FindById(id);
            _context.CompEnvs.Delete(id);
            _audit.Log(
                User.Identity?.Name ?? "Unknown",
                "Delete",
                "CompEnv",
                id.ToString(),
                $"Deleted CompEnv {id}",
                oldValue,
                null,
                HttpContext.Connection.RemoteIpAddress?.ToString());
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.CompEnvs.FindAll().ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "CompEnvs.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile != null && jsonFile.Length > 0)
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                try
                {
                    var items = System.Text.Json.JsonSerializer.Deserialize<List<CompEnv>>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (items == null || items.Count == 0)
                    {
                        ModelState.AddModelError("", "JSON file is empty or invalid.");
                        return RedirectToAction("Index");
                    }

                    // Validate required fields for each item
                    var invalidItems = items.Where(x =>
                        x.CompanyId == 0 ||
                        x.EnvId == 0
                    ).ToList();

                    if (invalidItems.Any())
                    {
                        ModelState.AddModelError("", "One or more items are missing required fields (CompanyId, EnvId).");
                        return RedirectToAction("Index");
                    }

                    _context.CompEnvs.InsertBulk(items);
                }
                catch (JsonException)
                {
                    ModelState.AddModelError("", "Invalid JSON structure.");
                }
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Clone(int id, long targetCompanyId)
        {
            var source = _context.CompEnvs.FindById(id);
            if (source is null)
                return RedirectToAction("Index");

            // Get next ID
            var nextId = _context.CompEnvs.Query().OrderByDescending(x => x.Id).Limit(1).ToList().FirstOrDefault()?.Id + 1 ?? 1;

            var clone = new CompEnv
            {
                Id = nextId,
                CompanyId = targetCompanyId,
                EnvId = source.EnvId,
                ClientId = source.ClientId,
                ClientSecret = source.ClientSecret,
                ClientSecretExpiryDate = source.ClientSecretExpiryDate,
                str_token = null,
                time_get = null
            };

            _context.CompEnvs.Insert(clone);

            _audit.Log(
                User.Identity?.Name ?? "Unknown",
                "Clone",
                "CompEnv",
                clone.Id.ToString(),
                $"Cloned CompEnv {id} to Company {targetCompanyId}",
                source,
                clone,
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return RedirectToAction("Index");
        }
    }
}
