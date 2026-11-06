using System.Globalization;
using System.Reflection;
using System.Text;
using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using LiteDB;
using LiteDB.Engine;
using Microsoft.Data.Sqlite;

namespace InvoiceWebApp.Data;

public static class LegacyDatabaseMigration
{
    /// <param name="SourceCollections">Collections (current names) that came from the source file, as opposed to the
    /// defaults the current schema seeds for collections the source did not have.</param>
    public sealed record Result(string Format, string DestinationPath, string BackupPath,
        IReadOnlyDictionary<string, long> RecordCounts, bool RequiresDataProtectionKeys,
        IReadOnlyCollection<string> SourceCollections);

    private static readonly IReadOnlyDictionary<string, Type> Models = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["Companies"] = typeof(Company), ["CompEnvs"] = typeof(CompEnv),
        ["EInvoiceEnvs"] = typeof(EInvoiceEnv), ["Settings"] = typeof(Settings), ["Logins"] = typeof(Login)
    };

    public static Result Run(string sourcePath, string destinationPath, string password, string? sourcePassword = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath)
            || !Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationPath))
            throw new InvalidOperationException("Migration requires explicit absolute source and destination paths.");
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            || File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new InvalidOperationException("Migration requires a separate, nonexistent destination. Existing databases are never replaced.");
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
            throw new InvalidOperationException("The migration source must be an existing, nonempty database.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 32)
            throw new InvalidOperationException("The destination database requires a random password of at least 32 characters.");
        var parent = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(parent)) throw new InvalidOperationException("Create and restrict access to the destination directory first.");

        var sqlite = IsSqlite(sourcePath);
        if (sqlite && !string.IsNullOrEmpty(sourcePassword))
            throw new InvalidOperationException("Encrypted SQLite is not supported. Supply an independently decrypted SQLite copy.");
        var work = Path.Combine(parent, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var backup = Path.Combine(work, "source.db");
        var staged = Path.Combine(work, "migrated.db");
        var expected = new Dictionary<string, List<BsonDocument>>(StringComparer.OrdinalIgnoreCase);
        var indexes = new List<BsonDocument>();

        if (sqlite)
        {
            using (var source = OpenSqlite(sourcePath, SqliteOpenMode.ReadOnly))
            using (var snapshot = OpenSqlite(backup, SqliteOpenMode.ReadWriteCreate))
                source.BackupDatabase(snapshot);
            ReadSqlite(backup, expected);
        }
        else
        {
            var log = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath) + "-log" + Path.GetExtension(sourcePath));
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (File.Exists(log))
                    throw new InvalidOperationException("The LiteDB source has a journal. Stop/checkpoint it with the original application before migration; do not delete the journal.");
                using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            using var snapshotStream = new MemoryStream(File.ReadAllBytes(backup), writable: false);
            using var source = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = snapshotStream, Password = sourcePassword, ReadOnly = true
            }));
            foreach (var name in source.GetCollectionNames())
                AddCollection(expected, AppDbContext.CurrentCollectionName(name), source.GetCollection(name).FindAll().ToList());
            using var reader = source.Execute("SELECT $ FROM $indexes");
            while (reader.Read())
            {
                var index = reader.Current.AsDocument;
                if (index["name"].AsString != "_id") indexes.Add(index);
            }
        }

        if (!expected.Keys.Any(Models.ContainsKey))
            throw new InvalidOperationException("No recognized application schema was found. The source was not treated as a new database.");
        foreach (var (name, documents) in expected)
            foreach (var document in documents) Normalize(name, document);
        if (!expected.TryGetValue("Logins", out var accounts) || accounts.Count == 0)
            throw new InvalidOperationException("Migration requires existing accounts. An empty or unrecognized account schema cannot be activated.");
        if (accounts.Select(d => d["u_name"].AsString).Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Count)
            throw new InvalidOperationException("Duplicate account names require reconciliation before migration.");
        ValidateSourceReferences(expected);

        using (var target = OpenLiteDb(staged, password, false))
        {
            if (!target.BeginTrans()) throw new InvalidOperationException("Unable to start migration transaction.");
            try
            {
                foreach (var (name, documents) in expected)
                {
                    var collection = target.GetCollection(name);
                    foreach (var document in documents)
                    {
                        var copy = BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
                        if (name.Equals("Logins", StringComparison.OrdinalIgnoreCase)) AccountSecurity.NormalizeDocument(copy);
                        collection.Insert(copy);
                    }
                    collection.EnsureIndex("_id");
                }
                foreach (var index in indexes)
                    target.GetCollection(AppDbContext.CurrentCollectionName(index["collection"].AsString)).EnsureIndex(
                        index["name"].AsString, index["expression"].AsString, index["unique"].AsBoolean);
                target.Commit();
            }
            catch { target.Rollback(); throw; }
            using var context = new AppDbContext(target);
            target.Checkpoint();
        }

        Dictionary<string, long> counts;
        using (var verified = OpenLiteDb(staged, password, true))
        {
            ValidateRecords(expected, verified);
            ValidateCurrentSchema(verified);
            using var actualIndexes = verified.Execute("SELECT $ FROM $indexes");
            var actual = new List<BsonDocument>();
            while (actualIndexes.Read()) actual.Add(actualIndexes.Current.AsDocument);
            foreach (var index in indexes)
                Require(actual.Any(x => x["collection"].AsString.Equals(AppDbContext.CurrentCollectionName(index["collection"].AsString), StringComparison.OrdinalIgnoreCase)
                    && x["name"] == index["name"] && x["expression"] == index["expression"] && x["unique"] == index["unique"]),
                    "A source index was not preserved.");
            counts = verified.GetCollectionNames().ToDictionary(n => n, n => verified.GetCollection(n).LongCount());
        }
        File.Move(staged, destinationPath, overwrite: false);
        return new Result(sqlite ? "SQLite" : "LiteDB", destinationPath, backup, counts,
            counts.GetValueOrDefault("GraphAppRegistrations") > 0 || counts.GetValueOrDefault("GraphMailConnections") > 0,
            expected.Keys.ToList());
    }

    internal static bool IsSqlite(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[16];
        return stream.Read(header) == header.Length && header.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0"));
    }

    private static SqliteConnection OpenSqlite(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static LiteDatabase OpenLiteDb(string path, string? password, bool readOnly) => new(new ConnectionString
    {
        Filename = path, Password = password, ReadOnly = readOnly, Connection = ConnectionType.Direct
    });

    private static void AddCollection(Dictionary<string, List<BsonDocument>> data, string name, List<BsonDocument> documents)
    {
        if (data.TryGetValue(name, out var previous) && previous.Count > 0)
        {
            if (documents.Count > 0) throw new InvalidOperationException($"Conflicting legacy/current data for {name}.");
            return;
        }
        data[name] = documents;
    }

    private static void ReadSqlite(string path, Dictionary<string, List<BsonDocument>> data)
    {
        using var connection = OpenSqlite(path, SqliteOpenMode.ReadOnly);
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Require(string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase), "SQLite integrity validation failed.");
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        var tables = new List<string>();
        using (var reader = schema.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (var table in tables)
        {
            var name = AppDbContext.CurrentCollectionName(table);
            if (!Models.ContainsKey(name)) throw new InvalidOperationException("Unsupported SQLite tables require an explicit mapping; migration was stopped without skipping data.");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var fields = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(fields.Contains("Id"), "A SQLite table has no Id column.");
            if (name == "Logins") Require(fields.Contains("u_name") && (fields.Contains("u_pass") || fields.Contains("PasswordHash")), "Unsupported SQLite account schema.");
            if (name == "CompEnvs") Require(fields.Contains("CompanyId") && fields.Contains("EnvId"), "Company-environment relationship columns are missing.");
            var documents = new List<BsonDocument>();
            while (reader.Read())
            {
                var document = new BsonDocument();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var field = reader.GetName(i);
                    var property = Models[name].GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    document[field.Equals("Id", StringComparison.OrdinalIgnoreCase) ? "_id" : property?.Name ?? field] =
                        reader.IsDBNull(i) ? BsonValue.Null : new BsonValue(reader.GetValue(i));
                }
                documents.Add(document);
            }
            AddCollection(data, name, documents);
        }
    }

    private static void Normalize(string name, BsonDocument document)
    {
        if (!Models.TryGetValue(name, out var model)) return;
        // Version 2 (EInvoiceAPI, 2025-08) stored the client secret expiry as ClientSecretExpirationDate (a DateOnly).
        if (name == "CompEnvs" && document.TryGetValue("ClientSecretExpirationDate", out var oldExpiry))
        {
            if (!document.ContainsKey("ClientSecretExpiryDate") || document["ClientSecretExpiryDate"].IsNull)
                document["ClientSecretExpiryDate"] = LegacyDate(oldExpiry);
            document.Remove("ClientSecretExpirationDate");
        }
        Require(document.TryGetValue("_id", out var id) && !id.IsNull, "A record is missing its ID.");
        document["_id"] = checked((int)Integer(id!));
        Require(document["_id"].AsInt32 > 0, "IDs must be positive integers.");
        foreach (var property in model.GetProperties())
        {
            if (property.Name == "Id" || !document.TryGetValue(property.Name, out var value) || value.IsNull) continue;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(int)) document[property.Name] = checked((int)Integer(value));
            else if (type == typeof(long)) document[property.Name] = Integer(value);
            else if (type == typeof(bool)) document[property.Name] = Boolean(value);
            else if (type == typeof(DateTime)) document[property.Name] = value.IsDateTime ? value : new BsonValue(
                DateTime.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            else if (type == typeof(string)) document[property.Name] = Text(value);
        }
        if (name == "Companies" && document.TryGetValue("UseXMLTOJSON", out var xml))
            document["UseXMLTOJSON"] = xml.IsNull || string.IsNullOrWhiteSpace(Text(xml)) ? "false" : Boolean(xml) ? "true" : "false";
        if (name == "Logins")
        {
            Require(document.TryGetValue("u_name", out var user) && user.IsString && !string.IsNullOrWhiteSpace(user.AsString), "An account has no username.");
            Require((document.TryGetValue("u_pass", out var pass) && pass.IsString)
                || (document.TryGetValue("PasswordHash", out var hash) && hash.IsString && !string.IsNullOrEmpty(hash.AsString)), "An account has no password or hash.");
        }
    }

    private static BsonValue LegacyDate(BsonValue value)
    {
        if (value.IsNull) return BsonValue.Null;
        if (value.IsDateTime) return value;
        if (value.IsDocument)
        {
            // DateOnly serialized by an older BsonMapper as its public properties.
            var date = value.AsDocument;
            if (date.TryGetValue("Year", out var y) && date.TryGetValue("Month", out var m) && date.TryGetValue("Day", out var d))
                return new BsonValue(new DateTime((int)Integer(y), (int)Integer(m), (int)Integer(d)));
            if (date.TryGetValue("DayNumber", out var n)) return new BsonValue(DateOnly.FromDayNumber((int)Integer(n)).ToDateTime(TimeOnly.MinValue));
            throw new InvalidOperationException("Unrecognized client secret expiry date.");
        }
        var text = Text(value).Trim();
        if (text.Length == 0) return BsonValue.Null;
        return new BsonValue(DateTime.Parse(text, CultureInfo.InvariantCulture));
    }

    private static string Text(BsonValue value) => Convert.ToString(value.RawValue, CultureInfo.InvariantCulture) ?? string.Empty;
    private static long Integer(BsonValue value) => long.Parse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static bool Boolean(BsonValue value) => Text(value).Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw new InvalidOperationException("Invalid legacy boolean value.")
    };

    private static void ValidateSourceReferences(Dictionary<string, List<BsonDocument>> data)
    {
        var companies = data.GetValueOrDefault("Companies", []).Select(d => (long)d["_id"].AsInt32).ToHashSet();
        var environments = data.GetValueOrDefault("EInvoiceEnvs", []).Select(d => (long)d["_id"].AsInt32).ToHashSet();
        foreach (var document in data.GetValueOrDefault("CompEnvs", []))
            Require(companies.Contains(document["CompanyId"].AsInt64) && environments.Contains(document["EnvId"].AsInt64), "An orphaned company-environment relationship was found.");
    }

    private static void ValidateRecords(Dictionary<string, List<BsonDocument>> expected, LiteDatabase target)
    {
        foreach (var (name, documents) in expected)
        {
            var collection = target.GetCollection(name);
            Require(collection.LongCount() >= documents.Count, "Migration lost records.");
            if (documents.Count > 0) Require(collection.LongCount() == documents.Count, "Migration changed a populated collection count.");
            foreach (var original in documents)
            {
                var migrated = collection.FindById(original["_id"]) ?? throw new InvalidOperationException("Migration lost an ID.");
                foreach (var field in original.Keys)
                {
                    if (name.Equals("Logins", StringComparison.OrdinalIgnoreCase) && field is "u_pass" or "PasswordHash" or "SecurityStamp") continue;
                    Require(original[field].Equals(migrated[field]), "Migration changed an original field unexpectedly.");
                }
                if (name.Equals("Logins", StringComparison.OrdinalIgnoreCase))
                {
                    var account = BsonMapper.Global.ToObject<Login>(migrated);
                    Require(!migrated.ContainsKey("u_pass") && !string.IsNullOrEmpty(account.PasswordHash) && !string.IsNullOrEmpty(account.SecurityStamp), "Account security migration failed.");
                    Require(Convert.FromBase64String(account.PasswordHash).Length >= 49, "An account password hash is invalid.");
                    if (original.TryGetValue("u_pass", out var password)) Require(AccountSecurity.Verify(account, password.AsString), "An original password no longer verifies.");
                    else Require(original["PasswordHash"].AsString == account.PasswordHash, "An existing password hash changed.");
                }
            }
        }
    }

    private static void ValidateCurrentSchema(LiteDatabase db)
    {
        foreach (var (name, type) in Models)
            foreach (var document in db.GetCollection(name).FindAll()) _ = BsonMapper.Global.ToObject(type, document);
        var profiles = db.GetCollection<SmtpProfile>("SmtpProfiles").FindAll().Select(p => p.Id).ToHashSet();
        var templates = db.GetCollection<EmailTemplate>("EmailTemplates").FindAll().Select(t => t.Id).ToHashSet();
        foreach (var rule in db.GetCollection<AlertRule>("AlertRules").FindAll())
            Require((rule.SmtpProfileId is null || profiles.Contains(rule.SmtpProfileId.Value))
                && (rule.EmailTemplateId is null || templates.Contains(rule.EmailTemplateId.Value)), "An alert rule has an invalid profile/template reference.");
        foreach (var name in new[] { "GraphAppRegistrations", "GraphMailConnections" })
            foreach (var document in db.GetCollection(name).FindAll())
                Require(profiles.Contains(document["_id"].AsInt32), "Graph data refers to a missing mail profile.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
