using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class LoginController : Controller
    {
        private readonly AppDbContext _context;
        public LoginController(AppDbContext context) { _context = context; }

        [AllowAnonymous]
        public IActionResult Index()
        {
            return View(_context.Logins.FindAll().ToList());
        }

        [HttpPost]
        public IActionResult Create(Login obj)
        {
            if (ModelState.IsValid)
            {
                _context.Logins.Upsert(obj);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(Login obj)
        {
            if (ModelState.IsValid)
            {
                _context.Logins.Update(obj);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            _context.Logins.Delete(id);
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.Logins.FindAll().ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "Logins.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile != null && jsonFile.Length > 0)
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                var items = System.Text.Json.JsonSerializer.Deserialize<List<Login>>(content);
                if (items != null)
                {
                    _context.Logins.InsertBulk(items);
                }
            }
            return RedirectToAction("Index");
        }
    }
}
