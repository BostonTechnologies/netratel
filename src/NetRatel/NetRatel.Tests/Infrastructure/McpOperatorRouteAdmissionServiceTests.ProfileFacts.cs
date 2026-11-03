using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class McpOperatorRouteAdmissionServiceTests
{
    [Theory]
    [InlineData(McpOperatorEnvironment.Production)]
    [InlineData(McpOperatorEnvironment.Development)]
    public async Task Unrestricted_tenant_policy_covers_new_unprofiled_clients_and_rechecks_changed_profiles(McpOperatorEnvironment environment)
    {
        await using var db = CreateDb();
        var first = Guid.NewGuid();
        var policy = Policy(first); policy.Environment = environment; policy.TargetClassification = null;
        policy.TargetSelectorKind = McpOperatorTargetSelectorKind.Tenant; policy.AgentId = null;
        policy.Operation = "netratel_monitoring/client_series"; policy.OperationFamily = McpOperatorOperationCatalog.Find("netratel_monitoring", "client_series")!.OperationFamily;
        db.McpOperatorPolicies.Add(policy); await db.SaveChangesAsync(Ct);
        var routes = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));
        foreach (var agent in new[] { first, Guid.NewGuid() })
        {
            db.Agents.Add(new Agent { Id = agent, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active });
            await db.SaveChangesAsync(Ct);
            var original = Request(agent);
            var request = original with { Environment = environment, Tool = "netratel_monitoring", Operation = "client_series",
                RequiredScopes = Set("netratel.mcp.observe"), TargetOnline = false,
                Principal = original.Principal with { Roles = Set("Operator"), Scopes = Set("netratel.mcp.observe") } };
            var allowed = await routes.EvaluateAsync(request, Ct);
            allowed.Decision.IsAllowed.Should().BeTrue(); allowed.Decision.Request.TargetTags.Should().BeNull();
            (await routes.RecordAcceptedAsync(request, Ct)).PolicyId.Should().Be(policy.Id);
            var profile = new McpOperatorTargetProfileRecord { AgentId = agent, TenantId = 42,
                Classification = McpOperatorTargetClassification.DevelopmentSafe, TagsJson = "[]", Version = 1, UpdatedBy = "reviewer" };
            db.McpOperatorTargetProfiles.Add(profile); await db.SaveChangesAsync(Ct);
            var changed = await routes.EvaluateAsync(request, Ct);
            changed.Decision.TargetSetDigest.Should().NotBe(allowed.Decision.TargetSetDigest);
            profile.TagsJson = "{corrupt"; await db.SaveChangesAsync(Ct);
            (await routes.EvaluateAsync(request, Ct)).Decision.FailureCode.Should().Be("target_profile_invalid");
            var accept = () => routes.RecordAcceptedAsync(request, Ct);
            var rejected = await accept.Should().ThrowAsync<McpOperatorAdmissionRejectedException>();
            rejected.Which.FailureCode.Should().Be("target_profile_invalid");
        }
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Full_development_grant_does_not_admit_a_corrupt_profile()
    {
        await using var db = CreateDb(); var agent = Guid.NewGuid();
        await SeedTargetAndPolicyAsync(db, agent);
        var policy = await db.McpOperatorPolicies.SingleAsync();
        policy.TargetSelectorKind = McpOperatorTargetSelectorKind.DevelopmentEnvironment; policy.TenantId = 0;
        policy.AgentId = null; policy.TargetClassification = null;
        (await db.McpOperatorTargetProfiles.SingleAsync()).TagsJson = "null";
        await db.SaveChangesAsync();
        var routes = new McpOperatorRouteAdmissionService(db, new McpOperatorAuthorization(db));
        (await routes.EvaluateAsync(Request(agent), Ct)).Decision.FailureCode.Should().Be("target_profile_invalid");
    }
}
