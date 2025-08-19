using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using LiteDB;
using EInvoiceAPI.Services;

namespace EInvoiceAPI
{
    public static class TokenManager
    {
        // POCOs mapped to LiteDB collections
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
            public string CompanyName { get; set; }
            public string TokenSerial { get; set; }
            public string TokenPin { get; set; }
            public string CertSerialNum { get; set; }
            public string UseXMLTOJSON { get; set; } = "false"; // keep as string to match previous expectations
        }

        private class TbEInvoiceEnv
        {
            public int Id { get; set; }
            public string EnvName { get; set; }
            public string EnvTokenUrl { get; set; }
            public string EnvInvoicingUrl { get; set; }
        }

        private class TbCompEnv
        {
            public int Id { get; set; }
            public long CompanyId { get; set; }
            public long EnvId { get; set; }
            public string? ClientId { get; set; }
            public string? ClientSecret { get; set; }
            public DateOnly? ClientSecretExpirationDate { get; set; }
            public string? str_token { get; set; }
            public string? time_get { get; set; }
        }

        private class TbLogin
        {
            public int Id { get; set; }
            public string u_name { get; set; } = string.Empty;
            public string u_pass { get; set; } = string.Empty;
            public bool IsInitial { get; set; } = true;
        }

        private static string DbPath => Path.Combine(AppContext.BaseDirectory, "config.db");

        private static LiteDatabase OpenDatabase()
        {
            Directory.CreateDirectory(AppContext.BaseDirectory);
            // Use shared connection mode in case multiple threads access it
            return new LiteDatabase($"Filename={DbPath};Password=@Mina&29012017&Mariam@;Connection=shared");
        }

        public static object? GetSettings(string settingName, SlackLogger? slackLogger = null, string? companyCode = null)
        {
            try
            {
                using var db = OpenDatabase();
                CreateOrMigrateConfigFile(db);
                var col = db.GetCollection<TbSetting>("Settings");
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
            CreateOrMigrateConfigFile(db);
            var col = db.GetCollection<TbSetting>("Settings");
            var existing = col.FindOne(x => x.SettingName == settingName);
            if (existing == null)
            {
                existing = new TbSetting { SettingName = settingName, SettingValue = settingValue?.ToString() };
                col.Insert(existing);
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
            CreateOrMigrateConfigFile(db);

            var dt = new DataTable();
            dt.Columns.Add("TokenSerial", typeof(string));
            dt.Columns.Add("TokenPin", typeof(string));
            dt.Columns.Add("CertSerialNum", typeof(string));
            dt.Columns.Add("UseXMLTOJSON", typeof(string));

            var companies = db.GetCollection<TbCompany>("Companies");
            if (long.TryParse(companyCode, out var cc))
            {
                var comp = companies.FindOne(c => c.CompanyCode == cc);
                if (comp != null)
                {
                    dt.Rows.Add(comp.TokenSerial, comp.TokenPin, comp.CertSerialNum, comp.UseXMLTOJSON);
                }
            }

            return dt;
        }

        public static DataTable GetToken(string companyCode, string environmentId)
        {
            using var db = OpenDatabase();
            CreateOrMigrateConfigFile(db);

            int EnvId = environmentId.Equals("Prod", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

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

            var companies = db.GetCollection<TbCompany>("Companies");
            var compEnvs = db.GetCollection<TbCompEnv>("CompEnvs");
            var envs = db.GetCollection<TbEInvoiceEnv>("EInvoiceEnvs");

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
                    compEnv.ClientSecretExpirationDate,
                    compEnv.str_token,
                    compEnv.time_get,
                    comp.UseXMLTOJSON
                );
            }

            return dt;
        }

        public static void SaveToken(string? token, string timeGet, string companyCode, string environmentId)
        {
            using var db = OpenDatabase();
            CreateOrMigrateConfigFile(db);

            int EnvId = environmentId.Equals("Prod", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

            var companies = db.GetCollection<TbCompany>("Companies");
            var compEnvs = db.GetCollection<TbCompEnv>("CompEnvs");

            if (!long.TryParse(companyCode, out var cc))
                return;

            var comp = companies.FindOne(c => c.CompanyCode == cc);
            if (comp == null) return;

            var compEnv = compEnvs.FindOne(ce => ce.CompanyId == comp.Id && ce.EnvId == EnvId);
            if (compEnv != null)
            {
                compEnv.str_token = token;
                compEnv.time_get = timeGet;
                compEnvs.Update(compEnv);
            }
        }

        // Create configuration data if database is empty or migrate minimal fields
        private static void CreateOrMigrateConfigFile(LiteDatabase db)
        {
            try
            {
                var settings = db.GetCollection<TbSetting>("Settings");
                var companies = db.GetCollection<TbCompany>("Companies");
                var compEnvs = db.GetCollection<TbCompEnv>("CompEnvs");
                var envs = db.GetCollection<TbEInvoiceEnv>("EInvoiceEnvs");
                var logins = db.GetCollection<TbLogin>("Logins");

                // Ensure indexes
                settings.EnsureIndex(x => x.SettingName, true);
                companies.EnsureIndex(x => x.CompanyCode);
                compEnvs.EnsureIndex(x => new { x.CompanyId, x.EnvId }, true);
                envs.EnsureIndex(x => x.Id);

                // Seed if first run (no settings present)
                if (settings.Count() == 0)
                {
                    // Seed settings
                    settings.Insert(new TbSetting { SettingName = "DBVersion", SettingValue = "2025-06-28 19:40:00" });
                    settings.Insert(new TbSetting { SettingName = "UseImpersonation", SettingValue = "false" });
                    settings.Insert(new TbSetting { SettingName = "ImpDomain", SettingValue = null });
                    settings.Insert(new TbSetting { SettingName = "ImpUsername", SettingValue = null });
                    settings.Insert(new TbSetting { SettingName = "ImpPassword", SettingValue = null });
                    settings.Insert(new TbSetting { SettingName = "DateTimeFormat", SettingValue = "dd/MM/yyyy hh:mm:ss tt" });
                    settings.Insert(new TbSetting { SettingName = "CertCacheLastRefresh", SettingValue = null });

                    // Seed environments
                    envs.Insert(new TbEInvoiceEnv
                    {
                        Id = 1,
                        EnvName = "PreProd",
                        EnvTokenUrl = "https://id.preprod.eta.gov.eg/connect/token",
                        EnvInvoicingUrl = "https://api.preprod.invoicing.eta.gov.eg/api/v1/"
                    });
                    envs.Insert(new TbEInvoiceEnv
                    {
                        Id = 2,
                        EnvName = "Prod",
                        EnvTokenUrl = "https://id.eta.gov.eg/connect/token",
                        EnvInvoicingUrl = "https://api.invoicing.eta.gov.eg/api/v1/"
                    });

                    // Seed login
                    logins.Insert(new TbLogin
                    {
                        Id = 1,
                        u_name = "mina.mouris",
                        u_pass = "Mariam1995",
                        IsInitial = true
                    });
                }

                // Minimal migration: ensure UseXMLTOJSON exists on existing companies
                foreach (var comp in companies.FindAll().Where(c => c.UseXMLTOJSON == null))
                {
                    comp.UseXMLTOJSON = "false";
                    companies.Update(comp);
                }

                // Ensure DBVersion setting exists and set at least the threshold
                var versionSetting = settings.FindOne(x => x.SettingName == "DBVersion");
                var migrationThreshold = "2025-06-28 19:40:00";
                if (versionSetting == null)
                {
                    settings.Insert(new TbSetting { SettingName = "DBVersion", SettingValue = migrationThreshold });
                }
                else
                {
                    DateTime parsedCurrent, parsedThreshold;
                    if (!DateTime.TryParseExact(versionSetting.SettingValue ?? string.Empty, "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out parsedCurrent))
                    {
                        versionSetting.SettingValue = migrationThreshold;
                        settings.Update(versionSetting);
                    }
                    else if (DateTime.TryParseExact(migrationThreshold, "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out parsedThreshold) && parsedCurrent < parsedThreshold)
                    {
                        versionSetting.SettingValue = migrationThreshold;
                        settings.Update(versionSetting);
                    }
                }
            }
            catch
            {
                // If anything goes wrong, just let upper level handle
                throw;
            }
        }
    }
}