using InvoiceWebApp.Data;
using InvoiceWebApp.Models;

namespace InvoiceWebApp.Services;

public class AlertRunner(AppDbContext ctx, EmailSender sender)
{
    private readonly AppDbContext _ctx = ctx;
    private readonly EmailSender _sender = sender;

    public IReadOnlyList<AlertTestEntity> GetTestEntities(AlertType type)
    {
        if (type == AlertType.CertificateExpiration)
            return GetCertificatesWithExpiry()
                .Select(x => new AlertTestEntity(x.EntityKey, x.DisplayName, x.ExpiryDateUtc.Date, string.Empty, string.Empty))
                .OrderBy(x => x.DisplayName).ToList();

        if (type != AlertType.ClientSecretExpiration)
            throw new InvalidOperationException("Unsupported alert type.");

        var companies = _ctx.Companies.FindAll().ToDictionary(x => (long)x.Id);
        var environments = _ctx.EInvoiceEnvs.FindAll().ToDictionary(x => (long)x.Id);
        return _ctx.CompEnvs.Find(x => x.ClientSecretExpiryDate != null).Select(x =>
        {
            var company = companies.TryGetValue(x.CompanyId, out var c)
                ? $"{c.CompanyCode} - {c.CompanyName}" : x.CompanyId.ToString();
            var environment = environments.TryGetValue(x.EnvId, out var e) ? e.EnvName : x.EnvId.ToString();
            return new AlertTestEntity($"CompEnv:{x.Id}", $"{company} / {environment}",
                x.ClientSecretExpiryDate!.Value.Date, company, environment);
        }).OrderBy(x => x.DisplayName).ToList();
    }

    public async Task SendTestAsync(int ruleId, string dataSource, string? entityKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rule = _ctx.AlertRules.FindById(ruleId)
            ?? throw new InvalidOperationException("Alert rule not found.");
        if (rule.Type is not (AlertType.CertificateExpiration or AlertType.ClientSecretExpiration))
            throw new InvalidOperationException("Unsupported alert type.");
        if (dataSource is not ("Sample" or "Real"))
            throw new InvalidOperationException("Select sample data or one real record.");

        var profile = (rule.SmtpProfileId is int profileId ? _ctx.SmtpProfiles.FindById(profileId) : null)
            ?? _ctx.SmtpProfiles.FindOne(x => x.IsDefault) ?? _ctx.SmtpProfiles.FindAll().FirstOrDefault()
            ?? throw new InvalidOperationException("No mail profile is configured for this rule.");
        if (profile.DeliveryProvider == SmtpProfile.ProviderSmtp && string.IsNullOrWhiteSpace(profile.Host))
            throw new InvalidOperationException("Configure the SMTP host on the rule's mail profile first.");
        var template = (rule.EmailTemplateId is int templateId ? _ctx.EmailTemplates.FindById(templateId) : null)
            ?? _ctx.EmailTemplates.FindAll().FirstOrDefault()
            ?? throw new InvalidOperationException("No email template is configured for this rule.");
        var to = AlertTemplateRenderer.SplitEmails(rule.To).ToArray();
        var cc = AlertTemplateRenderer.SplitEmails(rule.Cc).ToArray();
        if (to.Length == 0 && cc.Length == 0)
            throw new InvalidOperationException("Configure at least one To or CC recipient on this rule first.");

        var today = DateTime.UtcNow.Date;
        AlertTestEntity entity;
        if (dataSource == "Real")
        {
            entity = GetTestEntities(rule.Type).FirstOrDefault(x => x.Key == entityKey)
                ?? throw new InvalidOperationException("Select an available record for this alert type. The record may have been removed or no longer have an expiration date.");
        }
        else
        {
            DateTime expiration;
            try { expiration = today.AddDays(rule.DaysBeforeExpiration); }
            catch (ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException("The rule's days-before-expiration value is outside the supported date range.");
            }
            var isClient = rule.Type == AlertType.ClientSecretExpiration;
            entity = new AlertTestEntity("Sample", isClient ? "Sample company / Sample environment" : "Sample certificate",
                expiration, isClient ? "Sample company" : string.Empty, isClient ? "Sample environment" : string.Empty);
        }

        var days = (entity.ExpirationDate.Date - today).Days;
        var values = new Dictionary<string, string?>
        {
            ["Type"] = rule.Type == AlertType.ClientSecretExpiration ? "Client/Secret" : "Certificate",
            ["Days"] = days.ToString(),
            ["ExpirationDate"] = entity.ExpirationDate.ToString("yyyy-MM-dd"),
            ["Entity"] = entity.DisplayName,
            ["CompanyName"] = entity.CompanyName,
            ["Environment"] = entity.Environment,
            ["RuleName"] = rule.Name,
            ["Severity"] = days <= 0 ? "Critical" : days <= 7 ? "Warning" : "Info"
        };
        var notice = dataSource == "Sample"
            ? "TEST ALERT — sample data. This is not a scheduled expiration notification."
            : "TEST ALERT — selected real record. This is not a scheduled expiration notification.";
        var body = AlertTemplateRenderer.Render(template.Body, values);
        body = template.IsHtml ? $"<p><strong>{notice}</strong></p>{body}" : $"{notice}\n\n{body}";
        await _sender.SendAsync(profile, to, cc, "[TEST] " + AlertTemplateRenderer.Render(template.Subject, values),
            body, template.IsHtml, ct);
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        // Client/secret alerts + certificate alerts are evaluated.
        var rules = _ctx.AlertRules.Find(r => r.Enabled && r.Type == AlertType.ClientSecretExpiration).ToList();
        var certRules = _ctx.AlertRules.Find(r => r.Enabled && r.Type == AlertType.CertificateExpiration).ToList();
        if (rules.Count == 0 && certRules.Count == 0) return;

        var defaultSmtp = _ctx.SmtpProfiles.FindOne(x => x.IsDefault) ?? _ctx.SmtpProfiles.FindAll().FirstOrDefault();
        var templates = _ctx.EmailTemplates.FindAll().ToDictionary(x => x.Id);

        var envs = _ctx.EInvoiceEnvs.FindAll().ToDictionary(x => (long)x.Id);
        var companies = _ctx.Companies.FindAll().ToDictionary(x => (long)x.Id);

        var today = DateTime.UtcNow.Date;
        var sentDate = today.ToString("yyyy-MM-dd");

        foreach (var compEnv in _ctx.CompEnvs.Find(x => x.ClientSecretExpiryDate != null))
        {
            ct.ThrowIfCancellationRequested();
            var entityKey = $"CompEnv:{compEnv.Id}";
            var expiry = compEnv.ClientSecretExpiryDate!.Value.Date;
            var days = (expiry - today).Days;

            foreach (var rule in rules.Where(r => r.DaysBeforeExpiration == days))
            {
                if (AlreadySent(rule.Id, entityKey, sentDate))
                    continue;

                var smtp = rule.SmtpProfileId is null
                    ? defaultSmtp
                    : _ctx.SmtpProfiles.FindById(rule.SmtpProfileId.Value) ?? defaultSmtp;

                if (smtp is null || (smtp.DeliveryProvider == SmtpProfile.ProviderSmtp && string.IsNullOrWhiteSpace(smtp.Host)))
                    continue; // not configured

                EmailTemplate? template = null;
                if (rule.EmailTemplateId is not null && templates.TryGetValue(rule.EmailTemplateId.Value, out var t))
                    template = t;
                template ??= templates.Values.FirstOrDefault();
                if (template is null) continue;

                var companyName = companies.TryGetValue(compEnv.CompanyId, out var c)
                    ? $"{c.CompanyCode} - {c.CompanyName}"
                    : compEnv.CompanyId.ToString();

                var envName = envs.TryGetValue(compEnv.EnvId, out var e)
                    ? e.EnvName
                    : compEnv.EnvId.ToString();

                var entity = $"{companyName} / {envName}";
                var severity = days <= 0 ? "Critical" : days <= 7 ? "Warning" : "Info";

                var values = new Dictionary<string, string?>
                {
                    ["Type"] = "Client/Secret",
                    ["Days"] = days.ToString(),
                    ["ExpirationDate"] = expiry.ToString("yyyy-MM-dd"),
                    ["Entity"] = entity,
                    ["CompanyName"] = companyName,
                    ["Environment"] = envName,
                    ["RuleName"] = rule.Name,
                    ["Severity"] = severity
                };

                var subject = AlertTemplateRenderer.Render(template.Subject, values);
                var body = AlertTemplateRenderer.Render(template.Body, values);

                var to = AlertTemplateRenderer.SplitEmails(rule.To);
                var cc = AlertTemplateRenderer.SplitEmails(rule.Cc);
                if (!to.Any() && !cc.Any())
                    continue;

                await _sender.SendAsync(smtp, to, cc, subject, body, template.IsHtml, ct);
                LogSent(rule.Id, rule.Type, entityKey, sentDate);
            }
        }

        // Certificate alerts
        foreach (var certHit in GetCertificatesWithExpiry())
        {
            ct.ThrowIfCancellationRequested();
            var days = (certHit.ExpiryDateUtc.Date - today).Days;
            foreach (var rule in certRules.Where(r => r.DaysBeforeExpiration == days))
            {
                if (AlreadySent(rule.Id, certHit.EntityKey, sentDate))
                    continue;

                var smtp = rule.SmtpProfileId is null
                    ? defaultSmtp
                    : _ctx.SmtpProfiles.FindById(rule.SmtpProfileId.Value) ?? defaultSmtp;

                if (smtp is null || (smtp.DeliveryProvider == SmtpProfile.ProviderSmtp && string.IsNullOrWhiteSpace(smtp.Host)))
                    continue;

                EmailTemplate? template = null;
                if (rule.EmailTemplateId is not null && templates.TryGetValue(rule.EmailTemplateId.Value, out var t))
                    template = t;
                template ??= templates.Values.FirstOrDefault();
                if (template is null) continue;

                var certSeverity = days <= 0 ? "Critical" : days <= 7 ? "Warning" : "Info";
                var values = new Dictionary<string, string?>
                {
                    ["Type"] = "Certificate",
                    ["Days"] = days.ToString(),
                    ["ExpirationDate"] = certHit.ExpiryDateUtc.ToString("yyyy-MM-dd"),
                    ["Entity"] = certHit.DisplayName,
                    ["CompanyName"] = string.Empty,
                    ["Environment"] = string.Empty,
                    ["RuleName"] = rule.Name,
                    ["Severity"] = certSeverity
                };

                var subject = AlertTemplateRenderer.Render(template.Subject, values);
                var body = AlertTemplateRenderer.Render(template.Body, values);

                var to = AlertTemplateRenderer.SplitEmails(rule.To);
                var cc = AlertTemplateRenderer.SplitEmails(rule.Cc);
                if (!to.Any() && !cc.Any())
                    continue;

                await _sender.SendAsync(smtp, to, cc, subject, body, template.IsHtml, ct);
                LogSent(rule.Id, rule.Type, certHit.EntityKey, sentDate);
            }
        }
    }

    private bool AlreadySent(int ruleId, string entityKey, string sentDate)
    {
        return _ctx.AlertSendLogs.Exists(x => x.RuleId == ruleId && x.EntityKey == entityKey && x.SentDate == sentDate);
    }

    private void LogSent(int ruleId, AlertType type, string entityKey, string sentDate)
    {
        var nextId = _ctx.AlertSendLogs.Query().OrderByDescending(x => x.Id).Limit(1).ToList().FirstOrDefault()?.Id + 1 ?? 1;
        _ctx.AlertSendLogs.Insert(new AlertSendLog
        {
            Id = nextId,
            RuleId = ruleId,
            Type = type,
            EntityKey = entityKey,
            SentDate = sentDate,
            SentAtUtc = DateTime.UtcNow
        });
    }

    private sealed record CertExpiryHit(string EntityKey, string DisplayName, DateTime ExpiryDateUtc);

    private IEnumerable<CertExpiryHit> GetCertificatesWithExpiry()
    {
        // Evaluate current-user "My" store. (This matches the existing certificate UI behavior on Windows/Linux.)
        // If the app uses impersonation for certificate access, this can be extended to follow that setting.
        var results = new List<CertExpiryHit>();
        try
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);

            foreach (var cert in store.Certificates)
            {
                var thumb = cert.Thumbprint ?? cert.SerialNumber ?? Guid.NewGuid().ToString("N");
                var entityKey = $"Cert:{thumb}";
                var display = string.IsNullOrWhiteSpace(cert.FriendlyName) ? (cert.Subject ?? thumb) : cert.FriendlyName;
                results.Add(new CertExpiryHit(entityKey, display, cert.NotAfter.ToUniversalTime()));
            }
        }
        catch
        {
            return Array.Empty<CertExpiryHit>();
        }

        return results;
    }
}
