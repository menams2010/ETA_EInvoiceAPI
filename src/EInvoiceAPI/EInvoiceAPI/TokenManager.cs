using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using System.IO;

namespace EInvoiceAPI
{
    public static class TokenManager
    {
        public static object GetSettings(string settingName)
        {
            SQLiteConnection sqliteConn = CreateConnection();
            SQLiteCommand cmd = new SQLiteCommand(sqliteConn);
            cmd.Parameters.Add(new SQLiteParameter("@SettingName"));
            cmd.Parameters["@SettingName"].Value = settingName;
            cmd.CommandText = @"Select [SettingValue] From ""main"".""tb_Settings"" WHERE [SettingName] = @SettingName;";
            object settingValue = cmd.ExecuteScalar();
            if (sqliteConn.State == ConnectionState.Open)
            {
                sqliteConn.Close();
            }

            return settingValue;
        }
        public static void SaveSettings(string settingName, object settingValue)
        {
            SQLiteConnection sqliteConn = CreateConnection();
            SQLiteCommand cmd = new SQLiteCommand(sqliteConn);
            cmd.Parameters.Add(new SQLiteParameter("@SettingName"));
            cmd.Parameters.Add(new SQLiteParameter("@SettingValue"));
            cmd.Parameters["@SettingName"].Value = settingName.ToString();
            cmd.Parameters["@SettingValue"].Value = settingValue.ToString();
            cmd.CommandText = @"UPDATE [tb_Settings] 
                                               SET [SettingValue] = @SettingValue
                                             WHERE [SettingName] = @SettingName;";
            cmd.ExecuteNonQuery();
            if (sqliteConn.State == ConnectionState.Open)
            {
                sqliteConn.Close();
            }
        }

        public static DataTable getE_Signing(string companyCode)
        {
            CreateOrMigrateConfigFile();

            DataTable dt = new DataTable();
            SQLiteConnection? sqlite_conn = CreateConnection();
            SQLiteCommand cmd = new SQLiteCommand(sqlite_conn);
            cmd.CommandText = @"SELECT TokenSerial, TokenPin, CertSerialNum, UseXMLTOJSON FROM ""main"".""tb_Company"" WHERE CompanyCode = '" + companyCode + "'";
            SQLiteDataAdapter da = new SQLiteDataAdapter(cmd);
            da.Fill(dt);
            if (sqlite_conn.State == ConnectionState.Open)
            {
                sqlite_conn.Close();
            }

            return dt;
        }

        public static DataTable GetToken(string companyCode, string environmentId)
        {
            CreateOrMigrateConfigFile();

            int EnvId = environmentId.Equals("Prod") ? 2 : 1;
            DataTable dt = new DataTable();
            SQLiteConnection? sqlite_conn = CreateConnection();
            SQLiteCommand cmd = new SQLiteCommand(sqlite_conn);
            cmd.CommandText = @"SELECT * FROM ""main"".""V_Data"" WHERE CompanyCode = '" + companyCode + "' And EnvId = " + EnvId;
            SQLiteDataAdapter da = new SQLiteDataAdapter(cmd);
            da.Fill(dt);
            if (sqlite_conn.State == ConnectionState.Open)
            {
                sqlite_conn.Close();
            }

            return dt;
        }

        public static void SaveToken(string? token, string timeGet, string companyCode, string environmentId)
        {
            int EnvId = environmentId.Equals("Prod") ? 2 : 1;
            SQLiteConnection? sqlite_conn = CreateConnection();
            SQLiteCommand cmd = new SQLiteCommand(sqlite_conn);
            cmd.CommandText =
                @"UPDATE ""main"".""tb_CompEnv"" SET str_token=@str_token,time_get=@time_get WHERE CompanyId = (SELECT Id FROM ""main"".""tb_company"" WHERE CompanyCode = " +
                companyCode + ") AND EnvId = " + EnvId;
            cmd.Parameters.Add(new SQLiteParameter("@str_token", token));
            cmd.Parameters.Add(new SQLiteParameter("@time_get", timeGet));
            cmd.ExecuteNonQuery();

            if (sqlite_conn.State == ConnectionState.Open)
            {
                sqlite_conn.Close();
            }
        }

        private static SQLiteConnection? CreateConnection()
        {
            SQLiteConnection? sqliteConn = new SQLiteConnection("Data Source=" + AppContext.BaseDirectory + @"\config.db;");
            try
            {
                sqliteConn.Open();
            }
            catch (Exception)
            {
                return null;
            }
            return sqliteConn;
        }

        private static void CreateConfigFile()
        {
            SQLiteConnection? sqliteConn = new SQLiteConnection("Data Source=" + AppContext.BaseDirectory + @"\config.db;");
            sqliteConn.Open();
            SQLiteCommand cmd = new SQLiteCommand(sqliteConn);
            cmd.CommandText = @"SELECT 1;
PRAGMA foreign_keys=OFF;
BEGIN TRANSACTION;
CREATE TABLE [tb_Login] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [u_name] text NOT NULL
, [u_pass] text NOT NULL
, [IsInitial] numeric(1,0) DEFAULT (1) NOT NULL
);
CREATE TABLE [tb_eInvoiceEnv] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [EnvName] text NULL
, [EnvTokenUrl] text NULL
, [EnvInvoicingUrl] text NULL
);
CREATE TABLE [tb_CompEnv] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [CompanyId] bigint NOT NULL
, [EnvId] bigint NOT NULL
, [ClientId] text NULL
, [ClientSecret] text NULL
, [str_token] text NULL
, [time_get] text NULL
);
CREATE TABLE [tb_Company] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [CompanyCode] bigint NULL
, [CompanyName] text NULL
, [TokenSerial] text NULL
, [TokenPin] text NULL
, [CertSerialNum] text NULL
, [UseXMLTOJSON] text DEFAULT 'false'
);
CREATE TABLE [tb_Settings] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [SettingName] text NOT NULL
, [SettingValue] text NULL
);
INSERT INTO [tb_Login] ([Id],[u_name],[u_pass],[IsInitial]) VALUES (1,'mina.mouris','Mariam1995',1);
INSERT INTO [tb_eInvoiceEnv] ([Id],[EnvName],[EnvTokenUrl],[EnvInvoicingUrl]) VALUES (1,'PreProd','https://id.preprod.eta.gov.eg/connect/token','https://api.preprod.invoicing.eta.gov.eg/api/v1/');
INSERT INTO [tb_eInvoiceEnv] ([Id],[EnvName],[EnvTokenUrl],[EnvInvoicingUrl]) VALUES (2,'Prod','https://id.eta.gov.eg/connect/token','https://api.invoicing.eta.gov.eg/api/v1/');
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (1,'DBVersion','2025-06-28 19:40:00');
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (2,'UseImpersonation','false');
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (3,'ImpDomain',NULL);
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (4,'ImpUsername',NULL);
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (5,'ImpPassword',NULL);
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (6,'DateTimeFormat','dd/MM/yyyy hh:mm:ss tt');
CREATE VIEW V_Data as  
Select CompanyCode,CompanyName,TokenSerial,TokenPin,CertSerialNum,EnvId,EnvName,EnvTokenUrl,EnvInvoicingUrl,ClientId,ClientSecret,str_token,time_get,UseXMLTOJSON
 From tb_company 
 Left Join tb_CompEnv ON tb_CompEnv.CompanyId = tb_company.Id 
 Left Join tb_eInvoiceEnv ON tb_eInvoiceEnv.Id = tb_CompEnv.EnvId;
COMMIT;";
            cmd.ExecuteNonQuery();
            if (sqliteConn.State == ConnectionState.Open)
            {
                sqliteConn.Close();
            }
        }

        public static void CreateOrMigrateConfigFile()
        {
            string dbPath = AppContext.BaseDirectory + @"\config.db";
            if (!File.Exists(dbPath))
            {
                CreateConfigFile();
                return;
            }

            using var sqliteConn = new SQLiteConnection("Data Source=" + dbPath + ";");
            sqliteConn.Open();

            // Migration condition: DBVersion not found or less than 2025-06-28 19:40:00
            string dbVersion = null;
            using (var cmd = new SQLiteCommand("SELECT SettingValue FROM tb_Settings WHERE SettingName = 'DBVersion'", sqliteConn))
            {
                var result = cmd.ExecuteScalar();
                dbVersion = result == null || result == DBNull.Value ? null : result.ToString();
            }
            string migrationThreshold = "2025-06-28 19:40:00";
            bool shouldMigrate = false;

            if (string.IsNullOrEmpty(dbVersion))
            {
                shouldMigrate = true;
            }
            else
            {
                DateTime dbVersionDate = DateTime.ParseExact(dbVersion, "yyyy-MM-dd HH:mm:ss", null);
                DateTime thresholdDate = DateTime.ParseExact(migrationThreshold, "yyyy-MM-dd HH:mm:ss", null);
                if (dbVersionDate < thresholdDate)
                    shouldMigrate = true;
            }
            if (!shouldMigrate)
            {
                sqliteConn.Close();
                return;
            }

            // 1. Ensure UseXMLTOJSON column exists in tb_Company (as string)
            using (var cmd = new SQLiteCommand(sqliteConn))
            {
                cmd.CommandText = "PRAGMA table_info(tb_Company);";
                using (var reader = cmd.ExecuteReader())
                {
                    bool hasUseXMLTOJSON = false;
                    while (reader.Read())
                    {
                        if (reader["name"].ToString().Equals("UseXMLTOJSON", StringComparison.OrdinalIgnoreCase))
                        {
                            hasUseXMLTOJSON = true;
                            break;
                        }
                    }
                    reader.Close();

                    if (!hasUseXMLTOJSON)
                    {
                        cmd.CommandText = "ALTER TABLE tb_Company ADD COLUMN UseXMLTOJSON TEXT DEFAULT 'false';";
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            // 2. Cleanse tb_Settings: remove UseXMLTOJSON, reset Id, insert DBVersion as Id=1
            // 2.1 Read all rows into a DataTable
            DataTable tempTable = new DataTable();
            using (var selectCmd = new SQLiteCommand("SELECT SettingName, SettingValue FROM tb_Settings", sqliteConn))
            using (var adapter = new SQLiteDataAdapter(selectCmd))
            {
                adapter.Fill(tempTable);
            }

            // 2.2 Delete all rows from tb_Settings
            using (var deleteCmd = new SQLiteCommand("DELETE FROM tb_Settings", sqliteConn))
            {
                deleteCmd.ExecuteNonQuery();
            }

            // 2.3 Reset the auto-increment seed
            using (var resetCmd = new SQLiteCommand("DELETE FROM sqlite_sequence WHERE name='tb_Settings';", sqliteConn))
            {
                resetCmd.ExecuteNonQuery();
            }

            // 2.4 Insert DBVersion as the first row (Id = 1) with fixed value
            string fixedDbVersion = "2025-06-28 19:40:00";
            using (var insertCmd = new SQLiteCommand("INSERT INTO tb_Settings (Id, SettingName, SettingValue) VALUES (1, @dbVersionName, @dbVersionValue)", sqliteConn))
            {
                insertCmd.Parameters.AddWithValue("@dbVersionName", "DBVersion");
                insertCmd.Parameters.AddWithValue("@dbVersionValue", fixedDbVersion);
                insertCmd.ExecuteNonQuery();
            }

            // 2.5 Re-insert rows from DataTable, excluding UseXMLTOJSON and DBVersion
            foreach (DataRow row in tempTable.Rows)
            {
                string settingName = row["SettingName"]?.ToString();
                if (string.Equals(settingName, "UseXMLTOJSON", StringComparison.OrdinalIgnoreCase))
                    continue;

                string settingValue = row["SettingValue"]?.ToString();

                using (var insertCmd = new SQLiteCommand("INSERT INTO tb_Settings (SettingName, SettingValue) VALUES (@SettingName, @SettingValue)", sqliteConn))
                {
                    insertCmd.Parameters.AddWithValue("@SettingName", settingName);
                    insertCmd.Parameters.AddWithValue("@SettingValue", settingValue ?? (object)DBNull.Value);
                    insertCmd.ExecuteNonQuery();
                }
            }

            sqliteConn.Close();
        }
    }
}