using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class CompEnvController : Controller
    {
        private readonly AppDbContext _context;
        public CompEnvController(AppDbContext context) { _context = context; }

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
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(CompEnv compEnv)
        {
            if (ModelState.IsValid)
            {
                _context.CompEnvs.Update(compEnv);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            _context.CompEnvs.Delete(id);
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
    }
}
