using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Notifications;

namespace NetRatel.API.Services;

public sealed record McpOperatorEventSummary(
    Guid Id,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string Source,
    string Status,
    NetRatelNotificationSeverity Severity,
    int Attempts);

public sealed record McpOperatorEventMutationOutcome(bool Succeeded, string? FailureCode = null);

/// <summary>
/// Redacted, bounded event authority for the operator surface.  It exposes
/// neither event payloads nor callback content and keeps callback replay
/// implementation details behind the server boundary.
/// </summary>
public interface IMcpOperatorEventAuthority
{
    Task<IReadOnlyList<McpOperatorEventSummary>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<McpOperatorEventSummary?> GetAsync(Guid eventId, CancellationToken cancellationToken);
    Task<McpOperatorEventMutationOutcome> RetryAsync(Guid eventId, CancellationToken cancellationToken);
    Task<McpOperatorEventMutationOutcome> DisableAsync(Guid eventId, CancellationToken cancellationToken);
}

public sealed class McpOperatorEventAuthority(
    INetRatelNotificationService notifications,
    INetRatelExternalServiceCallbackReplayService callbackReplay) : IMcpOperatorEventAuthority
{
    private const string OperatorReader = "mcp.operator.events";

    public async Task<IReadOnlyList<McpOperatorEventSummary>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var result = await notifications.GetPageAsync(
            OperatorReader, page, pageSize, null, null, null, null, null, null, null, null, null, cancellationToken)
            .ConfigureAwait(false);
        return result.Items.Select(ToSummary).ToArray();
    }

    public async Task<McpOperatorEventSummary?> GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetByIdAsync(eventId, OperatorReader, cancellationToken).ConfigureAwait(false);
        return notification is null ? null : ToSummary(notification);
    }

    public async Task<McpOperatorEventMutationOutcome> RetryAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetByIdAsync(eventId, OperatorReader, cancellationToken).ConfigureAwait(false);
        if (notification is null)
        {
            return new(false, "event_not_found");
        }

        if (string.Equals(notification.Status, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "event_retry_not_allowed");
        }

        if (callbackReplay.CanReplay(notification))
        {
            await callbackReplay.ReplayAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await notifications.RetryAsync(eventId, cancellationToken).ConfigureAwait(false);
        }

        return new(true);
    }

    public async Task<McpOperatorEventMutationOutcome> DisableAsync(Guid eventId, CancellationToken cancellationToken)
    {
        if (await notifications.GetByIdAsync(eventId, OperatorReader, cancellationToken).ConfigureAwait(false) is null)
        {
            return new(false, "event_not_found");
        }

        await notifications.DisableAsync(eventId, cancellationToken).ConfigureAwait(false);
        return new(true);
    }

    private static McpOperatorEventSummary ToSummary(NetRatelNotificationDto notification) => new(
        notification.Id,
        notification.EventType,
        notification.OccurredUtc,
        notification.Source,
        notification.Status,
        notification.Severity,
        notification.Attempts);
}
