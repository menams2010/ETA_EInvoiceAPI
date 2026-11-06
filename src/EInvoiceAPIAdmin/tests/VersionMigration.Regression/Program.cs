// Admin > Migrate Database: builds a config.db of every known version, migrates each one into a live current
// database with ConfigDatabaseMigrator, and checks the version detection and the result.
using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using LiteDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

const string BuiltIn = "@Mina&29012017&Mariam@";
const string OtherServerKey = "other-server-key-0123456789abcdef0123456789";
var root = Path.Combine(Path.GetTempPath(), "version-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

// ---------- fixtures, shaped like each historical version ----------

string V1Sqlite(string path)
{
    // The exact schema EInvoiceAPI created in 2025-06 (TokenManager.CreateConfigFile), plus data.
    using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE [tb_Login] ([Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, [u_name] text NOT NULL, [u_pass] text NOT NULL, [IsInitial] numeric(1,0) DEFAULT (1) NOT NULL);
        CREATE TABLE [tb_eInvoiceEnv] ([Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, [EnvName] text NULL, [EnvTokenUrl] text NULL, [EnvInvoicingUrl] text NULL);
        CREATE TABLE [tb_CompEnv] ([Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, [CompanyId] bigint NOT NULL, [EnvId] bigint NOT NULL, [ClientId] text NULL, [ClientSecret] text NULL, [str_token] text NULL, [time_get] text NULL);
        CREATE TABLE [tb_Company] ([Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, [CompanyCode] bigint NULL, [CompanyName] text NULL, [TokenSerial] text NULL, [TokenPin] text NULL, [CertSerialNum] text NULL, [UseXMLTOJSON] text NULL);
        CREATE TABLE [tb_Settings] ([Id] INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, [SettingName] text NOT NULL, [SettingValue] text NULL);
        INSERT INTO [tb_Login] VALUES (1,'v1admin','V1Password!',1);
        INSERT INTO [tb_eInvoiceEnv] VALUES (1,'PreProd','https://id.preprod.eta.gov.eg/connect/token','https://api.preprod.invoicing.eta.gov.eg/api/v1/');
        INSERT INTO [tb_eInvoiceEnv] VALUES (2,'Prod','https://id.eta.gov.eg/connect/token','https://api.invoicing.eta.gov.eg/api/v1/');
        INSERT INTO [tb_Settings] VALUES (1,'UseImpersonation','false');
        INSERT INTO [tb_Settings] VALUES (6,'DateTimeFormat','dd/MM/yyyy hh:mm:ss tt');
        INSERT INTO [tb_Company] VALUES (1,1001,'V1 Company','TS1','1111','CS1','1');
        INSERT INTO [tb_CompEnv] VALUES (1,1,2,'cid1','secret1','tok1','2025-06-10 10:00:00');
        CREATE VIEW V_Data as Select CompanyCode,CompanyName,EnvId From tb_company Left Join tb_CompEnv ON tb_CompEnv.CompanyId = tb_company.Id;
        """;
    command.ExecuteNonQuery();
    return path;
}

string LiteFixture(string path, string? password, bool tbNames, bool alerts, bool audit)
{
    using var db = new LiteDatabase(new ConnectionString { Filename = path, Password = password, Connection = ConnectionType.Direct });
    string N(string current, string legacy) => tbNames ? legacy : current;
    db.GetCollection(N("Settings", "tb_Settings")).Insert(new BsonDocument { ["_id"] = 1, ["SettingName"] = "DBVersion", ["SettingValue"] = alerts ? "2026-01-21 00:00:00" : "2025-06-28 19:40:00" });
    db.GetCollection(N("EInvoiceEnvs", "tb_eInvoiceEnv")).Insert(new BsonDocument { ["_id"] = 1, ["EnvName"] = "PreProd", ["EnvTokenUrl"] = "t", ["EnvInvoicingUrl"] = "i" });
    db.GetCollection(N("EInvoiceEnvs", "tb_eInvoiceEnv")).Insert(new BsonDocument { ["_id"] = 2, ["EnvName"] = "Prod", ["EnvTokenUrl"] = "t", ["EnvInvoicingUrl"] = "i" });
    db.GetCollection(N("Logins", "tb_Login")).Insert(new BsonDocument { ["_id"] = 1, ["u_name"] = "legacyadmin", ["u_pass"] = "LegacyPassword!", ["IsInitial"] = false });
    db.GetCollection(N("Companies", "tb_Company")).Insert(new BsonDocument { ["_id"] = 1, ["CompanyCode"] = 3001L, ["CompanyName"] = "Lite Company", ["UseXMLTOJSON"] = "false" });
    db.GetCollection(N("CompEnvs", "tb_CompEnv")).Insert(new BsonDocument { ["_id"] = 1, ["CompanyId"] = 1L, ["EnvId"] = 2L, ["ClientId"] = "cid", ["ClientSecretExpiryDate"] = new DateTime(2027, 1, 31) });
    if (alerts)
    {
        db.GetCollection("SmtpProfiles").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "Old SMTP", ["Host"] = "smtp.example.com", ["Port"] = 25, ["IsDefault"] = true });
        db.GetCollection("EmailTemplates").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "Old template", ["Subject"] = "s", ["Body"] = "b", ["IsHtml"] = false });
        db.GetCollection("AlertRules").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "Old rule", ["SmtpProfileId"] = 1, ["EmailTemplateId"] = 1 });
    }
    if (audit) db.GetCollection("AuditLogs").Insert(new BsonDocument { ["_id"] = 1, ["UserName"] = "legacyadmin", ["Action"] = "Login", ["TimestampUtc"] = new DateTime(2026, 2, 8, 0, 0, 0, DateTimeKind.Utc) });
    return path;
}

IConfiguration Config(string path, string password, string user, string userPassword) => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Database:Path"] = path, ["Database:Password"] = password,
        ["Bootstrap:Username"] = user, ["Bootstrap:Password"] = userPassword
    }).Build();

// A current-version database, as setup creates it (with SAP credentials and an API key).
string CurrentDatabase(string folder, string password, string user, string apiKey, bool asV7 = false, int companyId = 99, string companyName = "Live company")
{
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "config.db");
    var config = Config(path, password, user, "CurrentPassword1!");
    var security = new DatabaseSecurity(config);
    using var db = security.Open();
    using (var ctx = new AppDbContext(db))
    {
        DatabaseSecurity.InitializeAccounts(db, config);
        var prod = ctx.SapEndpoints.FindOne(x => x.Name == SapEndpoint.Prod);
        prod.Username = "live.sap"; prod.Password = "live-secret";
        ctx.SapEndpoints.Update(prod);
        ctx.NotificationSettings.Upsert(new NotificationSettings { ApiKey = apiKey, ApiKeyChangedUtc = DateTime.UtcNow });
        ctx.Companies.Insert(new Company { Id = companyId, CompanyCode = 9000 + companyId, CompanyName = companyName, UseXMLTOJSON = "false" });
    }
    if (asV7) { db.DropCollection("SapEndpoints"); db.DropCollection("NotificationSettings"); }
    return path;
}

// ---------- run one migration ----------

void Migrate(string label, string sourceFile, string expectedVersion, string? enteredPassword, Action<LiteDatabase, AppDbContext> verify,
    bool keepsLiveSap = true, string? expectFailure = null)
{
    var live = Path.Combine(root, label.Replace(' ', '-'));
    var livePassword = "live-server-key-0123456789abcdef0123456789";
    var livePath = CurrentDatabase(live, livePassword, "liveadmin", "LiveApiKey-1");
    var security = new DatabaseSecurity(Config(livePath, livePassword, "", ""));
    var migrator = new ConfigDatabaseMigrator(security);

    using var upload = migrator.Save(File.OpenRead(sourceFile));
    ConfigDatabaseMigrator.Analysis analysis;
    try { analysis = migrator.Analyze(upload, enteredPassword); }
    catch (Exception ex)
    {
        Check(expectFailure != null, $"{label}: analysis failed: {ex.Message}");
        return;
    }
    Check(expectFailure == null, $"{label}: expected failure ({expectFailure}) but the file was accepted");
    if (expectFailure != null) return;
    Check(analysis.Version == expectedVersion, $"{label}: detected {analysis.Version}, expected {expectedVersion}");

    using var db = security.Open();
    var (backup, imported, _) = migrator.Import(upload, enteredPassword, db);
    Check(File.Exists(backup), $"{label}: no backup of the live database");
    using (var before = new LiteDatabase(new ConnectionString { Filename = backup, Password = livePassword, ReadOnly = true }))
        Check(before.GetCollection("Companies").FindById(99) != null, $"{label}: backup does not hold the previous data");

    using var ctx = new AppDbContext(db);
    Check(ctx.Settings.FindOne(x => x.SettingName == "DBVersion")?.SettingValue == ConfigDatabaseMigrator.CurrentSchemaVersion, $"{label}: DBVersion not stamped");
    Check(ctx.Companies.FindById(99) == null, $"{label}: live companies were not replaced");
    Check(ctx.Logins.FindOne(x => x.u_name == "liveadmin") == null, $"{label}: live accounts were not replaced");
    Check(ctx.Logins.FindAll().All(l => !string.IsNullOrEmpty(l.PasswordHash) && !string.IsNullOrEmpty(l.SecurityStamp)), $"{label}: an account has no hash");
    if (keepsLiveSap)
    {
        Check(ctx.SapEndpoints.FindOne(x => x.Name == SapEndpoint.Prod)?.Username == "live.sap", $"{label}: live SAP endpoint was lost");
        Check(ctx.NotificationSettings.FindById(1)?.ApiKey == "LiveApiKey-1", $"{label}: live API key was lost");
    }
    foreach (var name in new[] { "SmtpProfiles", "EmailTemplates", "SapEndpoints", "NotificationSettings", "AuditLogs" })
        Check(db.CollectionExists(name) || name is "NotificationSettings" or "AuditLogs", $"{label}: current collection {name} missing");
    verify(db, ctx);
    // Each collection can be read with the current models.
    _ = ctx.Companies.FindAll().ToList(); _ = ctx.CompEnvs.FindAll().ToList(); _ = ctx.Settings.FindAll().ToList();
    _ = ctx.EInvoiceEnvs.FindAll().ToList(); _ = ctx.Logins.FindAll().ToList(); _ = ctx.AlertRules.FindAll().ToList();
    Console.WriteLine($"ok    {label}: {analysis.Version} ({analysis.Format}, password: {analysis.Password}); imported {string.Join(", ", imported.Select(i => $"{i.Collection} {i.Records}"))}");
}

bool LoginWorks(AppDbContext ctx, string user, string password)
{
    var login = ctx.Logins.FindOne(x => x.u_name == user);
    return login != null && AccountSecurity.Verify(login, password);
}

var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
try
{
    Migrate("V1 SQLite", V1Sqlite(Path.Combine(root, "v1.db")), "V1", null, (db, ctx) =>
    {
        Check(LoginWorks(ctx, "v1admin", "V1Password!"), "V1: plain-text password does not verify after hashing");
        Check(ctx.Companies.FindById(1)?.CompanyName == "V1 Company" && ctx.Companies.FindById(1)?.UseXMLTOJSON == "true", "V1: company not migrated / UseXMLTOJSON not normalized");
        Check(ctx.CompEnvs.FindById(1)?.ClientSecret == "secret1", "V1: company environment not migrated");
    });

    Migrate("V2 real LiteDB 5.0.21 file", Path.Combine(fixtures, "v2-einvoiceapi-litedb-5.0.21.db"), "V2", null, (db, ctx) =>
    {
        Check(LoginWorks(ctx, "v2admin", "V2Password!"), "V2: password does not verify");
        var env = ctx.CompEnvs.FindAll().FirstOrDefault();
        Check(env?.ClientSecretExpiryDate?.Date == new DateTime(2027, 3, 15), $"V2: DateOnly expiry not converted ({env?.ClientSecretExpiryDate})");
        Check(!db.GetCollection("CompEnvs").FindAll().First().ContainsKey("ClientSecretExpirationDate"), "V2: old expiry field left behind");
        Check(ctx.Settings.FindOne(x => x.SettingName == "DateTimeFormat")?.SettingValue == "dd/MM/yyyy hh:mm:ss tt", "V2: settings not migrated");
    });

    Migrate("V3 tb_ collections", LiteFixture(Path.Combine(root, "v3.db"), BuiltIn, tbNames: true, alerts: false, audit: false), "V3", null, (db, ctx) =>
    {
        Check(!db.GetCollectionNames().Any(n => n.StartsWith("tb_")), "V3: tb_ collections left behind");
        Check(LoginWorks(ctx, "legacyadmin", "LegacyPassword!"), "V3: password does not verify");
        Check(ctx.CompEnvs.FindById(1)?.ClientSecretExpiryDate?.Date == new DateTime(2027, 1, 31), "V3: expiry date lost");
    });

    Migrate("V4 renamed collections", LiteFixture(Path.Combine(root, "v4.db"), BuiltIn, tbNames: false, alerts: false, audit: false), "V2/V4", null, (db, ctx) =>
        Check(ctx.Companies.FindById(1)?.CompanyName == "Lite Company", "V4: company not migrated"));

    Migrate("V5 alerts", LiteFixture(Path.Combine(root, "v5.db"), BuiltIn, tbNames: false, alerts: true, audit: false), "V5", null, (db, ctx) =>
    {
        Check(ctx.SmtpProfiles.FindById(1)?.Host == "smtp.example.com", "V5: SMTP profile not migrated");
        Check(ctx.AlertRules.FindById(1)?.Name == "Old rule", "V5: alert rule not migrated");
    });

    Migrate("V6 audit log", LiteFixture(Path.Combine(root, "v6.db"), BuiltIn, tbNames: false, alerts: true, audit: true), "V6", null, (db, ctx) =>
        Check(ctx.AuditLogs.FindById(1)?.UserName == "legacyadmin", "V6: audit log not migrated"));

    Migrate("unencrypted LiteDB", LiteFixture(Path.Combine(root, "plain.db"), null, tbNames: true, alerts: false, audit: false), "V3", null, (db, ctx) =>
        Check(LoginWorks(ctx, "legacyadmin", "LegacyPassword!"), "plain: password does not verify"));

    var v7 = CurrentDatabase(Path.Combine(root, "v7-source"), OtherServerKey, "v7admin", "unused", asV7: true, companyId: 50, companyName: "V7 company");
    Migrate("V7 other server, password entered", v7, "V7", OtherServerKey, (db, ctx) =>
    {
        Check(LoginWorks(ctx, "v7admin", "CurrentPassword1!"), "V7: hashed password does not verify");
        Check(ctx.Companies.FindById(50)?.CompanyName == "V7 company", "V7: company not migrated");
    });
    Migrate("V7 other server, no password", v7, "V7", null, (_, _) => { }, expectFailure: "unknown password");
    Migrate("V7 other server, wrong password", v7, "V7", "wrong-password", (_, _) => { }, expectFailure: "wrong password");

    var v8 = CurrentDatabase(Path.Combine(root, "v8-source"), OtherServerKey, "v8admin", "SourceApiKey-8", companyId: 60, companyName: "V8 company");
    Migrate("V8 current version from another server", v8, "V8", OtherServerKey, (db, ctx) =>
    {
        Check(ctx.NotificationSettings.FindById(1)?.ApiKey == "SourceApiKey-8", "V8: API key from the file not imported");
        Check(LoginWorks(ctx, "v8admin", "CurrentPassword1!"), "V8: password does not verify");
    }, keepsLiveSap: false);

    Migrate("LiteDB 4 file", Path.Combine(fixtures, "litedb4.db"), "LiteDB 4", null, (db, ctx) =>
    {
        Check(LoginWorks(ctx, "v4admin", "V4Password!"), "LiteDB 4: password does not verify");
        Check(ctx.Companies.FindById(1)?.CompanyName == "LiteDB4 Company", "LiteDB 4: company not migrated");
        // The last page of a LiteDB 4 file holds CompEnvs; LiteDB 5 truncates it if it ever opens the original.
        Check(ctx.CompEnvs.FindById(1)?.ClientId == "c4", "LiteDB 4: last page lost (CompEnvs)");
    });

    var junk = Path.Combine(root, "junk.db");
    File.WriteAllBytes(junk, System.Security.Cryptography.RandomNumberGenerator.GetBytes(16384));
    Migrate("random bytes", junk, "", null, (_, _) => { }, expectFailure: "not a database");

    // Download path: the converted file opens with the server's password and has the current schema.
    {
        var folder = Path.Combine(root, "download");
        var livePath = CurrentDatabase(folder, "live-server-key-0123456789abcdef0123456789", "liveadmin", "k-12345678");
        var migrator = new ConfigDatabaseMigrator(new DatabaseSecurity(Config(livePath, "live-server-key-0123456789abcdef0123456789", "", "")));
        using var upload = migrator.Save(File.OpenRead(Path.Combine(fixtures, "v2-einvoiceapi-litedb-5.0.21.db")));
        var (converted, _, _) = migrator.Convert(upload, null);
        using var db = new LiteDatabase(new ConnectionString { Filename = converted, Password = "live-server-key-0123456789abcdef0123456789", ReadOnly = true });
        Check(db.CollectionExists("SapEndpoints") && db.GetCollection("Logins").Count() == 1, "download: converted file is not the current schema");
        Console.WriteLine("ok    download: V2 converted to a current-version file");
    }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.WriteLine("FAIL  " + failure);
    return 1;
}
Console.WriteLine("PASS: V1 SQLite, V2 (real LiteDB 5.0.21 file), V3, V4, V5, V6, unencrypted, V7 (entered/missing/wrong password), V8, LiteDB 4, invalid file, download.");
return 0;
