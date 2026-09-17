using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorTerminalSessionStoreTests
{
    [Fact]
    public async Task CreateOrGetAsync_PersistsTheFrozenPolicyLeaseAndRejectsASecondConcurrentSession()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var first = CreateRequest(now, maxConcurrent: 1);
        var store = new McpOperatorTerminalSessionStore(db);

        var lease = await store.CreateOrGetAsync(first, CancellationToken.None);
        var retry = await store.CreateOrGetAsync(first, CancellationToken.None);
        var second = first with { SessionId = Guid.NewGuid().ToString("N"), IdempotencyId = Guid.NewGuid() };
        var rejected = () => store.CreateOrGetAsync(second, CancellationToken.None);

        lease.SessionId.Should().Be(first.SessionId);
        lease.EffectiveConstraints.AllowedShells.Should().Equal("bash");
        lease.EffectiveConstraints.WorkingDirectories.Should().Equal("/srv/netratel");
        retry.SessionId.Should().Be(lease.SessionId);
        await rejected.Should().ThrowAsync<McpOperatorTerminalSessionLimitException>();
        (await db.McpOperatorTerminalSessionAudits.SingleAsync()).Action.Should().Be("lease_created");
    }

    [Fact]
    public async Task CreateOrGetAsync_AllowsThePolicyAdmittedDevelopmentOperatorSurface()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now, maxConcurrent: 1, environment: McpOperatorEnvironment.Development);
        var store = new McpOperatorTerminalSessionStore(db);

        var lease = await store.CreateOrGetAsync(request, CancellationToken.None);

        lease.SessionId.Should().Be(request.SessionId);
        lease.TenantId.Should().Be(request.Decision.Request.TenantId);
        lease.EffectiveConstraints.MaxTerminalLifetimeSeconds.Should().Be(1800);
    }

    [Fact]
    public async Task TouchAsync_ConvertsAnExpiredLeaseToDurableClosePending()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now.AddMinutes(-5), maxConcurrent: 2) with
        {
            IdleExpiresAtUtc = now.AddSeconds(-1),
            ExpiresAtUtc = now.AddMinutes(5)
        };
        var store = new McpOperatorTerminalSessionStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);

        var lease = await store.TouchAsync(request.SessionId, now, CancellationToken.None);

        lease.Should().NotBeNull();
        lease!.State.Should().Be(McpOperatorTerminalSessionState.Closing);
        lease.CloseReason.Should().Be("terminal_policy_lease_expired");
        (await db.McpOperatorTerminalSessionAudits.OrderBy(audit => audit.OccurredAtUtc).LastAsync()).Action.Should().Be("close_requested");
    }

    [Fact]
    public async Task MarkTerminalAsync_DoesNotReviveAClosePendingLeaseFromALateOpenedFrame()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now, maxConcurrent: 2);
        var store = new McpOperatorTerminalSessionStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);
        await store.RequestCloseAsync(request.SessionId, "terminal_policy_revoked", now.AddSeconds(1), CancellationToken.None);

        var lease = await store.MarkTerminalAsync(request.SessionId, McpOperatorTerminalSessionState.Opened, null, now.AddSeconds(2), CancellationToken.None);

        lease.Should().NotBeNull();
        lease!.State.Should().Be(McpOperatorTerminalSessionState.Closing);
        lease.CloseReason.Should().Be("terminal_policy_revoked");
        (await db.McpOperatorTerminalSessionAudits.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task MarkTerminalAsync_DoesNotAuditOrVersionABroadcastDuplicateOpenedTransition()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest(now, maxConcurrent: 2);
        var store = new McpOperatorTerminalSessionStore(db);
        await store.CreateOrGetAsync(request, CancellationToken.None);

        await store.MarkTerminalAsync(request.SessionId, McpOperatorTerminalSessionState.Opened, null, now.AddSeconds(1), CancellationToken.None);
        var duplicate = await store.MarkTerminalAsync(request.SessionId, McpOperatorTerminalSessionState.Opened, null, now.AddSeconds(2), CancellationToken.None);

        duplicate.Should().NotBeNull();
        duplicate!.State.Should().Be(McpOperatorTerminalSessionState.Opened);
        (await db.McpOperatorTerminalSessions.SingleAsync(record => record.SessionId == request.SessionId)).Version.Should().Be(2);
        (await db.McpOperatorTerminalSessionAudits.CountAsync()).Should().Be(2, "the duplicate Opened frame must not create a second durable audit transition");
    }

    [Fact]
    public async Task Terminal_action_admission_replays_the_same_signed_delivery_and_rejects_a_changed_payload()
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var sessions = new McpOperatorTerminalSessionStore(db);
        var lease = await sessions.CreateOrGetAsync(CreateRequest(now, maxConcurrent: 2), CancellationToken.None);
        var actions = new McpOperatorTerminalActionStore(db);
        var request = new McpOperatorTerminalActionAdmissionRequest(
            lease.SessionId,
            "send_input",
            "delegation-request-42",
            new string('A', 64),
            now);

        var first = await actions.AdmitAsync(request, CancellationToken.None);
        await actions.CompleteAsync(first.ActionId!.Value, McpOperatorIdempotencyOutcome.Succeeded, "input_queued", Guid.NewGuid(), CancellationToken.None);
        var replay = await actions.AdmitAsync(request, CancellationToken.None);
        var conflict = await actions.AdmitAsync(request with { PayloadHash = new string('B', 64) }, CancellationToken.None);

        first.IsNewDispatch.Should().BeTrue();
        replay.Should().Be(new McpOperatorTerminalActionAdmission(
            first.ActionId, false, true, null, McpOperatorIdempotencyOutcome.Succeeded, "input_queued"));
        conflict.Should().Be(McpOperatorTerminalActionAdmission.Denied("idempotency_conflict"));
        (await db.McpOperatorTerminalActions.SingleAsync()).Should().Match<McpOperatorTerminalActionRecord>(record =>
            record.Operation == "send_input" &&
            record.DelegationRequestId == "delegation-request-42" &&
            record.PayloadHash == new string('A', 64) &&
            record.ResultReference == "input_queued");
    }

    private static McpOperatorTerminalSessionCreateRequest CreateRequest(
        DateTimeOffset createdAtUtc,
        int maxConcurrent,
        McpOperatorEnvironment environment = McpOperatorEnvironment.Production)
    {
        var agentId = Guid.Parse("bb69ba7b-1e56-4ea8-bc36-533950b50610");
        var policyId = Guid.Parse("9252da81-08cf-4f62-a546-241428329d2b");
        var auditId = Guid.Parse("3252da81-08cf-4f62-a546-241428329d2b");
        var principal = new McpOperatorPrincipal("operator@example.test", "operator-client", "operator-client", Set(), Set(), Set("netratel.mcp.execute"));
        var access = new McpOperatorAccessRequest(
            environment,
            principal,
            42,
            agentId,
            McpOperatorTargetClassification.ManagedStandard,
            McpOperatorOperationFamily.TerminalExecute,
            "netratel_terminal/open",
            Set("netratel.mcp.execute"),
            McpOperatorConfirmationClass.RemoteExecution,
            "corr-42",
            "request-42",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            McpResource: $"https://mcp.{(environment == McpOperatorEnvironment.Production ? "prod" : "dev")}.example/mcp",
            McpInstance: environment == McpOperatorEnvironment.Production ? "prod" : "dev",
            Tool: "netratel_terminal");
        var constraints = new McpOperatorConstraints(
            AllowedShells: ["bash"],
            WorkingDirectories: ["/srv/netratel"],
            MaxTerminalIdleSeconds: 300,
            MaxTerminalLifetimeSeconds: 1800,
            MaxConcurrentTerminalSessions: maxConcurrent,
            MaxOutputBytes: 16 * 1024);
        var decision = new McpOperatorDecision(true, null, null, [policyId], constraints, access.TargetSetDigest, access, 4);
        var audit = new McpOperatorAcceptedAudit(
            auditId, policyId, access.Environment, "netratel-mcp-http-prod", principal.Subject, principal.ClientId, principal.AuthorizedParty,
            [], [], principal.Scopes.Order(StringComparer.Ordinal).ToArray(), access.McpResource, access.McpInstance, access.Tool,
            access.TenantId, access.AgentId, access.OperationFamily, access.Operation, access.CorrelationId, access.RequestId, createdAtUtc);
        return new McpOperatorTerminalSessionCreateRequest(
            Guid.NewGuid().ToString("N"), decision, audit, Guid.NewGuid(), "bash", "/srv/netratel", 120, 32,
            createdAtUtc, createdAtUtc.AddMinutes(5), createdAtUtc.AddMinutes(30));
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
