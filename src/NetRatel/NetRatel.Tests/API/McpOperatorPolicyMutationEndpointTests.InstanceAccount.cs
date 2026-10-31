using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Endpoints.Client;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorPolicyMutationEndpointTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("delete")]
    public async Task Instance_account_manages_own_selected_policy_without_parent_with_durable_replay_and_change_audit(string action)
    {
        await using var f = await InstanceAccountFixture.CreateAsync();
        var stored = await f.Administration.CreateAsync(f.Draft, "independent-admin@example.test", CancellationToken.None);
        using var app = await BuildAppAsync(new McpOperatorAuthorization(f.Db), f.Administration,
            new McpOperatorConfirmationService(f.Db), true, f.Access, Environments.Production);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        var proposed = f.Draft with { Name = "Reviewed instance-admin change" };
        object previewRequest = action == "create" ? new PreviewMcpOperatorPolicyCreateRequest(proposed)
            : PolicyPreviewRequest(action, stored, proposed);
        var path = $"/api/v2/mcp-operator/policies/manage/{action}";
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", previewRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        (await f.Db.McpOperatorPolicyChangeAudits.CountAsync()).Should().Be(1, "preview must not change policy state");
        var plan = await f.Db.McpOperatorConfirmationPlans.SingleAsync();
        plan.PolicyId.Should().Be(Guid.Empty, "account authority is not a fabricated parent policy");
        plan.PolicyVersion.Should().Be(0);
        plan.McpResource.Should().Be(new McpOperatorAccountAuthority(f.User.PrincipalId).Resource);
        object confirmation = action == "create" ? new ConfirmMcpOperatorPolicyCreateRequest(proposed, preview.PlanToken, preview.IdempotencyKey)
            : PolicyConfirmRequest(action, stored, proposed, preview);
        var response = await client.PostAsJsonAsync($"{path}/confirm", confirmation);
        response.StatusCode.Should().Be(action == "create" ? HttpStatusCode.Created : HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!;
        var replay = await client.PostAsJsonAsync($"{path}/confirm", confirmation);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>())!.Replay.Should().BeTrue();
        var current = (await f.Administration.GetAsync(result.Policy.PolicyId, CancellationToken.None))!;
        current.LifecycleState.Should().Be(action switch
        {
            "disable" => McpOperatorPolicyLifecycleState.Disabled,
            "revoke" => McpOperatorPolicyLifecycleState.Revoked,
            "delete" => McpOperatorPolicyLifecycleState.Deleted,
            _ => McpOperatorPolicyLifecycleState.Active
        });
        if (action is "create" or "replace") current.Name.Should().Be(proposed.Name);
        (await f.Db.McpOperatorPolicyChangeAudits.CountAsync()).Should().Be(2);
        (await f.Db.McpOperatorPolicyChangeAudits.SingleAsync(a => a.ActorId == "policy.admin@example.test")).PolicyId.Should().Be(result.Policy.PolicyId);
        (await f.Db.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        (await f.Db.McpOperatorPolicies.CountAsync()).Should().Be(action == "create" ? 2 : 1);
        (await f.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0, "MCP accepted audits require a real authorizing policy; account changes use their immutable change audit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instance_account_authority_is_rechecked_at_confirmation_and_immediately_before_persistence(bool removeAfterAdmission)
    {
        await using var f = await InstanceAccountFixture.CreateAsync();
        var stored = await f.Administration.CreateAsync(f.Draft, "independent-admin@example.test", CancellationToken.None);
        using var app = await BuildAppAsync(new McpOperatorAuthorization(f.Db), f.Administration,
            new McpOperatorConfirmationService(f.Db), true, f.Access, Environments.Production);
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        const string path = "/api/v2/mcp-operator/policies/manage/replace";
        var proposed = f.Draft with { Name = "Must not persist" };
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", PolicyPreviewRequest("replace", stored, proposed));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        if (removeAfterAdmission)
            f.Db.SavingChanges += (_, _) =>
            {
                if (f.Db.ChangeTracker.Entries<McpOperatorIdempotencyRecord>().Any(e => e.State == EntityState.Added))
                {
                    f.User.IsInstanceAdministrator = false;
                    f.Identity.SaveChanges();
                }
            };
        else
        {
            f.User.IsInstanceAdministrator = false;
            await f.Identity.SaveChangesAsync();
        }
        var response = await client.PostAsJsonAsync($"{path}/confirm", PolicyConfirmRequest("replace", stored, proposed, preview));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.Administration.GetAsync(stored.PolicyId, CancellationToken.None)).Should().BeEquivalentTo(stored);
        (await f.Db.McpOperatorPolicyChangeAudits.CountAsync()).Should().Be(1);
        if (removeAfterAdmission)
        {
            (await response.Content.ReadAsStringAsync()).Should().Contain("instance_account_authority_changed");
            (await f.Db.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Failed);
        }
        else (await f.Db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("version")]
    [InlineData("target")]
    public async Task Instance_account_confirmation_preserves_exact_payload_version_and_target_binding(string change)
    {
        await using var f = await InstanceAccountFixture.CreateAsync();
        var stored = await f.Administration.CreateAsync(f.Draft, "independent-admin@example.test", CancellationToken.None);
        using var app = await BuildAppAsync(new McpOperatorAuthorization(f.Db), f.Administration,
            new McpOperatorConfirmationService(f.Db), true, f.Access, Environments.Production);
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        const string path = "/api/v2/mcp-operator/policies/manage/replace";
        var previewResponse = await client.PostAsJsonAsync($"{path}/preview", PolicyPreviewRequest("replace", stored, f.Draft));
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>())!;
        var proposed = change == "payload" ? f.Draft with { Name = "Not reviewed" }
            : change == "target" ? f.Draft with { TargetSelector = new(McpOperatorTargetSelectorKind.Tenant, 43) } : f.Draft;
        if (change == "version") await f.Administration.ReplaceAsync(stored.PolicyId, stored.Version,
            f.Draft with { Name = "Independent update" }, "independent-admin@example.test", CancellationToken.None);
        var before = await f.Administration.GetAsync(stored.PolicyId, CancellationToken.None);
        var response = await client.PostAsJsonAsync($"{path}/confirm", PolicyConfirmRequest("replace", stored, proposed, preview));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await f.Administration.GetAsync(stored.PolicyId, CancellationToken.None)).Should().BeEquivalentTo(before);
        (await f.Db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("AccountCredential", HttpStatusCode.Unauthorized)]
    [InlineData("AccountService", HttpStatusCode.Forbidden)]
    public async Task Administrator_owned_credentials_and_service_tokens_cannot_enter_account_administration(string mode, HttpStatusCode expected)
    {
        await using var f = await InstanceAccountFixture.CreateAsync();
        using var app = await BuildAppAsync(new McpOperatorAuthorization(f.Db), f.Administration,
            new McpOperatorConfirmationService(f.Db), true, f.Access, Environments.Production);
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(mode);
        var response = await client.PostAsJsonAsync("/api/v2/mcp-operator/policies/manage/create/preview", new PreviewMcpOperatorPolicyCreateRequest(f.Draft));
        response.StatusCode.Should().Be(expected);
        (await f.Db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
        (await f.Db.McpOperatorPolicies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task All_permissions_without_instance_authority_still_need_a_bounded_parent()
    {
        await using var f = await InstanceAccountFixture.CreateAsync();
        f.User.IsInstanceAdministrator = false; await f.Identity.SaveChangesAsync();
        using var app = await BuildAppAsync(new McpOperatorAuthorization(f.Db), f.Administration,
            new McpOperatorConfirmationService(f.Db), true, f.Access, Environments.Production);
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Account");
        var response = await client.PostAsJsonAsync("/api/v2/mcp-operator/policies/manage/create/preview", new PreviewMcpOperatorPolicyCreateRequest(f.Draft));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.Db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Signed_MCP_delegation_cannot_borrow_its_owners_instance_authority()
    {
        await using var f = await InstanceAccountFixture.CreateAsync();
        using var app = await BuildAppAsync(new McpOperatorAuthorization(f.Db), f.Administration,
            new McpOperatorConfirmationService(f.Db), true, f.Access);
        var client = M2mClient(app);
        var draft = f.Draft with { Environment = McpOperatorEnvironment.Development };
        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator", "InstanceAdministrator"], ["netratel.mcp.admin"]);
        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await f.Db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
    }

    private sealed class InstanceAccountFixture : IAsyncDisposable
    {
        public NetRatelIdentityDbContext Identity { get; } = new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public OrchestratorDbContext Db { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public LocalUser User { get; } = new() { Id = "instance-admin", PrincipalId = "policy-admin-principal", IsEnabled = true, IsInstanceAdministrator = true };
        public McpOperatorPolicyAdministration Administration => new(Db);
        public IEffectiveAccessService Access => new EffectiveAccessService(Identity, new ConfigurationBuilder().Build());
        public McpOperatorPolicyDraft Draft { get; } = McpOperatorPolicyMutationEndpointTests.Draft() with
        {
            Environment = McpOperatorEnvironment.Production,
            PrincipalSelector = new(McpOperatorPrincipalSelectorKind.OAuthSubject, "policy.admin@example.test")
        };
        public static async Task<InstanceAccountFixture> CreateAsync()
        {
            var f = new InstanceAccountFixture(); f.Identity.Users.Add(f.User);
            var role = new AccessRole { Name = "All explicit permissions", DelegationRank = 1 };
            foreach (var permission in NetRatelPermissions.All) role.Permissions.Add(new AccessRolePermission { Permission = permission });
            f.Identity.AccessRoles.Add(role);
            f.Identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = f.User.PrincipalId, RoleId = role.Id, TenantId = null });
            f.Identity.IntegrationCredentials.Add(new IntegrationCredential
            {
                Id = "synthetic-credential", PublicId = "synthetic-credential", Name = "Bounded API policy permission",
                TokenPrefix = "nrt_ic_test", SecretHash = "hash", OwnerPrincipalId = f.User.PrincipalId,
                Purpose = IntegrationCredentialPurpose.Api, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                InstanceGrants = [new IntegrationCredentialInstanceGrant { CredentialId = "synthetic-credential", Permission = NetRatelPermissions.McpPolicyAdministration }]
            });
            await f.Identity.SaveChangesAsync(); return f;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Identity.DisposeAsync(); }
    }
}
