using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using InvoiceWebApp.Services;
using System.Text.Json;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class LoginController : Controller
    {
        private readonly AppDbContext _context;
        public LoginController(AppDbContext context) { _context = context; }

        public IActionResult Index()
        {
            return View(_context.Logins.FindAll().ToList());
        }

        [HttpPost]
        public IActionResult Create(AccountInput obj)
        {
            if (!ModelState.IsValid || string.IsNullOrEmpty(obj.Password))
                return BadRequest("Supply a username and a password of 12–256 characters.");
            var name = obj.u_name.Trim();
            if (name.Length == 0 || _context.Logins.FindAll().Any(x => string.Equals(x.u_name, name, StringComparison.OrdinalIgnoreCase)))
                return BadRequest("A nonempty unique username is required.");
            var user = new Login { u_name = name, IsInitial = obj.IsInitial };
            AccountSecurity.SetPassword(user, obj.Password);
            _context.Logins.Insert(user);
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(AccountInput obj)
        {
            if (!ModelState.IsValid) return BadRequest("Invalid account settings.");
            var user = _context.Logins.FindById(obj.Id);
            if (user == null) return NotFound();
            var name = obj.u_name.Trim();
            if (name.Length == 0 || _context.Logins.FindAll().Any(x => x.Id != obj.Id && string.Equals(x.u_name, name, StringComparison.OrdinalIgnoreCase)))
                return BadRequest("A nonempty unique username is required.");
            if (user.u_name != name) user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.u_name = name;
            user.IsInitial = obj.IsInitial;
            if (!string.IsNullOrEmpty(obj.Password)) AccountSecurity.SetPassword(user, obj.Password);
            _context.Logins.Update(user);
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            if (_context.Logins.Count() <= 1) return BadRequest("The last administrator cannot be deleted.");
            _context.Logins.Delete(id);
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.Logins.FindAll().Select(x => new { x.Id, x.u_name, x.IsInitial }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "Logins.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile == null || jsonFile.Length == 0 || jsonFile.Length > 1_048_576)
                return BadRequest("Choose an account JSON file of at most 1 MB.");
            try
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                using var document = JsonDocument.Parse(content);
                var users = new List<Login>();
                var names = _context.Logins.FindAll().Select(x => x.u_name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var name = item.GetProperty("u_name").GetString()?.Trim();
                    var password = item.TryGetProperty("Password", out var supplied) ? supplied.GetString()
                        : item.TryGetProperty("u_pass", out var legacy) ? legacy.GetString() : null;
                    if (string.IsNullOrEmpty(name) || name.Length > 100 || password == null || password.Length is < 12 or > 256 || !names.Add(name))
                        return BadRequest("Every imported account needs a unique username and a password of 12–256 characters. Credential-free exports cannot be imported without new passwords.");
                    var user = new Login { u_name = name };
                    AccountSecurity.SetPassword(user, password);
                    users.Add(user);
                }
                _context.Logins.InsertBulk(users);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { return BadRequest("Invalid account import file."); }
            return RedirectToAction("Index");
        }
    }
}
