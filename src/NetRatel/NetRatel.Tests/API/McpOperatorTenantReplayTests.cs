using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorTenantReplayTests
{
    [Theory]
    [InlineData("create", 1)]
    [InlineData("update", 2)]
    [InlineData("delete", 1)]
    public async Task Exact_replay_returns_original_version_after_later_writes_and_deletion_without_dispatching_again(string action, long version)
    {
        using var rig = await ReplayRig.CreateAsync();
        var seed = action == "create" ? null : await rig.SeedTenantAsync();
        var request = await rig.PreviewAsync(action, Request(action, seed));
        var first = await rig.ConfirmResultAsync(action, request);
        first.Version.Should().Be(version);
        first.Deleted.Should().Be(action == "delete");
        first.Replayed.Should().BeFalse();

        var immediate = await rig.ConfirmResultAsync(action, request);
        AssertReplay(first, immediate);
        if (action != "delete")
        {
            await rig.ChangeTenantAsync(first.TenantId);
            AssertReplay(first, await rig.ConfirmResultAsync(action, request));
            await rig.InDbAsync(async db =>
            {
                var tenant = await db.Tenants.SingleAsync(row => row.Id == first.TenantId);
                await new TenantService(db).DeleteAsync(tenant.Id, tenant.Version);
            });
            AssertReplay(first, await rig.ConfirmResultAsync(action, request));
        }
        await rig.InDbAsync(async db =>
        {
            (await db.Tenants.CountAsync()).Should().Be(0);
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
            var receipt = await db.McpOperatorIdempotencyRecords.SingleAsync();
            receipt.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
            receipt.ResultReference.Should().Be($"tenant-mutation:{first.TenantId}:{version}:{action}");
        });
    }

    [Fact]
    public async Task Replay_still_requires_current_authentication_scope_role_binding_policy_expiry_and_payload()
    {
        using var rig = await ReplayRig.CreateAsync();
        var tenant = await rig.SeedTenantAsync();
        var request = await rig.PreviewAsync("update", Request("update", tenant));
        await rig.ConfirmResultAsync("update", request);

        (await rig.PostAsync("confirm", "update", request, authenticated: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request, scopes: []), HttpStatusCode.Forbidden, "oauth_scope_missing");
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request, roles: []), HttpStatusCode.Forbidden, "oauth_role_missing");
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request, resource: "https://other.example.test/mcp"), HttpStatusCode.Conflict, "confirmation_plan_stale");
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request with { Name = "Changed replay payload" }), HttpStatusCode.Conflict, "confirmation_plan_stale");

        await rig.InDbAsync(async db =>
        {
            (await db.McpOperatorPolicies.SingleAsync(policy => policy.Operation == "netratel_tenants/update")).Version++;
            await db.SaveChangesAsync();
        });
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request), HttpStatusCode.Conflict, "confirmation_plan_stale");
        await rig.InDbAsync(async db =>
        {
            var policy = await db.McpOperatorPolicies.SingleAsync(policy => policy.Operation == "netratel_tenants/update");
            policy.Version = 1;
            policy.DisabledAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request), HttpStatusCode.Forbidden, "target_operation_not_authorized");
        await rig.InDbAsync(async db =>
        {
            (await db.McpOperatorPolicies.SingleAsync(policy => policy.Operation == "netratel_tenants/update")).DisabledAtUtc = null;
            (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request), HttpStatusCode.Conflict, "confirmation_plan_expired");
        await rig.InDbAsync(async db =>
        {
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
            (await db.Tenants.SingleAsync()).Version.Should().Be(2);
        });
    }

    [Fact]
    public async Task Fresh_invalid_and_stale_confirmations_do_not_consume_the_plan_or_record_acceptance()
    {
        using var rig = await ReplayRig.CreateAsync();
        var tenant = await rig.SeedTenantAsync();
        foreach (var target in new[] { "latest", "1.2.3-beta.1" })
            await AssertFailureAsync(await rig.PostAsync("preview", "update", Request("update", tenant) with
            {
                AutoUpdateTargetVersion = target, AutoUpdateChannel = "stable"
            }), HttpStatusCode.BadRequest, "tenant_invalid");
        var request = await rig.PreviewAsync("update", Request("update", tenant));
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request with { Name = string.Empty }), HttpStatusCode.BadRequest, "tenant_invalid");
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request with { AutoUpdateTargetVersion = "latest" }), HttpStatusCode.BadRequest, "tenant_invalid");
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request with { PlanToken = "invalid" }), HttpStatusCode.Conflict, "confirmation_plan_invalid");
        await rig.ChangeTenantAsync(tenant.TenantId);
        await AssertFailureAsync(await rig.PostAsync("confirm", "update", request), HttpStatusCode.Conflict, "tenant_version_conflict");
        await rig.InDbAsync(async db =>
        {
            (await db.McpOperatorConfirmationPlans.SingleAsync()).ConsumedAtUtc.Should().BeNull();
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
        });
    }

    [Fact]
    public async Task A_confirmation_completed_after_the_first_lookup_is_reconciled_after_version_resolution_fails()
    {
        using var rig = await ReplayRig.CreateAsync();
        var tenant = await rig.SeedTenantAsync();
        var request = await rig.PreviewAsync("update", Request("update", tenant));
        McpOperatorTenantMutationResult? winner = null;
        rig.Hook.AfterFreshLookup = async () => { winner = await rig.ConfirmResultAsync("update", request); };

        var raced = await rig.ConfirmResultAsync("update", request);
        winner.Should().NotBeNull();
        winner!.Replayed.Should().BeFalse();
        AssertReplay(winner, raced);
        await rig.InDbAsync(async db =>
        {
            (await db.Tenants.SingleAsync()).Version.Should().Be(2);
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        });
    }

    [Fact]
    public async Task Deleted_tenant_replay_retains_the_separate_active_tenant_deletion_constraint()
    {
        using var rig = await ReplayRig.CreateAsync();
        var tenant = await rig.SeedTenantAsync();
        var request = await rig.PreviewAsync("delete", Request("delete", tenant));
        await rig.ConfirmResultAsync("delete", request);
        await AssertFailureAsync(await rig.PostAsync("confirm", "delete", request, activeTenantId: tenant.TenantId),
            HttpStatusCode.Forbidden, "active_caller_tenant_delete_requires_separate_approval");
        await rig.InDbAsync(async db => { (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1); });
    }

    [Theory]
    [InlineData("update", 3)]
    [InlineData("delete", 0)]
    public async Task Legacy_id_only_receipts_keep_the_documented_current_row_or_deleted_zero_fallback(string action, long expectedVersion)
    {
        using var rig = await ReplayRig.CreateAsync();
        var tenant = await rig.SeedTenantAsync();
        var request = await rig.PreviewAsync(action, Request(action, tenant));
        var first = await rig.ConfirmResultAsync(action, request);
        // Represent an already-persisted receipt from before original versions
        // were recorded. This is fixture setup, never a production rewrite.
        await rig.InDbAsync(async db =>
        {
            (await db.McpOperatorIdempotencyRecords.SingleAsync()).ResultReference = first.TenantId.ToString(CultureInfo.InvariantCulture);
            await db.SaveChangesAsync();
        });
        if (action == "update") await rig.ChangeTenantAsync(first.TenantId);
        var replay = await rig.ConfirmResultAsync(action, request);
        replay.TenantId.Should().Be(first.TenantId);
        replay.Version.Should().Be(expectedVersion);
        replay.Replayed.Should().BeTrue();
        replay.Deleted.Should().Be(action == "delete");
        await rig.InDbAsync(async db => { (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1); });
    }

    private static McpOperatorTenantMutationRequest Request(string action, TenantInfo? tenant) => action == "delete"
        ? new(TenantId: tenant!.TenantId, ExpectedVersion: tenant.Version, Cascade: false)
        : new(TenantId: action == "create" ? null : tenant!.TenantId,
            ExpectedVersion: action == "create" ? null : tenant!.Version, Name: "Mutation tenant", Domains: [], AutoUpdate: false);

    private static void AssertReplay(McpOperatorTenantMutationResult first, McpOperatorTenantMutationResult replay)
    {
        replay.TenantId.Should().Be(first.TenantId);
        replay.Version.Should().Be(first.Version);
        replay.Deleted.Should().Be(first.Deleted);
        replay.Replayed.Should().BeTrue();
    }

    private static async Task AssertFailureAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            response.StatusCode.Should().Be(status);
            (await response.Content.ReadAsStringAsync()).Should().Contain(code);
        }
    }

    private sealed class ReplayRig(IHost host, ReplayHook hook) : IDisposable
    {
        private readonly HttpClient _http = host.GetTestClient();
        internal ReplayHook Hook => hook;

        internal static async Task<ReplayRig> CreateAsync()
        {
            var databaseName = $"tenant-replay-{Guid.NewGuid():N}";
            var hook = new ReplayHook();
            var options = new McpOperatorDelegationOptions
            {
                Enabled = true, Issuer = "tenant-replay-tests", Audience = "tenant-api-tests", ServicePrincipal = "tenant-mcp-tests",
                KeyId = "test-key", SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-tenant-replay-signing-key-32-bytes"))
            };
            var host = await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
            {
                web.UseEnvironment(Environments.Production).UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("M2M").AddScheme<AuthenticationSchemeOptions, ReplayAuthenticationHandler>("M2M", _ => { });
                    services.AddAuthorization(authorization => authorization.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
                    services.AddSingleton(options);
                    services.AddSingleton<McpOperatorDelegationTokenService>();
                    services.AddSingleton(new McpOperatorLocalAgentOptions());
                    services.AddSingleton(hook);
                    services.AddDbContext<OrchestratorDbContext>(db => db.UseInMemoryDatabase(databaseName));
                    services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
                    services.AddScoped<IMcpOperatorConfirmationService, ObservedConfirmations>();
                    services.AddScoped<ITenantService, TenantService>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseMiddleware<McpOperatorDelegationMiddleware>();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapMcpOperatorTenantEndpoints());
                });
            }).StartAsync();
            var rig = new ReplayRig(host, hook);
            await rig.InDbAsync(async db =>
            {
                foreach (var action in new[] { "create", "update", "delete" })
                    db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
                    {
                        Id = Guid.NewGuid(), Name = $"Tenant {action}", Environment = McpOperatorEnvironment.Production,
                        Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
                        PrincipalSelectorValue = "tenant-operator", TargetSelectorKind = McpOperatorTargetSelectorKind.ControlPlane, TenantId = 0,
                        OperationFamily = McpOperatorOperationCatalog.Find("netratel_tenants", action)!.OperationFamily,
                        Operation = $"netratel_tenants/{action}", ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(), McpOperatorJsonContext.Default.McpOperatorConstraints),
                        CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "test-admin", Version = 1
                    });
                await db.SaveChangesAsync();
            });
            return rig;
        }

        internal async Task<TenantInfo> SeedTenantAsync()
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ITenantService>().CreateAsync(
                new CreateTenantCommand("Initial tenant", null, null, [], null, null, false));
        }

        internal async Task ChangeTenantAsync(int tenantId)
        {
            using var scope = host.Services.CreateScope();
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantService>();
            var current = (await tenants.GetAsync(tenantId))!;
            await tenants.UpdateAsync(new UpdateTenantCommand(tenantId, "Later tenant revision", null, null, [], null, null, false,
                ExpectedVersion: current.Version));
        }

        internal async Task InDbAsync(Func<OrchestratorDbContext, Task> action)
        {
            using var scope = host.Services.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
        }

        internal async Task<McpOperatorTenantMutationRequest> PreviewAsync(string action, McpOperatorTenantMutationRequest request)
        {
            using var response = await PostAsync("preview", action, request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var preview = (await response.Content.ReadFromJsonAsync<McpOperatorTenantPreview>())!;
            return request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };
        }

        internal async Task<McpOperatorTenantMutationResult> ConfirmResultAsync(string action, McpOperatorTenantMutationRequest request)
        {
            using var response = await PostAsync("confirm", action, request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<McpOperatorTenantMutationResult>())!;
        }

        internal async Task<HttpResponseMessage> PostAsync(string phase, string action, McpOperatorTenantMutationRequest payload,
            bool authenticated = true, string[]? scopes = null, string[]? roles = null,
            string resource = "https://tenant-mcp.example.test/mcp", int? activeTenantId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/mcp/operator/tenants/{phase}/{action}")
            { Content = JsonContent.Create(payload) };
            if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("M2M");
            var assertion = host.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
                new McpOperatorDelegationIdentity("tenant-operator", "tenant-client", null, [], roles ?? ["PolicyAdministrator"], scopes ?? ["netratel.mcp.admin"], activeTenantId),
                new McpOperatorDelegationRequest("netratel_tenants", action, $"tenant-{action}", resource, "prod", null, null, $"tenant-{phase}-{action}"));
            request.Headers.Add(McpOperatorDelegationOptions.HeaderName, assertion);
            return await _http.SendAsync(request);
        }

        public void Dispose()
        {
            _http.Dispose();
            host.Dispose();
        }
    }

    private sealed class ReplayHook
    {
        internal Func<Task>? AfterFreshLookup;
    }

    private sealed class ObservedConfirmations(OrchestratorDbContext db, ReplayHook hook) : IMcpOperatorConfirmationService
    {
        private readonly McpOperatorConfirmationService _inner = new(db);
        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken ct) => _inner.CreatePlanAsync(request, ct);
        public Task CompleteAsync(Guid id, McpOperatorIdempotencyOutcome outcome, string? reference, CancellationToken ct) => _inner.CompleteAsync(id, outcome, reference, ct);
        public async Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken ct)
        {
            var admission = await _inner.ConfirmAsync(request, ct);
            if (request.ReplayOnly && !admission.IsReplay && admission.FailureCode is null &&
                Interlocked.Exchange(ref hook.AfterFreshLookup, null) is { } afterLookup)
                await afterLookup();
            return admission;
        }
    }

    private sealed class ReplayAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Request.Headers.Authorization == "M2M"
            ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "tenant-mcp-tests")], Scheme.Name)), Scheme.Name)))
            : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}
