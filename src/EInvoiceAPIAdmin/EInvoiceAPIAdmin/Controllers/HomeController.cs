using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using InvoiceWebApp.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly AppDbContext _context;
        public HomeController(AppDbContext context)
        {
            _context = context;
        }

        // ---------- AUTH ----------
        [AllowAnonymous]
        [HttpGet]
        public IActionResult Login()
        {
            return View(new LoginViewModel());
        }

        [AllowAnonymous]
        [HttpPost]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            var user = ModelState.IsValid ? _context.Logins.FindOne(l => l.u_name == model.u_name) : null;
            if (ModelState.IsValid && AccountSecurity.Verify(user, model.u_pass))
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, user!.u_name),
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(AccountSecurity.StampClaim, user.SecurityStamp)
                };
                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = false
                };

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    authProperties);

                return RedirectToAction("Index");
            }
            ModelState.Remove(nameof(model.u_pass));
            model.u_pass = string.Empty;
            model.ErrorMessage = "Invalid username or password";
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        public IActionResult Index()
        {
            return View();
        }
    }
}
