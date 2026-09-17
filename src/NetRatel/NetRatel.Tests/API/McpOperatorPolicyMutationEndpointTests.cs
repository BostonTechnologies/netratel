using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorPolicyMutationEndpointTests
{
    [Fact]
    public async Task Preview_RequiresExactDelegatedAdministratorPolicyAndDoesNotCreateAPolicy()
    {
        var authorization = new RecordingAuthorization(allowed: false);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var request = new PreviewMcpOperatorPolicyCreateRequest(Draft());

        var missing = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", request);

        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_required");

        SetDelegation(client, app, "preview_create", Draft().TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var denied = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", request);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing");
        var evaluated = authorization.Evaluated.Should().ContainSingle().Subject;
        evaluated.OperationFamily.Should().Be(McpOperatorOperationFamily.PolicyAdministration);
        evaluated.Operation.Should().Be("netratel_policy/create");
        evaluated.RequiredScopes.Should().BeEquivalentTo(["netratel.mcp.admin"]);
        evaluated.ConfirmationClass.Should().Be(McpOperatorConfirmationClass.PolicyAdministration);
        evaluated.TenantId.Should().Be(42);
        evaluated.AgentId.Should().BeNull();
        administration.Validations.Should().Be(0);
        administration.Creates.Should().Be(0);
        confirmations.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewThenConfirm_BindsTheExactDraftAndRecordsAcceptedAndChangeAuditBoundaries()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft();

        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/create/preview",
            new PreviewMcpOperatorPolicyCreateRequest(draft));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyCreatePreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Should().NotBeNull();
        preview!.PlanToken.Should().Be(RecordingConfirmations.PlanToken);
        preview.IdempotencyKey.Should().Be(RecordingConfirmations.IdempotencyKey);
        administration.Validations.Should().Be(1);
        administration.Creates.Should().Be(0);
        confirmations.Plans.Should().ContainSingle().Which.PayloadHash.Should().HaveLength(64);

        SetDelegation(client, app, "confirm_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmed = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/create/confirm",
            new ConfirmMcpOperatorPolicyCreateRequest(draft, preview.PlanToken, preview.IdempotencyKey));
        var result = await confirmed.Content.ReadFromJsonAsync<McpOperatorPolicyCreateResult>();

        confirmed.StatusCode.Should().Be(HttpStatusCode.Created);
        result.Should().NotBeNull();
        result!.Replay.Should().BeFalse();
        result.Policy.PolicyId.Should().Be(administration.CreatedPolicy.PolicyId);
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/create");
        administration.Creates.Should().Be(1);
        confirmations.Confirmed.Should().ContainSingle().Which.PayloadHash.Should().Be(confirmations.Plans[0].PayloadHash);
        confirmations.Completed.Should().ContainSingle().Which.Should().Be((RecordingConfirmations.AdmissionId, McpOperatorIdempotencyOutcome.Succeeded, administration.CreatedPolicy.PolicyId.ToString("D")));
    }

    [Fact]
    public async Task TargetProfile_PreviewsAndConfirmsAnExactVersionedServerOwnedClassification()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var agentId = Guid.Parse("ef6a3f18-0d0d-45e0-8211-18067a95d9b9");
        var target = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ExactAgent, 42, agentId);
        var previewRequest = new PreviewMcpOperatorTargetProfileUpsertRequest(
            42,
            agentId,
            McpOperatorTargetClassification.ManagedStandard,
            [],
            null);

        SetDelegation(client, app, "preview_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/target-profile/preview", previewRequest);
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Should().NotBeNull();
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/upsert_target_profile");
        administration.TargetProfileUpserts.Should().Be(0);

        SetDelegation(client, app, "confirm_target_profile", target, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmed = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/target-profile/confirm",
            new ConfirmMcpOperatorTargetProfileUpsertRequest(
                42,
                agentId,
                McpOperatorTargetClassification.ManagedStandard,
                [],
                null,
                preview!.PlanToken,
                preview.IdempotencyKey));
        var result = await confirmed.Content.ReadFromJsonAsync<McpOperatorTargetProfileMutationResult>();

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Replay.Should().BeFalse();
        result.Profile.Should().BeEquivalentTo(new McpOperatorTargetProfile(agentId, 42, McpOperatorTargetClassification.ManagedStandard, [], result.Profile.UpdatedAtUtc, "policy.admin@example.test", 1));
        administration.TargetProfileUpserts.Should().Be(1);
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/upsert_target_profile");
        confirmations.Completed.Should().ContainSingle().Which.Should().Be((RecordingConfirmations.AdmissionId, McpOperatorIdempotencyOutcome.Succeeded, agentId.ToString("D")));
    }

    [Fact]
    public async Task Preview_RejectsAnAllowPolicyThatWouldEscalateTheDelegatedAdministrator()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft() with
        {
            PrincipalSelector = new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "policy-admins")
        };
        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"], ["policy-admins"]);

        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operator_policy_self_escalation");
        administration.Validations.Should().Be(0);
        administration.Creates.Should().Be(0);
        confirmations.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_BindsAnExplicitControlPlanePolicyWithoutAnAgentTarget()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var draft = Draft() with
        {
            Name = "reviewed tenant control-plane administration",
            TargetSelector = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ControlPlane, 0),
            OperationFamily = McpOperatorOperationFamily.TenantAdministration,
            Operation = "netratel_tenants/create"
        };

        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var evaluated = authorization.Evaluated.Should().ContainSingle().Subject;
        evaluated.TenantId.Should().Be(0);
        evaluated.AgentId.Should().BeNull();
    }

    [Fact]
    public async Task Replace_PreviewsAndConfirmsTheExactVersionedPolicyWithoutChangingItsTarget()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        var replacement = Draft() with { Name = "reviewed tenant observation - revised" };

        SetDelegation(client, app, "preview_replace", replacement.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/replace/preview",
            new PreviewMcpOperatorPolicyReplaceRequest(administration.CreatedPolicy.PolicyId, 1, replacement));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        preview.Should().NotBeNull();
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/replace");
        administration.Validations.Should().Be(1);
        administration.Replaces.Should().Be(0);

        SetDelegation(client, app, "confirm_replace", replacement.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmation = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/replace/confirm",
            new ConfirmMcpOperatorPolicyReplaceRequest(administration.CreatedPolicy.PolicyId, 1, replacement, preview!.PlanToken, preview.IdempotencyKey));
        var result = await confirmation.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>();

        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Policy.Name.Should().Be(replacement.Name);
        result.Policy.Version.Should().Be(2);
        administration.Replaces.Should().Be(1);
        administration.LastReplacement.Should().Be((administration.CreatedPolicy.PolicyId, 1L, replacement));
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/replace");
        confirmations.Completed.Should().ContainSingle().Which.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
    }

    [Fact]
    public async Task Disable_PreviewsAndConfirmsTheExactVersionedPolicy()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);

        SetDelegation(client, app, "preview_disable", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/disable");
        administration.Disables.Should().Be(0);

        SetDelegation(client, app, "confirm_disable", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmation = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/confirm",
            new ConfirmMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector, preview!.PlanToken, preview.IdempotencyKey));
        var result = await confirmation.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>();

        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Policy.DisabledAtUtc.Should().NotBeNull();
        administration.Disables.Should().Be(1);
        administration.LastDisable.Should().Be((administration.CreatedPolicy.PolicyId, 1L));
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/disable");
    }

    [Fact]
    public async Task Revoke_PreviewsAndConfirmsTheExactVersionedPolicyAsATerminalLifecycleChange()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);

        SetDelegation(client, app, "preview_revoke", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var previewResponse = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/revoke/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector));
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorPolicyMutationPreview>();

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        authorization.Evaluated.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/revoke");
        administration.Revocations.Should().Be(0);

        SetDelegation(client, app, "confirm_revoke", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var confirmation = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/revoke/confirm",
            new ConfirmMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector, preview!.PlanToken, preview.IdempotencyKey));
        var result = await confirmation.Content.ReadFromJsonAsync<McpOperatorPolicyMutationResult>();

        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Policy.LifecycleState.Should().Be(McpOperatorPolicyLifecycleState.Revoked);
        administration.Revocations.Should().Be(1);
        administration.LastRevocation.Should().Be((administration.CreatedPolicy.PolicyId, 1L));
        authorization.Accepted.Should().ContainSingle().Which.Operation.Should().Be("netratel_policy/revoke");
    }

    [Fact]
    public async Task Disable_RejectsAnExistingPolicyThatSelectsTheDelegatedAdministrator()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        SetDelegation(
            client,
            app,
            "preview_disable",
            administration.CreatedPolicy.TargetSelector,
            ["PolicyAdministrator"],
            ["netratel.mcp.admin"],
            ["incident-responders"]);

        var response = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, administration.CreatedPolicy.TargetSelector));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("operator_policy_self_escalation");
        authorization.Evaluated.Should().BeEmpty();
        administration.Disables.Should().Be(0);
        confirmations.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_RejectsATargetSelectorThatDoesNotMatchThePersistedPolicy()
    {
        var authorization = new RecordingAuthorization(allowed: true);
        var administration = new RecordingAdministration();
        var confirmations = new RecordingConfirmations();
        using var app = await BuildAppAsync(authorization, administration, confirmations);
        var client = M2mClient(app);
        SetDelegation(client, app, "preview_disable", administration.CreatedPolicy.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var mismatchedTarget = administration.CreatedPolicy.TargetSelector with { TenantId = 43 };

        var response = await client.PostAsJsonAsync(
            "/api/v2/mcp/operator/policy/disable/preview",
            new PreviewMcpOperatorPolicyDisableRequest(administration.CreatedPolicy.PolicyId, 1, mismatchedTarget));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("policy_target_immutable");
        authorization.Evaluated.Should().BeEmpty();
        confirmations.Plans.Should().BeEmpty();
    }

    private static HttpClient M2mClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        return client;
    }

    private static void SetDelegation(
        HttpClient client,
        IHost app,
        string operation,
        McpOperatorTargetSelector target,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> scopes,
        IReadOnlyList<string>? groups = null)
    {
        client.DefaultRequestHeaders.Remove(McpOperatorDelegationOptions.HeaderName);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity(
                "policy.admin@example.test",
                "policy-admin-client",
                "policy-admin-client",
                groups ?? [],
                roles,
                scopes),
            new McpOperatorDelegationRequest(
                "netratel_policy",
                operation,
                $"request-{operation}",
                "https://mcp.dev.example/mcp",
                "dev",
                target.Kind == McpOperatorTargetSelectorKind.ControlPlane ? null : target.TenantId,
                target.Kind == McpOperatorTargetSelectorKind.ExactAgent ? target.AgentId : null,
                $"correlation-{operation}"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
    }

    private static async Task<IHost> BuildAppAsync(
        RecordingAuthorization authorization,
        RecordingAdministration administration,
        RecordingConfirmations confirmations)
    {
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "M2M";
                    options.DefaultChallengeScheme = "M2M";
                }).AddScheme<AuthenticationSchemeOptions, TestM2mAuthenticationHandler>("M2M", _ => { });
                services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy =>
                {
                    policy.AddAuthenticationSchemes("M2M");
                    policy.RequireAuthenticatedUser();
                }));
                services.AddSingleton(delegationOptions);
                services.AddSingleton<McpOperatorDelegationTokenService>();
                services.AddSingleton<IMcpOperatorAuthorization>(authorization);
                services.AddSingleton<IMcpOperatorPolicyAdministration>(administration);
                services.AddSingleton<IMcpOperatorPolicyRevocation>(administration);
                services.AddSingleton<IMcpOperatorConfirmationService>(confirmations);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMcpOperatorPolicyMutationEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private static McpOperatorPolicyDraft Draft() => new(
        "reviewed tenant observation",
        McpOperatorEnvironment.Development,
        McpOperatorPolicyEffect.Allow,
        10,
        new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
        new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
        null,
        McpOperatorOperationFamily.Observability,
        null,
        new McpOperatorConstraints(),
        DateTimeOffset.UtcNow.AddDays(7),
        DateTimeOffset.UtcNow.AddDays(1),
        "change-347");

    private sealed class RecordingAuthorization(bool allowed) : IMcpOperatorAuthorization
    {
        private static readonly Guid GovernancePolicyId = Guid.Parse("6d321d63-efbd-47e5-aaef-a4e5408215e5");
        public List<McpOperatorAccessRequest> Evaluated { get; } = [];
        public List<McpOperatorAcceptedAudit> Accepted { get; } = [];

        public Task<bool> HasTenantVisibilityAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, CancellationToken cancellationToken) => Task.FromResult(allowed);

        public Task<McpOperatorDecision> EvaluateAsync(McpOperatorAccessRequest request, CancellationToken cancellationToken)
        {
            Evaluated.Add(request);
            return Task.FromResult(allowed
                ? new McpOperatorDecision(true, null, null, [GovernancePolicyId], new McpOperatorConstraints(), request.TargetSetDigest, request, 4)
                : McpOperatorDecision.Denied(request, "target_policy_missing", McpOperatorAuthorizationLayer.Policy));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorDecision decision, string servicePrincipal, CancellationToken cancellationToken)
        {
            var access = decision.Request;
            var audit = new McpOperatorAcceptedAudit(
                Guid.NewGuid(),
                GovernancePolicyId,
                access.Environment,
                servicePrincipal,
                access.Principal.Subject,
                access.Principal.ClientId,
                access.Principal.AuthorizedParty,
                access.Principal.Groups.Order(StringComparer.Ordinal).ToArray(),
                access.Principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                access.Principal.Scopes.Order(StringComparer.Ordinal).ToArray(),
                access.McpResource,
                access.McpInstance,
                access.Tool,
                access.TenantId,
                access.AgentId,
                access.OperationFamily,
                access.Operation,
                access.CorrelationId,
                access.RequestId,
                DateTimeOffset.UtcNow);
            Accepted.Add(audit);
            return Task.FromResult(audit);
        }
    }

    private sealed class RecordingAdministration : IMcpOperatorPolicyAdministration, IMcpOperatorPolicyRevocation
    {
        public int Validations { get; private set; }
        public int Creates { get; private set; }
        public int Replaces { get; private set; }
        public int Disables { get; private set; }
        public int Revocations { get; private set; }
        public int TargetProfileUpserts { get; private set; }
        public (Guid PolicyId, long ExpectedVersion, McpOperatorPolicyDraft Policy)? LastReplacement { get; private set; }
        public (Guid PolicyId, long ExpectedVersion)? LastDisable { get; private set; }
        public (Guid PolicyId, long ExpectedVersion)? LastRevocation { get; private set; }
        public McpOperatorPolicy CreatedPolicy { get; } = new(
            Guid.Parse("68310e19-5f49-4e63-a73d-342e98940c26"),
            "reviewed tenant observation",
            McpOperatorEnvironment.Development,
            McpOperatorPolicyEffect.Allow,
            10,
            new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders"),
            new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
            null,
            McpOperatorOperationFamily.Observability,
            null,
            new McpOperatorConstraints(),
            DateTimeOffset.UtcNow,
            "policy.admin@example.test",
            null,
            null,
            null,
            null,
            1,
            "change-347");

        private McpOperatorPolicy CurrentPolicy { get; set; }
        private McpOperatorTargetProfile? CurrentTargetProfile { get; set; }

        public RecordingAdministration() => CurrentPolicy = CreatedPolicy;

        public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment? environment, int? tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> ListChangeAuditsAsync(int? tenantId, Guid? policyId, Guid? agentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<McpOperatorAcceptedAuditPage> ListAcceptedAuditsAsync(McpOperatorAcceptedAuditFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<McpOperatorPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken) => Task.FromResult<McpOperatorPolicy?>(policyId == CreatedPolicy.PolicyId ? CurrentPolicy : null);
        public Task ValidateDraftAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            Validations++;
            return Task.CompletedTask;
        }
        public Task<McpOperatorPolicy> CreateAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            Creates++;
            return Task.FromResult(CreatedPolicy);
        }
        public Task<McpOperatorPolicy> ReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            Replaces++;
            LastReplacement = (policyId, expectedVersion, draft);
            CurrentPolicy = CurrentPolicy with
            {
                Name = draft.Name,
                Effect = draft.Effect,
                Priority = draft.Priority,
                PrincipalSelector = draft.PrincipalSelector,
                TargetSelector = draft.TargetSelector,
                TargetClassification = draft.TargetClassification,
                OperationFamily = draft.OperationFamily,
                Operation = draft.Operation,
                Constraints = draft.Constraints,
                ExpiresAtUtc = draft.ExpiresAtUtc,
                ReviewByUtc = draft.ReviewByUtc,
                Version = expectedVersion + 1,
                AuditReference = draft.AuditReference
            };
            return Task.FromResult(CurrentPolicy);
        }

        public Task<McpOperatorPolicy?> DisableAsync(Guid policyId, long expectedVersion, string actorId, CancellationToken cancellationToken)
        {
            Disables++;
            LastDisable = (policyId, expectedVersion);
            CurrentPolicy = CurrentPolicy with
            {
                DisabledAtUtc = DateTimeOffset.UtcNow,
                DisabledBy = actorId,
                LifecycleState = McpOperatorPolicyLifecycleState.Disabled,
                Version = expectedVersion + 1
            };
            return Task.FromResult<McpOperatorPolicy?>(CurrentPolicy);
        }

        public Task<McpOperatorPolicy?> RevokeAsync(Guid policyId, long expectedVersion, string actorId, CancellationToken cancellationToken)
        {
            Revocations++;
            LastRevocation = (policyId, expectedVersion);
            CurrentPolicy = CurrentPolicy with
            {
                DisabledAtUtc = CurrentPolicy.DisabledAtUtc ?? DateTimeOffset.UtcNow,
                DisabledBy = CurrentPolicy.DisabledBy ?? actorId,
                LifecycleState = McpOperatorPolicyLifecycleState.Revoked,
                Version = expectedVersion + 1
            };
            return Task.FromResult<McpOperatorPolicy?>(CurrentPolicy);
        }
        public Task<McpOperatorTargetProfile?> GetTargetProfileAsync(int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(CurrentTargetProfile is { TenantId: var currentTenant, AgentId: var currentAgent } profile && currentTenant == tenantId && currentAgent == agentId ? profile : null);

        public Task<McpOperatorTargetProfile> UpsertTargetProfileAsync(int tenantId, Guid agentId, McpOperatorTargetClassification classification, IReadOnlyCollection<string> tags, long? expectedVersion, string actorId, CancellationToken cancellationToken)
        {
            TargetProfileUpserts++;
            CurrentTargetProfile = new McpOperatorTargetProfile(
                agentId,
                tenantId,
                classification,
                tags.Order(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow,
                actorId,
                CurrentTargetProfile is null ? 1 : CurrentTargetProfile.Version + 1);
            return Task.FromResult(CurrentTargetProfile);
        }
    }

    private sealed class RecordingConfirmations : IMcpOperatorConfirmationService
    {
        public const string PlanToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        public const string IdempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        public static readonly Guid AdmissionId = Guid.Parse("9cb863f4-ff02-43c8-b60d-6703e8ca508f");
        public List<McpOperatorConfirmationPlanRequest> Plans { get; } = [];
        public List<McpOperatorConfirmationRequest> Confirmed { get; } = [];
        public List<(Guid IdempotencyId, McpOperatorIdempotencyOutcome Outcome, string? ResultReference)> Completed { get; } = [];

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken)
        {
            Plans.Add(request);
            return Task.FromResult(new McpOperatorConfirmationPlan(PlanToken, IdempotencyKey, DateTimeOffset.UtcNow.AddMinutes(5), McpOperatorConfirmationClass.PolicyAdministration, request.PayloadHash, request.Decision.TargetSetDigest));
        }

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            Confirmed.Add(request);
            return Task.FromResult(new McpOperatorConfirmationAdmission(true, false, null, AdmissionId, McpOperatorIdempotencyOutcome.Pending, null));
        }

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        {
            Completed.Add((idempotencyId, outcome, resultReference));
            return Task.CompletedTask;
        }
    }

    private sealed class TestM2mAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.Authorization == "M2M"
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-dev")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}
