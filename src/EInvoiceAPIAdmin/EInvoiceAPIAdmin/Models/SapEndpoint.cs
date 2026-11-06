using System.ComponentModel.DataAnnotations;
using LiteDB;

namespace InvoiceWebApp.Models;

/// <summary>
/// SAP endpoint that ETANotifications (IncomeTaxWebAPI) forwards ETA notifications to.
/// Stored in config.db, collection "SapEndpoints"; one record per name ("Prod", "Dev").
/// </summary>
public class SapEndpoint
{
    public const string Prod = "Prod";
    public const string Dev = "Dev";

    [BsonId]
    public int Id { get; set; }

    /// <summary>"Prod" (api/einvoice/...) or "Dev" (api/einvoice/dev/...). Fixed; not editable.</summary>
    public string Name { get; set; } = string.Empty;

    [Required, Url, Display(Name = "SAP endpoint URL")]
    public string Url { get; set; } = string.Empty;

    [Display(Name = "SAP service user name")]
    public string? Username { get; set; }

    /// <summary>Write-only in the UI; never rendered back to the browser.</summary>
    public string? Password { get; set; }

    [Range(500, 120000), Display(Name = "Timeout (ms)")]
    public int TimeoutMs { get; set; } = 3000;
}
