using System.Security.Cryptography;
using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using LiteDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Reject(Action action, string message)
{
    try { action(); }
    catch { return; }
    throw new Exception(message);
}

static LiteDatabase Open(string path, string? key, bool readOnly = false) => new(new ConnectionString
{
    Filename = path, Password = key, ReadOnly = readOnly
});

static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

static void SeedLite(string path, string? key, bool singular = false)
{
    using var db = Open(path, key);
    db.GetCollection(singular ? "Company" : "tb_Company").Insert(new BsonDocument
    {
        ["_id"] = 7, ["CompanyName"] = "Fixture", ["CompanyCode"] = 123L,
        ["UseXMLTOJSON"] = "1", ["CustomField"] = "preserved"
    });
    db.GetCollection(singular ? "Company" : "tb_Company").EnsureIndex("CompanyName", true);
    db.GetCollection(singular ? "eInvoiceEnv" : "tb_eInvoiceEnv").Insert(new BsonDocument
    {
        ["_id"] = 2, ["EnvName"] = "Fixture", ["EnvTokenUrl"] = "offline", ["EnvInvoicingUrl"] = "offline"
    });
    db.GetCollection(singular ? "CompEnv" : "tb_CompEnv").Insert(new BsonDocument
    {
        ["_id"] = 9, ["CompanyId"] = 7L, ["EnvId"] = 2L,
        ["ClientSecretExpiryDate"] = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        ["time_get"] = "legacy-timestamp", ["str_token"] = "fixture-token"
    });
    db.GetCollection(singular ? "Login" : "tb_Login").Insert(new BsonDocument
    {
        ["_id"] = 4, ["u_name"] = "legacy-user", ["u_pass"] = "fixture-password", ["IsInitial"] = true
    });
    db.GetCollection("Settings").Insert(new BsonDocument
    {
        ["_id"] = 6, ["SettingName"] = "DateTimeFormat", ["SettingValue"] = "dd/MM/yyyy"
    });
    db.GetCollection("CustomCollection").Insert(new BsonDocument { ["_id"] = 1, ["Value"] = "retained" });
}

static SqliteConnection SeedSqlite(string path, bool optionalFields = true, bool wal = false)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    if (wal) { command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;"; command.ExecuteNonQuery(); }
    command.CommandText = "CREATE TABLE tb_Company(Id INTEGER PRIMARY KEY, CompanyName TEXT, CompanyCode INTEGER, UseXMLTOJSON INTEGER, CustomField TEXT);"
        + "INSERT INTO tb_Company VALUES(7,'Fixture',123,1,'preserved');"
        + "CREATE TABLE tb_eInvoiceEnv(Id INTEGER PRIMARY KEY, EnvName TEXT, EnvTokenUrl TEXT, EnvInvoicingUrl TEXT);"
        + "INSERT INTO tb_eInvoiceEnv VALUES(2,'Fixture','offline','offline');"
        + "CREATE TABLE tb_CompEnv(Id INTEGER PRIMARY KEY, CompanyId INTEGER, EnvId INTEGER, str_token TEXT"
        + (optionalFields ? ", time_get TEXT, ClientSecretExpiryDate TEXT" : "") + ");"
        + "INSERT INTO tb_CompEnv VALUES(9,7,2,'fixture-token'" + (optionalFields ? ",'legacy-timestamp','2030-01-02T00:00:00Z'" : "") + ");"
        + "CREATE TABLE tb_Login(Id INTEGER PRIMARY KEY, u_name TEXT, u_pass TEXT, IsInitial INTEGER);"
        + "INSERT INTO tb_Login VALUES(4,'legacy-user','fixture-password',1);"
        + "CREATE TABLE tb_Settings(Id INTEGER PRIMARY KEY, SettingName TEXT, SettingValue TEXT);"
        + "INSERT INTO tb_Settings VALUES(6,'DateTimeFormat','dd/MM/yyyy');";
    command.ExecuteNonQuery();
    return connection;
}

static void Verify(string destination, string key, bool optionalFields = true)
{
    using var db = Open(destination, key, true);
    var account = db.GetCollection<Login>("Logins").FindById(4);
    Check(AccountSecurity.Verify(account, "fixture-password"), "Legacy password must still authenticate.");
    Check(!db.GetCollection("Logins").FindById(4).ContainsKey("u_pass"), "Plaintext account password survived migration.");
    Check(db.GetCollection("Companies").FindById(7)["CustomField"].AsString == "preserved", "Unknown column was lost.");
    Check(db.GetCollection<Company>("Companies").FindById(7).UseXMLTOJSON == "true", "Boolean normalization failed.");
    var environment = db.GetCollection<CompEnv>("CompEnvs").FindById(9);
    Check(environment.CompanyId == 7 && environment.EnvId == 2 && environment.str_token == "fixture-token", "References/token changed.");
    Check(optionalFields ? environment.time_get == "legacy-timestamp" && environment.ClientSecretExpiryDate?.Year == 2030
        : environment.time_get is null && environment.ClientSecretExpiryDate is null, "Optional fields were lost or invented.");
    Check(db.GetCollection<Settings>("Settings").FindById(6).SettingValue == "dd/MM/yyyy", "Original DateTimeFormat was not preserved.");
}

var root = Path.Combine(AppContext.BaseDirectory, "migration-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
try
{
    foreach (var singular in new[] { false, true })
    foreach (var encrypted in new[] { false, true })
    {
        var source = Path.Combine(root, $"lite-{singular}-{encrypted}.db");
        var destination = source + ".new";
        var oldKey = encrypted ? "old-fixture-key" : null;
        SeedLite(source, oldKey, singular);
        var before = Hash(source);
        var result = LegacyDatabaseMigration.Run(source, destination, key, oldKey);
        Check(result.Format == "LiteDB" && before.SequenceEqual(Hash(source)), "Source LiteDB changed.");
        Check(File.Exists(result.BackupPath) && before.SequenceEqual(Hash(result.BackupPath)), "Source snapshot differs.");
        Verify(destination, key);
        using (var db = Open(destination, key))
        {
            Check(db.GetCollection("CustomCollection").Count() == 1, "Unknown LiteDB collection lost.");
            var stamp = db.GetCollection<Login>("Logins").FindById(4).SecurityStamp;
            using var first = new AppDbContext(db);
            using var second = new AppDbContext(db);
            Check(db.GetCollection<Login>("Logins").FindById(4).SecurityStamp == stamp, "Migration is not idempotent.");
            using var indexes = db.Execute("SELECT $ FROM $indexes WHERE collection = 'Companies' AND name = 'CompanyName'");
            Check(indexes.Read() && indexes.Current["unique"].AsBoolean, "Legacy index lost.");
        }
        Reject(() => { using var db = Open(destination, "wrong-key", true); _ = db.GetCollectionNames().ToList(); }, "Wrong destination key accepted.");
    }
    Console.WriteLine("PASS: encrypted/plaintext LiteDB, tb_/singular aliases, IDs, custom fields/collections/indexes, unchanged sources and idempotence.");

    foreach (var optional in new[] { false, true })
    {
        var source = Path.Combine(root, $"sqlite-{optional}.db");
        using (SeedSqlite(source, optional)) { }
        var before = Hash(source);
        var result = LegacyDatabaseMigration.Run(source, source + ".new", key);
        Check(result.Format == "SQLite" && before.SequenceEqual(Hash(source)), "Source SQLite changed.");
        Verify(result.DestinationPath, key, optional);
    }
    var walPath = Path.Combine(root, "sqlite-wal.db");
    using (var writer = SeedSqlite(walPath, wal: true))
    {
        var result = LegacyDatabaseMigration.Run(walPath, walPath + ".new", key);
        Verify(result.DestinationPath, key);
    }
    Console.WriteLine("PASS: SQLite conversion, WAL snapshot, optional expiry/time fields, DateTimeFormat and login preservation.");

    var conflict = Path.Combine(root, "conflict.db");
    SeedLite(conflict, null);
    using (var db = Open(conflict, null)) db.GetCollection("Companies").Insert(new BsonDocument { ["_id"] = 10 });
    Reject(() => LegacyDatabaseMigration.Run(conflict, conflict + ".new", key), "Conflicting collections accepted.");
    Check(!File.Exists(conflict + ".new"), "Failed migration was published.");

    var invalid = Path.Combine(root, "invalid.db");
    using (var connection = SeedSqlite(invalid))
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tb_CompEnv SET CompanyId=999";
        command.ExecuteNonQuery();
    }
    Reject(() => LegacyDatabaseMigration.Run(invalid, invalid + ".new", key), "Orphan reference accepted.");
    Check(!File.Exists(invalid + ".new"), "Invalid database was activated.");
    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = invalid, Pooling = false }.ToString()))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Unsupported(Id INTEGER)";
        command.ExecuteNonQuery();
    }
    Reject(() => LegacyDatabaseMigration.Run(invalid, invalid + ".unknown", key), "Unsupported SQLite table silently discarded.");

    var encryptedSource = Path.Combine(root, "protected.db");
    SeedLite(encryptedSource, "old-fixture-key");
    Reject(() => LegacyDatabaseMigration.Run(encryptedSource, encryptedSource + ".bad-key", key, "wrong"), "Wrong source key accepted.");
    Reject(() => LegacyDatabaseMigration.Run(encryptedSource, encryptedSource, key), "In-place migration accepted.");
    var existing = Path.Combine(root, "existing.db");
    File.WriteAllText(existing, "do-not-replace");
    Reject(() => LegacyDatabaseMigration.Run(encryptedSource, existing, key), "Existing destination replaced.");
    Check(File.ReadAllText(existing) == "do-not-replace", "Destination was altered.");
    var random = Path.Combine(root, "random.db");
    File.WriteAllBytes(random, RandomNumberGenerator.GetBytes(128));
    Reject(() => LegacyDatabaseMigration.Run(random, random + ".new", key), "Unknown file treated as empty database.");
    var empty = Path.Combine(root, "empty.db");
    File.WriteAllBytes(empty, []);
    Reject(() => LegacyDatabaseMigration.Run(empty, empty + ".new", key), "Empty source accepted.");
    Console.WriteLine("PASS: conflicts, orphans, unknown schemas/files, empty source, wrong keys and destination protection.");

    using (var memory = new MemoryStream())
    using (var db = new LiteDatabase(memory))
    {
        db.GetCollection("tb_Login").Insert(new BsonDocument { ["_id"] = 1, ["u_name"] = "fixture", ["u_pass"] = "fixture-password" });
        db.GetCollection("tb_Settings").Insert(new BsonDocument { ["_id"] = 1, ["SettingName"] = "Alerts.Config", ["SettingValue"] = "{invalid" });
        Reject(() => { using var context = new AppDbContext(db); }, "Malformed alerts silently accepted.");
        Check(db.CollectionExists("tb_Login") && !db.CollectionExists("Logins"), "Failed initialization left renamed collections.");
        Check(db.GetCollection("tb_Login").FindById(1).ContainsKey("u_pass"), "Failed initialization partially migrated credentials.");
    }
    var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Database:Path"] = Path.Combine(root, "offline.db"), ["Database:Password"] = key,
        ["Database:RotateKey"] = "true", ["Database:Migration:SourcePath"] = encryptedSource
    }).Build();
    Reject(() => new DatabaseSecurity(settings).MigrateLegacy(settings), "Migration and rotation were both allowed.");
    Console.WriteLine("PASS: atomic initialization rollback and mutually exclusive offline operations.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, true);
}
