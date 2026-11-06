// ETA.SetupTool: prepares the shared config.db for EInvoiceAPI-Setup.exe.
//
//   ETA.SetupTool check       verify that ETA_DB_PASSWORD opens the existing ETA_DB_PATH
//   ETA.SetupTool configure   create/upgrade config.db, create the first Admin account (fresh databases only)
//                             and save the SAP endpoints that were entered
//
// All values, secrets included, come from environment variables so they never appear on a command line:
//   ETA_DB_PATH, ETA_DB_PASSWORD                      required
//   ETA_ADMIN_USER, ETA_ADMIN_PASSWORD                used only when the database has no accounts yet
//   ETA_SAP_{PROD|DEV}_{URL|USER|PASSWORD|TIMEOUT}    empty = leave the saved value unchanged
//   ETA_SETUP_LOG                                     optional file that receives the messages below
//
// Exit codes: 0 ok, 1 error, 2 wrong database password, 3 database not found (check only).
using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using LiteDB;
using Microsoft.Extensions.Configuration;

var log = Environment.GetEnvironmentVariable("ETA_SETUP_LOG");
void Say(string message)
{
    Console.WriteLine(message);
    if (!string.IsNullOrEmpty(log)) File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
}
string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

var command = args.FirstOrDefault()?.ToLowerInvariant();
if (command is not ("check" or "configure"))
{
    Say("Usage: ETA.SetupTool check|configure (settings come from ETA_* environment variables)");
    return 1;
}

var dbPath = Env("ETA_DB_PATH");
var dbPassword = Env("ETA_DB_PASSWORD");
if (string.IsNullOrWhiteSpace(dbPath) || !Path.IsPathFullyQualified(dbPath))
{
    Say("ETA_DB_PATH must be an absolute path.");
    return 1;
}

var settings = new Dictionary<string, string?>
{
    ["Database:Path"] = dbPath,
    ["Database:Password"] = dbPassword,
    ["Bootstrap:Username"] = Env("ETA_ADMIN_USER"),
    ["Bootstrap:Password"] = Env("ETA_ADMIN_PASSWORD"),
};
var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

try
{
    var security = new DatabaseSecurity(configuration); // validates the password length (32+)

    if (command == "check")
    {
        if (!File.Exists(dbPath)) { Say($"Not found: {dbPath}"); return 3; }
        using var existing = security.Open();
        _ = existing.GetCollectionNames().ToList(); // forces decryption
        Say("Database password OK.");
        return 0;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var isNew = !File.Exists(dbPath);
    using var db = security.Open();

    // Same initialization the Admin app runs: legacy collection migration, indexes and default data.
    using (var schema = new AppDbContext(db)) { }
    Say(isNew ? $"Created {dbPath}" : $"Upgraded schema of {dbPath}");

    using var ctx = new AppDbContext(db);
    if (ctx.Logins.Count() == 0)
    {
        DatabaseSecurity.InitializeAccounts(db, configuration); // throws if no admin user/password was given
        Say($"Created Admin account '{Env("ETA_ADMIN_USER").Trim()}'.");
    }
    else
    {
        Say("Admin accounts already exist; first-account settings ignored.");
    }

    foreach (var name in new[] { SapEndpoint.Prod, SapEndpoint.Dev })
    {
        var prefix = $"ETA_SAP_{name.ToUpperInvariant()}_";
        string url = Env(prefix + "URL"), user = Env(prefix + "USER"), password = Env(prefix + "PASSWORD"), timeout = Env(prefix + "TIMEOUT");
        if (url.Length + user.Length + password.Length + timeout.Length == 0) continue;

        var endpoint = ctx.SapEndpoints.FindOne(x => x.Name == name) ?? new SapEndpoint { Name = name };
        if (url.Length > 0)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"SAP {name} URL is not an absolute http(s) URL.");
            endpoint.Url = url;
        }
        if (user.Length > 0) endpoint.Username = user;
        if (password.Length > 0) endpoint.Password = password;
        if (timeout.Length > 0)
        {
            if (!int.TryParse(timeout, out var ms) || ms is < 500 or > 120000)
                throw new InvalidOperationException($"SAP {name} timeout must be 500-120000 ms.");
            endpoint.TimeoutMs = ms;
        }
        ctx.SapEndpoints.Upsert(endpoint);
        Say($"Saved SAP endpoint {name}" + (password.Length > 0 ? " (password set)." : "."));
    }

    return 0;
}
catch (LiteException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
{
    Say("The database password is wrong for the existing config.db.");
    return 2;
}
catch (Exception ex)
{
    Say("Error: " + ex.Message);
    return 1;
}
