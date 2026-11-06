namespace InvoiceWebApp.Models;

public sealed class AlertRuleBatchEdit
{
    public int[] SelectedIds { get; set; } = [];
    public bool ChangeTo { get; set; }
    public string? To { get; set; }
    public bool ChangeCc { get; set; }
    public string? Cc { get; set; }
    public bool ChangeEnabled { get; set; }
    public bool Enabled { get; set; }
    public bool ChangeSmtpProfile { get; set; }
    public int? SmtpProfileId { get; set; }
    public AlertType? Type { get; set; }
}
