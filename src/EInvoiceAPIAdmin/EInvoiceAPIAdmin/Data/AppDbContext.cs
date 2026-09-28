using InvoiceWebApp.Models;
using LiteDB;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InvoiceWebApp.Services;

namespace InvoiceWebApp.Data
{
    public class AppDbContext : IDisposable
    {
        private readonly LiteDatabase _db;
        private readonly bool _ownsDb;

        public ILiteCollection<Company> Companies => _db.GetCollection<Company>("Companies");
        public ILiteCollection<CompEnv> CompEnvs => _db.GetCollection<CompEnv>("CompEnvs");
        public ILiteCollection<EInvoiceEnv> EInvoiceEnvs => _db.GetCollection<EInvoiceEnv>("EInvoiceEnvs");
        public ILiteCollection<Settings> Settings => _db.GetCollection<Settings>("Settings");
        public ILiteCollection<Login> Logins => _db.GetCollection<Login>("Logins");

        public ILiteCollection<AlertRule> AlertRules => _db.GetCollection<AlertRule>("AlertRules");
        public ILiteCollection<SmtpProfile> SmtpProfiles => _db.GetCollection<SmtpProfile>("SmtpProfiles");
        public ILiteCollection<EmailTemplate> EmailTemplates => _db.GetCollection<EmailTemplate>("EmailTemplates");
        public ILiteCollection<AlertSendLog> AlertSendLogs => _db.GetCollection<AlertSendLog>("AlertSendLogs");
        public ILiteCollection<AuditLog> AuditLogs => _db.GetCollection<AuditLog>("AuditLogs");

        public AppDbContext(string databasePath)
        {
            _db = new LiteDatabase(databasePath);
            _ownsDb = true;
            try { Initialize(); }
            catch { _db.Dispose(); throw; }
        }

        public AppDbContext(LiteDatabase db)
        {
            _db = db;
            _ownsDb = false;
            Initialize();
        }

        private void Initialize()
        {
            ConfigureMapper();
            if (!_db.BeginTrans()) throw new InvalidOperationException("Schema initialization requires its own transaction.");
            try
            {
                MigrateLegacyCollections();
                EnsureSchemaAndSeed();
                _db.Commit();
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }

        private static void ConfigureMapper()
        {
            var m = BsonMapper.Global;
            // Ensure Id as primary key; default name "Id" is detected automatically.
            // Additional configuration can go here if needed.
        }

        internal static readonly IReadOnlyDictionary<string, string> LegacyCollectionNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tb_Company"] = "Companies", ["Company"] = "Companies",
                ["tb_CompEnv"] = "CompEnvs", ["CompEnv"] = "CompEnvs",
                ["tb_eInvoiceEnv"] = "EInvoiceEnvs", ["eInvoiceEnv"] = "EInvoiceEnvs",
                ["tb_Settings"] = "Settings", ["tb_Login"] = "Logins", ["Login"] = "Logins"
            };

        internal static string CurrentCollectionName(string name) =>
            LegacyCollectionNames.TryGetValue(name, out var current) ? current : name;

        private void MigrateLegacyCollections()
        {
            var names = _db.GetCollectionNames().ToList();
            foreach (var group in names.GroupBy(CurrentCollectionName, StringComparer.OrdinalIgnoreCase))
            {
                var populated = group.Where(name => _db.GetCollection(name).LongCount() > 0).ToList();
                if (populated.Count > 1)
                    throw new InvalidOperationException($"Conflicting legacy/current collections for {group.Key}. Reconcile them on a copy before migration.");
                var source = populated.FirstOrDefault() ?? group.First();
                foreach (var empty in group.Where(name => name != source)) _db.DropCollection(empty);
                if (!string.Equals(source, group.Key, StringComparison.OrdinalIgnoreCase))
                    _db.RenameCollection(source, group.Key);
            }
            AccountSecurity.Migrate(_db);
        }

        private void EnsureSchemaAndSeed()
        {
            // Ensure indexes on Id
            Companies.EnsureIndex(x => x.Id, true);
            CompEnvs.EnsureIndex(x => x.Id, true);
            EInvoiceEnvs.EnsureIndex(x => x.Id, true);
            Settings.EnsureIndex(x => x.Id, true);
            Logins.EnsureIndex(x => x.Id, true);

            AlertRules.EnsureIndex(x => x.Id, true);
            SmtpProfiles.EnsureIndex(x => x.Id, true);
            EmailTemplates.EnsureIndex(x => x.Id, true);
            AlertSendLogs.EnsureIndex(x => x.Id, true);
            AlertSendLogs.EnsureIndex(x => x.RuleId);
            AlertSendLogs.EnsureIndex(x => x.EntityKey);
            AlertSendLogs.EnsureIndex(x => x.SentDate);

            AuditLogs.EnsureIndex(x => x.Id, true);
            AuditLogs.EnsureIndex(x => x.TimestampUtc);
            AuditLogs.EnsureIndex(x => x.Action);

            // Index for client/secret expiration date alerts
            CompEnvs.EnsureIndex(x => x.ClientSecretExpiryDate);

            MigrateLegacyAlertsConfigFromSettings();

            if (EInvoiceEnvs.Count() == 0)
            {
                EInvoiceEnvs.Insert(new EInvoiceEnv { Id = 1, EnvName = "PreProd", EnvTokenUrl = "https://id.preprod.eta.gov.eg/connect/token", EnvInvoicingUrl = "https://api.preprod.invoicing.eta.gov.eg/api/v1/" });
                EInvoiceEnvs.Insert(new EInvoiceEnv { Id = 2, EnvName = "Prod", EnvTokenUrl = "https://id.eta.gov.eg/connect/token", EnvInvoicingUrl = "https://api.invoicing.eta.gov.eg/api/v1/" });
            }
            if (Settings.Count() == 0)
            {
                Settings.Insert(new Settings { Id = 1, SettingName = "DBVersion", SettingValue = "2026-02-08 00:00:00" });
                Settings.Insert(new Settings { Id = 2, SettingName = "UseImpersonation", SettingValue = "false" });
                Settings.Insert(new Settings { Id = 3, SettingName = "ImpDomain", SettingValue = null });
                Settings.Insert(new Settings { Id = 4, SettingName = "ImpUsername", SettingValue = null });
                Settings.Insert(new Settings { Id = 5, SettingName = "ImpPassword", SettingValue = null });
                Settings.Insert(new Settings { Id = 6, SettingName = "DateTimeFormat", SettingValue = "yyyy-MM-dd HH:mm:ss" });
                Settings.Insert(new Settings { Id = 7, SettingName = "CertCacheLastRefresh", SettingValue = null });
            }

            if (EmailTemplates.Count() == 0)
            {
                EmailTemplates.Insert(new EmailTemplate
                {
                    Id = 1,
                    Name = "Default Expiration",
                    Subject = "[Alert] {Type} expires in {Days} day(s)",
                    Body = "Hello,\n\nThis is an automated alert: {Type} will expire in {Days} day(s) (on {ExpirationDate}).\n\nEntity: {Entity}\n\nRegards",
                    IsHtml = false
                });
            }

            if (SmtpProfiles.Count() == 0)
            {
                SmtpProfiles.Insert(new SmtpProfile
                {
                    Id = 1,
                    Name = "Default",
                    IsDefault = true,
                    Host = "",
                    Port = 587,
                    EnableSsl = true
                });
            }

            if (AlertRules.Count() == 0)
            {
                // Default levels: month, 15 days, week, 3, 2, 1, today
                var levels = new[] { 30, 15, 7, 3, 2, 1, 0 };
                var id = 1;
                foreach (var d in levels)
                {
                    AlertRules.Insert(new AlertRule
                    {
                        Id = id++,
                        Name = $"Certificate expiration - {d} day(s)",
                        Type = AlertType.CertificateExpiration,
                        Enabled = true,
                        DaysBeforeExpiration = d,
                        SmtpProfileId = 1,
                        EmailTemplateId = 1
                    });
                }

                id = 100;
                foreach (var d in levels)
                {
                    AlertRules.Insert(new AlertRule
                    {
                        Id = id++,
                        Name = $"Client/Secret expiration - {d} day(s)",
                        Type = AlertType.ClientSecretExpiration,
                        Enabled = true,
                        DaysBeforeExpiration = d,
                        SmtpProfileId = 1,
                        EmailTemplateId = 1
                    });
                }
            }
        }

        public void Dispose()
        {
            if (_ownsDb)
            {
                _db?.Dispose();
            }
        }

        private void MigrateLegacyAlertsConfigFromSettings()
        {
            // Migration from prior JSON-based key/value storage (Settings row with SettingName = "Alerts.Config")
            // Only run if the new collections are empty to avoid overwriting.
            if (AlertRules.Count() > 0 || SmtpProfiles.Count() > 0 || EmailTemplates.Count() > 0)
                return;

            var raw = Settings.FindOne(x => x.SettingName == "Alerts.Config")?.SettingValue;
            if (string.IsNullOrWhiteSpace(raw))
                return;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                // SMTP (single) -> default profile
                if (root.TryGetProperty("smtp", out var smtp))
                {
                    var profile = new SmtpProfile
                    {
                        Id = 1,
                        Name = "Migrated Default",
                        IsDefault = true,
                        Host = smtp.TryGetProperty("host", out var host) ? host.GetString() ?? string.Empty : string.Empty,
                        Port = smtp.TryGetProperty("port", out var port) && port.TryGetInt32(out var p) ? p : 587,
                        EnableSsl = !smtp.TryGetProperty("enableSsl", out var ssl) || ssl.ValueKind != JsonValueKind.False,
                        Username = smtp.TryGetProperty("username", out var user) ? user.GetString() : null,
                        Password = smtp.TryGetProperty("password", out var pass) ? pass.GetString() : null,
                        FromEmail = smtp.TryGetProperty("fromEmail", out var fromEmail) ? fromEmail.GetString() : null,
                        FromName = smtp.TryGetProperty("fromName", out var fromName) ? fromName.GetString() : null,
                    };
                    if (SmtpProfiles.Count() == 0) SmtpProfiles.Insert(profile);
                }

                // Template (previously implicit) -> create default
                if (EmailTemplates.Count() == 0)
                {
                    EmailTemplates.Insert(new EmailTemplate
                    {
                        Id = 1,
                        Name = "Migrated Default",
                        Subject = "[Alert] {Type} expires in {Days} day(s)",
                        Body = "Hello,\n\nThis is an automated alert: {Type} will expire in {Days} day(s) (on {ExpirationDate}).\n\nEntity: {Entity}\n\nRegards",
                        IsHtml = false
                    });
                }

                // Levels -> rules for enabled alert types
                var enabled = !root.TryGetProperty("enabled", out var enabledProp) || enabledProp.ValueKind != JsonValueKind.False;
                var certEnabled = !root.TryGetProperty("certificateExpirationEnabled", out var cprop) || cprop.ValueKind != JsonValueKind.False;
                var csEnabled = !root.TryGetProperty("clientSecretExpirationEnabled", out var sprop) || sprop.ValueKind != JsonValueKind.False;

                var nextId = 1;
                if (root.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var level in levels.EnumerateArray())
                    {
                        var levelEnabled = !level.TryGetProperty("enabled", out var le) || le.ValueKind != JsonValueKind.False;
                        if (!enabled || !levelEnabled) continue;
                        if (!level.TryGetProperty("daysBeforeExpiration", out var daysProp) || !daysProp.TryGetInt32(out var days))
                            continue;

                        if (certEnabled)
                        {
                            AlertRules.Insert(new AlertRule
                            {
                                Id = nextId++,
                                Name = $"Migrated Certificate - {days} day(s)",
                                Type = AlertType.CertificateExpiration,
                                Enabled = true,
                                DaysBeforeExpiration = days,
                                SmtpProfileId = 1,
                                EmailTemplateId = 1,
                                To = root.TryGetProperty("testEmailTo", out var toProp) ? toProp.GetString() : null
                            });
                        }
                        if (csEnabled)
                        {
                            AlertRules.Insert(new AlertRule
                            {
                                Id = nextId++,
                                Name = $"Migrated Client/Secret - {days} day(s)",
                                Type = AlertType.ClientSecretExpiration,
                                Enabled = true,
                                DaysBeforeExpiration = days,
                                SmtpProfileId = 1,
                                EmailTemplateId = 1,
                                To = root.TryGetProperty("testEmailTo", out var toProp) ? toProp.GetString() : null
                            });
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                throw new InvalidOperationException("Legacy Alerts.Config is invalid. Schema initialization was rolled back.", ex);
            }
        }
    }
}
