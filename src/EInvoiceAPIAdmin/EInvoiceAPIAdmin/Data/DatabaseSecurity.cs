using InvoiceWebApp.Models;
using InvoiceWebApp.Services;
using LiteDB;
using LiteDB.Engine;

namespace InvoiceWebApp.Data;

public sealed class DatabaseSecurity
{
    public string Path { get; }
    private readonly string _password;

    public DatabaseSecurity(IConfiguration configuration)
    {
        Path = System.IO.Path.GetFullPath(configuration["Database:Path"]
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "..", "config.db"));
        _password = configuration["Database:Password"] ?? string.Empty;
        if (_password.Length < 32)
            throw new InvalidOperationException("Configure Database:Password with a random secret of at least 32 characters. See docs/security-hardening.md.");
    }

    // The live database is opened in shared mode because EInvoiceAPI reads and writes the same
    // config.db (tokens, companies, settings) while this app is running. Offline operations
    // below (backup copy, key rotation, legacy migration) keep exclusive Direct access.
    public LiteDatabase Open()
    {
        // Lets the other IIS app pools (EInvoiceAPI, ETANotifications) open the file in shared mode too.
        ETA.Shared.LiteDbSharedMutex.EnsureAccessible(Path);
        return OpenFile(Path, _password, connection: ConnectionType.Shared);
    }

    public LegacyDatabaseMigration.Result MigrateLegacy(IConfiguration configuration)
    {
        var destination = configuration["Database:Path"];
        if (string.IsNullOrWhiteSpace(destination) || !System.IO.Path.IsPathFullyQualified(destination))
            throw new InvalidOperationException("Offline migration requires an explicit absolute Database:Path for a new destination.");
        if (configuration.GetValue<bool>("Database:RotateKey"))
            throw new InvalidOperationException("Do not enable key rotation and legacy migration in the same run.");
        return LegacyDatabaseMigration.Run(configuration["Database:Migration:SourcePath"] ?? string.Empty,
            destination, _password, configuration["Database:Migration:SourcePassword"]);
    }

    public LiteDatabase OpenBackup(string path, string? password) => OpenFile(path, password ?? _password, true);

    public byte[] CreateBackup(LiteDatabase source)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        if (!source.BeginTrans()) throw new InvalidOperationException("Unable to start backup transaction.");
        try
        {
            using (var backup = OpenFile(temporary, _password))
            {
                foreach (var name in source.GetCollectionNames().ToList())
                {
                    var destination = backup.GetCollection(name);
                    foreach (var document in source.GetCollection(name).FindAll())
                        destination.Insert(document);
                }
                using var indexes = source.Execute("SELECT $ FROM $indexes");
                while (indexes.Read())
                {
                    var index = indexes.Current.AsDocument;
                    if (index["name"].AsString != "_id")
                        backup.GetCollection(index["collection"].AsString).EnsureIndex(
                            index["name"].AsString, index["expression"].AsString, index["unique"].AsBoolean);
                }
                backup.Checkpoint();
            }
            return File.ReadAllBytes(temporary);
        }
        finally
        {
            source.Rollback();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static LiteDatabase OpenFile(string path, string password, bool readOnly = false,
        ConnectionType connection = ConnectionType.Direct) => new(new ConnectionString
    {
        Filename = path,
        Password = password,
        Connection = connection,
        ReadOnly = readOnly
    });

    public void RotateKey(IConfiguration configuration)
    {
        var previous = configuration["Database:PreviousPassword"];
        if (string.IsNullOrEmpty(previous) || previous == _password || !File.Exists(Path))
            throw new InvalidOperationException("Offline rotation requires an existing database and a different Database:PreviousPassword.");
        var staged = Path + ".rotation";
        var backup = Path + ".pre-security-backup";
        if (File.Exists(staged) || File.Exists(backup))
            throw new InvalidOperationException("Rotation artifacts already exist. Review them before retrying; never overwrite the recovery backup.");
        using (var original = OpenFile(Path, previous))
        {
            original.Checkpoint();
        }
        File.Copy(Path, staged, false);
        try
        {
            using (var copy = OpenFile(staged, previous))
            {
                copy.Rebuild(new RebuildOptions { Password = _password });
                copy.Checkpoint();
            }
            using (var verified = OpenFile(staged, _password))
            {
                foreach (var collection in verified.GetCollectionNames())
                    _ = verified.GetCollection(collection).Count();
            }
            File.Replace(staged, Path, backup);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public static void InitializeAccounts(LiteDatabase db, IConfiguration configuration)
    {
        using var context = new AppDbContext(db);
        if (context.Logins.Count() != 0) return;
        var name = configuration["Bootstrap:Username"];
        var password = configuration["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || password == null || password.Length is < 12 or > 256)
            throw new InvalidOperationException("An empty account database requires Bootstrap:Username and Bootstrap:Password (12–256 characters) from secure configuration.");
        var user = new Login { u_name = name.Trim(), IsInitial = true };
        AccountSecurity.SetPassword(user, password);
        context.Logins.Insert(user);
    }
}
