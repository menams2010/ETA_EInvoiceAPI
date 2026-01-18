using InvoiceWebApp.Models;
using LiteDB;
using System.Collections.Generic;
using System.Linq;

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

        public AppDbContext(string databasePath)
        {
            _db = new LiteDatabase(databasePath);
            _ownsDb = true;
            ConfigureMapper();
            MigrateLegacyCollections();
            EnsureSchemaAndSeed();
        }

        public AppDbContext(LiteDatabase db)
        {
            _db = db;
            _ownsDb = false;
            ConfigureMapper();
            MigrateLegacyCollections();
            EnsureSchemaAndSeed();
        }

        private static void ConfigureMapper()
        {
            var m = BsonMapper.Global;
            // Ensure Id as primary key; default name "Id" is detected automatically.
            // Additional configuration can go here if needed.
        }

        private void MigrateLegacyCollections()
        {
            // If old collections exist, copy data into the new collections
            // We migrate from:
            //   - legacy "tb_*" names
            //   - previous singular names (Company, CompEnv, eInvoiceEnv, Login)
            // Only copy when the target collection is empty to avoid duplications
            HashSet<string> names;
            try
            {
                names = _db.GetCollectionNames().ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // In case GetCollectionNames is unavailable, skip migration silently
                return;
            }

            void CopyIfExists<T>(string oldName, string newName)
            {
                if (!names.Contains(oldName)) return;
                var target = _db.GetCollection<T>(newName);
                if (target.Count() > 0) return;
                var source = _db.GetCollection<T>(oldName);
                foreach (var doc in source.FindAll())
                {
                    target.Upsert(doc);
                }
            }

            // Companies
            CopyIfExists<Company>("tb_Company", "Companies");
            CopyIfExists<Company>("Company", "Companies");

            // CompEnvs
            CopyIfExists<CompEnv>("tb_CompEnv", "CompEnvs");
            CopyIfExists<CompEnv>("CompEnv", "CompEnvs");

            // EInvoiceEnvs
            CopyIfExists<EInvoiceEnv>("tb_eInvoiceEnv", "EInvoiceEnvs");
            CopyIfExists<EInvoiceEnv>("eInvoiceEnv", "EInvoiceEnvs");

            // Settings (already plural previously)
            CopyIfExists<Settings>("tb_Settings", "Settings");
            // No need to copy from "Settings" to "Settings"

            // Logins
            CopyIfExists<Login>("tb_Login", "Logins");
            CopyIfExists<Login>("Login", "Logins");
        }

        private void EnsureSchemaAndSeed()
        {
            // Ensure indexes on Id
            Companies.EnsureIndex(x => x.Id, true);
            CompEnvs.EnsureIndex(x => x.Id, true);
            EInvoiceEnvs.EnsureIndex(x => x.Id, true);
            Settings.EnsureIndex(x => x.Id, true);
            Logins.EnsureIndex(x => x.Id, true);

            if (Logins.Count() == 0)
            {
                Logins.Insert(new Login { Id = 1, u_name = "mina.mouris", u_pass = "Mariam1995", IsInitial = true });
            }
            if (EInvoiceEnvs.Count() == 0)
            {
                EInvoiceEnvs.Insert(new EInvoiceEnv { Id = 1, EnvName = "PreProd", EnvTokenUrl = "https://id.preprod.eta.gov.eg/connect/token", EnvInvoicingUrl = "https://api.preprod.invoicing.eta.gov.eg/api/v1/" });
                EInvoiceEnvs.Insert(new EInvoiceEnv { Id = 2, EnvName = "Prod", EnvTokenUrl = "https://id.eta.gov.eg/connect/token", EnvInvoicingUrl = "https://api.invoicing.eta.gov.eg/api/v1/" });
            }
            if (Settings.Count() == 0)
            {
                Settings.Insert(new Settings { Id = 1, SettingName = "DBVersion", SettingValue = "2025-06-28 19:40:00" });
                Settings.Insert(new Settings { Id = 2, SettingName = "UseImpersonation", SettingValue = "false" });
                Settings.Insert(new Settings { Id = 3, SettingName = "ImpDomain", SettingValue = null });
                Settings.Insert(new Settings { Id = 4, SettingName = "ImpUsername", SettingValue = null });
                Settings.Insert(new Settings { Id = 5, SettingName = "ImpPassword", SettingValue = null });
                Settings.Insert(new Settings { Id = 6, SettingName = "DateTimeFormat", SettingValue = "yyyy-MM-dd HH:mm:ss" });
                Settings.Insert(new Settings { Id = 7, SettingName = "CertCacheLastRefresh", SettingValue = null });
            }
        }

        public void Dispose()
        {
            if (_ownsDb)
            {
                _db?.Dispose();
            }
        }
    }
}
