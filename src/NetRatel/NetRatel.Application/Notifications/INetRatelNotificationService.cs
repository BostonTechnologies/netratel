using NetRatel.Shared.Contracts;

namespace NetRatel.Application.Notifications;

public interface INetRatelNotificationService
{
    Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(
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
        CancellationToken ct);

    Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, string userId, CancellationToken ct);
    Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(string userId, int take, CancellationToken ct);
    Task<NetRatelNotificationSummaryDto> GetSummaryAsync(string userId, CancellationToken ct);
    Task<int> MarkReadAsync(string userId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task RetryAsync(Guid id, CancellationToken ct);
    Task DisableAsync(Guid id, CancellationToken ct);
}
