using System;
using System.Data;
using System.IO;
using LiteDB;
using EInvoiceAPI.Services;
using Microsoft.Extensions.Configuration;

namespace EInvoiceAPI
{
    /// <summary>
    /// Reads and writes the shared config.db that EInvoiceAPIAdmin manages.
    /// The Admin app owns the schema (collections, seeding, migrations, accounts); this class only reads
    /// company/environment/settings data and writes access tokens and a few settings back.
    /// </summary>
    public static class TokenManager
    {
        // Collection and field names must match EInvoiceAPIAdmin (src/EInvoiceAPIAdmin/EInvoiceAPIAdmin/Data/AppDbContext.cs).
        private const string SettingsCollection = "Settings";
        private const string CompaniesCollection = "Companies";
        private const string CompEnvsCollection = "CompEnvs";
        private const string EInvoiceEnvsCollection = "EInvoiceEnvs";

        private const int PreProdEnvId = 1;
        private const int ProdEnvId = 2;

        // Same rule the Admin app enforces in DatabaseSecurity.
        private const int MinimumPasswordLength = 32;

        private class TbSetting
        {
            public int Id { get; set; }
            public string SettingName { get; set; } = string.Empty;
            public string? SettingValue { get; set; }
        }

        private class TbCompany
        {
            public int Id { get; set; }
            public long? CompanyCode { get; set; }
            public string? CompanyName { get; set; }
            public string? TokenSerial { get; set; }
            public string? TokenPin { get; set; }
            public string? CertSerialNum { get; set; }
            public string? UseXMLTOJSON { get; set; }
        }

        private class TbEInvoiceEnv
        {
            public int Id { get; set; }
            public string? EnvName { get; set; }
            public string? EnvTokenUrl { get; set; }
            public string? EnvInvoicingUrl { get; set; }
        }

        private class TbCompEnv
        {
            public int Id { get; set; }
            public long CompanyId { get; set; }
            public long EnvId { get; set; }
            public string? ClientId { get; set; }
            public string? ClientSecret { get; set; }
            public DateTime? ClientSecretExpiryDate { get; set; }
            public string? str_token { get; set; }
            public string? time_get { get; set; }
        }

        private static string? _dbPath;
        private static string? _password;

        /// <summary>
        /// Reads Database:Path and Database:Password, the same keys EInvoiceAPIAdmin uses. Call once at startup.
        /// Throws if the password is missing, so a misconfigured API fails at startup instead of on the first request.
        /// </summary>
        public static void Configure(IConfiguration configuration)
        {
            var password = configuration["Database:Password"] ?? string.Empty;
            if (password.Length < MinimumPasswordLength)
                throw new InvalidOperationException(
                    $"Configure Database:Password (at least {MinimumPasswordLength} characters) with the same value EInvoiceAPIAdmin uses. " +
                    "See src/EInvoiceAPIAdmin/docs/security-hardening.md.");

            var configuredPath = configuration["Database:Path"];
            _dbPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(AppContext.BaseDirectory, "config.db")
                : configuredPath);
            _password = password;
        }

        private static LiteDatabase OpenDatabase()
        {
            if (_dbPath == null || _password == null)
                throw new InvalidOperationException("TokenManager.Configure must be called at startup before the config database is used.");

            if (!File.Exists(_dbPath))
                throw new FileNotFoundException(
                    "Config database not found. Create it by running EInvoiceAPIAdmin with the same Database:Path, or fix Database:Path.",
                    _dbPath);

            // Shared mode: EInvoiceAPIAdmin keeps the same file open while it runs.
            return new LiteDatabase(new ConnectionString
            {
                Filename = _dbPath,
                Password = _password,
                Connection = ConnectionType.Shared
            });
        }

        private static int ResolveEnvId(string environmentId)
            => environmentId.Equals("Prod", StringComparison.OrdinalIgnoreCase) ? ProdEnvId : PreProdEnvId;

        public static object? GetSettings(string settingName, SlackLogger? slackLogger = null, string? companyCode = null)
        {
            try
            {
                using var db = OpenDatabase();
                var col = db.GetCollection<TbSetting>(SettingsCollection);
                var doc = col.FindOne(x => x.SettingName == settingName);
                return doc?.SettingValue;
            }
            catch (Exception ex)
            {
                var msg = "Error reading setting from config file: " + ex.Message;
                slackLogger?.LogHelperAsync(companyCode, msg, "TokenManager.GetSettings").GetAwaiter().GetResult();
                throw new Exception(msg, ex);
            }
        }

        public static void SaveSettings(string settingName, object settingValue)
        {
            using var db = OpenDatabase();
            var col = db.GetCollection<TbSetting>(SettingsCollection);
            var existing = col.FindOne(x => x.SettingName == settingName);
            if (existing == null)
            {
                col.Insert(new TbSetting { SettingName = settingName, SettingValue = settingValue?.ToString() });
            }
            else
            {
                existing.SettingValue = settingValue?.ToString();
                col.Update(existing);
            }
        }

        public static DateTime? GetCacheLastRefresh()
        {
            var value = GetSettings("CertCacheLastRefresh");
            if (value != null && DateTime.TryParse(value.ToString(), out var dt))
                return dt;
            return null;
        }

        public static void SetCacheLastRefresh(DateTime dt)
        {
            SaveSettings("CertCacheLastRefresh", dt.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        public static DataTable getE_Signing(string companyCode)
        {
            using var db = OpenDatabase();

            var dt = new DataTable();
            dt.Columns.Add("TokenSerial", typeof(string));
            dt.Columns.Add("TokenPin", typeof(string));
            dt.Columns.Add("CertSerialNum", typeof(string));
            dt.Columns.Add("UseXMLTOJSON", typeof(string));

            var companies = db.GetCollection<TbCompany>(CompaniesCollection);
            if (long.TryParse(companyCode, out var cc))
            {
                var comp = companies.FindOne(c => c.CompanyCode == cc);
                if (comp != null)
                {
                    dt.Rows.Add(comp.TokenSerial, comp.TokenPin, comp.CertSerialNum, comp.UseXMLTOJSON ?? "false");
                }
            }

            return dt;
        }

        public static DataTable GetToken(string companyCode, string environmentId)
        {
            using var db = OpenDatabase();

            int EnvId = ResolveEnvId(environmentId);

            var dt = new DataTable();
            dt.Columns.Add("CompanyCode", typeof(long));
            dt.Columns.Add("CompanyName", typeof(string));
            dt.Columns.Add("TokenSerial", typeof(string));
            dt.Columns.Add("TokenPin", typeof(string));
            dt.Columns.Add("CertSerialNum", typeof(string));
            dt.Columns.Add("EnvId", typeof(int));
            dt.Columns.Add("EnvName", typeof(string));
            dt.Columns.Add("EnvTokenUrl", typeof(string));
            dt.Columns.Add("EnvInvoicingUrl", typeof(string));
            dt.Columns.Add("ClientId", typeof(string));
            dt.Columns.Add("ClientSecret", typeof(string));
            dt.Columns.Add("ClientSecretExpirationDate", typeof(DateOnly));
            dt.Columns.Add("str_token", typeof(string));
            dt.Columns.Add("time_get", typeof(string));
            dt.Columns.Add("UseXMLTOJSON", typeof(string));

            var companies = db.GetCollection<TbCompany>(CompaniesCollection);
            var compEnvs = db.GetCollection<TbCompEnv>(CompEnvsCollection);
            var envs = db.GetCollection<TbEInvoiceEnv>(EInvoiceEnvsCollection);

            if (!long.TryParse(companyCode, out var cc))
                return dt;

            var comp = companies.FindOne(c => c.CompanyCode == cc);
            if (comp == null) return dt;

            var compEnv = compEnvs.FindOne(ce => ce.CompanyId == comp.Id && ce.EnvId == EnvId);
            var env = envs.FindById(EnvId);

            if (env != null && compEnv != null)
            {
                dt.Rows.Add(
                    comp.CompanyCode ?? 0,
                    comp.CompanyName,
                    comp.TokenSerial,
                    comp.TokenPin,
                    comp.CertSerialNum,
                    EnvId,
                    env.EnvName,
                    env.EnvTokenUrl,
                    env.EnvInvoicingUrl,
                    compEnv.ClientId,
                    compEnv.ClientSecret,
                    // The Admin app stores the expiry as ClientSecretExpiryDate (DateTime); callers expect a DateOnly column.
                    compEnv.ClientSecretExpiryDate.HasValue
                        ? DateOnly.FromDateTime(compEnv.ClientSecretExpiryDate.Value)
                        : DBNull.Value,
                    compEnv.str_token,
                    compEnv.time_get,
                    comp.UseXMLTOJSON ?? "false"
                );
            }

            return dt;
        }

        public static void SaveToken(string? token, string timeGet, string companyCode, string environmentId)
        {
            using var db = OpenDatabase();

            int EnvId = ResolveEnvId(environmentId);

            var companies = db.GetCollection<TbCompany>(CompaniesCollection);
            var compEnvs = db.GetCollection<TbCompEnv>(CompEnvsCollection);

            if (!long.TryParse(companyCode, out var cc))
                return;

            var comp = companies.FindOne(c => c.CompanyCode == cc);
            if (comp == null) return;

            // Update only the token fields so fields the Admin app adds to CompEnv are never lost.
            var compEnvDoc = compEnvs.FindOne(ce => ce.CompanyId == comp.Id && ce.EnvId == EnvId);
            if (compEnvDoc != null)
            {
                var raw = db.GetCollection(CompEnvsCollection);
                var doc = raw.FindById(compEnvDoc.Id);
                if (doc == null) return;
                doc["str_token"] = token;
                doc["time_get"] = timeGet;
                raw.Update(doc);
            }
        }
    }
}
