using System.Net;
using System.Text.Json;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using InvoiceWebApp.Data;
using LiteDB;
using Microsoft.AspNetCore.DataProtection;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Reject(Func<Task> operation, string expected)
{
    try { await operation(); }
    catch (Exception ex) when (ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception($"Expected rejection containing '{expected}'.");
}

using var stream = new MemoryStream();
using var db = new LiteDatabase(stream);
var profiles = db.GetCollection<SmtpProfile>("SmtpProfiles");
var documents = db.GetCollection("SmtpProfiles");
documents.Insert(new BsonDocument { ["_id"] = 10, ["EnableSsl"] = false });
var legacy = profiles.FindById(10);
Check(legacy.DeliveryProvider == SmtpProfile.ProviderSmtp, "Legacy provider must remain SMTP.");
Check(legacy.GetEffectiveEncryptionMethod() == SmtpProfile.EncryptionNone, "Legacy plaintext setting must be preserved.");

var http = new FakeHttp();
var protection = new EphemeralDataProtectionProvider();
var registrations = new GraphAppRegistrationStore(db, protection);
const string clientId = "11111111-1111-1111-1111-111111111111";
registrations.Save(1, clientId, "test-secret");
var version = registrations.Require(1).Version;
registrations.Save(1, clientId, null);
Check(registrations.Require(1).Version == version, "Blank secret must preserve the existing registration.");
registrations.Save(2, "22222222-2222-2222-2222-222222222222", "second-secret");
Check(registrations.Require(1).ClientSecret == "test-secret", "Profiles must have isolated credentials.");
Check(!db.GetCollection("GraphAppRegistrations").FindById(1)["ProtectedRegistration"].AsString.Contains("test-secret"), "App secrets must be encrypted.");
Check(new GraphAppRegistrationStore(db, new EphemeralDataProtectionProvider()).Find(1) is null, "Missing keys must require credential re-entry.");
var graph = new GraphMailService(db, http, registrations, protection);
var router = new EmailSender(new SmtpEmailSender(), graph);
var profile = new SmtpProfile { Id = 1, Name = "Graph", DeliveryProvider = SmtpProfile.ProviderGraph };
profiles.Insert(profile);

await Reject(() => router.SendAsync(new SmtpProfile { DeliveryProvider = "invalid" }, [], [], "", "", false, default), "Unsupported email delivery provider");
await Reject(() => router.SendAsync(new SmtpProfile { EncryptionMethod = SmtpProfile.EncryptionSslTls }, [], [], "", "", false, default), "Implicit SSL/TLS");
await Reject(() => graph.SendAsync(profile, ["to@example.com"], [], "test", "body", false, default), "Connect a Microsoft account");
await graph.ConnectAsync(1, "initial-access", "initial-refresh", default);
Check(graph.GetConnectedMailbox(1) == "sender@example.com", "Connected mailbox must be displayed.");
var stored = db.GetCollection("GraphMailConnections").FindById(1)["ProtectedTokens"].AsString;
Check(!stored.Contains("initial-access") && !stored.Contains("initial-refresh"), "Tokens must be protected at rest.");
var lostKeys = new GraphMailService(db, http, registrations, new EphemeralDataProtectionProvider());
Check(lostKeys.GetConnectedMailbox(1) is null, "Lost protection keys must not expose a connected mailbox.");
await Reject(() => lostKeys.SendAsync(profile, ["to@example.com"], [], "test", "body", false, default), "cannot be decrypted");

await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => router.SendAsync(profile, ["To Person <to@example.com>"], ["cc@example.com"], "test", "<b>body</b>", true, default)));
Check(http.Refreshes == 1, "Concurrent sends must share one token refresh.");
Check(http.Sends == 3, "Expected three Graph sends.");
using (var payload = JsonDocument.Parse(http.LastMessage!))
{
    var message = payload.RootElement.GetProperty("message");
    Check(message.GetProperty("body").GetProperty("contentType").GetString() == "HTML", "HTML must be preserved.");
    Check(message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString() == "to@example.com", "Recipient address must be normalized.");
    Check(message.GetProperty("ccRecipients").GetArrayLength() == 1, "CC must be preserved.");
    Check(!message.TryGetProperty("from", out _), "Graph must send as the connected account.");
    Check(payload.RootElement.GetProperty("saveToSentItems").GetBoolean(), "Save to Sent Items must be enabled.");
}
var rotated = protection.CreateProtector("InvoiceWebApp.GraphMail.Tokens.v1").Unprotect(db.GetCollection("GraphMailConnections").FindById(1)["ProtectedTokens"].AsString);
Check(rotated.Contains("rotated-refresh"), "Rotated refresh token must be persisted.");
http.SendStatus = HttpStatusCode.Forbidden;
await Reject(() => router.SendAsync(profile, ["to@example.com"], [], "", "", false, default), "Mail.Send consent");
Check(http.Sends == 4, "Failed Graph sends must not be retried automatically.");
http.SendStatus = HttpStatusCode.TooManyRequests;
await Reject(() => router.SendAsync(profile, ["to@example.com"], [], "", "", false, default), "throttled");
Check(http.Sends == 5, "Throttled sends must not be retried automatically.");
registrations.Save(1, "33333333-3333-3333-3333-333333333333", "changed-secret");
await Reject(() => router.SendAsync(profile, ["to@example.com"], [], "", "", false, default), "registration changed");
await Reject(() => graph.ConnectAsync(1, "access", "refresh", default, version), "registration changed");
await graph.DisconnectAsync(1, default);
Check(graph.GetConnectedMailbox(1) is null, "Disconnect must remove the connection.");
Check(db.GetCollection("GraphMailConnections").Count() == 0, "Disconnect must remove stored tokens.");
Console.WriteLine("PASS: legacy SMTP, unsupported providers/encryption, Graph routing, protected tokens, missing keys, serialized refresh/rotation, recipients/HTML, no retries, changed registration, disconnect.");

registrations.Save(1, clientId, "test-secret");
await graph.ConnectAsync(1, "initial-access", "initial-refresh", default);
http.SendStatus = HttpStatusCode.Accepted;
using var context = new AppDbContext(db);
var runner = new AlertRunner(context, router);
var rule = new AlertRule
{
    Id = 9001, Name = "Offline test rule", Type = AlertType.ClientSecretExpiration, Enabled = false,
    DaysBeforeExpiration = 30, SmtpProfileId = 1, EmailTemplateId = 9001,
    To = "to@example.com", Cc = "cc@example.com"
};
context.AlertRules.Insert(rule);
context.EmailTemplates.Insert(new EmailTemplate
{
    Id = 9001, Name = "Test template", Subject = "{RuleName}: {Type} / {Days}",
    Body = "{Entity}|{ExpirationDate}|{CompanyName}|{Environment}|{Severity}", IsHtml = true
});
context.CompEnvs.Insert(new CompEnv { Id = 9001, CompanyId = 99991, EnvId = 99992, ClientSecretExpiryDate = DateTime.UtcNow.Date.AddDays(3) });
context.CompEnvs.Insert(new CompEnv { Id = 9002, CompanyId = 99991, EnvId = 99992 });
var logCount = context.AlertSendLogs.Count();
var sendsBeforeTests = http.Sends;
await runner.SendTestAsync(rule.Id, "Sample", null, default);
using (var payload = JsonDocument.Parse(http.LastMessage!))
{
    var message = payload.RootElement.GetProperty("message");
    Check(message.GetProperty("subject").GetString() == "[TEST] Offline test rule: Client/Secret / 30", "Sample must use rule days and test subject prefix.");
    Check(message.GetProperty("body").GetProperty("content").GetString()!.Contains("Sample company / Sample environment"), "Sample values must render.");
    Check(message.GetProperty("toRecipients").GetArrayLength() == 1 && message.GetProperty("ccRecipients").GetArrayLength() == 1, "Test must retain saved recipients.");
}
Check(!runner.GetTestEntities(rule.Type).Any(x => x.Key == "CompEnv:9002"), "Records without expiry must not be selectable.");
await runner.SendTestAsync(rule.Id, "Real", "CompEnv:9001", default);
using (var payload = JsonDocument.Parse(http.LastMessage!))
{
    var message = payload.RootElement.GetProperty("message");
    Check(message.GetProperty("subject").GetString()!.EndsWith("/ 3"), "Real record must use actual days, even when not due and rule disabled.");
    Check(message.GetProperty("body").GetProperty("content").GetString()!.Contains("selected real record"), "Real tests must be clearly marked.");
}
await Reject(() => runner.SendTestAsync(rule.Id, "Real", "CompEnv:missing", default), "available record");
await Reject(() => runner.SendTestAsync(rule.Id, "Invalid", null, default), "sample data");
await Reject(() => runner.SendTestAsync(-1, "Sample", null, default), "not found");
rule.Type = AlertType.CertificateExpiration;
context.AlertRules.Update(rule);
await Reject(() => runner.SendTestAsync(rule.Id, "Real", "CompEnv:9001", default), "available record");
await runner.SendTestAsync(rule.Id, "Sample", null, default);
Check(http.LastMessage!.Contains("Sample certificate"), "Certificate sample must render without a real certificate.");
rule.To = null;
rule.Cc = null;
context.AlertRules.Update(rule);
await Reject(() => runner.SendTestAsync(rule.Id, "Sample", null, default), "recipient");
Check(http.Sends == sendsBeforeTests + 3, "Each successful test must send exactly once; invalid tests must not send.");
Check(context.AlertSendLogs.Count() == logCount, "Tests must not modify scheduled send logs.");
Check(!context.AlertRules.FindById(rule.Id).Enabled, "Tests must not enable the rule.");
Console.WriteLine("PASS: alert sample/real data, disabled/not-due rule, saved recipients, certificate sample, missing/wrong-type record, invalid mode, missing recipients, single sends and unchanged scheduled logs.");

var batchController = new InvoiceWebApp.Controllers.AlertRulesController(context, runner);
foreach (var id in new[] { 9101, 9102, 9103 })
    context.AlertRules.Insert(new AlertRule
    {
        Id = id, Name = "Batch " + id, Type = AlertType.ClientSecretExpiration,
        To = "old@example.com", Cc = "oldcc@example.com", Enabled = true,
        SmtpProfileId = 10, EmailTemplateId = 9001, DaysBeforeExpiration = 7
    });
var batchResult = (Microsoft.AspNetCore.Mvc.RedirectToActionResult)batchController.BatchEdit(new AlertRuleBatchEdit
{
    SelectedIds = [9101, 9102, 9101], ChangeTo = true, To = "new@example.com; other@example.com",
    ChangeCc = true, Cc = " ", ChangeEnabled = true, Enabled = false,
    ChangeSmtpProfile = true, SmtpProfileId = 1, Type = AlertType.ClientSecretExpiration
}, db);
Check(batchResult.RouteValues!.ContainsKey("message"), "Valid batch must succeed.");
foreach (var id in new[] { 9101, 9102 })
{
    var updated = context.AlertRules.FindById(id);
    Check(updated.To == "new@example.com; other@example.com" && updated.Cc is null && !updated.Enabled && updated.SmtpProfileId == 1, "All checked fields must change.");
    Check(updated.EmailTemplateId == 9001 && updated.DaysBeforeExpiration == 7 && updated.Name == "Batch " + id, "Unrelated fields must stay unchanged.");
}
Check(context.AlertRules.FindById(9103).To == "old@example.com" && context.AlertRules.FindById(9103).Enabled, "Unselected rules must stay unchanged.");
batchController.BatchEdit(new AlertRuleBatchEdit { SelectedIds = [9101], ChangeSmtpProfile = true }, db);
Check(context.AlertRules.FindById(9101).SmtpProfileId is null && !context.AlertRules.FindById(9101).Enabled, "Default profile applies without changing unchecked fields.");
foreach (var invalidBatch in new[]
{
    new AlertRuleBatchEdit { ChangeEnabled = true },
    new AlertRuleBatchEdit { SelectedIds = [9101] },
    new AlertRuleBatchEdit { SelectedIds = [9101, -1], ChangeEnabled = true, Enabled = true },
    new AlertRuleBatchEdit { SelectedIds = [9101], ChangeTo = true, To = "not-an-email" },
    new AlertRuleBatchEdit { SelectedIds = [9101], ChangeSmtpProfile = true, SmtpProfileId = -1 }
})
{
    var result = (Microsoft.AspNetCore.Mvc.RedirectToActionResult)batchController.BatchEdit(invalidBatch, db);
    Check(result.RouteValues!.ContainsKey("error"), "Invalid batch must be rejected.");
    Check(!context.AlertRules.FindById(9101).Enabled && context.AlertRules.FindById(9101).SmtpProfileId is null, "Rejected batches must not partially update rules.");
}
Check(context.AlertSendLogs.Count() == logCount, "Batch editing must not alter send history.");
Console.WriteLine("PASS: batch selected-only changes, distinct IDs, recipient clearing, default profile, unchecked-field preservation, invalid selections/recipients/profiles and unchanged logs.");

sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    public int Refreshes;
    public int Sends;
    public string? LastMessage;
    public HttpStatusCode SendStatus = HttpStatusCode.Accepted;
    public HttpClient CreateClient(string name) => new(this, false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
        {
            Interlocked.Increment(ref Refreshes);
            var form = await request.Content!.ReadAsStringAsync(ct);
            if (!form.Contains("grant_type=refresh_token")) throw new Exception("Expected refresh-token grant.");
            if (!form.Contains("client_id=11111111-1111-1111-1111-111111111111") || !form.Contains("client_secret=test-secret"))
                throw new Exception("Refresh must use the selected profile's registration.");
            return Json("{\"access_token\":\"refreshed-access\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}");
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/sendMail"))
        {
            Interlocked.Increment(ref Sends);
            if (request.Headers.Authorization?.Parameter != "refreshed-access") throw new Exception("Expected refreshed bearer token.");
            LastMessage = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(SendStatus);
        }
        if (request.RequestUri.AbsolutePath == "/v1.0/me")
            return Json("{\"mail\":\"sender@example.com\"}");
        throw new Exception("Unexpected HTTP request.");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
