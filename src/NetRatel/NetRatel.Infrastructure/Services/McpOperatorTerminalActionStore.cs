using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Persists content-free replay protection for terminal control frames. A
/// signed delegation request ID is minted by the MCP host, so callers cannot
/// select or collide with another caller's idempotency key.
/// </summary>
public sealed class McpOperatorTerminalActionStore(OrchestratorDbContext db)
    : IMcpOperatorTerminalActionStore
{
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "send_input",
        "resize",
        "close"
    };

    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorTerminalActionAdmission> AdmitAsync(
        McpOperatorTerminalActionAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsRequestValid(request))
            return McpOperatorTerminalActionAdmission.Denied("terminal_action_invalid");

        var session = await _db.McpOperatorTerminalSessions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.SessionId == request.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
            return McpOperatorTerminalActionAdmission.Denied("terminal_session_not_found");

        var existing = await FindAsync(session.Id, request.Operation, request.DelegationRequestId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return ToAdmission(existing, request.PayloadHash);

        var record = new McpOperatorTerminalActionRecord
        {
            Id = Guid.NewGuid(),
            SessionRecordId = session.Id,
            Operation = request.Operation,
            DelegationRequestId = request.DelegationRequestId,
            PayloadHash = request.PayloadHash,
            Outcome = McpOperatorIdempotencyOutcome.Pending,
            CreatedAtUtc = request.OccurredAtUtc,
            Version = 1
        };
        _db.McpOperatorTerminalActions.Add(record);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new McpOperatorTerminalActionAdmission(
                record.Id,
                IsNewDispatch: true,
                IsReplay: false,
                FailureCode: null,
                record.Outcome,
                ResultReference: null);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            existing = await FindAsync(session.Id, request.Operation, request.DelegationRequestId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw;
            return ToAdmission(existing, request.PayloadHash);
        }
    }

    public async Task CompleteAsync(
        Guid actionId,
        McpOperatorIdempotencyOutcome outcome,
        string resultReference,
        Guid? acceptedAuditId,
        CancellationToken cancellationToken)
    {
        if (actionId == Guid.Empty || outcome == McpOperatorIdempotencyOutcome.Pending || !IsResultReference(resultReference))
            throw new ArgumentException("A terminal action completion requires a terminal outcome and bounded result reference.");

        var record = await _db.McpOperatorTerminalActions.SingleOrDefaultAsync(
            candidate => candidate.Id == actionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The terminal action idempotency admission was not found.");
        if (record.Outcome != McpOperatorIdempotencyOutcome.Pending)
        {
            if (record.Outcome != outcome || !string.Equals(record.ResultReference, resultReference, StringComparison.Ordinal) ||
                record.AcceptedAuditId != acceptedAuditId)
            {
                throw new InvalidOperationException("A completed terminal action cannot be overwritten by a conflicting result.");
            }
            return;
        }

        record.Outcome = outcome;
        record.ResultReference = resultReference;
        record.AcceptedAuditId = acceptedAuditId;
        record.CompletedAtUtc = DateTimeOffset.UtcNow;
        record.Version++;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<McpOperatorTerminalActionRecord?> FindAsync(
        Guid sessionRecordId,
        string operation,
        string delegationRequestId,
        CancellationToken cancellationToken) =>
        _db.McpOperatorTerminalActions.SingleOrDefaultAsync(candidate =>
            candidate.SessionRecordId == sessionRecordId &&
            candidate.Operation == operation &&
            candidate.DelegationRequestId == delegationRequestId,
            cancellationToken);

    private static McpOperatorTerminalActionAdmission ToAdmission(
        McpOperatorTerminalActionRecord record,
        string payloadHash) =>
        !string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal)
            ? McpOperatorTerminalActionAdmission.Denied("idempotency_conflict")
            : new(record.Id, false, true, null, record.Outcome, record.ResultReference);

    private static bool IsRequestValid(McpOperatorTerminalActionAdmissionRequest request) =>
        IsSessionId(request.SessionId) &&
        Operations.Contains(request.Operation) &&
        IsToken(request.DelegationRequestId) &&
        IsSha256(request.PayloadHash) &&
        request.OccurredAtUtc != default;

    private static bool IsSessionId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit);

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool IsResultReference(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
