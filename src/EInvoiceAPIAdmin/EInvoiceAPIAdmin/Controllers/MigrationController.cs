using InvoiceWebApp.Data;
using InvoiceWebApp.Services;
using LiteDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceWebApp.Controllers;

/// <summary>
/// Admin > Migrate Database: upload a config.db of any earlier version, review what was detected, then import it
/// into the live database (after an automatic backup) or download it converted to the current version.
/// </summary>
[Authorize]
[Route("[controller]/[action]")]
public class MigrationController(ConfigDatabaseMigrator migrator, LiteDatabase liteDb, AuditLogService audit) : Controller
{
    // The password typed for the old file is kept server-side (session) between the review and the import.
    private const string PasswordKey = "migration-password:";

    [HttpGet]
    public IActionResult Index(string? error = null)
    {
        ViewBag.Error = error;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(200 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200 * 1024 * 1024)]
    public IActionResult Analyze(IFormFile? databaseFile, string? sourcePassword)
    {
        if (databaseFile == null || databaseFile.Length == 0)
            return RedirectToAction(nameof(Index), new { error = "Choose the config.db file to migrate." });

        ConfigDatabaseMigrator.Upload? upload = null;
        try
        {
            using (var stream = databaseFile.OpenReadStream()) upload = migrator.Save(stream);
            var analysis = migrator.Analyze(upload, sourcePassword);
            var id = Path.GetFileName(upload.Folder);
            if (!string.IsNullOrEmpty(sourcePassword)) HttpContext.Session.SetString(PasswordKey + id, sourcePassword);
            ViewBag.UploadId = id;
            ViewBag.FileName = Path.GetFileName(databaseFile.FileName);
            return View("Review", analysis);
        }
        catch (Exception ex)
        {
            upload?.Dispose();
            return RedirectToAction(nameof(Index), new { error = ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Import(string id, bool confirm, string? fileName)
    {
        var upload = migrator.Find(id);
        if (upload == null) return RedirectToAction(nameof(Index), new { error = "The uploaded file has expired. Upload it again." });
        if (!confirm)
        {
            // Keep the upload and show the review again.
            ViewBag.UploadId = id;
            ViewBag.FileName = fileName;
            ViewBag.Error = "Tick the confirmation box to replace the current data.";
            return View("Review", migrator.Analyze(upload, HttpContext.Session.GetString(PasswordKey + id)));
        }

        using var _ = upload;
        try
        {
            var (backupPath, imported, analysis) = migrator.Import(upload, HttpContext.Session.GetString(PasswordKey + id), liteDb);
            HttpContext.Session.Remove(PasswordKey + id);
            audit.Log(User.Identity?.Name ?? "Unknown", "Migrate", "Database", analysis.Version,
                $"Imported a {analysis.Version} config.db ({analysis.Format}); backup of the previous data: {backupPath}",
                null, imported.ToDictionary(x => x.Collection, x => x.Records), HttpContext.Connection.RemoteIpAddress?.ToString());
            ViewBag.BackupPath = backupPath;
            ViewBag.Analysis = analysis;
            return View("Done", imported);
        }
        catch (Exception ex)
        {
            return RedirectToAction(nameof(Index), new { error = "Migration failed; the current database was not changed. " + ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Download(string id)
    {
        using var upload = migrator.Find(id);
        if (upload == null) return RedirectToAction(nameof(Index), new { error = "The uploaded file has expired. Upload it again." });
        try
        {
            var (path, _, analysis) = migrator.Convert(upload, HttpContext.Session.GetString(PasswordKey + id));
            var bytes = System.IO.File.ReadAllBytes(path);
            HttpContext.Session.Remove(PasswordKey + id);
            audit.Log(User.Identity?.Name ?? "Unknown", "Export", "Database", analysis.Version,
                $"Downloaded a {analysis.Version} config.db converted to the current version", null, null,
                HttpContext.Connection.RemoteIpAddress?.ToString());
            return File(bytes, "application/octet-stream", $"config.migrated-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        }
        catch (Exception ex)
        {
            return RedirectToAction(nameof(Index), new { error = "Conversion failed. " + ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Cancel(string id)
    {
        migrator.Find(id)?.Dispose();
        HttpContext.Session.Remove(PasswordKey + id);
        return RedirectToAction(nameof(Index));
    }
}
