using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

namespace NetRatel.Infrastructure.Notifications;

public sealed class NetRatelNotificationService(
    OrchestratorDbContext db,
    NetRatelNotificationDisplaySanitizer displaySanitizer) : INetRatelNotificationService
{
    private readonly OrchestratorDbContext _db = db;
    private readonly NetRatelNotificationDisplaySanitizer _displaySanitizer = displaySanitizer;

    public async Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(
        string userId,
        int page,
        int pageSize,
        string? eventType,
        string? correlationId,
        string? entityId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? search,
        string? source,
        NetRatelNotificationSeverity? severity,
        CancellationToken ct)
    {
        ValidateUserId(userId);

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        var skip = (safePage - 1) * safePageSize;

        var query = _db.OutboxMessages.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(x => x.Type == eventType.Trim());
        if (!string.IsNullOrWhiteSpace(correlationId))
            query = query.Where(x => x.CorrelationId == correlationId.Trim());
        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(x => x.EntityId == entityId.Trim());
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x =>
                x.Type.Contains(term)
                || (x.Message != null && x.Message.Contains(term))
                || (x.EntityId != null && x.EntityId.Contains(term))
                || x.CorrelationId.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(x => x.Source == source.Trim());
        if (severity.HasValue)
            query = query.Where(x => x.Severity == severity.Value.ToString());
        if (from.HasValue)
            query = query.Where(x => x.OccurredUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(x => x.OccurredUtc <= to.Value);

        var total = await query.CountAsync(ct);

        var pageItems = await query
            .OrderByDescending(x => x.OccurredUtc)
            .Skip(skip)
            .Take(safePageSize)
            .ToListAsync(ct);

        var ids = pageItems.Select(x => x.Id).ToArray();

        var readSet = await _db.OutboxReadReceipts.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.EventId))
            .Select(x => x.EventId)
            .ToListAsync(ct);

        var readLookup = readSet.ToHashSet();
        var items = _displaySanitizer.SanitizeMany(pageItems.Select(x => ToDto(x, readLookup.Contains(x.Id))));

        return new PagedResult<NetRatelNotificationDto>(items, safePage, safePageSize, total);
    }

    public async Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, string userId, CancellationToken ct)
    {
        ValidateUserId(userId);

        var message = await _db.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (message is null)
            return null;

        var read = await _db.OutboxReadReceipts.AsNoTracking()
            .AnyAsync(x => x.EventId == id && x.UserId == userId, ct);

        return _displaySanitizer.Sanitize(ToDto(message, read));
    }

    public async Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(string userId, int take, CancellationToken ct)
    {
        ValidateUserId(userId);
        var safeTake = take <= 0 ? 20 : Math.Min(take, 100);

        var readEventIds = _db.OutboxReadReceipts.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.EventId);

        var messages = await _db.OutboxMessages.AsNoTracking()
            .Where(x => (x.Severity == "Warning" || x.Severity == "Error" || x.Severity == "Critical") && !readEventIds.Contains(x.Id))
            .OrderByDescending(x => x.OccurredUtc)
            .Take(safeTake)
            .ToListAsync(ct);

        return _displaySanitizer.SanitizeMany(messages.Select(x => ToDto(x, false)));
    }

    public async Task<NetRatelNotificationSummaryDto> GetSummaryAsync(string userId, CancellationToken ct)
    {
        ValidateUserId(userId);

        var total = await _db.OutboxMessages.AsNoTracking().CountAsync(ct);

        var readEventIds = _db.OutboxReadReceipts.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.EventId);

        var unread = await _db.OutboxMessages.AsNoTracking()
            .Where(x => !readEventIds.Contains(x.Id))
            .CountAsync(ct);

        var unreadErrors = await _db.OutboxMessages.AsNoTracking()
            .Where(x => !readEventIds.Contains(x.Id) && (x.Severity == "Warning" || x.Severity == "Error" || x.Severity == "Critical"))
            .CountAsync(ct);

        return new NetRatelNotificationSummaryDto
        {
            TotalCount = total,
            UnreadCount = unread,
            UnreadErrorCount = unreadErrors
        };
    }

    public async Task<int> MarkReadAsync(string userId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ValidateUserId(userId);

        var validIds = ids.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (validIds.Length == 0)
            return 0;

        var existing = await _db.OutboxReadReceipts
            .Where(x => x.UserId == userId && validIds.Contains(x.EventId))
            .Select(x => x.EventId)
            .ToListAsync(ct);

        var existingSet = existing.ToHashSet();
        var toAdd = validIds
            .Where(x => !existingSet.Contains(x))
            .Select(x => new OutboxReadReceipt
            {
                EventId = x,
                UserId = userId,
                ReadUtc = DateTimeOffset.UtcNow
            })
            .ToList();

        if (toAdd.Count == 0)
            return 0;

        _db.OutboxReadReceipts.AddRange(toAdd);
        await _db.SaveChangesAsync(ct);
        return toAdd.Count;
    }

    public async Task RetryAsync(Guid id, CancellationToken ct)
    {
        var message = await _db.OutboxMessages.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"Outbox message {id} not found.");

        message.Status = OutboxStatuses.Pending;
        message.NextAttemptUtc = DateTimeOffset.UtcNow;
        message.LockOwner = null;
        message.LockedUntilUtc = null;
        message.LastError = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DisableAsync(Guid id, CancellationToken ct)
    {
        var message = await _db.OutboxMessages.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"Outbox message {id} not found.");

        message.Status = OutboxStatuses.Disabled;
        message.LockOwner = null;
        message.LockedUntilUtc = null;
        await _db.SaveChangesAsync(ct);
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID is required.", nameof(userId));
    }

    private static NetRatelNotificationDto ToDto(OutboxMessage message, bool isRead)
        => new()
        {
            Id = message.Id,
            EventType = message.Type,
            OccurredUtc = message.OccurredUtc,
            Source = message.Source,
            CorrelationId = message.CorrelationId,
            TenantId = message.TenantId,
            EntityId = message.EntityId,
            Severity = ParseSeverity(message.Severity),
            Message = message.Message,
            PayloadJson = message.PayloadJson,
            Status = message.Status,
            IsRead = isRead,
            Attempts = message.Attempts,
            NextAttemptUtc = message.NextAttemptUtc,
            LastError = message.LastError
        };

    private static NetRatelNotificationSeverity ParseSeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
            return NetRatelNotificationSeverity.Info;

        return Enum.TryParse<NetRatelNotificationSeverity>(severity, true, out var parsed)
            ? parsed
            : NetRatelNotificationSeverity.Info;
    }
}
