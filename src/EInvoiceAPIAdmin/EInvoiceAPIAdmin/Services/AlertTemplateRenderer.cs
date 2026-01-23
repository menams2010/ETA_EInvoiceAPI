namespace InvoiceWebApp.Services;

public static class AlertTemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var result = template;
        foreach (var kvp in values)
        {
            var token = "{" + kvp.Key + "}";
            result = result.Replace(token, kvp.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public static IEnumerable<string> SplitEmails(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
