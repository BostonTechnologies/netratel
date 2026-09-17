using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorAuthorizationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EvaluateAsync_DefaultsToDenyWhenNoPersistedPolicyMatches()
    {
        await using var db = CreateDb();
        var decision = await Authority(db).EvaluateAsync(Request(), Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_policy_missing");
        decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.Policy);
    }

    [Fact]
    public async Task EvaluateAsync_AllowsAnExactTargetPolicyForTheMatchingScopedPrincipal()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeTrue();
        decision.MatchingPolicyIds.Should().ContainSingle();
        decision.EffectiveConstraints.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_AllowsAValidatedEmailLikeOAuthSubject()
    {
        await using var db = CreateDb();
        var original = Request();
        var request = original with
        {
            Principal = original.Principal with { Subject = "operator@example.test" }
        };
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ExplicitDenyWinsOverAHigherPriorityAllow()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request, McpOperatorPolicyEffect.Allow, priority: 100);
        AddPolicy(db, request, McpOperatorPolicyEffect.Deny, priority: -100, applyTargetClassification: false);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_policy_denied");
        decision.MatchingPolicyIds.Should().ContainSingle();
    }

    [Fact]
    public async Task EvaluateAsync_RejectsMissingOAuthScopeBeforePolicyDetails()
    {
        await using var db = CreateDb();
        var permittedRequest = Request();
        AddPolicy(db, permittedRequest);
        await db.SaveChangesAsync(Ct);
        var missingScopeRequest = permittedRequest with
        {
            Principal = permittedRequest.Principal with { Scopes = Set("netratel.mcp.read") }
        };

        var decision = await Authority(db).EvaluateAsync(missingScopeRequest, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("oauth_scope_missing");
        decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.OAuthScope);
        decision.MatchingPolicyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_RejectsMissingMappedRoleBeforeTenantOrPolicyDetails()
    {
        await using var db = CreateDb();
        var permittedRequest = Request();
        AddPolicy(db, permittedRequest);
        await db.SaveChangesAsync(Ct);
        var missingRoleRequest = permittedRequest with
        {
            Principal = permittedRequest.Principal with { Roles = Set("Operator") },
            TenantVisible = false
        };

        var decision = await Authority(db).EvaluateAsync(missingRoleRequest, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("oauth_role_missing");
        decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.OAuthScope);
        decision.MatchingPolicyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_ReportsAnExpiredMatchingPolicy()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request, expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_policy_expired");
    }

    [Fact]
    public async Task EvaluateAsync_ReportsCapabilityUnavailableAfterAuthorizationPasses()
    {
        await using var db = CreateDb();
        var request = Request() with { CapabilityAvailable = false };
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("capability_unavailable");
        decision.FailureLayer.Should().Be(McpOperatorAuthorizationLayer.Capability);
    }

    [Fact]
    public async Task RecordAcceptedAsync_PersistsOnlyTrustedContentFreeMetadata()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);
        var authority = Authority(db);
        var decision = await authority.EvaluateAsync(request, Ct);

        var accepted = await authority.RecordAcceptedAsync(decision, "netratel-mcp-service", Ct);
        var row = await db.McpOperatorAcceptedAudits.SingleAsync(Ct);

        accepted.AuditId.Should().Be(row.Id);
        row.PolicyId.Should().Be(decision.MatchingPolicyIds.Single());
        row.Subject.Should().Be("operator-42");
        row.ServicePrincipal.Should().Be("netratel-mcp-service");
        row.AuthorizedParty.Should().Be("automation-client");
        row.GroupsJson.Should().Be("[\"netratel-operators\"]");
        row.RolesJson.Should().Be("[\"AutomationOperator\"]");
        row.ScopesJson.Should().Be("[\"netratel.mcp.execute\"]");
        row.McpResource.Should().Be("https://mcp.dev.example/mcp");
        row.McpInstance.Should().Be("dev");
        row.Tool.Should().Be("netratel_terminal");
        typeof(McpOperatorAcceptedAuditRecord).GetProperties().Select(property => property.Name)
            .Should().NotContain(["BearerToken", "Command", "Payload", "FileContent", "Secret"]);
    }

    [Fact]
    public async Task RecordAcceptedAsync_ReportsTheCurrentSafeDenialWhenThePolicyIsRevokedBeforeAudit()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);
        var authority = Authority(db);
        var decision = await authority.EvaluateAsync(request, Ct);
        db.McpOperatorPolicies.Remove(await db.McpOperatorPolicies.SingleAsync(Ct));
        await db.SaveChangesAsync(Ct);

        var action = () => authority.RecordAcceptedAsync(decision, "netratel-mcp-service", Ct);

        var rejection = await action.Should().ThrowAsync<McpOperatorAdmissionRejectedException>();
        rejection.Which.FailureCode.Should().Be("target_policy_missing");
        (await db.McpOperatorAcceptedAudits.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task EvaluateAsync_RejectsAPersistedRevokedPolicy()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);
        var policy = await db.McpOperatorPolicies.SingleAsync(Ct);
        policy.LifecycleState = McpOperatorPolicyLifecycleState.Revoked;
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_policy_missing");
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotRevealTargetPolicyToACallerWithoutTenantVisibility()
    {
        await using var db = CreateDb();
        var request = Request() with { TenantVisible = false };
        AddPolicy(db, request);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("tenant_not_authorized");
        decision.MatchingPolicyIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_AllowsOnlyAnExplicitControlPlanePolicyForANonAgentControlRequest()
    {
        await using var db = CreateDb();
        var request = Request() with
        {
            TenantId = 0,
            AgentId = null,
            TargetClassification = null,
            OperationFamily = McpOperatorOperationFamily.TenantAdministration,
            Operation = "netratel_tenants/create",
            RequiredScopes = Set("netratel.mcp.admin"),
            ConfirmationClass = McpOperatorConfirmationClass.FleetWide,
            TargetSetDigest = new string('A', 64),
            Principal = Request().Principal with { Scopes = Set("netratel.mcp.admin") }
        };
        AddPolicy(db, request, targetSelectorKind: McpOperatorTargetSelectorKind.ControlPlane);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeTrue();
        (await Authority(db).HasTenantVisibilityAsync(request.Environment, request.Principal, 0, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotTreatATenantPolicyAsGlobalControlPlaneAuthority()
    {
        await using var db = CreateDb();
        var request = Request() with
        {
            TenantId = 0,
            AgentId = null,
            TargetClassification = null,
            OperationFamily = McpOperatorOperationFamily.TenantAdministration,
            Operation = "netratel_tenants/create",
            RequiredScopes = Set("netratel.mcp.admin"),
            ConfirmationClass = McpOperatorConfirmationClass.FleetWide,
            TargetSetDigest = new string('A', 64),
            Principal = Request().Principal with { Scopes = Set("netratel.mcp.admin") }
        };
        AddPolicy(db, request, targetSelectorKind: McpOperatorTargetSelectorKind.Tenant, tenantId: 42);
        await db.SaveChangesAsync(Ct);

        var decision = await Authority(db).EvaluateAsync(request, Ct);

        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_policy_missing");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(12345)]
    public async Task DevelopmentEnvironmentPolicy_CoversControlPlaneAndFutureTenants(int tenantId)
    {
        await using var db = CreateDb();
        var request = Request() with
        {
            TenantId = tenantId,
            AgentId = tenantId == 0 ? null : Guid.NewGuid(),
            TargetClassification = null
        };
        AddPolicy(db, request, targetSelectorKind: McpOperatorTargetSelectorKind.DevelopmentEnvironment,
            tenantId: 0, applyTargetClassification: false);
        await db.SaveChangesAsync(Ct);

        (await Authority(db).HasTenantVisibilityAsync(request.Environment, request.Principal, tenantId, Ct)).Should().BeTrue();
        var decision = await Authority(db).EvaluateAsync(request, Ct);
        decision.IsAllowed.Should().BeTrue();
        decision.DevelopmentEnvironmentAccess.Should().BeTrue();
        var batch = await new McpOperatorSearchAuthorization(db).EvaluateAsync([request], Ct);
        batch.Should().ContainSingle().Which.IsAllowed.Should().BeTrue();
        var audit = await Authority(db).RecordAcceptedAsync(decision, "mcp-service", Ct);
        audit.TenantId.Should().Be(tenantId);
        audit.PolicyId.Should().Be(decision.MatchingPolicyIds.Single());
    }

    [Fact]
    public async Task DevelopmentEnvironmentPolicy_NeverAuthorizesProductionEvenWithProductionPolicyRow()
    {
        await using var db = CreateDb();
        var request = Request() with { Environment = McpOperatorEnvironment.Production };
        AddPolicy(db, request, targetSelectorKind: McpOperatorTargetSelectorKind.DevelopmentEnvironment,
            tenantId: 0, applyTargetClassification: false);
        await db.SaveChangesAsync(Ct);

        (await Authority(db).HasTenantVisibilityAsync(request.Environment, request.Principal, 42, Ct)).Should().BeFalse();
        (await Authority(db).EvaluateAsync(request, Ct)).IsAllowed.Should().BeFalse();
        var caller = await new McpOperatorAccessEvaluationService(db, Authority(db))
            .WhoAmIAsync(request.Environment, request.Principal, Ct);
        caller.DevelopmentEnvironmentAccess.Should().BeFalse();
        caller.VisibleTenantIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DevelopmentEnvironmentPolicy_DiscoversNewTenantsAndStopsAfterRevocation()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request, targetSelectorKind: McpOperatorTargetSelectorKind.DevelopmentEnvironment,
            tenantId: 0, applyTargetClassification: false);
        db.Tenants.Add(new Tenant { Id = 42, Name = "Existing tenant" });
        await db.SaveChangesAsync(Ct);
        var service = new McpOperatorAccessEvaluationService(db, Authority(db));
        (await service.WhoAmIAsync(request.Environment, request.Principal, Ct)).VisibleTenantIds.Should().Equal(0, 42);
        db.Tenants.Add(new Tenant { Id = 99, Name = "New tenant" });
        await db.SaveChangesAsync(Ct);
        (await service.WhoAmIAsync(request.Environment, request.Principal, Ct)).VisibleTenantIds.Should().Equal(0, 42, 99);

        var policy = await db.McpOperatorPolicies.SingleAsync(Ct);
        policy.LifecycleState = McpOperatorPolicyLifecycleState.Revoked;
        await db.SaveChangesAsync(Ct);
        (await service.WhoAmIAsync(request.Environment, request.Principal, Ct)).VisibleTenantIds.Should().BeEmpty();
        (await Authority(db).EvaluateAsync(request, Ct)).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task DevelopmentEnvironmentPolicy_DoesNotGrantOtherPrincipalsOrMissingScopes()
    {
        await using var db = CreateDb();
        var request = Request();
        AddPolicy(db, request, targetSelectorKind: McpOperatorTargetSelectorKind.DevelopmentEnvironment,
            tenantId: 0, applyTargetClassification: false);
        await db.SaveChangesAsync(Ct);
        var stranger = request with { Principal = request.Principal with { Subject = "unassigned-user" } };
        (await Authority(db).EvaluateAsync(stranger, Ct)).IsAllowed.Should().BeFalse();
        var missingScope = request with { Principal = request.Principal with { Scopes = Set() } };
        (await Authority(db).EvaluateAsync(missingScope, Ct)).FailureCode.Should().Be("oauth_scope_missing");
    }

    private static McpOperatorAuthorization Authority(OrchestratorDbContext db) => new(db);

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrchestratorDbContext(options);
    }

    private static McpOperatorAccessRequest Request() => new(
        McpOperatorEnvironment.Development,
        new McpOperatorPrincipal("operator-42", "automation-client", "automation-client", Set("netratel-operators"), Set("AutomationOperator"), Set("netratel.mcp.execute")),
        42,
        Guid.Parse("9ea74b4e-2bdb-4d1e-929a-4e62891fbd52"),
        McpOperatorTargetClassification.DevelopmentSafe,
        McpOperatorOperationFamily.TerminalExecute,
        "netratel_terminal/open",
        Set("netratel.mcp.execute"),
        McpOperatorConfirmationClass.RemoteExecution,
        "corr-42",
        "request-42",
        McpResource: "https://mcp.dev.example/mcp",
        McpInstance: "dev",
        Tool: "netratel_terminal");

    private static void AddPolicy(
        OrchestratorDbContext db,
        McpOperatorAccessRequest request,
        McpOperatorPolicyEffect effect = McpOperatorPolicyEffect.Allow,
        int priority = 0,
        DateTimeOffset? expiresAtUtc = null,
        bool applyTargetClassification = true,
        McpOperatorTargetSelectorKind targetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
        int? tenantId = null)
    {
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = Guid.NewGuid(),
            Name = $"policy-{Guid.NewGuid():N}",
            Environment = request.Environment,
            Effect = effect,
            Priority = priority,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
            PrincipalSelectorValue = request.Principal.Subject,
            TargetSelectorKind = targetSelectorKind,
            TenantId = tenantId ?? request.TenantId,
            AgentId = targetSelectorKind == McpOperatorTargetSelectorKind.ExactAgent ? request.AgentId : null,
            TargetClassification = applyTargetClassification ? request.TargetClassification : null,
            OperationFamily = request.OperationFamily,
            Operation = request.Operation,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "policy-admin",
            ExpiresAtUtc = expiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(1),
            Version = 1
        });
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
