namespace InvoiceWebApp.Models;

public sealed class AlertRuleTestViewModel
{
    public int Id { get; set; }
    public string DataSource { get; set; } = "Sample";
    public string? EntityKey { get; set; }
    public bool ConfirmSend { get; set; }
    public AlertRule Rule { get; set; } = new();
    public IReadOnlyList<AlertTestEntity> Entities { get; set; } = [];
}

public sealed record AlertTestEntity(string Key, string DisplayName, DateTime ExpirationDate,
    string CompanyName, string Environment);
