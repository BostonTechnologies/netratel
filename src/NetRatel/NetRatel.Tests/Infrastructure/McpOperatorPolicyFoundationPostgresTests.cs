using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

/// <summary>
/// Exercises the generated PostgreSQL migration chain, including translation
/// of a pre-existing Dev target grant into the general policy model. An
/// in-memory provider cannot validate this JSONB and SQL migration path.
/// </summary>
public sealed class McpOperatorPolicyFoundationPostgresTests : IAsyncLifetime
{
    private const int DevTenantId = 1;
    private static readonly Guid DevQaAgentId = Guid.Parse("1641ed2c-26f7-4277-abfb-81a56c26cee9");
    private const string LegacyMigration = "20260828170000_AddDevelopmentMcpEnrollmentOwnership";
    private const string BootstrapFollowUpMigration = "20260831083000_RemoveUnresolvableDevBootstrapClassificationPredicate";
    private static readonly string[] RequiredOperatorMigrations =
    [
        "20260829150000_AddMcpOperatorScriptOwnership",
        "20260829160000_AddMcpOperatorJobOwnership",
        "20260829170000_AddMcpOperatorTaskOwnership",
        "20260829180000_AddMcpOperatorRequestOwnership",
        "20260829183612_AddAgentDecommissionState",
        "20260829190000_AddTenantOperatorConcurrency",
        BootstrapFollowUpMigration,
        "20260831133310_SeedDevMcpPolicyAdminTenantProvisioning"
    ];
    [Fact]
    public async Task SeededFullDevGrant_AdmitsEveryCatalogOperationWithAllScopesAndRoles()
    {
        await using var db = new OrchestratorDbContext(_options);
        var policy = await db.McpOperatorPolicies.SingleAsync(p =>
            p.PrincipalSelectorValue == "netratel-mcp-dev-feature-testers");
        policy.TargetSelectorKind.Should().Be(McpOperatorTargetSelectorKind.DevelopmentEnvironment);
        var principal = new McpOperatorPrincipal("feature-tester", "mcp-client", "mcp-client",
            new HashSet<string> { "netratel-mcp-dev-feature-testers" },
            new HashSet<string> { "Observer", "Operator", "AutomationOperator", "OnboardingOperator", "PolicyAdministrator" },
            new HashSet<string> { "netratel.mcp.read", "netratel.mcp.observe", "netratel.mcp.files", "netratel.mcp.write", "netratel.mcp.execute", "netratel.mcp.onboarding", "netratel.mcp.admin" });
        var authority = new McpOperatorAuthorization(db);
        foreach (var operation in McpOperatorOperationCatalog.Operations)
        {
            var request = new McpOperatorAccessRequest(McpOperatorEnvironment.Development, principal,
                12345, Guid.NewGuid(), null, operation.OperationFamily,
                $"{operation.ToolName}/{operation.OperationName}",
                new HashSet<string> { NetRatel.Shared.Operations.McpOperationAccessScopeNames.Canonical(operation.RequiredScope) },
                operation.ConfirmationClass, "certification-correlation", "certification-request", Tool: operation.ToolName);
            var decision = await authority.EvaluateAsync(request, CancellationToken.None);
            decision.IsAllowed.Should().BeTrue($"the seeded grant covers {request.Operation}");
            decision.DevelopmentEnvironmentAccess.Should().BeTrue();
        }
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private Guid _agentId;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var db = new OrchestratorDbContext(_options);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(LegacyMigration);

        _agentId = DevQaAgentId;
        var grantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // Seed against the historical schema under test. The current entity
        // model includes decommission columns introduced after this migration,
        // so tracking it here would incorrectly require future columns.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Agents" ("Id", "TenantId", "Name", "Status", "IsEnabled", "CreatedAtUtc")
            VALUES ({_agentId}, {DevTenantId}, {"migration-qa-target"}, {(short)AgentStatus.Active}, {true}, {now});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DevelopmentOperatorTargetGrants" (
                "Id", "TenantId", "AgentId", "Classification", "AllowedOperations", "FileFixtureRoot",
                "EvidenceReference", "GrantedBy", "GrantedAtUtc", "ExpiresAtUtc")
            VALUES (
                {grantId}, {DevTenantId}, {_agentId}, {(short)DevelopmentOperatorTargetClassification.DedicatedQa},
                {(int)(DevelopmentOperatorOperationScope.JobRuns | DevelopmentOperatorOperationScope.FileSystem)},
                {"/tmp/netratel-mcp-migration"}, {"issue-955"}, {"operator-1"}, {now.AddMinutes(-1)}, {now.AddDays(1)});
            """);

        await migrator.MigrateAsync(BootstrapFollowUpMigration);

        var normalAdmissionId = Guid.NewGuid();
        var normalAdmissionConstraints = """{"maxFanOut":1,"destructiveOperationsAllowed":false}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "McpOperatorPolicies" (
                "Id", "Name", "Environment", "Effect", "Priority",
                "PrincipalSelectorKind", "PrincipalSelectorValue",
                "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
                "TargetClassification", "OperationFamily", "Operation",
                "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
                "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference",
                "LifecycleState")
            VALUES (
                {normalAdmissionId}, {"test normal policy admission"}, {(short)McpOperatorEnvironment.Development}, {(short)McpOperatorPolicyEffect.Allow}, 90,
                {(short)McpOperatorPrincipalSelectorKind.OidcGroup}, {"netratel-mcp-dev-policy-administrator"},
                {(short)McpOperatorTargetSelectorKind.ExactAgent}, {DevTenantId}, {_agentId}, NULL,
                NULL, {(int)McpOperatorOperationFamily.PolicyAdministration}, NULL,
                {normalAdmissionConstraints}::jsonb, {now}, {"test-normal-policy-admission"}, {now.AddDays(1)},
                {now.AddHours(1)}, NULL, NULL, 1, {"test-normal-policy-admission"}, {(short)McpOperatorPolicyLifecycleState.Active});
            """);

        await migrator.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task Legacy_Dev_grant_is_preserved_as_a_usable_general_compatibility_policy()
    {
        await using var db = new OrchestratorDbContext(_options);
        var policies = await db.McpOperatorPolicies.ToArrayAsync();
        var policy = policies.Single(candidate => candidate.PrincipalSelectorValue == "development-compatibility");
        var bootstrapPolicy = policies.Single(candidate => candidate.AuditReference == "dev-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator");
        var normalAdmission = policies.Single(candidate => candidate.AuditReference == "test-normal-policy-admission");
        var provisioningAdmission = policies.Single(candidate => candidate.AuditReference == "dev-policy-bootstrap:netratel-mcp-dev-policy-administrator:tenant-provisioning");
        var targetProfile = await db.McpOperatorTargetProfiles.SingleAsync();
        (await db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);

        policy.Environment.Should().Be(McpOperatorEnvironment.Development);
        policy.Effect.Should().Be(McpOperatorPolicyEffect.Allow);
        policy.PrincipalSelectorKind.Should().Be(McpOperatorPrincipalSelectorKind.ServicePrincipal);
        policy.PrincipalSelectorValue.Should().Be("development-compatibility");
        policy.TargetSelectorKind.Should().Be(McpOperatorTargetSelectorKind.ExactAgent);
        policy.TenantId.Should().Be(DevTenantId);
        policy.AgentId.Should().Be(_agentId);
        policy.OperationFamily.Should().HaveFlag(McpOperatorOperationFamily.JobExecution);
        policy.OperationFamily.Should().HaveFlag(McpOperatorOperationFamily.AutomationWrite);
        policy.OperationFamily.Should().HaveFlag(McpOperatorOperationFamily.Requests);
        policy.OperationFamily.Should().HaveFlag(McpOperatorOperationFamily.FileRead);
        policy.OperationFamily.Should().HaveFlag(McpOperatorOperationFamily.FileWrite);
        policy.AuditReference.Should().StartWith("legacy-dev-grant:");
        policy.LifecycleState.Should().Be(McpOperatorPolicyLifecycleState.Active);

        bootstrapPolicy.Environment.Should().Be(McpOperatorEnvironment.Development);
        bootstrapPolicy.Effect.Should().Be(McpOperatorPolicyEffect.Allow);
        bootstrapPolicy.PrincipalSelectorKind.Should().Be(McpOperatorPrincipalSelectorKind.OidcGroup);
        bootstrapPolicy.PrincipalSelectorValue.Should().Be("netratel-mcp-dev-policy-bootstrap-administrator");
        bootstrapPolicy.TargetSelectorKind.Should().Be(McpOperatorTargetSelectorKind.ExactAgent);
        bootstrapPolicy.TenantId.Should().Be(DevTenantId);
        bootstrapPolicy.AgentId.Should().Be(_agentId);
        // Policy-administration mutations do not carry a target
        // classification. The durable bootstrap remains exact-agent-bound,
        // but must not require an unavailable runtime input.
        bootstrapPolicy.TargetClassification.Should().BeNull();
        bootstrapPolicy.OperationFamily.Should().Be(McpOperatorOperationFamily.PolicyAdministration);
        bootstrapPolicy.ExpiresAtUtc.Should().Be(policy.ExpiresAtUtc);
        bootstrapPolicy.ReviewByUtc.Should().NotBeNull();
        bootstrapPolicy.ExpiresAtUtc.Should().NotBeNull();
        bootstrapPolicy.ReviewByUtc!.Value.Should().BeOnOrBefore(bootstrapPolicy.ExpiresAtUtc!.Value);
        using var bootstrapConstraints = JsonDocument.Parse(bootstrapPolicy.ConstraintsJson);
        bootstrapConstraints.RootElement.GetProperty("maxFanOut").GetInt32().Should().Be(1);
        bootstrapConstraints.RootElement.TryGetProperty("allowedTargetClassifications", out _).Should().BeFalse();
        bootstrapConstraints.RootElement.GetProperty("destructiveOperationsAllowed").GetBoolean().Should().BeFalse();
        provisioningAdmission.Environment.Should().Be(McpOperatorEnvironment.Development);
        provisioningAdmission.Effect.Should().Be(McpOperatorPolicyEffect.Allow);
        provisioningAdmission.PrincipalSelectorKind.Should().Be(McpOperatorPrincipalSelectorKind.OidcGroup);
        provisioningAdmission.PrincipalSelectorValue.Should().Be("netratel-mcp-dev-policy-administrator");
        provisioningAdmission.TargetSelectorKind.Should().Be(McpOperatorTargetSelectorKind.Tenant);
        provisioningAdmission.TenantId.Should().Be(DevTenantId);
        provisioningAdmission.AgentId.Should().BeNull();
        provisioningAdmission.TargetClassification.Should().BeNull();
        provisioningAdmission.OperationFamily.Should().Be(McpOperatorOperationFamily.PolicyAdministration);
        provisioningAdmission.ExpiresAtUtc.Should().Be(normalAdmission.ExpiresAtUtc);
        provisioningAdmission.ReviewByUtc.Should().Be(normalAdmission.ReviewByUtc);
        using var provisioningConstraints = JsonDocument.Parse(provisioningAdmission.ConstraintsJson);
        provisioningConstraints.RootElement.GetProperty("maxFanOut").GetInt32().Should().Be(1);
        provisioningConstraints.RootElement.GetProperty("destructiveOperationsAllowed").GetBoolean().Should().BeFalse();
        targetProfile.AgentId.Should().Be(_agentId);
        targetProfile.TenantId.Should().Be(DevTenantId);
        targetProfile.Classification.Should().Be(McpOperatorTargetClassification.DedicatedQa);
        targetProfile.TagsJson.Should().Be("[]");

        var authority = new McpOperatorAuthorization(db);
        var decision = await authority.EvaluateAsync(new McpOperatorAccessRequest(
            McpOperatorEnvironment.Development,
            new McpOperatorPrincipal(
                "development-compatibility",
                null,
                null,
                Set(),
                Set(),
                Set("netratel.mcp.legacy-dev"),
                "development-compatibility"),
            DevTenantId,
            _agentId,
            McpOperatorTargetClassification.DedicatedQa,
            McpOperatorOperationFamily.FileRead,
            "legacy/FileRead",
            Set("netratel.mcp.legacy-dev"),
            McpOperatorConfirmationClass.None,
            "corr-77",
            "request-77",
            McpResource: "https://mcp.dev.example/mcp",
            McpInstance: "dev",
            Tool: "development-compatibility"),
            CancellationToken.None);

        decision.IsAllowed.Should().BeTrue();
        var accepted = await authority.RecordAcceptedAsync(decision, "development-compatibility", CancellationToken.None);
        accepted.PolicyId.Should().Be(policy.Id);
        accepted.McpInstance.Should().Be("dev");
        accepted.Tool.Should().Be("development-compatibility");
        var audit = await db.McpOperatorAcceptedAudits.SingleAsync();
        audit.GroupsJson.Should().Be("[]");
        audit.ScopesJson.Should().Be("[\"netratel.mcp.legacy-dev\"]");

        var bootstrapDecision = await authority.EvaluateAsync(new McpOperatorAccessRequest(
            McpOperatorEnvironment.Development,
            new McpOperatorPrincipal(
                "policy-admin-subject",
                null,
                null,
                Set("netratel-mcp-dev-policy-bootstrap-administrator"),
                Set("PolicyAdministrator"),
                Set("netratel.mcp.admin")),
            DevTenantId,
            _agentId,
            null,
            McpOperatorOperationFamily.PolicyAdministration,
            "netratel_policy/policies",
            Set("netratel.mcp.admin"),
            McpOperatorConfirmationClass.None,
            "bootstrap-correlation",
            "bootstrap-request",
            McpResource: "https://mcp.dev.example/mcp",
            McpInstance: "dev",
            Tool: "netratel_policy",
            TargetEnabled: true,
            TargetOnline: true,
            CapabilityAvailable: true),
            CancellationToken.None);

        bootstrapDecision.IsAllowed.Should().BeTrue();

        var provisioningDecision = await authority.EvaluateAsync(new McpOperatorAccessRequest(
            McpOperatorEnvironment.Development,
            new McpOperatorPrincipal(
                "normal-policy-admin-subject",
                null,
                null,
                Set("netratel-mcp-dev-policy-administrator"),
                Set("PolicyAdministrator"),
                Set("netratel.mcp.admin")),
            DevTenantId,
            null,
            null,
            McpOperatorOperationFamily.PolicyAdministration,
            "netratel_policy/preview_create",
            Set("netratel.mcp.admin"),
            McpOperatorConfirmationClass.PolicyAdministration,
            "provisioning-correlation",
            "provisioning-request",
            McpResource: "https://mcp.dev.example/mcp",
            McpInstance: "dev",
            Tool: "netratel_policy",
            TargetEnabled: true,
            TargetOnline: true,
            CapabilityAvailable: true),
            CancellationToken.None);

        provisioningDecision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Operator_ownership_and_tenant_concurrency_migrations_are_discoverable()
    {
        using var db = new OrchestratorDbContext(
            new DbContextOptionsBuilder<OrchestratorDbContext>()
                .UseNpgsql("Host=localhost;Database=netratel_migration_catalog")
                .Options);

        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys;

        migrations.Should().Contain(RequiredOperatorMigrations);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
