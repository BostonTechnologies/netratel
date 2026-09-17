using System.Collections.Concurrent;
using NetRatel.API.Gateway;

namespace NetRatel.API.Services;

/// <summary>The outcome of a browser-originated attachment renewal attempt.</summary>
public enum GatewayTerminalBrowserAttachmentRenewalOutcome
{
    Renewed,
    StaleGeneration,
    StaleAttachment,
    ClosePending,
    Final,
    NotTracked
}

/// <summary>The outcome and current opaque lease fence of a renewal attempt.</summary>
public sealed record GatewayTerminalBrowserAttachmentRenewalResult(
    GatewayTerminalBrowserAttachmentRenewalOutcome Outcome,
    string? AttachmentLeaseId = null);

/// <summary>
/// Holds a short-lived, process-local attachment lease for terminal sessions
/// created through the browser-facing V2 gateway API. The lease is renewed by
/// authenticated browser activity and turns an abandoned circuit or failed
/// hard-reload handoff into an exact, retried terminal close instead of an
/// indefinitely invisible remote PTY.
/// </summary>
public sealed class GatewayTerminalBrowserAttachmentLeaseRegistry(
    TimeProvider timeProvider,
    ILogger<GatewayTerminalBrowserAttachmentLeaseRegistry> logger)
{
    internal static readonly TimeSpan ReattachGracePeriod = TimeSpan.FromMinutes(2);
    private const string ExpiredCloseReason = "terminal_browser_attachment_expired";
    private const string ExplicitCloseReason = "terminal_browser_attachment_closed";
    private readonly ConcurrentDictionary<string, BrowserAttachmentLease> _leases = new(StringComparer.Ordinal);

    /// <summary>Starts the reattachment deadline before the open response leaves the API.</summary>
    public string Track(GatewayTerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var replacement = BrowserAttachmentLease.From(session, timeProvider.GetUtcNow().Add(ReattachGracePeriod));
        var tracked = _leases.AddOrUpdate(
            session.SessionId,
            replacement,
            (_, current) => current.Matches(session)
                ? current with { ExpiresAtUtc = replacement.ExpiresAtUtc }
                : replacement);

        logger.LogInformation(
            "api.terminal.browser.attachment.tracked tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} expiresAtUtc={ExpiresAtUtc}",
            session.TenantId,
            session.AgentId,
            session.SessionId,
            session.Generation,
            tracked.ExpiresAtUtc);

        return tracked.AttachmentLeaseId;
    }

    /// <summary>
    /// Returns the current non-secret browser-handle fence without extending
    /// the lease. Session reads must not count as browser liveness.
    /// </summary>
    public string? GetAttachmentLeaseId(GatewayTerminalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _leases.TryGetValue(session.SessionId, out var current) && current.Matches(session)
            ? current.AttachmentLeaseId
            : null;
    }

    /// <summary>
    /// Renews only the exact session generation. This must only be called by
    /// the dedicated browser heartbeat route: ordinary server-side terminal
    /// traffic can outlive a detached browser circuit and is not evidence that
    /// the browser is still attached.
    /// </summary>
    public GatewayTerminalBrowserAttachmentRenewalResult TryRenew(
        GatewayTerminalSession session,
        ulong generation,
        string? attachmentLeaseId,
        string? browserAttachmentId,
        bool claimOwnership)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (generation == 0 || generation != session.Generation)
        {
            return new(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleGeneration);
        }

        if (IsFinal(session))
        {
            RemoveExact(session.SessionId, session);
            return new(GatewayTerminalBrowserAttachmentRenewalOutcome.Final);
        }

        if (IsClosing(session))
        {
            return new(GatewayTerminalBrowserAttachmentRenewalOutcome.ClosePending);
        }

        if (!IsValidAttachmentIdentity(attachmentLeaseId) || !IsValidAttachmentIdentity(browserAttachmentId))
        {
            return new(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleAttachment);
        }

        while (_leases.TryGetValue(session.SessionId, out var current))
        {
            if (!current.Matches(session))
            {
                return new(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleGeneration);
            }

            if (current.CloseRequestedAtUtc is not null)
            {
                return new(GatewayTerminalBrowserAttachmentRenewalOutcome.ClosePending);
            }

            var isCurrentBrowser = string.Equals(
                current.BrowserAttachmentId,
                browserAttachmentId,
                StringComparison.Ordinal);
            var isCurrentLease = string.Equals(
                current.AttachmentLeaseId,
                attachmentLeaseId,
                StringComparison.Ordinal);
            var isPreviousLeaseForCurrentBrowser =
                isCurrentBrowser &&
                string.Equals(current.PreviousAttachmentLeaseId, attachmentLeaseId, StringComparison.Ordinal);
            if (!isCurrentLease && !isPreviousLeaseForCurrentBrowser)
            {
                return new(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleAttachment);
            }

            if (!isCurrentBrowser && !claimOwnership)
            {
                return new(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleAttachment);
            }

            // The first browser callback establishes an owner. A distinct
            // browser may claim exactly once when it starts (hard reload or a
            // cloned tab); rotate the opaque lease ID atomically so the former
            // runtime cannot keep the new owner alive with copied storage.
            var renewed = current with
            {
                AttachmentLeaseId = isCurrentBrowser || current.BrowserAttachmentId is null
                    ? current.AttachmentLeaseId
                    : NewAttachmentLeaseId(),
                PreviousAttachmentLeaseId = isCurrentBrowser || current.BrowserAttachmentId is null
                    ? current.PreviousAttachmentLeaseId
                    : current.AttachmentLeaseId,
                BrowserAttachmentId = browserAttachmentId,
                ExpiresAtUtc = timeProvider.GetUtcNow().Add(ReattachGracePeriod)
            };
            if (_leases.TryUpdate(session.SessionId, renewed, current))
            {
                return new(GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed, renewed.AttachmentLeaseId);
            }
        }

        return new(GatewayTerminalBrowserAttachmentRenewalOutcome.NotTracked);
    }

    /// <summary>
    /// Records an explicit browser close before its outbound frame is sent so
    /// a transient transport loss is retried by the expiry worker.
    /// </summary>
    public void MarkClosePending(GatewayTerminalSession session) =>
        MarkClosePending(session, ExplicitCloseReason);

    /// <summary>
    /// Dispatches exact close frames for expired or explicitly closing leases.
    /// Ownership is retained until the gateway confirms that the matching
    /// session is final, so a reconnect cannot orphan the client PTY.
    /// </summary>
    public async Task<int> CloseDueAsync(IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminals);

        var retired = 0;
        foreach (var candidate in _leases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = terminals.Get(candidate.Key);
            if (session is null || !candidate.Value.Matches(session) || IsFinal(session))
            {
                if (RemoveExact(candidate.Key, candidate.Value))
                {
                    retired++;
                }

                continue;
            }

            var now = timeProvider.GetUtcNow();
            if (candidate.Value.CloseRequestedAtUtc is null && candidate.Value.ExpiresAtUtc > now)
            {
                continue;
            }

            var lease = candidate.Value.CloseRequestedAtUtc is null
                ? TryMarkExpiredClosePending(session, now)
                : CurrentClosePendingLease(session, candidate.Value);
            if (lease is null)
            {
                // A browser lifecycle request renewed this exact lease after
                // the enumeration snapshot. Its new grace period wins over
                // the stale expiry decision.
                continue;
            }

            if (!lease.Matches(session))
            {
                continue;
            }

            try
            {
                await terminals.CloseAsync(
                    session.SessionId,
                    session.Generation,
                    lease.CloseReason ?? ExpiredCloseReason,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TerminalGatewayActionException exception) when (exception.Code is "terminal_session_not_found" or "terminal_session_closed" or "terminal_session_failed")
            {
                if (RemoveExact(candidate.Key, lease))
                {
                    retired++;
                }

                continue;
            }
            catch (TerminalGatewayActionException exception)
            {
                logger.LogInformation(
                    "api.terminal.browser.attachment.close.pending sessionId={SessionId} generation={Generation} code={Code}",
                    session.SessionId,
                    session.Generation,
                    exception.Code);
                continue;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "api.terminal.browser.attachment.close.retry-failed sessionId={SessionId} generation={Generation}",
                    session.SessionId,
                    session.Generation);
                continue;
            }

            var afterDispatch = terminals.Get(candidate.Key);
            if (afterDispatch is null || !lease.Matches(afterDispatch) || IsFinal(afterDispatch))
            {
                if (RemoveExact(candidate.Key, lease))
                {
                    retired++;
                }
            }
        }

        return retired;
    }

    private BrowserAttachmentLease? MarkClosePending(GatewayTerminalSession session, string reason)
    {
        while (_leases.TryGetValue(session.SessionId, out var current))
        {
            if (!current.Matches(session))
            {
                return null;
            }

            if (current.CloseRequestedAtUtc is not null)
            {
                return current;
            }

            var pending = current with
            {
                CloseRequestedAtUtc = timeProvider.GetUtcNow(),
                CloseReason = reason
            };
            if (_leases.TryUpdate(session.SessionId, pending, current))
            {
                logger.LogInformation(
                    "api.terminal.browser.attachment.close.queued tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} reason={Reason}",
                    session.TenantId,
                    session.AgentId,
                    session.SessionId,
                    session.Generation,
                    reason);
                return pending;
            }
        }

        return null;
    }

    private BrowserAttachmentLease? TryMarkExpiredClosePending(
        GatewayTerminalSession session,
        DateTimeOffset now)
    {
        while (_leases.TryGetValue(session.SessionId, out var current))
        {
            if (!current.Matches(session))
            {
                return null;
            }

            if (current.CloseRequestedAtUtc is not null)
            {
                return current;
            }

            // Never convert a renewed attachment into a close merely because
            // a concurrent sweep enumerated its pre-renewal value.
            if (current.ExpiresAtUtc > now)
            {
                return null;
            }

            var pending = current with
            {
                CloseRequestedAtUtc = now,
                CloseReason = ExpiredCloseReason
            };
            if (_leases.TryUpdate(session.SessionId, pending, current))
            {
                logger.LogInformation(
                    "api.terminal.browser.attachment.close.queued tenantId={TenantId} agentId={AgentId} sessionId={SessionId} generation={Generation} reason={Reason}",
                    session.TenantId,
                    session.AgentId,
                    session.SessionId,
                    session.Generation,
                    ExpiredCloseReason);
                return pending;
            }
        }

        return null;
    }

    private BrowserAttachmentLease? CurrentClosePendingLease(
        GatewayTerminalSession session,
        BrowserAttachmentLease expected)
    {
        if (!_leases.TryGetValue(session.SessionId, out var current) ||
            !current.Matches(session) ||
            current.CloseRequestedAtUtc is null)
        {
            return null;
        }

        return current;
    }

    private void RemoveExact(string sessionId, GatewayTerminalSession session)
    {
        if (_leases.TryGetValue(sessionId, out var current) && current.Matches(session))
        {
            _leases.TryRemove(KeyValuePair.Create(sessionId, current));
        }
    }

    private bool RemoveExact(string sessionId, BrowserAttachmentLease lease) =>
        _leases.TryRemove(KeyValuePair.Create(sessionId, lease));

    private static bool IsFinal(GatewayTerminalSession session) =>
        session.State is "closed" or "failed";

    private static bool IsClosing(GatewayTerminalSession session) =>
        session.State is "closing";

    private static bool IsValidAttachmentIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static string NewAttachmentLeaseId() => Guid.NewGuid().ToString("N");

    private sealed record BrowserAttachmentLease(
        int TenantId,
        Guid AgentId,
        ulong Generation,
        string AttachmentLeaseId,
        DateTimeOffset ExpiresAtUtc,
        string? BrowserAttachmentId = null,
        string? PreviousAttachmentLeaseId = null,
        DateTimeOffset? CloseRequestedAtUtc = null,
        string? CloseReason = null)
    {
        public static BrowserAttachmentLease From(GatewayTerminalSession session, DateTimeOffset expiresAtUtc) =>
            new(session.TenantId, session.AgentId, session.Generation, NewAttachmentLeaseId(), expiresAtUtc);

        public bool Matches(GatewayTerminalSession session) =>
            TenantId == session.TenantId &&
            AgentId == session.AgentId &&
            Generation == session.Generation;
    }
}

/// <summary>Retries expiry closes without requiring a still-live browser circuit.</summary>
public sealed class GatewayTerminalBrowserAttachmentExpiryService(
    GatewayTerminalBrowserAttachmentLeaseRegistry attachments,
    IAgentTerminalSessionRegistry terminals,
    ILogger<GatewayTerminalBrowserAttachmentExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await attachments.CloseDueAsync(terminals, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not sweep expired browser terminal attachment leases; pending closes will be retried.");
        }
    }
}
