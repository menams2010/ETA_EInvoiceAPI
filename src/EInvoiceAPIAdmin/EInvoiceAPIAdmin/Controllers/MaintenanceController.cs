using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using LiteDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using InvoiceWebApp.Services;
using System.Security.Cryptography;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class MaintenanceController(AppDbContext ctx, LiteDatabase liteDb, IWebHostEnvironment env, DatabaseSecurity databaseSecurity) : Controller
{
    private readonly AppDbContext _ctx = ctx;
    private readonly LiteDatabase _liteDb = liteDb;
    private readonly IWebHostEnvironment _env = env;
    private readonly DatabaseSecurity _databaseSecurity = databaseSecurity;

    private static readonly string[] KnownTables = new[]
    {
        "EInvoiceEnvs",
        "Settings",
        "Companies",
        "CompEnvs",
        "Logins",
        "AlertRules",
        "SmtpProfiles",
        "EmailTemplates"
    };

    private static HashSet<string>? ParseTables(string? tables)
    {
        if (string.IsNullOrWhiteSpace(tables)) return null; // null means all
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tables.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (!string.IsNullOrEmpty(t)) set.Add(t);
        }
        return set.Count == 0 ? null : set;
    }

    private static bool ShouldProcess(string table, HashSet<string>? selected) => selected is null || selected.Contains(table);

    [HttpGet]
    public IActionResult Index(string? message = null, string? error = null)
    {
        ViewBag.Message = message;
        ViewBag.Error = error;
        return View();
    }

    // BACKUP: returns the encrypted LiteDB file as a download
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Backup()
    {
        try
        {
            var bytes = databaseSecurity.CreateBackup(_liteDb);
            var fileName = $"config_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
            return File(bytes, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            return RedirectToAction(nameof(Index), new { error = ex.Message });
        }
    }

    // RESTORE: import all data from an uploaded LiteDB file into current database (drop-and-copy per collection)
    // Optionally restrict by comma/semicolon separated table names (use LiteDB collection names e.g. tb_Company)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Restore(IFormFile backupFile, string? tables, string? backupPassword)
    {
        if (backupFile == null || backupFile.Length == 0)
            return RedirectToAction(nameof(Index), new { error = "Please select a valid backup file." });

        var tmpPath = Path.GetTempFileName();
        try
        {
            using (var fs = System.IO.File.OpenWrite(tmpPath))
            {
                backupFile.CopyTo(fs);
            }

            var selected = ParseTables(tables);
            var processed = new List<string>();
            using var src = databaseSecurity.OpenBackup(tmpPath, string.IsNullOrEmpty(backupPassword) ? null : backupPassword);
            if (!_liteDb.BeginTrans()) throw new InvalidOperationException("Unable to start restore transaction.");

            if (ShouldProcess("EInvoiceEnvs", selected)) { CopyCollection(src, _liteDb, "EInvoiceEnvs"); processed.Add("EInvoiceEnvs"); }
            if (ShouldProcess("Settings", selected)) { CopyCollection(src, _liteDb, "Settings"); processed.Add("Settings"); }
            if (ShouldProcess("Companies", selected)) { CopyCollection(src, _liteDb, "Companies"); processed.Add("Companies"); }
            if (ShouldProcess("CompEnvs", selected)) { CopyCollection(src, _liteDb, "CompEnvs"); processed.Add("CompEnvs"); }
            if (ShouldProcess("Logins", selected)) { CopyCollection(src, _liteDb, "Logins"); processed.Add("Logins"); }
            if (ShouldProcess("AlertRules", selected)) { CopyCollection(src, _liteDb, "AlertRules"); processed.Add("AlertRules"); }
            if (ShouldProcess("SmtpProfiles", selected)) { CopyCollection(src, _liteDb, "SmtpProfiles"); processed.Add("SmtpProfiles"); }
            if (ShouldProcess("EmailTemplates", selected)) { CopyCollection(src, _liteDb, "EmailTemplates"); processed.Add("EmailTemplates"); }

            // Normalize after restore only if companies were included or all tables
            if (ShouldProcess("Companies", selected))
            {
                NormalizeCompaniesUseXmlToJson();
            }

            _liteDb.Commit();

            if (selected is not null && processed.Count == 0)
            {
                // Nothing recognized was requested
                var known = string.Join(", ", KnownTables);
                return RedirectToAction(nameof(Index), new { error = $"No recognized tables selected. Known tables: {known}" });
            }

            var msg = selected is null
                ? "Database restored successfully (all tables)."
                : $"Database restored successfully. Tables: {string.Join(", ", processed)}";

            return RedirectToAction(nameof(Index), new { message = msg });
        }
        catch (Exception)
        {
            _liteDb.Rollback();
            return RedirectToAction(nameof(Index), new { error = "Restore failed. Verify the backup file, password, and account data. No restore changes were committed." });
        }
        finally
        {
            System.IO.File.Delete(tmpPath);
        }
    }

    private static void CopyCollection(LiteDatabase src, LiteDatabase dest, string name)
    {
        var srcCol = src.GetCollection(name);
        var destCol = dest.GetCollection(name);
        var documents = srcCol.FindAll().ToList();
        if (name == "Logins")
        {
            if (documents.Count == 0) throw new InvalidOperationException("Cannot restore an empty account collection.");
            foreach (var document in documents) AccountSecurity.NormalizeDocument(document, renewStamp: true);
        }

        // Clear destination
        destCol.DeleteAll();

        // Enumerate all docs and insert
        foreach (var doc in documents)
        {
            destCol.Upsert(doc);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ImportFromSqlite(IFormFile legacyFile, string destinationPath, string destinationPassword, string? sourcePassword)
    {
        if (legacyFile == null || legacyFile.Length == 0)
            return RedirectToAction(nameof(Index), new { error = "Please select a legacy SQLite or LiteDB file." });
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
            return RedirectToAction(nameof(Index), new { error = "Enter a new absolute destination path for the migrated LiteDB database." });
        if (string.IsNullOrWhiteSpace(destinationPassword) || destinationPassword.Length < 32)
            return RedirectToAction(nameof(Index), new { error = "Enter a random destination password of at least 32 characters." });

        var uploadDirectory = Path.Combine(_env.ContentRootPath, "App_Data", "migration-uploads");
        Directory.CreateDirectory(uploadDirectory);
        var extension = Path.GetExtension(legacyFile.FileName);
        var sourcePath = Path.Combine(uploadDirectory, Guid.NewGuid().ToString("N") + (string.IsNullOrWhiteSpace(extension) ? ".db" : extension));
        try
        {
            using (var fs = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                legacyFile.CopyTo(fs);
            }

            var result = LegacyDatabaseMigration.Run(sourcePath, destinationPath, destinationPassword,
                string.IsNullOrWhiteSpace(sourcePassword) ? null : sourcePassword);
            var counts = string.Join(", ", result.RecordCounts.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            var message = $"{result.Format} migration completed to {result.DestinationPath}. Source snapshot: {result.BackupPath}. {counts}";
            if (result.RequiresDataProtectionKeys)
                message += " Preserve the matching Data Protection key ring and verify Graph connections before enabling mail.";
            return RedirectToAction(nameof(Index), new { message });
        }
        catch (Exception ex)
        {
            return RedirectToAction(nameof(Index), new { error = ex.Message });
        }
        finally
        {
            if (System.IO.File.Exists(sourcePath)) System.IO.File.Delete(sourcePath);
        }
    }

    // To remove the empty lines in the end of file
    ~MaintenanceController()
    {
        _ctx.Dispose();
        _liteDb.Dispose();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult NormalizeUseXmlToJson()
    {
        NormalizeCompaniesUseXmlToJson();
        return RedirectToAction(nameof(Index), new { message = "Normalized UseXMLTOJSON for all companies." });
    }

    private void NormalizeCompaniesUseXmlToJson()
    {
        var companies = _ctx.Companies.FindAll().ToList();
        foreach (var c in companies)
        {
            // Normalize null/empty and legacy 0/1 to true/false
            if (string.IsNullOrWhiteSpace(c.UseXMLTOJSON) || c.UseXMLTOJSON == "0")
            {
                c.UseXMLTOJSON = "false";
                _ctx.Companies.Update(c);
            }
            else if (c.UseXMLTOJSON == "1")
            {
                c.UseXMLTOJSON = "true";
                _ctx.Companies.Update(c);
            }
        }
    }
}
