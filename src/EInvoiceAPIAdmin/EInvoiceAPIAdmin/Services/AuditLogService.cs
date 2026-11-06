using InvoiceWebApp.Data;
using InvoiceWebApp.Models;
using System.Text.Json;

namespace InvoiceWebApp.Services;

public class AuditLogService(AppDbContext ctx)
{
    private readonly AppDbContext _ctx = ctx;

    public void Log(string userName, string action, string entityType, string entityId, string? description = null, object? oldValues = null, object? newValues = null, string? ipAddress = null)
    {
        var nextId = _ctx.AuditLogs.Query().OrderByDescending(x => x.Id).Limit(1).ToList().FirstOrDefault()?.Id + 1 ?? 1;

        var log = new AuditLog
        {
            Id = nextId,
            TimestampUtc = DateTime.UtcNow,
            UserName = userName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            OldValues = oldValues is null ? null : JsonSerializer.Serialize(oldValues),
            NewValues = newValues is null ? null : JsonSerializer.Serialize(newValues),
            IpAddress = ipAddress
        };

        _ctx.AuditLogs.Insert(log);
    }

    public IEnumerable<AuditLog> GetAll(int? limit = null)
    {
        var query = _ctx.AuditLogs.Query().OrderByDescending(x => x.TimestampUtc);
        if (limit.HasValue)
            return query.Limit(limit.Value).ToList();
        return query.ToList();
    }

    public IEnumerable<AuditLog> GetByEntityType(string entityType, int? limit = null)
    {
        var query = _ctx.AuditLogs.Query()
            .Where(x => x.EntityType == entityType)
            .OrderByDescending(x => x.TimestampUtc);
        if (limit.HasValue)
            return query.Limit(limit.Value).ToList();
        return query.ToList();
    }

    public IEnumerable<AuditLog> GetByUser(string userName, int? limit = null)
    {
        var query = _ctx.AuditLogs.Query()
            .Where(x => x.UserName == userName)
            .OrderByDescending(x => x.TimestampUtc);
        if (limit.HasValue)
            return query.Limit(limit.Value).ToList();
        return query.ToList();
    }
}
