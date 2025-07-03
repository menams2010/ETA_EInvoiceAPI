using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace InvoiceWebApp.Controllers
{
    [Authorize]
    public class CompanyController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly string DllLibPath = "eps2003csp11.dll";
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LogonUser(String lpszUsername, String lpszDomain, String lpszPassword, int dwLogonType, int dwLogonProvider, out SafeAccessTokenHandle phToken);

        public CompanyController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }
        public IActionResult Index()
        {
            return View(_context.Companies.ToList());
        }

        [HttpGet]
        public IActionResult Certificates()
        {
            var certificates = new List<object>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Retrieve credentials from Settings table
                string domainName = _context.Settings.FirstOrDefault(s => s.SettingName == "ImpDomain")?.SettingValue ?? "";
                string userName = _context.Settings.FirstOrDefault(s => s.SettingName == "ImpUsername")?.SettingValue ?? "";
                string password = _context.Settings.FirstOrDefault(s => s.SettingName == "ImpPassword")?.SettingValue ?? "";

                const int LOGON32_PROVIDER_DEFAULT = 0;
                const int LOGON32_LOGON_INTERACTIVE = 2;

                SafeAccessTokenHandle safeAccessTokenHandle;
                bool returnValue = LogonUser(userName, domainName, password, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out safeAccessTokenHandle);

                if (!returnValue)
                {
                    int ret = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(ret);
                }

                using (safeAccessTokenHandle)
                {
                    WindowsIdentity.RunImpersonated(safeAccessTokenHandle, () =>
                    {
                        X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                        store.Open(OpenFlags.MaxAllowed);

                        foreach (X509Certificate2 cert in store.Certificates)
                        {
                            certificates.Add(new
                            {
                                friendlyName = cert.FriendlyName ?? "",
                                issuer = cert.Issuer,
                                issuerName = cert.IssuerName?.Name ?? "",
                                notBefore = cert.NotBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                notAfter = cert.NotAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                serialNumber = cert.SerialNumber,
                                subject = cert.Subject,
                                subjectName = cert.SubjectName?.Name ?? "",
                                version = cert.Version.ToString()
                            });
                        }
                        store.Close();
                    });
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadOnly);

                    foreach (X509Certificate2 cert in store.Certificates)
                    {
                        certificates.Add(new
                        {
                            friendlyName = cert.FriendlyName ?? "",
                            issuer = cert.Issuer,
                            issuerName = cert.IssuerName?.Name ?? "",
                            notBefore = cert.NotBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            notAfter = cert.NotAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            serialNumber = cert.SerialNumber,
                            subject = cert.Subject,
                            subjectName = cert.SubjectName?.Name ?? "",
                            version = cert.Version.ToString()
                        });
                    }
                    store.Close();
                }
                catch (Exception ex)
                {
                    return Content(JsonSerializer.Serialize(new { error = ex.Message }), "application/json");
                }
            }
            else
            {
                return Content(JsonSerializer.Serialize(new { error = "Unsupported OS" }), "application/json");
            }

            var json = JsonSerializer.Serialize(new { certificates });
            return Content(json, "application/json");
        }

        public IActionResult Tokens()
        {
            var factories = new Pkcs11InteropFactories();
            var tokens = new List<object>();

            string pkcs11LibPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                pkcs11LibPath = "eps2003csp11.dll";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                pkcs11LibPath = "/usr/lib/libeps2003csp11.so"; // Adjust path as needed
            else
                return Content(JsonSerializer.Serialize(new { error = "Unsupported OS" }), "application/json");

            using (IPkcs11Library pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, pkcs11LibPath, AppType.MultiThreaded))
            {
                foreach (ISlot slot in pkcs11Library.GetSlotList(SlotsType.WithTokenPresent))
                {
                    var slotInfo = slot.GetSlotInfo();
                    var tokenInfo = slot.GetTokenInfo();
                    tokens.Add(new
                    {
                        slotId = slotInfo.SlotId,
                        slotLabel = tokenInfo.Label?.Trim(),
                        SerialNumber = tokenInfo.SerialNumber?.Trim()
                    });
                }
            }

            var json = JsonSerializer.Serialize(new { tokens });
            return Content(json, "application/json");
        }

        //[HttpGet]
        //public async Task<IActionResult> Certificate()
        //{
        //    var apiUrl = _configuration["CertificateApiUrl"];
        //    using var httpClient = new HttpClient();
        //    var response = await httpClient.GetAsync(apiUrl);
        //    var content = await response.Content.ReadAsStringAsync();
        //    return Content(content, "application/json");
        //}

        [HttpPost]
        public IActionResult Create(Company company)
        {
            if (string.IsNullOrEmpty(company.UseXMLTOJSON))
            {
                company.UseXMLTOJSON = "false";
            }
            else
            {
                company.UseXMLTOJSON = (company.UseXMLTOJSON == "true") ? "true" : "false";
            }

            ModelState.Remove(nameof(company.UseXMLTOJSON));
            TryValidateModel(company);

            if (ModelState.IsValid)
            {
                _context.Companies.Add(company);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Edit(Company company)
        {
            if (string.IsNullOrEmpty(company.UseXMLTOJSON))
            {
                company.UseXMLTOJSON = "false";
            }
            else
            {
                company.UseXMLTOJSON = (company.UseXMLTOJSON == "true") ? "true" : "false";
            }

            ModelState.Remove(nameof(company.UseXMLTOJSON));
            TryValidateModel(company);

            if (ModelState.IsValid)
            {
                _context.Companies.Update(company);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult Delete(int id)
        {
            var company = _context.Companies.Find(id);
            if (company != null)
            {
                _context.Companies.Remove(company);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }

        public IActionResult Export()
        {
            var data = _context.Companies.ToList();
            var json = JsonSerializer.Serialize(data);
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", "Companies.json");
        }

        [HttpPost]
        public IActionResult Import(IFormFile jsonFile)
        {
            if (jsonFile != null && jsonFile.Length > 0)
            {
                using var reader = new StreamReader(jsonFile.OpenReadStream());
                var content = reader.ReadToEnd();
                var items = JsonSerializer.Deserialize<List<Company>>(content);
                if (items != null)
                {
                    _context.Companies.AddRange(items);
                    _context.SaveChanges();
                }
            }
            return RedirectToAction("Index");
        }
        [HttpGet]
        [SupportedOSPlatform("linux")]
        public IActionResult CertificatesLinux()
        {
            // Only supports reading from the user's certificate store in Linux (if available)
            // No impersonation or domain logon, just reads from the default location
            var certificates = new List<object>();
            try
            {
                X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);

                foreach (X509Certificate2 cert in store.Certificates)
                {
                    certificates.Add(new
                    {
                        friendlyName = cert.FriendlyName ?? "",
                        issuer = cert.Issuer,
                        issuerName = cert.IssuerName?.Name ?? "",
                        notBefore = cert.NotBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        notAfter = cert.NotAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        serialNumber = cert.SerialNumber,
                        subject = cert.Subject,
                        subjectName = cert.SubjectName?.Name ?? "",
                        version = cert.Version.ToString()
                    });
                }
                store.Close();
            }
            catch (Exception ex)
            {
                return Content(JsonSerializer.Serialize(new { error = ex.Message }), "application/json");
            }

            var json = JsonSerializer.Serialize(new { certificates });
            return Content(json, "application/json");
        }
    }
}
