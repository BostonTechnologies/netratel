using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.API.Services.Events;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DevelopmentMcpOnboardingEndpointTests
{
    [Fact]
    public async Task OnboardingWorkflow_IsTargetOwned_AndOnlyCreationReturnsTheRawCode()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/onboarding";

        var collateral = await client.GetFromJsonAsync<DevelopmentMcpOnboardingCollateralDto>($"{root}/collateral/linux-x64");
        var create = await client.PostAsJsonAsync($"{root}/enrollment-codes", new CreateDevelopmentMcpEnrollmentCodeRequest(5, "MCP-QA-onboarding-001"));
        var created = await create.Content.ReadFromJsonAsync<DevelopmentMcpCreatedEnrollmentCodeDto>();
        var metadata = await client.GetAsync($"{root}/enrollment-codes/{created!.EnrollmentCodeId:D}");
        var metadataBody = await metadata.Content.ReadAsStringAsync();
        var revoked = await client.PostAsync($"{root}/enrollment-codes/{created.EnrollmentCodeId:D}/revoke", null);
        var revokedMetadata = await revoked.Content.ReadFromJsonAsync<DevelopmentMcpEnrollmentMetadataDto>();

        collateral!.Runtime.Should().Be("linux-x64");
        collateral.Version.Should().Be("1.2.3-test");
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        created.EnrollmentCode.Should().NotBeNullOrWhiteSpace();
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        metadataBody.Should().NotContain(created.EnrollmentCode);
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        revokedMetadata!.EnrollmentCodeId.Should().Be(created.EnrollmentCodeId);
        revokedMetadata.IsActive.Should().BeFalse();
        app.Services.GetRequiredService<FakeEnrollmentCodes>().Issues.Should().ContainSingle(issue => issue.MaxUses == 1 && issue.Notes == "MCP-QA-onboarding-001");
        app.Services.GetRequiredService<FakeEnrollmentCodes>().Issues.Single().DevelopmentMcpTargetAgentId.Should().Be(agentId);
        app.Services.GetRequiredService<FakeEnrollmentCodes>().Issues.Single().DevelopmentMcpMarker.Should().Be("MCP-QA-onboarding-001");
        app.Services.GetRequiredService<FakeEnrollmentCodes>().Revocations.Should().ContainSingle(revocation => revocation.EnrollmentCodeId == created.EnrollmentCodeId);
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().Equal(
            DevelopmentOperatorOperation.OnboardingCollateralRead,
            DevelopmentOperatorOperation.OnboardingMutation,
            DevelopmentOperatorOperation.OnboardingEnrollmentMetadataRead,
            DevelopmentOperatorOperation.OnboardingMutation);
    }

    [Fact]
    public async Task OnboardingWorkflow_RejectsInvalidMarkers_AndCannotReadOrRevokeUnownedCodes()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/onboarding";
        var unknown = Guid.NewGuid();

        var invalid = await client.PostAsJsonAsync($"{root}/enrollment-codes", new CreateDevelopmentMcpEnrollmentCodeRequest(4, "not-a-campaign-marker"));
        var getUnknown = await client.GetAsync($"{root}/enrollment-codes/{unknown:D}");
        var revokeUnknown = await client.PostAsync($"{root}/enrollment-codes/{unknown:D}/revoke", null);
        var rawInput = await client.PostAsJsonAsync($"{root}/enrollment-codes/{unknown:D}/input", new { code = "caller-supplied" });

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        getUnknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        revokeUnknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        rawInput.StatusCode.Should().Be(HttpStatusCode.NotFound);
        app.Services.GetRequiredService<FakeEnrollmentCodes>().Issues.Should().BeEmpty();
        app.Services.GetRequiredService<FakeEnrollmentCodes>().Revocations.Should().BeEmpty();
    }

    private static HttpClient AuthorizedClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(Guid agentId)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddHttpContextAccessor();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
                services.AddSingleton(new TestTargetAuthority(agentId));
                services.AddSingleton<IDevelopmentOperatorTargetAuthority>(provider => provider.GetRequiredService<TestTargetAuthority>());
                services.AddSingleton<FakeEnrollmentCodes>();
                services.AddSingleton<IEnrollmentCodeIssueService>(provider => provider.GetRequiredService<FakeEnrollmentCodes>());
                services.AddSingleton<IClientArtifactsService, FakeArtifacts>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapDevelopmentMcpOnboardingEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class TestTargetAuthority(Guid agentId) : IDevelopmentOperatorTargetAuthority
    {
        private readonly DevelopmentOperatorTargetGrantView _grant = new(Guid.NewGuid(), 3, agentId, DevelopmentOperatorTargetClassification.DedicatedQa, DevelopmentOperatorOperationScope.Onboarding, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), true, "test-evidence");
        public List<DevelopmentOperatorOperation> AcceptedOperations { get; } = [];
        public Task<DevelopmentOperatorTargetGrantView> GrantAsync(DevelopmentOperatorTargetGrantRequest request, CancellationToken cancellationToken) => Task.FromResult(_grant);
        public Task<DevelopmentOperatorTargetGrantView?> RevokeAsync(int tenantId, Guid requestedAgentId, string reason, string actorId, string correlationId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(null);
        public Task<DevelopmentOperatorTargetGrantView?> GetActiveGrantAsync(int tenantId, Guid requestedAgentId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(tenantId == 3 && requestedAgentId == agentId ? _grant : null);
        public Task<DevelopmentOperatorTargetDecision> EvaluateAsync(DevelopmentOperatorTargetRequest request, CancellationToken cancellationToken)
        {
            var allowed = request.TenantId == 3 && request.AgentId == agentId;
            return Task.FromResult(new DevelopmentOperatorTargetDecision(allowed, allowed ? null : "target_not_authorized", allowed ? _grant.GrantId : null, request));
        }
        public Task<DevelopmentOperatorAcceptedAudit> RecordAcceptedAsync(DevelopmentOperatorTargetDecision decision, CancellationToken cancellationToken)
        {
            AcceptedOperations.Add(decision.Request.Operation);
            return Task.FromResult(new DevelopmentOperatorAcceptedAudit(Guid.NewGuid(), decision.Request.TenantId, decision.Request.AgentId, decision.Request.Operation, decision.Request.ActorId, decision.Request.CorrelationId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeEnrollmentCodes : IEnrollmentCodeIssueService
    {
        public List<EnrollmentCodeIssueRequest> Issues { get; } = [];
        public List<(Guid EnrollmentCodeId, int TenantId)> Revocations { get; } = [];
        private readonly Dictionary<Guid, DevelopmentMcpEnrollmentOwnershipRecord> _ownerships = [];
        public Task<EnrollmentCodeIssueResult> IssueAsync(EnrollmentCodeIssueRequest request, CancellationToken ct)
        {
            Issues.Add(request);
            var id = Guid.NewGuid();
            var created = DateTimeOffset.UtcNow;
            _ownerships[id] = new DevelopmentMcpEnrollmentOwnershipRecord(id, request.TenantId, request.DevelopmentMcpTargetAgentId!.Value, request.DevelopmentMcpMarker!, created, created.AddMinutes(request.ValidForMinutes), request.MaxUses, 0, null);
            return Task.FromResult(new EnrollmentCodeIssueResult(id, request.TenantId, "test-code-value", created, created.AddMinutes(request.ValidForMinutes)));
        }
        public Task<PrimaryClientEnrollmentIssueResult> IssueForPrimaryClientBindingAsync(PrimaryClientEnrollmentIssueRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EnrollmentCodeIssueResult> GetActiveAsync(Guid enrollmentCodeId, int tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DevelopmentMcpEnrollmentOwnershipRecord?> GetDevelopmentMcpOwnershipAsync(Guid enrollmentCodeId, int tenantId, Guid targetAgentId, CancellationToken ct)
        {
            return Task.FromResult(_ownerships.TryGetValue(enrollmentCodeId, out var ownership) && ownership.TenantId == tenantId && ownership.TargetAgentId == targetAgentId ? ownership : null);
        }
        public Task<EnrollmentCodeIssueResult> ValidateActiveCodeAsync(string enrollmentCode, int tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EnrollmentCodeListItem>> ListAsync(int tenantId, string? status, string? search, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAsync(Guid enrollmentCodeId, int tenantId, string? actor, string? reason, CancellationToken ct)
        {
            Revocations.Add((enrollmentCodeId, tenantId));
            if (_ownerships.TryGetValue(enrollmentCodeId, out var ownership)) _ownerships[enrollmentCodeId] = ownership with { RevokedAtUtc = DateTimeOffset.UtcNow };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifacts : IClientArtifactsService
    {
        public Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct) => Task.FromResult<ClientArtifactSummaryDto?>(new ClientArtifactSummaryDto { Rid = rid, Version = "1.2.3-test", FileName = "netratel-client.zip", Size = 4096, Sha256 = "test-sha", UploadedAt = DateTimeOffset.UtcNow });
        public Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-admin")], Scheme.Name)), Scheme.Name)));
    }
}
