using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Reads the durable ownership lease when an authenticated agent reannounces a
/// PTY after the API process has restarted. Expired leases become close-pending
/// before recovery so the gateway can only issue an idempotent remote close.
/// </summary>
public sealed class McpOperatorTerminalSessionRecoveryService(OrchestratorDbContext db)
    : IMcpOperatorTerminalSessionRecovery
{
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorTerminalRecovery?> TryRecoverAsync(
        int tenantId,
        Guid agentId,
        string sessionId,
        ulong generation,
        CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || agentId == Guid.Empty || generation == 0 || !IsSessionId(sessionId))
            return null;

        // SessionId is globally unique. Resolve it first and fence the
        // remaining authority values in memory so the unsigned/numeric gateway
        // generation never becomes a provider-specific query conversion edge.
        var session = await _db.McpOperatorTerminalSessions.SingleOrDefaultAsync(
            candidate => candidate.SessionId == sessionId,
            cancellationToken).ConfigureAwait(false);
        if (session is null ||
            session.TenantId != tenantId ||
            session.AgentId != agentId ||
            session.Generation != generation ||
            session.State is McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (session.State is not McpOperatorTerminalSessionState.Closing &&
            (session.ExpiresAtUtc <= now || session.IdleExpiresAtUtc <= now))
        {
            session.State = McpOperatorTerminalSessionState.Closing;
            session.CloseRequestedAtUtc = now;
            session.CloseReason = "terminal_policy_lease_expired";
            session.Version++;
            _db.McpOperatorTerminalSessionAudits.Add(new McpOperatorTerminalSessionAuditRecord
            {
                Id = Guid.NewGuid(),
                SessionRecordId = session.Id,
                State = session.State,
                Action = "close_requested",
                Reason = session.CloseReason,
                OccurredAtUtc = now
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new McpOperatorTerminalRecovery(
            session.SessionId,
            session.TenantId,
            session.AgentId,
            session.Generation,
            session.ShellType,
            session.Columns,
            session.Rows,
            session.CreatedAtUtc,
            session.State == McpOperatorTerminalSessionState.Closing,
            session.CloseReason);
    }

    private static bool IsSessionId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
}
