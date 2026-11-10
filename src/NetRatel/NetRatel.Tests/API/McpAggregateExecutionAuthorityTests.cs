using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpAggregateExecutionAuthorityTests
{
    [Fact]
    public async Task External_MCP_authority_requires_current_application_grants_even_when_saved_Operator_facts_remain()
    {
        await using var identity = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseInMemoryDatabase("reviewed-external-" + Guid.NewGuid()).Options);
        const string issuer = "https://login.example.test";
        identity.ApplicationPrincipals.Add(new() { Id = "mapped-operator", ExternalIssuer = issuer, ExternalSubject = "external-subject",
            ExternalAuthorityIssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExternalAuthorityExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            ExternalAuthorityClaimsJson = JsonSerializer.Serialize(new[] { new ExternalAuthorityClaim("roles", "Operator") }) });
        await identity.SaveChangesAsync();
        var scopes = new HashSet<string> { "netratel.mcp.execute" };
        var principal = new McpOperatorPrincipal("external-subject", "external-client", null, new HashSet<string>(), new HashSet<string>(), scopes);
        var route = new McpOperatorRouteAccessRequest(McpOperatorEnvironment.Production, principal, "mcp-service", "https://mcp.example.test/mcp",
            "prod", "netratel_job_runs", "start", 42, Guid.NewGuid(), scopes, "correlation", "request", true, true);
        var reviewed = new McpAggregateReviewedAuthority(route, issuer, null, []);
        var access = new EffectiveAccessService(identity, new ConfigurationBuilder().Build());
        var authority = new McpAggregateExecutionAuthority(null!, null!, null!, access, new ApplicationPrincipalResolver(identity), null!, null!);
        (await Assert.ThrowsAsync<McpOperatorAdmissionRejectedException>(() => authority.RequireApplicationAsync(reviewed, CancellationToken.None)))
            .FailureCode.Should().Be("application_permission_denied");
        var role = new AccessRole { Name = "Explicit reviewed job author", DelegationRank = 10 };
        role.Permissions.Add(new() { Permission = NetRatelPermissions.JobManagement });
        role.Permissions.Add(new() { Permission = NetRatelPermissions.ScriptEdit });
        identity.AccessRoles.Add(role);
        var assignment = new PrincipalRoleAssignment { PrincipalId = "mapped-operator", RoleId = role.Id, TenantId = 42 };
        identity.PrincipalRoleAssignments.Add(assignment); await identity.SaveChangesAsync();
        await authority.RequireApplicationAsync(reviewed, CancellationToken.None);
        identity.PrincipalRoleAssignments.Remove(assignment); await identity.SaveChangesAsync();
        (await Assert.ThrowsAsync<McpOperatorAdmissionRejectedException>(() => authority.RequireApplicationAsync(reviewed, CancellationToken.None)))
            .FailureCode.Should().Be("application_permission_denied");
    }

    [Theory]
    [InlineData(true, "delegated_identity_revoked")]
    [InlineData(false, "application_permission_denied")]
    public async Task Current_credential_and_application_withdrawal_are_native_execution_denials(bool revokeCredential, string code)
    {
        var agent = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var scopes = new HashSet<string> { "netratel.mcp.execute" };
        var principal = new McpOperatorPrincipal("operator", "credential", null, new HashSet<string>(), new HashSet<string>(), scopes);
        var route = new McpOperatorRouteAccessRequest(McpOperatorEnvironment.Production, principal, "mcp-service",
            "https://mcp.example.test/mcp", "prod", "netratel_job_runs", "start", 42, agent, scopes, "correlation", "request", true, true);
        var target = JobTargetDefinition.FromLegacy(agent);
        var definition = new JobDefinitionDetails(new(11, "reviewed", "/", null, 42, "", now, now, null)
            { Target = target }, [], []);
        var snapshot = new McpOperatorJobExecutionSnapshot(definition, 1, "operator", "credential", route.McpResource,
            route.McpInstance, new string('A', 64), [], []);
        var plan = new JobResolvedTargetPlan(1, 42, 11, snapshot.ComputeDefinitionDigest(), target, null, [],
            [new(agent, "lab-node", null, [])], new());
        snapshot = snapshot with { TargetPlan = plan, ReviewedAuthority = new(route, null, "credential",
            [new(agent, Guid.NewGuid(), 1, new string('A', 64), new string('B', 64))]) };
        var boundary = new McpJobLocalDelegationTestBoundary("operator", "credential", route.McpResource)
        { HasPermission = false, CurrentFailureCode = revokeCredential ? "delegated_identity_revoked" : null };
        // Withdrawal is checked before any directory lookup or native work.
        var authority = new McpAggregateExecutionAuthority(null!, null!, null!, boundary, null!, boundary, null!);
        var denial = await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
            authority.RequireAllowedAsync(snapshot, agent, CancellationToken.None));
        denial.Message.Should().Contain(code);
    }
}
