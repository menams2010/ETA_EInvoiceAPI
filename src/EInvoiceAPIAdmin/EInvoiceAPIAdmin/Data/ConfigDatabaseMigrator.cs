using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using LiteDB;
using Microsoft.Data.Sqlite;

namespace InvoiceWebApp.Data;

/// <summary>
/// Recognizes every config.db format these apps have written and brings one up to the current format and schema.
/// Used by Admin > Migrate Database.
/// </summary>
/// <remarks>
/// Known versions (see docs/DATABASE_VERSIONS.md):
///   V1  2025-06  SQLite, tables tb_*, plain-text Admin passwords (EInvoiceAPI)
///   V2  2025-08  LiteDB 5, built-in password, collections Settings/Companies/CompEnvs/EInvoiceEnvs/Logins (EInvoiceAPI)
///   V3  2025-09  LiteDB 5, built-in password, collections tb_* (EInvoiceAPIAdmin)
///   V4  2026-01  LiteDB 5, built-in password, collections renamed to Settings/Companies/... (EInvoiceAPIAdmin)
///   V5  2026-01  + AlertRules, SmtpProfiles, EmailTemplates, AlertSendLogs
///   V6  2026-02  + AuditLogs
///   V7  2026-09  own password (Database:Password), hashed Admin passwords, Microsoft mail connections
///   V8  current  + SapEndpoints, NotificationSettings (merged ETA_EInvoiceAPI solution)
/// LiteDB 4 data files are also accepted: they are upgraded to LiteDB 5 first.
/// </remarks>
public sealed class ConfigDatabaseMigrator(DatabaseSecurity databaseSecurity)
{
    /// <summary>
    /// The password versions 2 to 6 used for every config.db (it was compiled into EInvoiceAPI and EInvoiceAPIAdmin).
    /// Needed to read those files; it protects nothing any more, because it is public in the old source code.
    /// </summary>
    internal const string BuiltInLegacyPassword = "@Mina&29012017&Mariam@";

    /// <summary>Written to Settings "DBVersion" after a migration.</summary>
    public const string CurrentSchemaVersion = "2026-11-06 00:00:00";

    public enum PasswordSource { None, Entered, BuiltIn, Current }

    public sealed record Analysis(
        string Version,
        string Description,
        string Format,
        PasswordSource Password,
        string? DbVersionSetting,
        IReadOnlyList<(string Collection, long Records)> Collections,
        IReadOnlyList<string> Notes);

    /// <summary>Copy of the uploaded file plus a workspace, inside the database folder (never the web root).</summary>
    public sealed class Upload : IDisposable
    {
        public string Folder { get; }
        public string SourcePath => System.IO.Path.Combine(Folder, "source.db");
        internal Upload(string folder) => Folder = folder;
        public void Dispose()
        {
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string WorkRoot => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(databaseSecurity.Path)!, ".migration-uploads");

    public Upload Save(Stream content)
    {
        RemoveStaleUploads();
        var folder = System.IO.Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var upload = new Upload(folder);
        using (var file = new FileStream(upload.SourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            content.CopyTo(file);
        return upload;
    }

    /// <summary>Re-opens an upload saved by <see cref="Save"/>, by its id (the folder name).</summary>
    public Upload? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 32 || !id.All(Uri.IsHexDigit)) return null;
        var upload = new Upload(System.IO.Path.Combine(WorkRoot, id));
        return File.Exists(upload.SourcePath) ? upload : null;
    }

    private void RemoveStaleUploads()
    {
        if (!Directory.Exists(WorkRoot)) return;
        foreach (var folder in Directory.GetDirectories(WorkRoot))
            if (Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddHours(-2)) new Upload(folder).Dispose();
    }

    // ---------- detection ----------

    public Analysis Analyze(Upload upload, string? enteredPassword)
    {
        var path = upload.SourcePath;
        if (new FileInfo(path).Length == 0) throw new InvalidOperationException("The file is empty.");

        if (LegacyDatabaseMigration.IsSqlite(path)) return AnalyzeSqlite(path);

        var (source, password, upgradedFromV4) = OpenLiteDb(upload, enteredPassword);
        using (source)
        {
            var names = source.GetCollectionNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var collections = names.Select(n => (n, source.GetCollection(n).LongCount())).ToList();
            var current = names.Select(AppDbContext.CurrentCollectionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? dbVersion = null;
            var settingsName = names.FirstOrDefault(n => AppDbContext.CurrentCollectionName(n) == "Settings");
            if (settingsName != null)
                dbVersion = source.GetCollection(settingsName).FindOne(Query.EQ("SettingName", "DBVersion"))?["SettingValue"].AsString;
            var loginsName = names.FirstOrDefault(n => AppDbContext.CurrentCollectionName(n) == "Logins");
            var hashed = loginsName != null && source.GetCollection(loginsName).FindAll().Any(d => d.ContainsKey("PasswordHash"));
            var compEnvsName = names.FirstOrDefault(n => AppDbContext.CurrentCollectionName(n) == "CompEnvs");
            var v2ExpiryField = compEnvsName != null && source.GetCollection(compEnvsName).FindAll().Any(d => d.ContainsKey("ClientSecretExpirationDate"));

            var notes = new List<string>();
            if (upgradedFromV4) notes.Add("This is a LiteDB 4 data file. It is upgraded to LiteDB 5 during the migration.");
            if (!hashed && loginsName != null) notes.Add("Admin passwords are stored in plain text in this file. They are converted to secure hashes; the same passwords keep working.");
            if (password == PasswordSource.BuiltIn) notes.Add("The file uses the built-in password of versions 2 to 6. The migrated data is protected with this server's own database password.");
            if (password == PasswordSource.None) notes.Add("The file is not encrypted. The migrated data is protected with this server's own database password.");
            if (current.Contains("GraphAppRegistrations") || current.Contains("GraphMailConnections"))
                notes.Add("Microsoft mail connections are copied, but they only work on a server with the same Data Protection keys. Reconnect them in SMTP Profiles if they fail.");

            var (version, description) =
                current.Contains("NotificationSettings") || current.Contains("SapEndpoints") ? ("V8", "Current version (merged ETA_EInvoiceAPI, 2026-11)") :
                hashed ? ("V7", "EInvoiceAPIAdmin with its own database password and hashed Admin passwords (2026-09)") :
                current.Contains("AuditLogs") ? ("V6", "EInvoiceAPIAdmin with the audit log (2026-02)") :
                current.Contains("AlertRules") || current.Contains("SmtpProfiles") ? ("V5", "EInvoiceAPIAdmin with expiry alerts and SMTP (2026-01)") :
                names.Any(n => n.StartsWith("tb_", StringComparison.OrdinalIgnoreCase)) ? ("V3", "EInvoiceAPIAdmin, collections named tb_* (2025-09)") :
                v2ExpiryField ? ("V2", "EInvoiceAPI on LiteDB (2025-08)") :
                ("V2/V4", "EInvoiceAPI on LiteDB (2025-08) or EInvoiceAPIAdmin with renamed collections (2026-01)");
            if (upgradedFromV4) (version, description) = ("LiteDB 4", "LiteDB 4 data file");
            if (current.Contains("SapEndpoints") || current.Contains("NotificationSettings"))
                notes.Add("This file is already the current version. Migrating it works like restoring a backup.");

            return new Analysis(version, description, upgradedFromV4 ? "LiteDB 4" : "LiteDB 5", password, dbVersion, collections, notes);
        }
    }

    private static Analysis AnalyzeSqlite(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var collections = new List<(string, long)>();
        foreach (var table in tables)
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"") + "\"";
            collections.Add((table, System.Convert.ToInt64(count.ExecuteScalar())));
        }
        return new Analysis("V1", "EInvoiceAPI on SQLite (2025-06)", "SQLite", PasswordSource.None, null, collections,
            ["Admin passwords are stored in plain text in this file. They are converted to secure hashes; the same passwords keep working.",
             "The migrated data is protected with this server's own database password."]);
    }

    /// <summary>
    /// Opens a copy of the upload, trying each known password; a LiteDB 4 file is upgraded (on a copy) first.
    /// The uploaded original is never opened by LiteDB: LiteDB 5 truncates a LiteDB 4 file to whole 8 KB pages
    /// even when it opens it read-only, which loses its last page of data.
    /// </summary>
    private (LiteDatabase Db, PasswordSource Password, bool UpgradedFromV4) OpenLiteDb(Upload upload, string? enteredPassword)
    {
        var candidates = new List<(string? Password, PasswordSource Source)>();
        if (!string.IsNullOrEmpty(enteredPassword)) candidates.Add((enteredPassword, PasswordSource.Entered));
        candidates.Add((null, PasswordSource.None));
        candidates.Add((databaseSecurity.CurrentPassword, PasswordSource.Current));
        candidates.Add((BuiltInLegacyPassword, PasswordSource.BuiltIn));

        var isV4 = IsLiteDb4(upload.SourcePath);
        var working = isV4 ? UpgradedPath(upload) : System.IO.Path.Combine(upload.Folder, "probe.db");
        foreach (var (password, source) in candidates)
        {
            DeleteUpgradeFiles(working); // also removes the "-backup" copy a failed upgrade leaves behind
            File.Copy(upload.SourcePath, working);
            try
            {
                if (isV4)
                    using (var db = new LiteDatabase(new ConnectionString { Filename = working, Password = password, Upgrade = true, Connection = ConnectionType.Direct }))
                        _ = db.GetCollectionNames().ToList();
                var opened = TryOpen(working, password);
                if (opened != null) return (opened, source, isV4);
            }
            catch (Exception) { /* not this password */ }
        }
        DeleteUpgradeFiles(working);

        throw new InvalidOperationException(string.IsNullOrEmpty(enteredPassword)
            ? "This file could not be opened. It is not a known config.db format, or it uses a password setup does not know: enter the file's password and try again."
            : "This file could not be opened with the password you entered, or with any known password. Check the password, or that the file is a config.db.");
    }

    /// <summary>LiteDB 4 header page: "** This is a LiteDB file **" at byte 25, file format 7 at byte 52 (never encrypted).</summary>
    private static bool IsLiteDb4(string path)
    {
        var header = new byte[53];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (stream.Read(header, 0, header.Length) != header.Length) return false;
        return System.Text.Encoding.ASCII.GetString(header, 25, 27) == "** This is a LiteDB file **" && header[52] == 7;
    }

    private static void DeleteUpgradeFiles(string upgraded)
    {
        var folder = System.IO.Path.GetDirectoryName(upgraded)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(upgraded);
        foreach (var file in Directory.GetFiles(folder, name + "*"))
            File.Delete(file);
    }

    private static string UpgradedPath(Upload upload) => System.IO.Path.Combine(upload.Folder, "source-v5.db");

    private static LiteDatabase? TryOpen(string path, string? password)
    {
        LiteDatabase? db = null;
        try
        {
            db = new LiteDatabase(new ConnectionString { Filename = path, Password = password, ReadOnly = true, Connection = ConnectionType.Direct });
            _ = db.GetCollectionNames().ToList(); // forces reading (and decrypting) the header
            return db;
        }
        catch (Exception)
        {
            db?.Dispose();
            return null;
        }
    }

    // ---------- conversion ----------

    /// <summary>
    /// Converts the upload to a current-format database encrypted with this server's password, in the upload folder.
    /// Every record is checked (LegacyDatabaseMigration). Returns the converted file and what came from the source.
    /// </summary>
    public (string Path, LegacyDatabaseMigration.Result Result, Analysis Analysis) Convert(Upload upload, string? enteredPassword)
    {
        var analysis = Analyze(upload, enteredPassword);
        var source = analysis.Format == "LiteDB 4" ? UpgradedPath(upload) : upload.SourcePath;
        var password = analysis.Password switch
        {
            PasswordSource.Entered => enteredPassword,
            PasswordSource.BuiltIn => BuiltInLegacyPassword,
            PasswordSource.Current => databaseSecurity.CurrentPassword,
            _ => null
        };
        var converted = System.IO.Path.Combine(upload.Folder, "converted", "config.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(converted)!);
        if (File.Exists(converted)) File.Delete(converted);
        var result = LegacyDatabaseMigration.Run(source, converted, databaseSecurity.CurrentPassword, password);
        using (var db = new LiteDatabase(new ConnectionString { Filename = converted, Password = databaseSecurity.CurrentPassword, Connection = ConnectionType.Direct }))
            StampVersion(db);
        return (converted, result, analysis);
    }

    /// <summary>
    /// Replaces the live database's data with the upload's, collection by collection, in one transaction.
    /// Collections the old file does not have (for example SAP endpoints when migrating V1-V7) are left as they are.
    /// A backup of the live database is written next to it first. Returns the backup path and the imported collections.
    /// </summary>
    public (string BackupPath, IReadOnlyList<(string Collection, long Records)> Imported, Analysis Analysis) Import(
        Upload upload, string? enteredPassword, LiteDatabase live)
    {
        var (converted, result, analysis) = Convert(upload, enteredPassword);

        var backupPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(databaseSecurity.Path)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(databaseSecurity.Path)}.before-migration-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        File.WriteAllBytes(backupPath, databaseSecurity.CreateBackup(live));

        var imported = new List<(string, long)>();
        using (var source = new LiteDatabase(new ConnectionString { Filename = converted, Password = databaseSecurity.CurrentPassword, ReadOnly = true, Connection = ConnectionType.Direct }))
        {
            if (!live.BeginTrans()) throw new InvalidOperationException("Unable to start the migration transaction.");
            try
            {
                foreach (var name in result.SourceCollections.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    var documents = source.GetCollection(name).FindAll().ToList();
                    if (name == "Logins")
                    {
                        if (documents.Count == 0) throw new InvalidOperationException("The old database has no Admin accounts.");
                        foreach (var document in documents) AccountSecurity.NormalizeDocument(document, renewStamp: true);
                    }
                    var target = live.GetCollection(name);
                    target.DeleteAll();
                    foreach (var document in documents) target.Insert(document);
                    imported.Add((name, documents.Count));
                }
                StampVersion(live);
                live.Commit();
            }
            catch
            {
                live.Rollback();
                throw;
            }
        }
        // Indexes and default records for anything the old version did not have.
        using (new AppDbContext(live)) { }
        return (backupPath, imported, analysis);
    }

    private static void StampVersion(LiteDatabase db)
    {
        var settings = db.GetCollection<Settings>("Settings");
        var version = settings.FindOne(x => x.SettingName == "DBVersion");
        if (version == null)
        {
            var nextId = settings.FindAll().Select(x => x.Id).DefaultIfEmpty(0).Max() + 1;
            settings.Insert(new Settings { Id = nextId, SettingName = "DBVersion", SettingValue = CurrentSchemaVersion });
        }
        else if (version.SettingValue != CurrentSchemaVersion)
        {
            version.SettingValue = CurrentSchemaVersion;
            settings.Update(version);
        }
    }
}
