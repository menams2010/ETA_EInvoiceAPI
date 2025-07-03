using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class SettingsController : Controller
    {
        private readonly AppDbContext _context;
        public SettingsController(AppDbContext context) { _context = context; }

        public IActionResult Index()
        {
            return View(_context.Settings.ToList());
        }

        [HttpPost]
        public IActionResult Create(Settings obj)
        {
            if (ModelState.IsValid)
            {
                _context.Settings.Add(obj);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(Settings obj)
        {
            if (ModelState.IsValid)
            {
                _context.Settings.Update(obj);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            var obj = _context.Settings.Find(id);
            if (obj != null)
            {
                _context.Settings.Remove(obj);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.Settings.ToList();
            var json = JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "Settings.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile != null && jsonFile.Length > 0)
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                var items = JsonSerializer.Deserialize<List<Settings>>(content);
                if (items != null)
                {
                    _context.Settings.AddRange(items);
                    _context.SaveChanges();
                }
            }
            return RedirectToAction("Index");
        }
    }
}
