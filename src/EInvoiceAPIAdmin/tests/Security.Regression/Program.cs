using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceWebApp.Controllers;
using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using LiteDB;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

if (args.Length > 0 && args[0] == "live")
{
    var address = new Uri(args[1]);
    Check(address.IsLoopback && address.Scheme == "https", "Live probes require loopback HTTPS.");
    var name = Environment.GetEnvironmentVariable("SECURITY_TEST_USERNAME") ?? throw new Exception("Missing test username.");
    var password = Environment.GetEnvironmentVariable("SECURITY_TEST_PASSWORD") ?? throw new Exception("Missing test password.");
    var certificatePin = Environment.GetEnvironmentVariable("SECURITY_TEST_CERTIFICATE_THUMBPRINT");
    HttpClientHandler CreateHandler()
    {
        var result = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        if (!string.IsNullOrEmpty(certificatePin))
            result.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                request.RequestUri?.IsLoopback == true && certificate != null
                && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime()
                && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime()
                && (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                && string.Equals(certificate.Thumbprint, certificatePin, StringComparison.OrdinalIgnoreCase);
        return result;
    }
    using var handler = CreateHandler();
    using var client = new HttpClient(handler) { BaseAddress = address };
    if (args.Length > 2)
    {
        var httpAddress = new Uri(args[2]);
        Check(httpAddress.IsLoopback && httpAddress.Scheme == "http", "Redirect probes require loopback HTTP.");
        var redirect = await client.GetAsync(httpAddress);
        Check(redirect.StatusCode == HttpStatusCode.TemporaryRedirect && redirect.Headers.Location?.Scheme == "https"
            && redirect.Headers.Location.Port == address.Port, "HTTP must redirect to the configured HTTPS endpoint.");
    }
    async Task<string> Token(string path)
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }
    async Task<HttpResponseMessage> Post(string path, Dictionary<string, string> data) => await client.PostAsync(path, new FormUrlEncodedContent(data));
    var anonymous = await client.GetAsync("/Login/Index");
    Check(anonymous.StatusCode == HttpStatusCode.Redirect, "Anonymous accounts must be denied.");
    var noToken = await Post("/Home/Login", new() { ["u_name"] = name, ["u_pass"] = password });
    Check(noToken.StatusCode == HttpStatusCode.BadRequest, "Login needs CSRF validation.");
    var token = await Token("/Home/Login");
    var login = await Post("/Home/Login", new() { ["u_name"] = name, ["u_pass"] = password, ["__RequestVerificationToken"] = token });
    Check(login.StatusCode == HttpStatusCode.Redirect, "Valid login must succeed.");
    var cookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
    Check(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase), "Secure HttpOnly cookie required.");
    var page = await client.GetAsync("/Login/Index");
    var body = await page.Content.ReadAsStringAsync();
    Check(page.StatusCode == HttpStatusCode.OK && !body.Contains(password) && !body.Contains("PasswordHash"), "Account page must not reveal credentials.");
    Check(page.Headers.Contains("Content-Security-Policy") && page.Headers.Contains("X-Frame-Options") && page.Headers.Contains("X-Content-Type-Options"), "Security headers required.");
    Check(body.Contains("nonce=\"") && body.Contains("data-confirm=") && !body.Contains("onclick="), "Razor scripts and confirmations must use CSP-safe markup.");
    var nonce = WebUtility.HtmlDecode(Regex.Match(body, "<script[^>]*nonce=\"([^\"]+)\"").Groups[1].Value);
    Check(nonce.Length > 0 && page.Headers.GetValues("Content-Security-Policy").Single().Contains("'nonce-" + nonce + "'"), "Rendered script nonce must match the CSP header.");
    foreach (var path in new[] { "/Settings/Create", "/Login/Create", "/AlertRules/BatchEdit", "/Home/Logout" })
        Check((await Post(path, new())).StatusCode == HttpStatusCode.BadRequest, $"Missing CSRF token accepted: {path}");
    Check((await client.GetAsync("/Home/Logout")).StatusCode == HttpStatusCode.MethodNotAllowed, "GET logout must not mutate sessions.");
    foreach (var path in new[] { "/Company/Index", "/Settings/Index", "/CompEnv/Index", "/EInvoiceEnv/Index", "/SmtpProfiles/Index", "/AlertRules/Index", "/EmailTemplates/Create", "/Maintenance/Index" })
        Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, $"Page failed under hardening: {path}");
    token = await Token("/Login/Index");
    var backup = await Post("/Maintenance/Backup", new() { ["__RequestVerificationToken"] = token });
    Check(backup.StatusCode == HttpStatusCode.OK && backup.Content.Headers.ContentType?.MediaType == "application/octet-stream", "Encrypted backup download failed.");
    using (var restoreForm = new MultipartFormDataContent())
    {
        restoreForm.Add(new StringContent(token), "__RequestVerificationToken");
        restoreForm.Add(new StringContent("Settings"), "tables");
        restoreForm.Add(new ByteArrayContent(await backup.Content.ReadAsByteArrayAsync()), "backupFile", "isolated-backup.db");
        var restore = await client.PostAsync("/Maintenance/Restore", restoreForm);
        Check(restore.StatusCode == HttpStatusCode.Redirect && restore.Headers.Location?.ToString().Contains("message=") == true,
            "Encrypted backup restore round trip failed.");
    }
    Check((await Post("/Login/Create", new() { ["u_name"] = "security-live-user", ["Password"] = password, ["__RequestVerificationToken"] = token })).StatusCode == HttpStatusCode.Redirect, "Token-protected create failed.");
    var exported = await client.GetStringAsync("/Login/Export");
    Check(!exported.Contains("Password") && !exported.Contains("SecurityStamp") && !exported.Contains(password), "Export leaked credentials.");
    using var users = JsonDocument.Parse(exported);
    var id = users.RootElement.EnumerateArray().Single(x => x.GetProperty("u_name").GetString() == "security-live-user").GetProperty("Id").GetInt32();
    using var secondHandler = CreateHandler();
    using var second = new HttpClient(secondHandler) { BaseAddress = address };
    var secondHtml = await second.GetStringAsync("/Home/Login");
    var secondToken = WebUtility.HtmlDecode(Regex.Match(secondHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    var secondLogin = await second.PostAsync("/Home/Login", new FormUrlEncodedContent(new Dictionary<string, string> { ["u_name"] = "security-live-user", ["u_pass"] = password, ["__RequestVerificationToken"] = secondToken }));
    Check(secondLogin.StatusCode == HttpStatusCode.Redirect, "Synthetic user login failed.");
    Check((await Post("/Login/Delete", new() { ["id"] = id.ToString(), ["__RequestVerificationToken"] = token })).StatusCode == HttpStatusCode.Redirect, "Synthetic delete failed.");
    Check((await second.GetAsync("/Settings/Export")).StatusCode == HttpStatusCode.Redirect, "Deleted account cookie was accepted.");
    var throttled = false;
    for (var i = 0; i < 6; i++)
    {
        var attempt = await Post("/Home/Login", new() { ["u_name"] = "invalid-security-user", ["u_pass"] = "invalid-password", ["__RequestVerificationToken"] = token });
        throttled |= attempt.StatusCode == HttpStatusCode.TooManyRequests;
    }
    Check(throttled, "Login rate limit did not engage.");
    Console.WriteLine("PASS: live HTTPS, anonymous denial, write-only credentials, CSRF, nonce markup, page rendering, account deletion revocation, throttling.");
    return;
}

using var stream = new MemoryStream();
using var db = new LiteDatabase(stream);
var raw = db.GetCollection("Logins");
raw.Insert(new BsonDocument { ["_id"] = 1, ["u_name"] = "legacy-user", ["u_pass"] = "unchanged-legacy-password" });
using var context = new AppDbContext(db);
var user = context.Logins.FindById(1);
Check(!raw.FindById(1).ContainsKey("u_pass"), "Plaintext must be removed.");
Check(AccountSecurity.Verify(user, "unchanged-legacy-password"), "Existing password must survive migration.");
Check(!AccountSecurity.Verify(user, "incorrect") && !AccountSecurity.Verify(null, "incorrect"), "Invalid credentials must fail.");
var stamp = user.SecurityStamp;
AccountSecurity.Migrate(db);
Check(context.Logins.FindById(1).SecurityStamp == stamp, "Migration must be idempotent.");
var controller = new LoginController(context);
var export = (FileContentResult)controller.Export();
var json = System.Text.Encoding.UTF8.GetString(export.FileContents);
Check(!json.Contains("PasswordHash") && !json.Contains("u_pass") && !json.Contains(stamp), "Export must omit credentials.");
controller.Edit(new AccountInput { Id = 1, u_name = user.u_name });
Check(AccountSecurity.Verify(context.Logins.FindById(1), "unchanged-legacy-password"), "Blank edit must preserve password.");
controller.Edit(new AccountInput { Id = 1, u_name = user.u_name, Password = "replacement-password" });
Check(context.Logins.FindById(1).SecurityStamp != stamp, "Password changes must revoke sessions.");
var restored = raw.FindById(1);
var oldStamp = restored["SecurityStamp"].AsString;
AccountSecurity.NormalizeDocument(restored, true);
Check(restored["SecurityStamp"].AsString != oldStamp, "Restore must renew stamps.");
var serviceCollection = new ServiceCollection();
serviceCollection.AddLogging();
serviceCollection.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
using var services = serviceCollection.BuildServiceProvider();
var http = new DefaultHttpContext { RequestServices = services };
var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Name, user.u_name), new Claim(AccountSecurity.StampClaim, stamp) }, "test"));
var cookieContext = new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
await new AccountCookieEvents(context).ValidatePrincipal(cookieContext);
Check(cookieContext.Principal == null, "Stale stamp must reject principal.");
var directory = Path.Combine(AppContext.BaseDirectory, "security-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "test.db");
    var oldKey = Guid.NewGuid().ToString("N");
    var newKey = Guid.NewGuid().ToString("N");
    using (var oldDb = new LiteDatabase(new ConnectionString { Filename = path, Password = oldKey }))
        oldDb.GetCollection("Proof").Insert(new BsonDocument { ["value"] = "preserved" });
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = path, ["Database:Password"] = newKey, ["Database:PreviousPassword"] = oldKey }).Build();
    var security = new DatabaseSecurity(configuration);
    security.RotateKey(configuration);
    using (var rotated = security.Open())
    {
        Check(rotated.GetCollection("Proof").Count() == 1, "Rotation lost data.");
        rotated.GetCollection("Proof").EnsureIndex("value", true);
        var download = Path.Combine(directory, "download.db");
        File.WriteAllBytes(download, security.CreateBackup(rotated));
        using var downloaded = security.OpenBackup(download, null);
        Check(downloaded.GetCollection("Proof").FindAll().Single()["value"].AsString == "preserved", "Logical backup lost data.");
        using var indexes = downloaded.Execute("SELECT $ FROM $indexes WHERE name = 'value'");
        Check(indexes.Read() && indexes.Current["unique"].AsBoolean, "Logical backup lost its unique index.");
    }
    using (var backup = new LiteDatabase(new ConnectionString { Filename = path + ".pre-security-backup", Password = oldKey }))
        Check(backup.GetCollection("Proof").Count() == 1, "Original backup must remain recoverable.");
    var rejected = false;
    try { using var wrong = new LiteDatabase(new ConnectionString { Filename = path, Password = oldKey }); _ = wrong.GetCollectionNames().ToList(); }
    catch (LiteException) { rejected = true; }
    Check(rejected, "Old key must not open the rotated database.");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("PASS: legacy hashing, unchanged passwords, safe exports, password updates, restore stamps, stale-cookie rejection, backup-first key rotation.");
