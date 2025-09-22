using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class EInvoiceEnvController : Controller
    {
        private readonly AppDbContext _context;
        public EInvoiceEnvController(AppDbContext context) { _context = context; }

        public IActionResult Index()
        {
            return View(_context.EInvoiceEnvs.FindAll().ToList());
        }

        [HttpPost]
        public IActionResult Create(EInvoiceEnv env)
        {
            if (ModelState.IsValid)
            {
                _context.EInvoiceEnvs.Upsert(env);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(EInvoiceEnv env)
        {
            if (ModelState.IsValid)
            {
                _context.EInvoiceEnvs.Update(env);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            _context.EInvoiceEnvs.Delete(id);
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.EInvoiceEnvs.FindAll().ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "EInvoiceEnvs.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile != null && jsonFile.Length > 0)
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                var items = System.Text.Json.JsonSerializer.Deserialize<List<EInvoiceEnv>>(content);
                if (items != null)
                {
                    _context.EInvoiceEnvs.InsertBulk(items);
                }
            }
            return RedirectToAction("Index");
        }
    }
}
