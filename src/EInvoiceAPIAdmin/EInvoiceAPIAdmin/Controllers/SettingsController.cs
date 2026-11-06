using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class SettingsController : Controller
    {
        private readonly AppDbContext _context;
        public SettingsController(AppDbContext context) { _context = context; }

        public IActionResult Index()
        {
            return View(_context.Settings.FindAll().ToList());
        }

        [HttpPost]
        public IActionResult Create(Settings obj)
        {
            if (ModelState.IsValid)
            {
                _context.Settings.Upsert(obj);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(Settings obj)
        {
            if (ModelState.IsValid)
            {
                _context.Settings.Update(obj);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            _context.Settings.Delete(id);
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.Settings.FindAll().ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "Settings.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile != null && jsonFile.Length > 0)
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                var items = System.Text.Json.JsonSerializer.Deserialize<List<Settings>>(content);
                if (items != null)
                {
                    _context.Settings.InsertBulk(items);
                }
            }
            return RedirectToAction("Index");
        }
    }
}
