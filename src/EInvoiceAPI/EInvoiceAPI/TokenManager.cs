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
            if (!File.Exists(AppContext.BaseDirectory + @"\config.db"))
            {
                CreateConfigFile();
            }

            DataTable dt = new DataTable();
            SQLiteConnection? sqlite_conn = CreateConnection();
            SQLiteCommand cmd = new SQLiteCommand(sqlite_conn);
            cmd.CommandText = @"SELECT TokenSerial, TokenPin, CertSerialNum FROM ""main"".""tb_Company"" WHERE CompanyCode = '" + companyCode + "'";
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
            if (!File.Exists(AppContext.BaseDirectory + @"\config.db"))
            {
                CreateConfigFile();
            }

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
            // Create a new database connection:
            SQLiteConnection? sqliteConn = new SQLiteConnection("Data Source=" + AppContext.BaseDirectory + @"\config.db;");
            // Open the connection:
            try
            {
                sqliteConn.Open();
            }
            catch (Exception ex)
            {
                return null;
            }

            return sqliteConn;
        }

        private static void CreateConfigFile()
        {
            // Create a new database connection:
            SQLiteConnection? sqliteConn = new SQLiteConnection("Data Source=" + AppContext.BaseDirectory + @"\config.db;");
            // Open the connection:
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
, [UseXMLTOJSON]  text NULL
);
CREATE TABLE [tb_Settings] (
  [Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL
, [SettingName] text NOT NULL
, [SettingValue] text NULL
);
INSERT INTO [tb_Login] ([Id],[u_name],[u_pass],[IsInitial]) VALUES (1,'mina.mouris','Mariam1995',1);
INSERT INTO [tb_eInvoiceEnv] ([Id],[EnvName],[EnvTokenUrl],[EnvInvoicingUrl]) VALUES (1,'PreProd','https://id.preprod.eta.gov.eg/connect/token','https://api.preprod.invoicing.eta.gov.eg/api/v1/');
INSERT INTO [tb_eInvoiceEnv] ([Id],[EnvName],[EnvTokenUrl],[EnvInvoicingUrl]) VALUES (2,'Prod','https://id.eta.gov.eg/connect/token','https://api.invoicing.eta.gov.eg/api/v1/');
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (1,'UseImpersonation','false');
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (2,'ImpDomain',NULL);
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (3,'ImpUsername',NULL);
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (4,'ImpPassword',NULL);
INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (5,'DateTimeFormat','dd/MM/yyyy hh:mm:ss tt');

CREATE VIEW V_Data as  
Select CompanyCode,CompanyName,TokenSerial,TokenPin,CertSerialNum,EnvId,EnvName,EnvTokenUrl,EnvInvoicingUrl,ClientId,ClientSecret,str_token,time_get 
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

    }
}