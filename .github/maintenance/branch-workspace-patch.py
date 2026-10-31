from pathlib import Path

root = Path('.')
def change(path, old, new, count=1):
    p = root / path
    text = p.read_text()
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f'{path}: expected {count} replacements, found {actual}: {old[:100]}')
    p.write_text(text.replace(old, new))
def add(path, text):
    p = root / path
    if p.exists(): raise RuntimeError(f'new file already exists: {path}')
    p.write_text(text)

app = 'src/NetRatel/NetRatel.Application/Operations/'
api = 'src/NetRatel/NetRatel.API/Endpoints/Client/'
infra = 'src/NetRatel/NetRatel.Infrastructure/Services/'
add(app + 'McpOperatorAccountAuthority.cs', '''using System.Security.Cryptography;
using System.Text;

namespace NetRatel.Application.Operations;

/// <summary>
/// A server-verified instance account, not a delegated MCP policy. Only the
/// interactive policy adapter may construct this after current effective-access
/// evaluation. Confirmation binds the account in its distinct resource; it does
/// not create a policy or confer authority on an integration credential.
/// </summary>
public sealed record McpOperatorAccountAuthority(string PrincipalId)
{
    public string Resource => "netratel-web-instance-policy-administration:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PrincipalId)));

    /// <summary>Validates the confirmation binding, not the account's permission.</summary>
    public bool Matches(McpOperatorAccessRequest request) =>
        PrincipalId is { Length: > 0 and <= 256 } && !PrincipalId.Any(char.IsControl) &&
        request.McpResource == Resource && request.Principal.ServicePrincipal == "netratel-web" &&
        request.Tool == "netratel_policy" &&
        request.OperationFamily == McpOperatorOperationFamily.PolicyAdministration &&
        request.ConfirmationClass == McpOperatorConfirmationClass.PolicyAdministration &&
        request.Operation is "netratel_policy/create" or "netratel_policy/replace" or
            "netratel_policy/disable" or "netratel_policy/revoke" or "netratel_policy/delete";
}
''')
change(app + 'McpOperatorContracts.cs', '''{
    public static McpOperatorDecision Denied(''', '''{
    /// <summary>Trusted account-adapter authority; never accepted from request JSON.</summary>
    [JsonIgnore]
    public McpOperatorAccountAuthority? AccountAuthority { get; init; }

    public static McpOperatorDecision Denied(''')
add(api + 'InstancePolicyAccountAdmission.cs', '''using System.Security.Claims;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Request-local proof of instance-account authority, installed only on the
/// interactive policy route. Raw roles, integration credentials and signed MCP
/// delegations cannot produce it. Effective access is re-read before persistence.
/// </summary>
internal sealed class InstancePolicyAccountAdmission(
    HttpContext http, IEffectiveAccessService effectiveAccess, string principalId)
{
    private static readonly object AdmissionKey = new();
    public string PrincipalId { get; } = principalId;

    public static async ValueTask<object?> FilterAsync(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        if (TryInteractivePrincipal(http.User, out var principalId) &&
            http.RequestServices.GetService<IEffectiveAccessService>() is { } access)
        {
            var admission = new InstancePolicyAccountAdmission(http, access, principalId!);
            if (await admission.IsCurrentAsync(http.RequestAborted).ConfigureAwait(false))
                http.Items[AdmissionKey] = admission;
        }
        return await next(invocation).ConfigureAwait(false);
    }

    public static InstancePolicyAccountAdmission? FromContext(HttpContext http) =>
        http.Items.TryGetValue(AdmissionKey, out var value) ? value as InstancePolicyAccountAdmission : null;

    public async Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
    {
        if (!TryInteractivePrincipal(http.User, out var currentId) || currentId != PrincipalId)
            return false;
        var snapshot = await effectiveAccess.GetSnapshotAsync(http.User, null, cancellationToken).ConfigureAwait(false);
        return snapshot.IsInstanceAdministrator &&
            (snapshot.PrincipalId == PrincipalId || snapshot.IsLegacyOperator);
    }

    internal static bool TryInteractivePrincipal(ClaimsPrincipal user, out string? principalId)
    {
        principalId = user.FindFirst("netratel_principal_id")?.Value;
        return user.Identity?.IsAuthenticated == true &&
            principalId is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(principalId) && !principalId.Any(char.IsControl) &&
            !user.HasClaim(claim => claim.Type == "netratel_integration_credential_id") &&
            !user.HasClaim("token_use", "netratel_service") && !user.HasClaim("auth_mode", "service") &&
            !user.HasClaim("auth_mode", "integration_credential") &&
            user.Identity.AuthenticationType != "NetclawSavedAuthority";
    }
}
''')
p = api + 'McpOperatorPolicyMutationEndpoints.cs'
change(p, '.WithMetadata(new AccountPolicyMutation());', '.WithMetadata(new AccountPolicyMutation())\n            .AddEndpointFilter(InstancePolicyAccountAdmission.FilterAsync);')
change(p, 'if (WouldEscalateCaller(request.Policy, context.Principal))', 'if (context.InstanceAccount is null && WouldEscalateCaller(request.Policy, context.Principal))', 2)
change(p, 'context!.Principal, out failure))', 'context!.Principal, out failure, context.InstanceAccount is not null))', 4)
change(p, 'context.Principal, operation, out failure))', 'context.Principal, operation, out failure, context.InstanceAccount is not null))', 2)
change(p, '''        McpOperatorPrincipal principal,
        out string? failure)
    {''', '''        McpOperatorPrincipal principal,
        out string? failure,
        bool instanceAdministrator = false)
    {''', 3)
change(p, 'if (SelectsCaller(existing.PrincipalSelector, principal) || WouldEscalateCaller(proposed, principal))', 'if (!instanceAdministrator && (SelectsCaller(existing.PrincipalSelector, principal) || WouldEscalateCaller(proposed, principal)))')
change(p, 'if (SelectsCaller(existing.PrincipalSelector, principal))', 'if (!instanceAdministrator && SelectsCaller(existing.PrincipalSelector, principal))', 2)
change(p, 'string operation, out string? failure)\n    {\n        if (operation != DeleteOperation) return CanRevoke(existing, version, principal, out failure);', 'string operation, out string? failure, bool instanceAdministrator = false)\n    {\n        if (operation != DeleteOperation) return CanRevoke(existing, version, principal, out failure, instanceAdministrator);')
change(p, '            SelectsCaller(existing.PrincipalSelector, principal) ? "operator_policy_self_escalation" : null;', '            !instanceAdministrator && SelectsCaller(existing.PrincipalSelector, principal) ? "operator_policy_self_escalation" : null;')
change(p, '''        var tenantVisible = await authorization.HasTenantVisibilityAsync(
            context.Environment, context.Principal, target.TenantId, cancellationToken).ConfigureAwait(false);''', '''        var accountAuthority = context.InstanceAccount;
        var currentInstanceAccount = accountAuthority is not null &&
            await accountAuthority.IsCurrentAsync(cancellationToken).ConfigureAwait(false);
        var tenantVisible = currentInstanceAccount || await authorization.HasTenantVisibilityAsync(
            context.Environment, context.Principal, target.TenantId, cancellationToken).ConfigureAwait(false);''')
change(p, '''        // Creating an initial profile is the reviewed bootstrap operation.''', '''        if (accountAuthority is not null && !currentInstanceAccount)
            return McpOperatorDecision.Denied(access, "instance_account_authority_changed", McpOperatorAuthorizationLayer.Authentication);
        // Creating an initial profile is the reviewed bootstrap operation.''')
change(p, '''        return await authorization.EvaluateAsync(access, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteMutationAsync''', '''        if (currentInstanceAccount)
            return new McpOperatorDecision(true, null, null, [], null, access.TargetSetDigest, access)
            {
                AccountAuthority = new(accountAuthority!.PrincipalId)
            };
        return await authorization.EvaluateAsync(access, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteMutationAsync''')
change(p, '''            await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, mutationToken).ConfigureAwait(false);
            return await mutation(mutationToken).ConfigureAwait(false);''', '''            if (context.InstanceAccount is { } account)
            {
                if (decision.AccountAuthority?.PrincipalId != account.PrincipalId ||
                    !await account.IsCurrentAsync(mutationToken).ConfigureAwait(false))
                    throw new McpOperatorAdmissionRejectedException("instance_account_authority_changed");
                // This is application administration, not admission under a delegated
                // MCP policy. The durable plan/receipt and immutable policy-change
                // audit retain the exact actor, target, payload and resulting version.
                // Do not fabricate an MCP parent or a policy-backed accepted audit.
            }
            else
                await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, mutationToken).ConfigureAwait(false);
            return await mutation(mutationToken).ConfigureAwait(false);''')
change(p, '''        if (http.User.Identity?.IsAuthenticated != true ||
            !http.User.HasClaim(claim => claim.Type == "netratel_principal_id" && !string.IsNullOrWhiteSpace(claim.Value)) ||
            http.User.HasClaim(claim => claim.Type == "netratel_integration_credential_id") ||
            http.User.HasClaim("token_use", "netratel_service") || http.User.HasClaim("auth_mode", "service") ||''', '''        if (!InstancePolicyAccountAdmission.TryInteractivePrincipal(http.User, out _) ||''')
change(p, '''        // Translate the already-authorized application permission into the shared
        // policy engine's role/scope vocabulary. Parent policies still bound all targets
        // and operations, and their current versions are re-evaluated at confirmation.''', '''        // Non-instance accounts keep the existing bounded parent-policy path.
        // A current instance account uses a distinct confirmation authority and never
        // turns these translated role/scope names into a grant for an MCP credential.
        var instanceAccount = InstancePolicyAccountAdmission.FromContext(http);
        var resource = instanceAccount is null ? "netratel-web-policy-administration"
            : new McpOperatorAccountAuthority(instanceAccount.PrincipalId).Resource;''')
change(p, '            "netratel-web", "netratel-web-policy-administration",', '            "netratel-web", resource,')
change(p, 'null, null) { SignedInAccount = true };', 'null, null) { SignedInAccount = true, InstanceAccount = instanceAccount };')
change(p, '    public bool SignedInAccount { get; init; }', '    public bool SignedInAccount { get; init; }\n    public InstancePolicyAccountAdmission? InstanceAccount { get; init; }')
change(p, '        "oauth_scope_missing" => "oauth_scope",', '        "instance_account_authority_changed" => "authentication",\n        "oauth_scope_missing" => "oauth_scope",')
change(p, '        "oauth_scope_missing" => "The signed delegation does not include the required administrator scope.",', '        "instance_account_authority_changed" => "Current instance-administrator account authority is no longer available.",\n        "oauth_scope_missing" => "The signed delegation does not include the required administrator scope.",')
change(p, '        "oauth_scope_missing" => "Request the netratel.mcp.admin OAuth scope, then obtain a fresh delegation assertion.",', '        "instance_account_authority_changed" => "Refresh account access and create a new preview before confirming the policy change.",\n        "oauth_scope_missing" => "Request the netratel.mcp.admin OAuth scope, then obtain a fresh delegation assertion.",')
p = infra + 'McpOperatorConfirmationService.cs'
change(p, 'request.Decision.MatchingPolicyIds.Single()', 'PolicyId(request.Decision)')
change(p, 'request.Decision.SelectedPolicyVersion!.Value', 'PolicyVersion(request.Decision)')
change(p, 'request.CurrentDecision.MatchingPolicyIds.Single()', 'PolicyId(request.CurrentDecision)')
change(p, 'request.CurrentDecision.SelectedPolicyVersion!.Value', 'PolicyVersion(request.CurrentDecision)')
change(p, 'plan.PolicyId == decision.MatchingPolicyIds.Single()', 'plan.PolicyId == PolicyId(decision)')
change(p, 'plan.PolicyVersion == decision.SelectedPolicyVersion', 'plan.PolicyVersion == PolicyVersion(decision)')
change(p, '''        if (!decision.IsAllowed || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null)
            return false;
        var access = decision.Request;''', '''        if (!decision.IsAllowed) return false;
        var access = decision.Request;
        if (decision.AccountAuthority is { } account)
        {
            if (decision.MatchingPolicyIds.Count != 0 || decision.SelectedPolicyVersion is not null || !account.Matches(access))
                return false;
        }
        else if (decision.MatchingPolicyIds.Count != 1 || decision.MatchingPolicyIds[0] == Guid.Empty || decision.SelectedPolicyVersion is null or <= 0)
            return false;''')
change(p, '    private static string ClientId(McpOperatorAccessRequest access)', '''    // The existing confirmation tables have no policy foreign key. Empty/zero
    // explicitly means account authority, never a synthetic operator-policy row.
    // The account-specific resource binds its principal and is matched at confirm.
    private static Guid PolicyId(McpOperatorDecision decision) =>
        decision.AccountAuthority is not null ? Guid.Empty : decision.MatchingPolicyIds.Single();

    private static long PolicyVersion(McpOperatorDecision decision) =>
        decision.AccountAuthority is not null ? 0 : decision.SelectedPolicyVersion!.Value;

    private static string ClientId(McpOperatorAccessRequest access)''')
p = 'src/NetRatel/NetRatel.Web/Components/Pages/Settings/OperatorPolicies.razor.cs'
change(p, '''        "target_policy_missing" or "tenant_not_authorized" or "target_operation_not_authorized" => "The signed-in administrator needs an existing bounded parent policy for this target and lifecycle operation.",''', '''        "target_policy_missing" => "This account has scoped policy-administration access but no matching parent policy for this target. Use an instance-administrator account or obtain a bounded grant.",
        "tenant_not_authorized" => "This account is not authorized to administer policies for the selected tenant.",
        "target_operation_not_authorized" => "The current delegated policy does not permit this lifecycle operation on the selected target.",
        "instance_account_authority_changed" => "Your instance-administrator authority changed. Refresh account access and preview this change again.",''')
p = 'src/NetRatel/NetRatel.Web/Components/Pages/AIOps/Tools.razor.css'
change(p, '.ai-toolbar select {', '.ai-ops .ai-toolbar select {')
change(p, '.ai-toolbar > button {', '.ai-ops .ai-toolbar > button {')
p = 'src/NetRatel/NetRatel.Web/Components/Pages/Monitoring/MonitoringPage.razor'
change(p, 'Monitored clients · @row.OfflineAgentIds.Length offline', 'Monitored clients · @(row.OfflineAgentIds.IsDefaultOrEmpty ? 0 : row.OfflineAgentIds.Length) offline')
change(p, 'Icons.Material.Filled.InfoOutline', 'Icons.Material.Outlined.Info')

p='src/NetRatel/NetRatel.Tests/API/McpOperatorPolicyMutationEndpointTests.cs'
change(p, 'public sealed class McpOperatorPolicyMutationEndpointTests', 'public sealed partial class McpOperatorPolicyMutationEndpointTests')
change(p, 'using NetRatel.API.Middleware;', 'using NetRatel.API.Middleware;\nusing NetRatel.API.Security.Authorization;\nusing NetRatel.Infrastructure.Identity.Authorization;')
change(p, '        bool accountRoutes = false)\n    {', '        bool accountRoutes = false,\n        IEffectiveAccessService? effectiveAccess = null,\n        string environment = Environments.Development)\n    {')
change(p, '            web.UseEnvironment(Environments.Development);', '            web.UseEnvironment(environment);')
change(p, '''                services.AddAuthorization(options => options.AddPolicy("McpOperatorPolicyAdmin", policy =>
                    policy.RequireAuthenticatedUser().RequireClaim("test-policy-permission", "mcp.policy.admin")));''', '''                services.AddAuthorization(options => options.AddPolicy("McpOperatorPolicyAdmin", policy =>
                {
                    policy.RequireAuthenticatedUser();
                    if (effectiveAccess is null) policy.RequireClaim("test-policy-permission", "mcp.policy.admin");
                    else policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.McpPolicyAdministration));
                }));
                if (effectiveAccess is not null)
                {
                    services.AddSingleton<IEffectiveAccessService>(effectiveAccess);
                    services.AddSingleton<IAuthorizationHandler, EffectiveAccessHandler>();
                }''')
change(p, 'mode is not ("Account" or "AccountReadOnly" or "AccountCredential")', 'mode is not ("Account" or "AccountReadOnly" or "AccountCredential" or "AccountService")')
change(p, '            if (mode == "AccountCredential") claims.Add(new("netratel_integration_credential_id", "synthetic-credential"));', '''            if (mode == "AccountCredential") claims.Add(new("netratel_integration_credential_id", "synthetic-credential"));
            if (mode == "AccountService") claims.Add(new("token_use", "netratel_service"));''')
add('src/NetRatel/NetRatel.Tests/API/McpOperatorPolicyMutationEndpointTests.InstanceAccount.cs', '''using System.Net;
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
''')
p='src/NetRatel/NetRatel.Web.ComponentTests/MonitoringPageTests.cs'
change(p, '''        Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == $"/flows?tenantId=1&run={runId:D}");
    }''', '''        Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == $"/flows?tenantId=1&run={runId:D}");
        var detailRow = cut.Find("[data-testid=monitoring-detail-row]");
        Assert.Equal("6", detailRow.QuerySelector("td")!.GetAttribute("colspan"));
        Assert.Equal(4, detailRow.QuerySelectorAll(".monitoring-detail-card").Length);
        Assert.NotNull(detailRow.QuerySelector("details > summary"));
        Assert.Contains("monitoring-detail-button", cut.Find("[data-testid=monitoring-rateldesk-ticket]").ClassName);
        Assert.Empty(cut.Find("[data-testid=monitoring-series-row]").QuerySelectorAll(".monitoring-detail-card"));
    }''')
text=Path(p).read_text(); start=text.index('    [Fact]\n    public void SuccessfulIncidentReceiptIsShownWithTenantQualifiedRunLink()'); end=text.index('    [Fact]',start+10)
case=text[start:end]
case=case.replace('    [Fact]\n    public void SuccessfulIncidentReceiptIsShownWithTenantQualifiedRunLink()', '''    [Theory]
    [InlineData(null, true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("http://desk.example/incidents/incident-123", false)]
    public void Incident_card_never_invents_or_renders_an_unsafe_ticket_link(string? incidentUrl, bool receiptVisible)''')
case=case.replace('"https://desk.example/incidents/incident-123"','incidentUrl')
pos=case.index('        cut.WaitForAssertion(() => Assert.Contains("NR-123"')
case=case[:pos]+'''        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=monitoring-detail-row]")));
        Assert.Empty(cut.FindAll("[data-testid=monitoring-rateldesk-ticket]"));
        Assert.Equal(receiptVisible ? 1 : 0, cut.FindAll("[data-testid=monitoring-incident-receipt]").Count);
        if (receiptVisible) Assert.Contains("no ticket link was provided", cut.Markup);
        else Assert.Contains("No validated incident receipt", cut.Markup);
        Assert.DoesNotContain("javascript:", cut.Markup);
    }
'''
Path(p).write_text(text[:end]+case+text[end:])
p='src/NetRatel/NetRatel.Web.ComponentTests/NetclawWorkspaceTests.cs'
change(p, '''        Assert.True(cut.Find("[data-testid=ai-new-investigation]").HasAttribute("disabled"));''', '''        Assert.True(cut.Find("[data-testid=ai-new-investigation]").HasAttribute("disabled"));
        Assert.Equal("Investigations", cut.Find(".ai-toolbar h1").TextContent);
        Assert.NotNull(cut.Find(".ai-toolbar select"));
        Assert.NotNull(cut.Find(".ai-toolbar [data-testid=ai-new-investigation]"));
        Assert.Equal(3, cut.FindAll(".ai-metrics > div").Count);
        Assert.NotNull(cut.Find(".ai-workspace .ai-conversation"));''')
p='docs/mcp-http/authentication.md'
change(p, '''The signed-in administrator also needs an active, independently assigned parent operator policy that permits `PolicyAdministration` for the selected environment, tenant and target. A `ControlPlane` parent cannot authorize a `DevelopmentEnvironment` policy; environment-wide lifecycle changes require an environment-wide parent on every phase. The page displays the authenticated subject needed for that selector. This account route does not bypass a missing parent policy or allow an administrator to change a policy selecting their own subject, group, role or client. Initial authority setup remains an independent administrator's existing policy-administration task. A stale policy version, changed authorizing policy or changed target profile requires a fresh review.''', '''An interactive **instance administrator** manages policies using current application account authority, without first granting themself an MCP parent policy. This includes create, edit/update, disable, revoke and delete, including policies selecting the administrator. Every preview and confirmation rechecks effective instance authority; persistence checks it again. Exact environment, target, payload, policy version, confirmation expiry and idempotency still apply. The durable confirmation/receipt and immutable policy-change audit retain the reviewed action and actor. No synthetic parent policy is created.

Accounts with only scoped `mcp.policy.admin` permission still need an active, independently assigned parent operator policy permitting `PolicyAdministration` for the selected environment, tenant and target. A `ControlPlane` parent cannot authorize a `DevelopmentEnvironment` policy, and scoped administrators cannot alter policies selecting themselves. The page displays the authenticated subject needed for that selector. Integration credentials, service tokens and signed MCP delegations never inherit their owner's instance authority; their existing bounded admission remains unchanged. A stale policy version, changed authority or changed target profile requires a fresh review.''')
p='docs/operations/rbac.md'
change(p, '''MCP target/environment policy, confirmations, and command admission remain
additional constraints and cannot be bypassed by a role.''', '''MCP target/environment policy, confirmations, and command admission remain
additional constraints for delegated operations and cannot be bypassed by a role.
Interactive instance administrators manage the policies themselves through current
application authority, not a self-granted MCP parent policy. Their policy changes
still require exact preview/confirmation, current authority, version checks and
immutable change audits. Scoped policy administrators retain bounded-parent
requirements. Credentials and service tokens cannot use this account-only path.''')
p='docs/operations/MONITORING.md'
text=Path(p).read_text(); text+='''
## Expanded alert evidence

Open **Alert details** below an alert row for a full-width view of its target and
rule, observed/received evidence, automation outcome, and incident/acknowledgement.
**Flow run and receipt** and **Published flow** open the tenant-qualified records.
**Open RatelDesk ticket** appears only for a validated incident receipt containing
a safe ticket URL. A missing link is shown as missing rather than reconstructed
from an internal identifier. Flow success remains distinct from confirmed
incident evidence; unconfirmed and absent results are labelled explicitly.
'''; Path(p).write_text(text)
