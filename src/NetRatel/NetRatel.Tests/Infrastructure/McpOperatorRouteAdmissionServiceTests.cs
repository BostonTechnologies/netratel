using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorRouteAdmissionServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EvaluateAndRecord_UsesServerResolvedTargetFactsAndDelegatedPrincipal()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedTargetAndPolicyAsync(db, agentId);
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));

        var admission = await service.EvaluateAsync(Request(agentId), Ct);
        var audit = await service.RecordAcceptedAsync(Request(agentId), Ct);

        admission.Operation.Should().NotBeNull();
        admission.Operation!.OperationFamily.Should().Be(McpOperatorOperationFamily.TerminalExecute);
        admission.Decision.IsAllowed.Should().BeTrue();
        admission.Decision.Request.TargetClassification.Should().Be(McpOperatorTargetClassification.DevelopmentSafe);
        admission.Decision.Request.TargetTags.Should().BeEquivalentTo(["qa", "development"]);
        admission.Decision.TargetSetDigest.Should().HaveLength(64);
        audit.Subject.Should().Be("operator@example.test");
        audit.ServicePrincipal.Should().Be("netratel-mcp-dev");
        (await db.McpOperatorAcceptedAudits.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Evaluate_HidesTargetExistenceBeforeTenantVisibilityIsEstablished()
    {
        await using var db = CreateDb();
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));

        var admission = await service.EvaluateAsync(Request(Guid.NewGuid()), Ct);

        admission.Decision.IsAllowed.Should().BeFalse();
        admission.Decision.FailureCode.Should().Be("tenant_not_authorized");
        admission.Decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.Tenant);
    }

    [Fact]
    public async Task Evaluate_RejectsMissingMappedRoleBeforeTenantOrTargetResolution()
    {
        await using var db = CreateDb();
        var request = Request(Guid.NewGuid());
        request = request with
        {
            Principal = request.Principal with { Roles = Set("Operator") }
        };
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));

        var admission = await service.EvaluateAsync(request, Ct);

        admission.Decision.IsAllowed.Should().BeFalse();
        admission.Decision.FailureCode.Should().Be("oauth_role_missing");
        admission.Decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.OAuthScope);
    }

    [Fact]
    public async Task Evaluate_RejectsAnUnprofiledTargetEvenWhenTheCallerCanSeeTheTenant()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.McpOperatorPolicies.Add(Policy(agentId));
        await db.SaveChangesAsync(Ct);
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));

        var admission = await service.EvaluateAsync(Request(agentId), Ct);

        admission.Decision.IsAllowed.Should().BeFalse();
        admission.Decision.FailureCode.Should().Be("target_policy_missing");
        admission.Decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.Policy);
    }

    [Fact]
    public async Task FullDevGrant_AdmitsNewUnprofiledClientAndPreservesAudit()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agentId, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active });
        var policy = Policy(agentId);
        policy.TargetSelectorKind = McpOperatorTargetSelectorKind.DevelopmentEnvironment;
        policy.TenantId = 0;
        policy.AgentId = null;
        policy.TargetClassification = null;
        db.McpOperatorPolicies.Add(policy);
        await db.SaveChangesAsync(Ct);
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));

        var admission = await service.EvaluateAsync(Request(agentId), Ct);
        admission.Decision.IsAllowed.Should().BeTrue();
        admission.Decision.DevelopmentEnvironmentAccess.Should().BeTrue();
        admission.Decision.TargetSetDigest.Should().HaveLength(64);
        (await service.RecordAcceptedAsync(Request(agentId), Ct)).PolicyId.Should().Be(policy.Id);
    }

    [Theory]
    [InlineData("get", "netratel.mcp.observe")]
    [InlineData("disable", "netratel.mcp.admin")]
    [InlineData("preview_enable", "netratel.mcp.admin")]
    [InlineData("enable", "netratel.mcp.admin")]
    [InlineData("preview_delete", "netratel.mcp.admin")]
    [InlineData("delete", "netratel.mcp.admin")]
    public async Task Disabled_unprofiled_client_remains_administrable_with_current_full_Dev_policy(string operation, string scope)
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agentId, TenantId = 42, IsEnabled = false, Status = AgentStatus.Disabled });
        var policy = Policy(agentId);
        policy.TargetSelectorKind = McpOperatorTargetSelectorKind.DevelopmentEnvironment;
        policy.TenantId = 0;
        policy.AgentId = null;
        policy.TargetClassification = null;
        policy.OperationFamily |= McpOperatorOperationFamily.ClientAdministration;
        policy.Operation = null;
        db.McpOperatorPolicies.Add(policy);
        await db.SaveChangesAsync(Ct);
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));
        var request = Request(agentId) with
        {
            Tool = "netratel_clients", Operation = operation, RequiredScopes = Set(scope),
            Principal = Request(agentId).Principal with
            {
                Roles = Set("Observer", "PolicyAdministrator"), Scopes = Set(scope)
            }
        };

        var allowed = await service.EvaluateAsync(request, Ct);
        allowed.Decision.IsAllowed.Should().BeTrue();
        allowed.Decision.Request.TargetEnabled.Should().BeFalse();
        (await service.RecordAcceptedAsync(request, Ct)).PolicyId.Should().Be(policy.Id);

        // The same disabled installation must still reject remote execution,
        // and report the real target state rather than a misleading policy error.
        var execution = await service.EvaluateAsync(Request(agentId), Ct);
        execution.Decision.IsAllowed.Should().BeFalse();
        execution.Decision.FailureCode.Should().Be("target_disabled");

        var missingScope = request with { Principal = request.Principal with { Scopes = Set() } };
        (await service.EvaluateAsync(missingScope, Ct)).Decision.FailureCode.Should().Be("oauth_scope_missing");
        policy.Effect = McpOperatorPolicyEffect.Deny;
        await db.SaveChangesAsync(Ct);
        var denied = await service.EvaluateAsync(request, Ct);
        denied.Decision.IsAllowed.Should().BeFalse();
        denied.Decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.Policy);
    }

    [Fact]
    public async Task RecordAccepted_ReevaluatesAndRejectsAPolicyRevokedAfterTheInitialDecision()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedTargetAndPolicyAsync(db, agentId);
        var service = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));
        var request = Request(agentId);

        (await service.EvaluateAsync(request, Ct)).Decision.IsAllowed.Should().BeTrue();
        db.McpOperatorPolicies.Remove(await db.McpOperatorPolicies.SingleAsync(Ct));
        await db.SaveChangesAsync(Ct);

        var action = () => service.RecordAcceptedAsync(request, Ct);

        var rejection = await action.Should().ThrowAsync<McpOperatorAdmissionRejectedException>();
        rejection.Which.FailureCode.Should().Be("tenant_not_authorized");
        (await db.McpOperatorAcceptedAudits.CountAsync(Ct)).Should().Be(0);
    }

    private static async Task SeedTargetAndPolicyAsync(OrchestratorDbContext db, Guid agentId)
    {
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            AgentId = agentId,
            TenantId = 42,
            Classification = McpOperatorTargetClassification.DevelopmentSafe,
            TagsJson = "[\"qa\",\"development\"]",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedBy = "policy-admin@example.test",
            Version = 3
        });
        db.McpOperatorPolicies.Add(Policy(agentId));
        await db.SaveChangesAsync(Ct);
    }

    private static McpOperatorPolicyRecord Policy(Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "terminal execution for operator",
        Environment = McpOperatorEnvironment.Development,
        Effect = McpOperatorPolicyEffect.Allow,
        Priority = 10,
        PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
        PrincipalSelectorValue = "operator@example.test",
        TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
        TenantId = 42,
        AgentId = agentId,
        TargetClassification = McpOperatorTargetClassification.DevelopmentSafe,
        OperationFamily = McpOperatorOperationFamily.TerminalExecute,
        Operation = "netratel_terminal/open",
        ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(), McpOperatorJsonContext.Default.McpOperatorConstraints),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        CreatedBy = "policy-admin@example.test",
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
        Version = 1
    };

    private static McpOperatorRouteAccessRequest Request(Guid agentId) => new(
        McpOperatorEnvironment.Development,
        new McpOperatorPrincipal(
            "operator@example.test",
            "automation-client",
            "automation-client",
            Set("netratel-operators"),
            Set("AutomationOperator"),
            Set("netratel.mcp.execute")),
        "netratel-mcp-dev",
        "https://mcp.dev.example/mcp",
        "dev",
        "netratel_terminal",
        "open",
        42,
        agentId,
        Set("netratel.mcp.execute"),
        "corr-42",
        "request-42",
        TargetOnline: true,
        CapabilityAvailable: true);

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
