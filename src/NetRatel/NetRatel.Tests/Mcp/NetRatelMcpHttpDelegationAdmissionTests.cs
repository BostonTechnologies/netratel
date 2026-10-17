using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed partial class NetRatelMcpHttpTests
{
    [Theory]
    [InlineData("local_credential_expired")]
    [InlineData("local_credential_revoked")]
    public async Task Local_http_ingress_classifies_only_hash_purpose_and_resource_bound_credential_lifecycle(string expectedCode)
    {
        var clock = new AdmissionClock(DateTimeOffset.UtcNow);
        var tokens = new McpOperatorDelegationTokenService(LocalCredentialDelegationOptions());
        await using var api = await CreatePersistedLocalCredentialApiAsync(tokens, clock);
        await using var mcp = await CreateApplicationAsync(localCredentialMode: true,
            localCredentialApiHandler: api.Application.GetTestServer().CreateHandler());
        async Task<HttpResponseMessage> InvokeAsync(WebApplication application, string secret)
        {
            using var request = McpRequest(41, "tools/call", new { name = "netratel_capabilities", arguments = new { operation = "get" } });
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return await application.GetTestClient().SendAsync(request);
        }
        using (var accepted = await InvokeAsync(mcp.Application, api.Credential.Secret))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await ReadMcpPayloadAsync(accepted);
            body.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue();
        }
        if (expectedCode == "local_credential_expired") clock.UtcNow = clock.UtcNow.AddHours(2);
        else await api.RevokeCredentialAsync();
        async Task AssertDeniedAsync(WebApplication application, string secret, string code)
        {
            using var response = await InvokeAsync(application, secret);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("code").GetString().Should().Be(code);
            body.GetProperty("failure").GetProperty("retryable").GetBoolean().Should().BeFalse();
            body.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
            body.GetRawText().Should().NotContain(api.Credential.Secret).And.NotContain("local-owner").And.NotContain(api.Credential.CredentialId);
        }
        await AssertDeniedAsync(mcp.Application, api.Credential.Secret, expectedCode);
        await AssertDeniedAsync(mcp.Application, "nrt_ic_" + new string('0', 64), "local_credential_invalid");

        await using (var scope = api.Application.Services.CreateAsyncScope())
        {
            var apiPurpose = await scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>().CreateAsync("local-owner",
                new IntegrationCredentialCreateRequest("wrong-purpose synthetic control", IntegrationCredentialPurpose.Api,
                    clock.GetUtcNow().AddHours(1), [], InstancePermissions: [NetRatelPermissions.McpDiscoveryRead]));
            await AssertDeniedAsync(mcp.Application, apiPurpose.Secret, "local_credential_invalid");
        }
        await using var wrongResource = await CreateApplicationAsync(localCredentialMode: true,
            localCredentialApiHandler: api.Application.GetTestServer().CreateHandler(), publicResourceUri: "https://other-mcp.example.invalid/mcp");
        await AssertDeniedAsync(wrongResource.Application, api.Credential.Secret, "local_credential_invalid");
    }

    private sealed class AdmissionClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Theory]
    [InlineData("netratel_policy", "policies")]
    [InlineData("netratel_connectivity", "settings")]
    public async Task Local_http_valid_credential_remains_authenticated_around_policy_and_connectivity_admission(string tool, string operation)
    {
        await using var api = await AdmissionApiAsync(new AdmissionGrants(NetRatelPermissions.McpDiscoveryRead,
            NetRatelPermissions.McpPolicyAdministration, NetRatelPermissions.McpConnectivityRead));
        var business = new ContractBusinessApi();
        await using var mcp = await CreateApplicationAsync(localCredentialMode: true,
            localCredentialApiHandler: api.GetTestServer().CreateHandler(), businessApi: business, operatorSurfaceEnabled: true);
        async Task<JsonDocument> InvokeAsync(string selectedTool, string selectedOperation)
        {
            using var response = await mcp.Application.GetTestClient().SendAsync(ContractCall(selectedTool, new { operation = selectedOperation }));
            return await ReadMcpPayloadAsync(response);
        }
        using var before = await InvokeAsync("netratel_auth", "status");
        using var target = await InvokeAsync(tool, operation);
        using var after = await InvokeAsync("netratel_auth", "status");
        before.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue();
        after.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue();
        var admission = target.RootElement.GetProperty("result").GetProperty("structuredContent");
        admission.GetProperty("success").GetBoolean().Should().BeTrue($"the active credential has the explicit {tool}/{operation} application permission; result {admission.GetRawText()}");
        business.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Local_http_policy_shapes_and_connectivity_inspection_reach_real_exchange_without_invented_agents()
    {
        var grants = new AdmissionGrants(NetRatelPermissions.McpPolicyAdministration, NetRatelPermissions.McpConnectivityRead, NetRatelPermissions.JobManagement);
        await using var api = await AdmissionApiAsync(grants);
        var business = new ContractBusinessApi();
        await using var mcp = await CreateApplicationAsync(localCredentialMode: true,
            localCredentialApiHandler: api.GetTestServer().CreateHandler(), businessApi: business, operatorSurfaceEnabled: true);
        var draft = new
        {
            name = "inert tenant denial", environment = 1, effect = 1, priority = 0,
            principalSelector = new { kind = 3, value = "synthetic-review-group" },
            targetSelector = new { kind = 2, tenantId = 17 }, operationFamily = 1, constraints = new { }
        };
        var calls = new (string Tool, object Arguments)[]
        {
            ("netratel_policy", new { operation = "policies" }),
            ("netratel_policy", new { operation = "policies", request = new { tenantId = 17 } }),
            ("netratel_policy", new { operation = "preview_create", request = new { policy = draft } }),
            ("netratel_policy", new { operation = "policy", request = new { policyId = AdmissionPolicyId } }),
            ("netratel_jobs", new { operation = "get", request = new { tenantId = 17, agentId = ContractAgentId, jobId = "1001" } }),
            ("netratel_connectivity", new { operation = "settings" }),
            ("netratel_connectivity", new { operation = "netratel" }),
            ("netratel_connectivity", new { operation = "preview_test" })
        };
        foreach (var call in calls)
        {
            using var response = await mcp.Application.GetTestClient().SendAsync(ContractCall(call.Tool, call.Arguments));
            using var payload = await ReadMcpPayloadAsync(response);
            var result = payload.RootElement.GetProperty("result");
            result.GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue(call.Tool);
        }
        business.Calls.Should().HaveCount(calls.Length);
        grants.Checks.Should().Contain((NetRatelPermissions.McpPolicyAdministration, (int?)17));
        grants.Checks.Should().Contain((NetRatelPermissions.McpPolicyAdministration, (int?)null));
        grants.Checks.Should().NotContain(check => check.Permission == NetRatelPermissions.McpConnectivityTest,
            "preview must not require or perform a probe dispatch");

        using var denied = await mcp.Application.GetTestClient().SendAsync(ContractCall("netratel_connectivity", new
        {
            operation = "test", confirm = true,
            request = new { planToken = new string('a', 40), idempotencyKey = new string('b', 40) }
        }));
        using var deniedPayload = await ReadMcpPayloadAsync(denied);
        var failure = deniedPayload.RootElement.GetProperty("result").GetProperty("structuredContent");
        failure.GetProperty("failure").GetProperty("code").GetString().Should().Be("local_credential_permission_denied");
        failure.GetProperty("failure").GetProperty("layer").GetString().Should().Be("application_permission");
        failure.GetProperty("failure").GetProperty("retryable").GetBoolean().Should().BeFalse();
        failure.GetProperty("failure").GetProperty("requiredOperation").GetString().Should().Be("netratel_connectivity/test");
        failure.GetProperty("data").GetProperty("requiredPermission").GetString().Should().Be(NetRatelPermissions.McpConnectivityTest);
        failure.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        business.Calls.Should().HaveCount(calls.Length, "inspection access cannot dispatch a confirmed test");

        grants.Permissions.Add(NetRatelPermissions.McpConnectivityTest);
        using var authorizedTest = await mcp.Application.GetTestClient().SendAsync(ContractCall("netratel_connectivity", new
        {
            operation = "test", confirm = true,
            request = new { planToken = new string('a', 40), idempotencyKey = new string('b', 40) }
        }));
        using var authorizedPayload = await ReadMcpPayloadAsync(authorizedTest);
        authorizedPayload.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("success").GetBoolean().Should().BeTrue();
        business.Calls.Should().HaveCount(calls.Length + 1, "both named grants admit the receiving confirmation authority; this fixture sends no probe");

        using var wrongOwner = await mcp.Application.GetTestClient().SendAsync(ContractCall("netratel_jobs", new
        {
            operation = "get", request = new { tenantId = 29, agentId = ContractAgentId, jobId = "1001" }
        }));
        using var ownershipPayload = await ReadMcpPayloadAsync(wrongOwner);
        ownershipPayload.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("failure").GetProperty("code").GetString().Should().Be("local_delegation_binding_mismatch");
        business.Calls.Should().HaveCount(calls.Length + 1);
    }

    [Fact]
    public async Task Local_http_receiving_denials_preserve_permission_role_policy_and_safe_correlation()
    {
        await using var api = await AdmissionApiAsync(new AdmissionGrants(NetRatelPermissions.McpConnectivityRead));
        var business = new AdmissionFailureBusinessApi();
        await using var mcp = await CreateApplicationAsync(localCredentialMode: true,
            localCredentialApiHandler: api.GetTestServer().CreateHandler(), businessApi: business, operatorSurfaceEnabled: true);
        foreach (var (code, layer) in new[]
        {
            ("local_credential_permission_denied", "application_permission"),
            ("oauth_role_missing", "oauth_role"),
            ("target_policy_missing", "policy"),
            ("target_policy_denied", "policy")
        })
        {
            business.Code = code;
            using var response = await mcp.Application.GetTestClient().SendAsync(ContractCall("netratel_connectivity", new { operation = "settings" }));
            using var payload = await ReadMcpPayloadAsync(response);
            var result = payload.RootElement.GetProperty("result").GetProperty("structuredContent");
            result.GetProperty("failure").GetProperty("code").GetString().Should().Be(code);
            result.GetProperty("failure").GetProperty("layer").GetString().Should().Be(layer);
            result.GetProperty("failure").GetProperty("retryable").GetBoolean().Should().BeFalse();
            result.GetProperty("correlationId").GetString().Should().Be("receiving-correlation");
        }
    }

    private sealed class AdmissionFailureBusinessApi : INetRatelMcpApiClient
    {
        public string Code { get; set; } = "local_credential_permission_denied";
        public Task<System.Text.Json.Nodes.JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get, path, null, cancellationToken);
        public Task<System.Text.Json.Nodes.JsonNode?> SendAsync(HttpMethod method, string path, System.Text.Json.Nodes.JsonNode? body = null, CancellationToken cancellationToken = default) =>
            throw new NetRatelMcpApiException("upstream_forbidden", "Receiving authority did not admit this operation.", 403, false, Code) { CorrelationId = "receiving-correlation" };
    }

    [Fact]
    public async Task Local_execution_distinguishes_permission_removal_expiry_and_revocation()
    {
        var grants = new AdmissionGrants(NetRatelPermissions.McpConnectivityRead);
        var current = new AdmissionCurrentCredential();
        await using var api = await AdmissionApiAsync(grants, current);
        var tokens = new McpOperatorDelegationTokenService(LocalCredentialDelegationOptions());
        var assertion = tokens.Create(new McpOperatorDelegationIdentity("local-owner", "credential-id", null, [], ["Operator"], ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest("netratel_connectivity", "settings", "admission-test", "https://mcp.dev.example/mcp", "dev", CorrelationId: "synthetic-correlation")
            { IngressCredentialId = "credential-id", IngressPermission = NetRatelPermissions.McpConnectivityRead });
        async Task<HttpResponseMessage> ExecuteAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/execution");
            request.Headers.Add(McpOperatorDelegationOptions.HeaderName, assertion);
            return await api.GetTestClient().SendAsync(request);
        }
        using (var accepted = await ExecuteAsync()) accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        grants.Permissions.Clear();
        using (var denied = await ExecuteAsync())
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var payload = await denied.Content.ReadFromJsonAsync<JsonElement>();
            payload.GetProperty("code").GetString().Should().Be("local_credential_permission_denied");
            payload.GetProperty("correlationId").GetString().Should().Be("synthetic-correlation");
        }
        foreach (var code in new[] { "local_credential_expired", "delegated_identity_revoked" })
        {
            current.FailureCode = code;
            using var denied = await ExecuteAsync();
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
        }
    }

    [Fact]
    public void Advertised_local_operations_have_explicit_named_grants_and_scope_role_parity()
    {
        foreach (var tool in NetRatelMcpCatalog.Tools.Where(tool => tool.AvailableOverHttp))
        foreach (var operation in tool.Operations.Where(operation => operation.IsAvailableOverHttpIn("prod")))
        {
            var permission = McpLocalOperationAuthorizationCatalog.RequiredPermission(tool.Name, operation.Name);
            permission.Should().NotBeNullOrWhiteSpace($"{tool.Name}/{operation.Name} must have an explicit local-mode mapping");
            NetRatelPermissions.All.Should().Contain(permission!);
            var access = McpOperationAccessCatalog.Find(tool.Name, operation.Name)!;
            McpOperationRoleRequirements.IsSatisfied(access.RequiredScope,
                McpLocalOperationAuthorizationCatalog.RolesFor(access.RequiredScope).ToHashSet(StringComparer.Ordinal)).Should().BeTrue();
        }
        McpLocalOperationAuthorizationCatalog.RequiredPermission("netratel_connectivity", "test").Should().Be(NetRatelPermissions.McpConnectivityTest);
        McpLocalOperationAuthorizationCatalog.AdditionalPermissions("netratel_connectivity", "test").Should().Contain(NetRatelPermissions.McpConnectivityRead);
        IntegrationPermissionCatalog.Delegable.Select(permission => permission.Id).Should().Contain(
            [NetRatelPermissions.McpConnectivityRead, NetRatelPermissions.McpConnectivityTest], "operator approval must offer the same explicit named grants");
        McpLocalOperationAuthorizationCatalog.RequiredPermission("unknown", "get").Should().BeNull();
    }

    private static readonly Guid AdmissionPolicyId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static async Task<WebApplication> AdmissionApiAsync(AdmissionGrants grants, AdmissionCurrentCredential? current = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(IntegrationCredentialAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalExchangeAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("McpLocalDelegationExchange", policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(LocalCredentialDelegationOptions());
        builder.Services.AddSingleton<McpOperatorDelegationTokenService>();
        builder.Services.AddSingleton<IEffectiveAccessService>(grants);
        builder.Services.AddSingleton<IMcpOperationObjectTargetResolver, AdmissionPolicyResolver>();
        builder.Services.AddSingleton<IIntegrationCredentialCurrentVerifier>(current ?? new AdmissionCurrentCredential());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<McpOperatorDelegationMiddleware>();
        app.UseAuthorization();
        app.MapMcpLocalDelegationEndpoints();
        app.MapGet("/execution", () => "admitted");
        await app.StartAsync();
        return app;
    }

    private sealed class AdmissionPolicyResolver : IMcpOperationObjectTargetResolver
    {
        public Task<McpOperationResolvedPolicyTarget?> ResolvePolicyTargetAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(reference == AdmissionPolicyId.ToString("D") ? new McpOperationResolvedPolicyTarget(17, null) : null);
        public Task<bool> MatchesDelegationAsync(McpOperatorDelegation delegation, CancellationToken cancellationToken) =>
            Task.FromResult(delegation.Tool switch
            {
                "netratel_jobs" => delegation.ObjectReference == "1001" && delegation.TenantId == 17 && delegation.AgentId == ContractAgentId,
                "netratel_job_runs" => delegation.ObjectReference == "9007199254740993" && delegation.TenantId == 17 && delegation.AgentId == ContractAgentId,
                _ => delegation.ObjectReference == AdmissionPolicyId.ToString("D") && delegation.TenantId == 17 && delegation.AgentId is null
            });
    }

    private sealed class AdmissionCurrentCredential : IIntegrationCredentialCurrentVerifier
    {
        public string? FailureCode { get; set; }
        public Task<IntegrationCredentialCurrentResult> CheckCurrentAsync(string credentialId, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntegrationCredentialCurrentResult(FailureCode is null
                ? new VerifiedIntegrationCredential(credentialId, "local-owner", purpose, []) { Resource = "https://mcp.dev.example/mcp" } : null, FailureCode));
        public async Task<VerifiedIntegrationCredential?> VerifyCurrentAsync(string credentialId, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default) =>
            (await CheckCurrentAsync(credentialId, purpose, cancellationToken)).Credential;
    }

    private sealed class AdmissionGrants(params string[] permissions) : IEffectiveAccessService
    {
        public HashSet<string> Permissions { get; } = new(permissions, StringComparer.Ordinal);
        public List<(string Permission, int? Tenant)> Checks { get; } = [];
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
        {
            Checks.Add((permission, tenantId));
            return Task.FromResult(Permissions.Contains(permission) && tenantId is null or 17);
        }
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("local-owner", false, false, Permissions));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
