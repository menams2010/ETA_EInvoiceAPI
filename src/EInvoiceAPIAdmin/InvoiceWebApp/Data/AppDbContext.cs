using InvoiceWebApp.Models;
using LiteDB;

namespace InvoiceWebApp.Data
{
    public class AppDbContext : IDisposable
    {
        private readonly LiteDatabase _db;
        private readonly bool _ownsDb;

        public ILiteCollection<Company> Companies => _db.GetCollection<Company>("tb_Company");
        public ILiteCollection<CompEnv> CompEnvs => _db.GetCollection<CompEnv>("tb_CompEnv");
        public ILiteCollection<EInvoiceEnv> EInvoiceEnvs => _db.GetCollection<EInvoiceEnv>("tb_eInvoiceEnv");
        public ILiteCollection<Settings> Settings => _db.GetCollection<Settings>("tb_Settings");
        public ILiteCollection<Login> Logins => _db.GetCollection<Login>("tb_Login");

        public AppDbContext(string databasePath)
        {
            _db = new LiteDatabase(databasePath);
            _ownsDb = true;
            ConfigureMapper();
            EnsureSchemaAndSeed();
        }

        public AppDbContext(LiteDatabase db)
        {
            _db = db;
            _ownsDb = false;
            ConfigureMapper();
            EnsureSchemaAndSeed();
        }

        private static void ConfigureMapper()
        {
            var m = BsonMapper.Global;
            // Ensure Id as primary key; default name "Id" is detected automatically.
            // Additional configuration can go here if needed.
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
