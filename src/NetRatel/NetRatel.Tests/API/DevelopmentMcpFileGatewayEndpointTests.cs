using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.API.Services.Events;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DevelopmentMcpFileGatewayEndpointTests
{
    [Fact]
    public async Task Browse_AllowsOnlyThePersistedFixtureRoot_AndRecordsAcceptedAudit()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);

        var accepted = await client.GetAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/files?path=%2Ftmp%2Fnetratel-mcp-qa");
        var rejected = await client.GetAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/files?path=%2Ftmp%2Fnetratel-mcp-qa-other");

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await accepted.Content.ReadFromJsonAsync<FileBrowseResponse>();
        payload!.Authority.Should().Be("development-mcp-fixture");
        payload.Entries.Should().ContainSingle(entry => entry.FullPath == "/tmp/netratel-mcp-qa/marker.txt");
        accepted.Headers.Should().Contain(header => header.Key == "X-Development-Operation-Audit-Id");
        rejected.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("fixture_path_not_authorized");
        app.Services.GetRequiredService<TargetAuthority>().AcceptedOperations.Should().Equal(DevelopmentOperatorOperation.FileBrowse);
        app.Services.GetRequiredService<FileRegistry>().ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task InlineRead_ReturnsBoundedUtf8TextAndHash()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, "fixture text");
        var response = await AuthorizedClient(app).GetAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/files/inline?path=%2Ftmp%2Fnetratel-mcp-qa%2Fmarker.txt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<FileReadResponse>();
        payload!.Content.Should().Be("fixture text");
        payload.SizeBytes.Should().Be(12);
        payload.Sha256.Should().Be("5cb72f90e968922d30557d0af8f719d21f61792becaa87eb32477767d739dc0b");
        app.Services.GetRequiredService<TargetAuthority>().AcceptedOperations.Should().Equal(DevelopmentOperatorOperation.FileRead);
    }

    [Fact]
    public async Task InlineRead_RejectsOversizedContentBeforeReturningBytes_AndCancelsOnce()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, new string('x', 64 * 1024 + 1));
        var response = await AuthorizedClient(app).GetAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/files/inline?path=%2Ftmp%2Fnetratel-mcp-qa%2Flarge.txt");

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Contain("inline_read_too_large");
        app.Services.GetRequiredService<FileRegistry>().CancelReasons.Should().Equal("development_inline_read_too_large");
    }

    [Fact]
    public async Task InlineRead_RejectsNonUtf8FixtureContent()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, new byte[] { 0xff, 0xfe });
        var response = await AuthorizedClient(app).GetAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/files/inline?path=%2Ftmp%2Fnetratel-mcp-qa%2Fbinary.bin");

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await response.Content.ReadAsStringAsync()).Should().Contain("inline_text_required");
    }

    [Fact]
    public async Task FixtureReads_RejectTraversalAndAbsentTargetGrantBeforeGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/files";

        var traversal = await client.GetAsync($"{root}?path=%2Ftmp%2Fnetratel-mcp-qa%2F..%2Fsecrets.txt");
        app.Services.GetRequiredService<TargetAuthority>().Enabled = false;
        var noGrant = await client.GetAsync($"{root}/inline?path=%2Ftmp%2Fnetratel-mcp-qa%2Fmarker.txt");

        traversal.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await traversal.Content.ReadAsStringAsync()).Should().Contain("fixture_path_not_authorized");
        noGrant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noGrant.Content.ReadAsStringAsync()).Should().Contain("file_fixture_not_authorized");
        app.Services.GetRequiredService<FileRegistry>().ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task FileGatewayDisabled_StartsAndFailsClosedWithoutAFileGatewayService()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, registerFileGateway: false, fileBrowseAuthorityEnabled: true);

        var response = await AuthorizedClient(app).GetAsync(
            $"/api/v2/development/mcp/agents/3/{agentId:D}/files?path=%2Ftmp%2Fnetratel-mcp-qa");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ArtifactCollection_DownloadAndMarkerCleanup_AreBoundedAndAudited()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, "fixture artifact");
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/files";

        var collect = await client.PostAsJsonAsync($"{root}/collect", new { path = "/tmp/netratel-mcp-qa/MCP-QA-file-artifact.txt", marker = "MCP-QA-file-artifact" });
        collect.StatusCode.Should().Be(HttpStatusCode.Created);
        var artifact = await collect.Content.ReadFromJsonAsync<ArtifactMetadata>();
        artifact!.MarkerOwned.Should().BeTrue();
        artifact.IsAvailable.Should().BeTrue();
        artifact.ArtifactId.Should().NotBe(Guid.Empty);
        using (var scope = app.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().DevelopmentMcpFileArtifacts.CountAsync()).Should().Be(1);
        }

        var status = await client.GetAsync($"{root}/artifacts/{artifact.ArtifactId:D}");
        var download = await client.GetAsync($"{root}/artifacts/{artifact.ArtifactId:D}/download");
        var cleanup = await client.DeleteAsync($"{root}/artifacts/{artifact.ArtifactId:D}");
        var duplicateCleanup = await client.DeleteAsync($"{root}/artifacts/{artifact.ArtifactId:D}");

        status.StatusCode.Should().Be(HttpStatusCode.OK);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture artifact")));
        cleanup.StatusCode.Should().Be(HttpStatusCode.OK);
        duplicateCleanup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicateCleanup.Content.ReadAsStringAsync()).Should().Contain("artifact_already_cleaned");
        app.Services.GetRequiredService<TargetAuthority>().AcceptedOperations.Should().Equal(
            DevelopmentOperatorOperation.FileCollect,
            DevelopmentOperatorOperation.FileArtifactStatus,
            DevelopmentOperatorOperation.FileArtifactDownload,
            DevelopmentOperatorOperation.FileArtifactCleanup);
    }

    [Fact]
    public async Task ArtifactCollection_RejectsNonMarkerAndOversizedFixtureContent()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, new string('x', 512 * 1024 + 1));
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/files/collect";

        var nonMarker = await client.PostAsJsonAsync(root, new { path = "/tmp/netratel-mcp-qa/not-a-marker.txt", marker = "MCP-QA-file-artifact" });
        var oversized = await client.PostAsJsonAsync(root, new { path = "/tmp/netratel-mcp-qa/MCP-QA-file-artifact.txt", marker = "MCP-QA-file-artifact" });

        nonMarker.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await nonMarker.Content.ReadAsStringAsync()).Should().Contain("marker_artifact_required");
        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await oversized.Content.ReadAsStringAsync()).Should().Contain("artifact_too_large");
        app.Services.GetRequiredService<FileRegistry>().ReadCalls.Should().Be(1);
        app.Services.GetRequiredService<FileRegistry>().CancelReasons.Should().Equal("development_artifact_too_large");
    }

    [Fact]
    public async Task ArtifactStatus_RejectsCrossTargetAndExpiredArtifactsWithoutReturningContent()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, "fixture artifact");
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/files";
        var collect = await client.PostAsJsonAsync($"{root}/collect", new { path = "/tmp/netratel-mcp-qa/MCP-QA-file-artifact.txt", marker = "MCP-QA-file-artifact" });
        var artifact = await collect.Content.ReadFromJsonAsync<ArtifactMetadata>();

        var wrongTarget = await client.GetAsync($"/api/v2/development/mcp/agents/3/{Guid.NewGuid():D}/files/artifacts/{artifact!.ArtifactId:D}");
        var wrongTenant = await client.GetAsync($"/api/v2/development/mcp/agents/4/{agentId:D}/files/artifacts/{artifact.ArtifactId:D}");
        using (var scope = app.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().DevelopmentMcpFileArtifacts.SingleAsync();
            stored.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().SaveChangesAsync();
        }
        var retention = new DevelopmentMcpFileArtifactRetentionService(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DevelopmentMcpFileArtifactRetentionService>.Instance);
        (await retention.PurgeExpiredAsync(CancellationToken.None)).Should().Be(1);
        using (var scope = app.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().DevelopmentMcpFileArtifacts.SingleAsync();
            stored.Content.Should().BeEmpty();
            stored.DeletedAtUtc.Should().NotBeNull();
        }
        var expired = await client.GetAsync($"{root}/artifacts/{artifact.ArtifactId:D}/download");

        wrongTarget.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrongTenant.StatusCode.Should().Be(HttpStatusCode.NotFound);
        expired.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await expired.Content.ReadAsStringAsync()).Should().Contain("artifact_expired").And.NotContain("contentBase64");
    }

    private static HttpClient AuthorizedClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(Guid agentId, string content = "fixture text", bool registerFileGateway = true, bool? fileBrowseAuthorityEnabled = null) =>
        await BuildAppAsync(agentId, Encoding.UTF8.GetBytes(content), registerFileGateway, fileBrowseAuthorityEnabled);

    private static async Task<IHost> BuildAppAsync(Guid agentId, byte[] content, bool registerFileGateway = true, bool? fileBrowseAuthorityEnabled = null)
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = $"development-mcp-files-{Guid.NewGuid():N}";
        var options = new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            FileGatewayEnabled = registerFileGateway,
            FileBrowseAuthorityEnabled = fileBrowseAuthorityEnabled ?? registerFileGateway
        };
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
                services.AddAuthorization(policyOptions => policyOptions.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton(options);
                services.AddDbContext<OrchestratorDbContext>(dbOptions => dbOptions.UseInMemoryDatabase(databaseName, databaseRoot));
                if (registerFileGateway)
                {
                    services.AddSingleton(new FileRegistry(content));
                    services.AddSingleton<IAgentFileGatewaySessionRegistry>(provider => provider.GetRequiredService<FileRegistry>());
                }
                services.AddSingleton(new TargetAuthority(agentId));
                services.AddSingleton<IDevelopmentOperatorTargetAuthority>(provider => provider.GetRequiredService<TargetAuthority>());
                services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapDevelopmentMcpFileGatewayEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class FileRegistry(byte[] content) : IAgentFileGatewaySessionRegistry
    {
        public int ListCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public List<string> CancelReasons { get; } = [];

        public AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch) => throw new NotSupportedException();

        public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<GatewayFileEntry>>([new("marker.txt", "/tmp/netratel-mcp-qa/marker.txt", false, content.Length)]);
        }

        public Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, CancellationToken cancellationToken)
        {
            ReadCalls++;
            var chunks = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
            chunks.Writer.TryWrite(content);
            chunks.Writer.TryComplete();
            return Task.FromResult(new GatewayFileReadOperation(
                chunks.Reader,
                Task.CompletedTask,
                (reason, _) =>
                {
                    CancelReasons.Add(reason);
                    return Task.CompletedTask;
                }));
        }

        public Task WriteAsync(ClientKey client, string path, Stream source, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool TryAccept(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileRequestAccepted accepted) => false;
        public bool TryAddPage(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileListPage page) => false;
        public Task<bool> TryAddReadChunkAsync(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileTransferChunk chunk, CancellationToken cancellationToken) => Task.FromResult(false);
        public bool TryComplete(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileRequestCompleted completed) => false;
        public bool TryFail(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileRequestFailed failed) => false;
    }

    private sealed class TargetAuthority(Guid agentId) : IDevelopmentOperatorTargetAuthority
    {
        private readonly DevelopmentOperatorTargetGrantView _grant = new(
            Guid.NewGuid(), 3, agentId, DevelopmentOperatorTargetClassification.DedicatedQa,
            DevelopmentOperatorOperationScope.FileSystem, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), true,
            "test-evidence", "/tmp/netratel-mcp-qa");

        public List<DevelopmentOperatorOperation> AcceptedOperations { get; } = [];
        public bool Enabled { get; set; } = true;
        public Task<DevelopmentOperatorTargetGrantView> GrantAsync(DevelopmentOperatorTargetGrantRequest request, CancellationToken cancellationToken) => Task.FromResult(_grant);
        public Task<DevelopmentOperatorTargetGrantView?> RevokeAsync(int tenantId, Guid agentId, string reason, string actorId, string correlationId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(null);
        public Task<DevelopmentOperatorTargetGrantView?> GetActiveGrantAsync(int tenantId, Guid requestedAgentId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(Enabled && tenantId == 3 && requestedAgentId == agentId ? _grant : null);
        public Task<DevelopmentOperatorTargetDecision> EvaluateAsync(DevelopmentOperatorTargetRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DevelopmentOperatorTargetDecision(Enabled, Enabled ? null : "target_not_authorized", Enabled ? _grant.GrantId : null, request));
        public Task<DevelopmentOperatorAcceptedAudit> RecordAcceptedAsync(DevelopmentOperatorTargetDecision decision, CancellationToken cancellationToken)
        {
            AcceptedOperations.Add(decision.Request.Operation);
            return Task.FromResult(new DevelopmentOperatorAcceptedAudit(Guid.NewGuid(), decision.Request.TenantId, decision.Request.AgentId, decision.Request.Operation, decision.Request.ActorId, decision.Request.CorrelationId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "test-admin")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed record FileBrowseResponse(string Authority, IReadOnlyList<FileEntry> Entries);
    private sealed record FileEntry(string FullPath);
    private sealed record FileReadResponse(string Content, int SizeBytes, string Sha256);
    private sealed record ArtifactMetadata(Guid ArtifactId, bool MarkerOwned, bool IsAvailable);
}
