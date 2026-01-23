using LiteDB;

namespace InvoiceWebApp.Models;

public class EmailTemplate
{
    [BsonId]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public bool IsHtml { get; set; } = true;
}
