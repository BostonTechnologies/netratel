using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorOnboardingEndpointTests
{
    [Fact]
    public void Production_onboarding_routes_are_tenant_scoped_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMcpOperatorAuthorization>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEnrollmentCodeIssueService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClientArtifactsService>(_ => throw new NotSupportedException());
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase($"operator-onboarding-routes-{Guid.NewGuid():N}"));
        var app = builder.Build();

        app.MapMcpOperatorOnboardingEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/collateral/{runtime}",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/collateral/{runtime}/download",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/enrollments",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/enrollments/{enrollmentCodeId:guid}",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/preview/create-enrollment",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/confirm/create-enrollment",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/preview/revoke-enrollment",
            "/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding/confirm/revoke-enrollment");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }

    [Fact]
    public async Task Production_onboarding_is_tenant_authorized_policy_bounded_and_never_replays_the_raw_code()
    {
        var admission = new RecordingAuthorization(new McpOperatorConstraints(MaxOnboardingCodeLifetimeSeconds: 300, MaxOnboardingCodeUses: 1));
        using var app = await BuildAppAsync(admission);
        const int tenantId = 42;
        var root = $"/api/v2/mcp/operator/tenants/{tenantId}/onboarding";

        var collateral = await ClientFor(app, "collateral", tenantId).GetFromJsonAsync<McpOperatorOnboardingCollateral>($"{root}/collateral/linux-x64");
        var download = await ClientFor(app, "collateral_download", tenantId).GetFromJsonAsync<McpOperatorOnboardingCollateralDownload>($"{root}/collateral/linux-x64/download");
        var rejected = await ClientFor(app, "create_enrollment", tenantId).PostAsJsonAsync($"{root}/preview/create-enrollment", new { runtime = "linux-x64", validForMinutes = 6, maxUses = 1 });
        var previewResponse = await ClientFor(app, "create_enrollment", tenantId).PostAsJsonAsync($"{root}/preview/create-enrollment", new { runtime = "linux-x64", validForMinutes = 5, maxUses = 1 });
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorEnrollmentPreview>();
        var confirmed = await ClientFor(app, "create_enrollment", tenantId).PostAsJsonAsync($"{root}/confirm/create-enrollment", new
        {
            runtime = "linux-x64",
            validForMinutes = 5,
            maxUses = 1,
            planToken = preview!.PlanToken,
            idempotencyKey = preview.IdempotencyKey
        });
        var created = await confirmed.Content.ReadFromJsonAsync<McpOperatorEnrollmentCreateResult>();
        var replayed = await ClientFor(app, "create_enrollment", tenantId).PostAsJsonAsync($"{root}/confirm/create-enrollment", new
        {
            runtime = "linux-x64",
            validForMinutes = 5,
            maxUses = 1,
            planToken = preview.PlanToken,
            idempotencyKey = preview.IdempotencyKey
        });
        var replay = await replayed.Content.ReadFromJsonAsync<McpOperatorEnrollmentCreateResult>();
        var metadata = await ClientFor(app, "get_enrollment", tenantId).GetAsync($"{root}/enrollments/{created!.EnrollmentCodeId:D}");
        var page = await ClientFor(app, "list_enrollments", tenantId).GetFromJsonAsync<McpOperatorEnrollmentPage>($"{root}/enrollments?status=active");

        collateral.Should().BeEquivalentTo(new McpOperatorOnboardingCollateral("linux-x64", "1.2.3-test", "netratel-client.zip", 4096, "test-sha", collateral!.UploadedAtUtc));
        download!.Collateral.Runtime.Should().Be("linux-x64");
        Convert.FromBase64String(download.ContentBase64).Should().Equal(Encoding.UTF8.GetBytes("bounded-installer"));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        created!.EnrollmentCode.Should().NotBeNullOrWhiteSpace();
        replayed.StatusCode.Should().Be(HttpStatusCode.OK, await replayed.Content.ReadAsStringAsync());
        replay!.Replay.Should().BeTrue();
        replay.EnrollmentCode.Should().BeNull();
        (await metadata.Content.ReadAsStringAsync()).Should().NotContain(created.EnrollmentCode!);
        page!.Items.Should().ContainSingle(item => item.EnrollmentCodeId == created.EnrollmentCodeId && item.Runtime == "linux-x64");
        admission.Requests.Should().NotBeEmpty();
        admission.Requests.All(request => request.TenantId == tenantId && request.AgentId is null && request.OperationFamily == McpOperatorOperationFamily.Onboarding).Should().BeTrue();
        admission.Requests.All(request => request.RequiredScopes.SetEquals(new[] { "netratel.mcp.onboarding" })).Should().BeTrue();
    }

    [Fact]
    public async Task Production_onboarding_revoke_requires_a_preview_and_returns_only_metadata()
    {
        var admission = new RecordingAuthorization(new McpOperatorConstraints(MaxOnboardingCodeLifetimeSeconds: 600, MaxOnboardingCodeUses: 2));
        using var app = await BuildAppAsync(admission);
        const int tenantId = 42;
        var root = $"/api/v2/mcp/operator/tenants/{tenantId}/onboarding";
        var preview = await (await ClientFor(app, "create_enrollment", tenantId).PostAsJsonAsync($"{root}/preview/create-enrollment", new { runtime = "win-x64", validForMinutes = 5, maxUses = 2 })).Content.ReadFromJsonAsync<McpOperatorEnrollmentPreview>();
        var created = await (await ClientFor(app, "create_enrollment", tenantId).PostAsJsonAsync($"{root}/confirm/create-enrollment", new { runtime = "win-x64", validForMinutes = 5, maxUses = 2, planToken = preview!.PlanToken, idempotencyKey = preview.IdempotencyKey })).Content.ReadFromJsonAsync<McpOperatorEnrollmentCreateResult>();
        var revokePreviewResponse = await ClientFor(app, "revoke_enrollment", tenantId).PostAsJsonAsync($"{root}/preview/revoke-enrollment", new { enrollmentCodeId = created!.EnrollmentCodeId });
        var revokePreview = await revokePreviewResponse.Content.ReadFromJsonAsync<McpOperatorEnrollmentRevokePreview>();
        var revoked = await ClientFor(app, "revoke_enrollment", tenantId).PostAsJsonAsync($"{root}/confirm/revoke-enrollment", new { enrollmentCodeId = created.EnrollmentCodeId, planToken = revokePreview!.PlanToken, idempotencyKey = revokePreview.IdempotencyKey });
        var result = await revoked.Content.ReadFromJsonAsync<McpOperatorEnrollmentRevokeResult>();

        revokePreviewResponse.StatusCode.Should().Be(HttpStatusCode.OK, await revokePreviewResponse.Content.ReadAsStringAsync());
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Enrollment.IsActive.Should().BeFalse();
        JsonSerializer.Serialize(result).Should().NotContain("test-code-value");
    }

    [Fact]
    public async Task Production_onboarding_collateral_download_is_bounded_by_the_current_policy()
    {
        using var app = await BuildAppAsync(new RecordingAuthorization(new McpOperatorConstraints(MaxArtifactBytes: 1024)));

        var response = await ClientFor(app, "collateral_download", 42)
            .GetAsync("/api/v2/mcp/operator/tenants/42/onboarding/collateral/linux-x64/download");

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Contain("onboarding_collateral_download_too_large");
    }

    [Fact]
    public async Task Large_installer_returns_pinned_credential_free_transfer_without_buffering_bytes()
    {
        var artifacts = new TestArtifacts { Size = 63_148_942 };
        using var app = await BuildAppAsync(new RecordingAuthorization(new McpOperatorConstraints(MaxArtifactBytes: 256 * 1024 * 1024)), artifacts);
        var result = await ClientFor(app, "collateral_download", 42)
            .GetFromJsonAsync<McpOperatorOnboardingCollateralDownload>("/api/v2/mcp/operator/tenants/42/onboarding/collateral/linux-x64/download");

        result!.TransferMode.Should().Be("enrollment_download");
        result.ContentBase64.Should().BeEmpty();
        result.Collateral.Size.Should().Be(63_148_942);
        result.Download.Should().Be(new McpOperatorOnboardingDownloadDescriptor(
            "/api/v2/client-artifacts/linux-x64/1.2.3-test/onboarding-download", "GET", 42,
            "X-NetRatel-Tenant-Id", "X-NetRatel-Enrollment-Code"));
        artifacts.DownloadedVersion.Should().BeNull("large installers must not be buffered into the MCP response");
    }

    [Fact]
    public async Task Inline_download_pins_the_advertised_version_instead_of_racing_latest()
    {
        var artifacts = new TestArtifacts();
        using var app = await BuildAppAsync(new RecordingAuthorization(new McpOperatorConstraints()), artifacts);
        var result = await ClientFor(app, "collateral_download", 42)
            .GetFromJsonAsync<McpOperatorOnboardingCollateralDownload>("/api/v2/mcp/operator/tenants/42/onboarding/collateral/linux-x64/download");

        result!.TransferMode.Should().Be("inline");
        result.Download.Should().BeNull();
        artifacts.DownloadedVersion.Should().Be(result.Collateral.Version);
    }

    private static HttpClient ClientFor(IHost app, string operation, int tenantId)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity("onboarding.operator@example.test", "onboarding-client", null, [], ["Operator"], ["netratel.mcp.onboarding"]),
            new McpOperatorDelegationRequest("netratel_onboarding", operation, $"request-{operation}-{Guid.NewGuid():N}", "https://mcp.prod.example/mcp", "prod", tenantId, null, $"correlation-{operation}"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
        return client;
    }

    private static async Task<IHost> BuildAppAsync(RecordingAuthorization authorization, TestArtifacts? artifacts = null)
    {
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-test",
            Audience = "netratel-api-test",
            ServicePrincipal = "netratel-mcp-http-test",
            KeyId = "test-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var databaseName = $"operator-onboarding-{Guid.NewGuid():N}";
        var databaseRoot = new InMemoryDatabaseRoot();
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Production);
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
                services.AddSingleton<IMcpOperatorConfirmationService, TestConfirmations>();
                services.AddSingleton<IClientArtifactsService>(artifacts ?? new TestArtifacts());
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName, databaseRoot));
                services.AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMcpOperatorOnboardingEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class RecordingAuthorization(McpOperatorConstraints constraints) : IMcpOperatorAuthorization
    {
        public List<McpOperatorAccessRequest> Requests { get; } = [];

        public Task<bool> HasTenantVisibilityAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<McpOperatorDecision> EvaluateAsync(McpOperatorAccessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new McpOperatorDecision(true, null, null, [Guid.Parse("dfe3ebfb-2af0-47c6-b4f5-6e26f2346f4d")], constraints, request.TargetSetDigest, request, 1));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorDecision decision, string servicePrincipal, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorAcceptedAudit(
                Guid.NewGuid(),
                Guid.Parse("dfe3ebfb-2af0-47c6-b4f5-6e26f2346f4d"),
                decision.Request.Environment,
                servicePrincipal,
                decision.Request.Principal.Subject,
                decision.Request.Principal.ClientId,
                decision.Request.Principal.AuthorizedParty,
                decision.Request.Principal.Groups.Order(StringComparer.Ordinal).ToArray(),
                decision.Request.Principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                decision.Request.Principal.Scopes.Order(StringComparer.Ordinal).ToArray(),
                decision.Request.McpResource,
                decision.Request.McpInstance,
                decision.Request.Tool,
                decision.Request.TenantId,
                decision.Request.AgentId,
                decision.Request.OperationFamily,
                decision.Request.Operation,
                decision.Request.CorrelationId,
                decision.Request.RequestId,
                DateTimeOffset.UtcNow));
    }

    private sealed class TestConfirmations : IMcpOperatorConfirmationService
    {
        private const string PlanToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        private const string IdempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        private readonly Dictionary<string, (Guid Id, McpOperatorIdempotencyOutcome Outcome, string? ResultReference)> completed = [];

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorConfirmationPlan(PlanToken, IdempotencyKey, DateTimeOffset.UtcNow.AddMinutes(5), request.Decision.Request.ConfirmationClass, request.PayloadHash, request.Decision.TargetSetDigest));

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            if (completed.TryGetValue(request.PayloadHash, out var prior))
                return Task.FromResult(new McpOperatorConfirmationAdmission(false, true, null, prior.Id, prior.Outcome, prior.ResultReference));
            var id = Guid.NewGuid();
            completed[request.PayloadHash] = (id, McpOperatorIdempotencyOutcome.Pending, null);
            return Task.FromResult(new McpOperatorConfirmationAdmission(true, false, null, id, McpOperatorIdempotencyOutcome.Pending, null));
        }

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        {
            var key = completed.Single(entry => entry.Value.Id == idempotencyId).Key;
            completed[key] = (idempotencyId, outcome, resultReference);
            return Task.CompletedTask;
        }
    }

    private sealed class TestArtifacts : IClientArtifactsService
    {
        public long Size { get; init; } = 4096;
        public string? DownloadedVersion { get; private set; }
        public Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct) => Task.FromResult<ClientArtifactSummaryDto?>(new ClientArtifactSummaryDto { Rid = rid, Version = "1.2.3-test", FileName = "netratel-client.zip", Size = Size, Sha256 = "test-sha", UploadedAt = DateTimeOffset.UtcNow });
        public Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct) { DownloadedVersion = versionOrLatest; return Task.FromResult(new ClientArtifactDownloadResult(new MemoryStream(Encoding.UTF8.GetBytes("bounded-installer")), "application/octet-stream", "netratel-client.zip", false, new ClientArtifactSummaryDto { Rid = rid, Version = "1.2.3-test", FileName = "netratel-client.zip", Size = Size, Sha256 = "test-sha", UploadedAt = DateTimeOffset.UtcNow })); }
        public Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestM2mAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.Authorization == "M2M"
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-test")], Scheme.Name)), Scheme.Name)))
                : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}
