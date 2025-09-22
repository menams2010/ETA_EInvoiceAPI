using System.Data;
using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using LiteDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace InvoiceWebApp.Controllers;

[Authorize]
[Route("[controller]/[action]")]
public class MaintenanceController(AppDbContext ctx, LiteDatabase liteDb, IWebHostEnvironment env) : Controller
{
    private readonly AppDbContext _ctx = ctx;
    private readonly LiteDatabase _liteDb = liteDb;
    private readonly IWebHostEnvironment _env = env;

    private static string GetDbPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config.db"));

    private static readonly string[] KnownTables = new[]
    {
        "tb_eInvoiceEnv",
        "tb_Settings",
        "tb_Company",
        "tb_CompEnv",
        "tb_Login"
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
            var dbPath = GetDbPath();
            if (!System.IO.File.Exists(dbPath))
                return RedirectToAction(nameof(Index), new { error = "Database file not found." });

            var bytes = System.IO.File.ReadAllBytes(dbPath);
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
    public IActionResult Restore(IFormFile backupFile, string? tables)
    {
        if (backupFile == null || backupFile.Length == 0)
            return RedirectToAction(nameof(Index), new { error = "Please select a valid backup file." });

        try
        {
            var tmpPath = Path.GetTempFileName();
            using (var fs = System.IO.File.OpenWrite(tmpPath))
            {
                backupFile.CopyTo(fs);
            }

            var selected = ParseTables(tables);
            var processed = new List<string>();
            using var src = new LiteDatabase($"Filename={tmpPath};Password=@Mina&29012017&Mariam@;Connection=shared;ReadOnly=true");

            if (ShouldProcess("tb_eInvoiceEnv", selected)) { CopyCollection(src, _liteDb, "tb_eInvoiceEnv"); processed.Add("tb_eInvoiceEnv"); }
            if (ShouldProcess("tb_Settings", selected)) { CopyCollection(src, _liteDb, "tb_Settings"); processed.Add("tb_Settings"); }
            if (ShouldProcess("tb_Company", selected)) { CopyCollection(src, _liteDb, "tb_Company"); processed.Add("tb_Company"); }
            if (ShouldProcess("tb_CompEnv", selected)) { CopyCollection(src, _liteDb, "tb_CompEnv"); processed.Add("tb_CompEnv"); }
            if (ShouldProcess("tb_Login", selected)) { CopyCollection(src, _liteDb, "tb_Login"); processed.Add("tb_Login"); }

            // Normalize after restore only if companies were included or all tables
            if (ShouldProcess("tb_Company", selected))
            {
                NormalizeCompaniesUseXmlToJson();
            }

            System.IO.File.Delete(tmpPath);

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
        catch (Exception ex)
        {
            return RedirectToAction(nameof(Index), new { error = ex.Message });
        }
    }

    private static void CopyCollection(LiteDatabase src, LiteDatabase dest, string name)
    {
        var srcCol = src.GetCollection(name);
        var destCol = dest.GetCollection(name);

        // Clear destination
        destCol.DeleteAll();

        // Enumerate all docs and insert
        foreach (var doc in srcCol.FindAll())
        {
            destCol.Upsert(doc);
        }
    }

    // IMPORT FROM SQLITE: reads from a provided sqlite file and migrates rows into LiteDB
    // Optionally restrict by comma/semicolon separated table names (use SQLite table names e.g. tb_Company)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ImportFromSqlite(IFormFile sqliteFile, string? tables)
    {
        if (sqliteFile == null || sqliteFile.Length == 0)
            return RedirectToAction(nameof(Index), new { error = "Please select a valid SQLite file." });

        var tmpPath = Path.GetTempFileName();
        try
        {
            using (var upload = sqliteFile.OpenReadStream())
            using (var fs = System.IO.File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                upload.CopyTo(fs);
            }

            var selected = ParseTables(tables);
            var processed = new List<string>();

            using (var conn = new SqliteConnection($"Data Source={tmpPath};Mode=ReadOnly;Pooling=False"))
            using (var cmd = conn.CreateCommand())
            {
                conn.Open();

                if (ShouldProcess("tb_eInvoiceEnv", selected)) { TryImportEInvoiceEnv(cmd); processed.Add("tb_eInvoiceEnv"); }
                if (ShouldProcess("tb_Settings", selected)) { TryImportSettings(cmd); processed.Add("tb_Settings"); }
                if (ShouldProcess("tb_Company", selected)) { TryImportCompanies(cmd); processed.Add("tb_Company"); }
                if (ShouldProcess("tb_CompEnv", selected)) { TryImportCompEnv(cmd); processed.Add("tb_CompEnv"); }
                if (ShouldProcess("tb_Login", selected)) { TryImportLogin(cmd); processed.Add("tb_Login"); }
            }

            // Normalize after import only if companies were included or all tables
            if (ShouldProcess("tb_Company", selected))
            {
                NormalizeCompaniesUseXmlToJson();
            }

            if (selected is not null && processed.Count == 0)
            {
                var known = string.Join(", ", KnownTables);
                return RedirectToAction(nameof(Index), new { error = $"No recognized tables selected. Known tables: {known}" });
            }

            var msg = selected is null
                ? "Import completed successfully (all tables)."
                : $"Import completed successfully. Tables: {string.Join(", ", processed)}";

            return RedirectToAction(nameof(Index), new { message = msg });
        }
        catch (Exception ex)
        {
            return RedirectToAction(nameof(Index), new { error = ex.Message });
        }
        finally
        {
            try { System.IO.File.Delete(tmpPath); } catch { }
        }
    }

    private void TryImportEInvoiceEnv(SqliteCommand cmd)
    {
        try
        {
            cmd.CommandText = "SELECT Id, EnvName, EnvTokenUrl, EnvInvoicingUrl FROM tb_eInvoiceEnv";
            using var rdr = cmd.ExecuteReader();
            var col = _ctx.EInvoiceEnvs;
            while (rdr.Read())
            {
                var item = new EInvoiceEnv
                {
                    Id = rdr.GetInt32(0),
                    EnvName = rdr.GetString(1),
                    EnvTokenUrl = rdr.GetString(2),
                    EnvInvoicingUrl = rdr.GetString(3)
                };
                col.Upsert(item);
            }
        }
        catch { /* table may not exist */ }
    }

    private void TryImportSettings(SqliteCommand cmd)
    {
        try
        {
            cmd.CommandText = "SELECT Id, SettingName, SettingValue FROM tb_Settings Where SettingName <> 'DateTimeFormat'";
            using var rdr = cmd.ExecuteReader();
            var col = _ctx.Settings;
            while (rdr.Read())
            {
                var item = new Settings
                {
                    Id = rdr.GetInt32(0),
                    SettingName = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                    SettingValue = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2)
                };
                col.Upsert(item);
            }
        }
        catch { }
    }

    // UPDATED: robust import for tb_Company
    private void TryImportCompanies(SqliteCommand cmd)
    {
        try
        {
            cmd.CommandText = "SELECT * FROM tb_Company";
            using var rdr = cmd.ExecuteReader();

            // Resolve column indexes (case-insensitive)
            int idxId = GetOrdinalCI(rdr, "Id");
            if (idxId < 0) return; // cannot import without Id

            int idxCompanyCode = GetOrdinalCI(rdr, "CompanyCode");
            int idxCompanyName = GetOrdinalCI(rdr, "CompanyName");
            int idxTokenSerial = GetOrdinalCI(rdr, "TokenSerial");
            int idxTokenPin = GetOrdinalCI(rdr, "TokenPin");
            int idxCertSerialNum = GetOrdinalCI(rdr, "CertSerialNum");
            int idxUseXMLTOJSON = GetOrdinalCI(rdr, "UseXMLTOJSON");

            var col = _ctx.Companies;

            while (rdr.Read())
            {
                try
                {
                    var idObj = rdr.GetValue(idxId);
                    if (idObj is DBNull) continue;
                    int id = Convert.ToInt32(idObj);

                    long? companyCode = GetNullableInt64(rdr, idxCompanyCode);
                    string companyName = GetStringSafe(rdr, idxCompanyName) ?? string.Empty;
                    string tokenSerial = GetStringSafe(rdr, idxTokenSerial) ?? string.Empty;
                    string tokenPin = GetStringSafe(rdr, idxTokenPin) ?? string.Empty;
                    string certSerialNum = GetStringSafe(rdr, idxCertSerialNum) ?? string.Empty;

                    // Normalize UseXMLTOJSON: accept 1/0, true/false, or text
                    string useXmlStr = NormalizeToString(rdr, idxUseXMLTOJSON) ?? string.Empty;

                    var item = new Company
                    {
                        Id = id,
                        CompanyCode = companyCode,
                        CompanyName = companyName,
                        TokenSerial = tokenSerial,
                        TokenPin = tokenPin,
                        CertSerialNum = certSerialNum,
                        UseXMLTOJSON = useXmlStr
                    };

                    col.Upsert(item);
                }
                catch
                {
                    // skip problematic row, continue with next
                }
            }
        }
        catch
        {
            // table may not exist or query failed
        }
    }

    private void TryImportCompEnv(SqliteCommand cmd)
    {
        try
        {
            cmd.CommandText = "SELECT Id, CompanyId, EnvId, ClientId, ClientSecret, str_token, time_get FROM tb_CompEnv";
            using var rdr = cmd.ExecuteReader();
            var col = _ctx.CompEnvs;
            while (rdr.Read())
            {
                var item = new CompEnv
                {
                    Id = rdr.GetInt32(0),
                    CompanyId = rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1),
                    EnvId = rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2),
                    ClientId = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    ClientSecret = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    str_token = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                    //time_get = rdr.IsDBNull(6) ? null : rdr.GetString(6)
                };
                col.Upsert(item);
            }
        }
        catch { }
    }

    private void TryImportLogin(SqliteCommand cmd)
    {
        try
        {
            cmd.CommandText = "SELECT Id, u_name, u_pass, IsInitial FROM tb_Login";
            using var rdr = cmd.ExecuteReader();
            var col = _ctx.Logins;
            while (rdr.Read())
            {
                var item = new Login
                {
                    Id = rdr.GetInt32(0),
                    u_name = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                    u_pass = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2),
                    IsInitial = !rdr.IsDBNull(3) && rdr.GetBoolean(3)
                };
                col.Upsert(item);
            }
        }
        catch { }
    }

    // -------- Helpers (case-insensitive column lookup + safe conversions) --------
    private static int GetOrdinalCI(SqliteDataReader rdr, string name)
    {
        for (int i = 0; i < rdr.FieldCount; i++)
        {
            if (string.Equals(rdr.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string? GetStringSafe(SqliteDataReader rdr, int idx)
    {
        if (idx < 0) return null;
        var val = rdr.GetValue(idx);
        if (val is DBNull) return null;
        return val?.ToString();
    }

    private static long? GetNullableInt64(SqliteDataReader rdr, int idx)
    {
        if (idx < 0) return null;
        var val = rdr.GetValue(idx);
        if (val is DBNull || val is null) return null;

        if (val is long l) return l;
        if (val is int i) return i;
        if (val is short s) return s;
        if (val is byte b) return b;
        if (val is double d) return (long)d;
        if (val is float f) return (long)f;
        if (val is decimal m) return (long)m;

        if (val is string sVal && long.TryParse(sVal, out var parsed))
            return parsed;

        return null;
    }

    private static string? NormalizeToString(SqliteDataReader rdr, int idx)
    {
        var s = GetStringSafe(rdr, idx);
        if (s == null) return null;

        // If it's numeric-like 0/1 keep as-is; otherwise return the string
        var trimmed = s.Trim();
        if (trimmed == "0" || trimmed == "1") return trimmed;
        if (bool.TryParse(trimmed, out var b)) return b ? "1" : "0"; // map true/false to 1/0 to match legacy usage
        return trimmed;
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
