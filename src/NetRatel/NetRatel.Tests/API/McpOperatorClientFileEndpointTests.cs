using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
using Microsoft.Extensions.Options;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorClientFileEndpointTests
{
    [Fact]
    public async Task ProductionBrowse_BindsTheExactDelegation_AndPassesTheMatchingReadRootToTheGateway()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse?path=%2Fvar%2Flog&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("agent.log").And.Contain("mcp-operator-policy");
        app.Services.GetRequiredService<TestFileRegistry>().ListPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/log");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "browse");
    }

    [Fact]
    public async Task Browse_EnforcesTheRequestedLimitAcrossGatewayPages()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        app.Services.GetRequiredService<TestFileRegistry>().ListedEntries = Enumerable.Range(0, 22)
            .Select(index => new GatewayFileEntry($"{index}.log", $"/var/log/{index}.log", false, 1)).ToArray();
        var response = await AuthorizedClient(app).GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse?path=%2Fvar%2Flog&pageSize=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("entries").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task ProductionRead_ReturnsOnlyTheBoundedContentHashAndBase64Payload()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/read?path=%2Fvar%2Flog%2Fagent.log");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("aGVhbHRoeS1nYXRld2F5").And.Contain("sha256").And.Contain("text/plain; charset=utf-8");
        app.Services.GetRequiredService<TestFileRegistry>().ReadPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/log");
    }

    [Fact]
    public async Task ProductionArtifactLifecycle_IsCallerBoundRootBoundAndByteWipedOnConfirmedCleanup()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/artifacts";

        var collectPreview = await client.PostAsJsonAsync($"{root}/collect/preview", new { path = "/var/log/agent.log" });
        var collect = await client.PostAsJsonAsync($"{root}/collect/confirm", new
        {
            path = "/var/log/agent.log",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });
        var artifact = await collect.Content.ReadFromJsonAsync<ArtifactMetadata>();
        var status = await client.GetAsync($"{root}/{artifact!.ArtifactId:D}");
        var download = await client.GetAsync($"{root}/{artifact.ArtifactId:D}/download");
        var cleanupPreview = await client.PostAsync($"{root}/{artifact.ArtifactId:D}/cleanup/preview", null);
        var cleanup = await client.PostAsJsonAsync($"{root}/{artifact.ArtifactId:D}/cleanup/confirm", new
        {
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });
        var unavailable = await client.GetAsync($"{root}/{artifact.ArtifactId:D}");

        collectPreview.StatusCode.Should().Be(HttpStatusCode.OK);
        collect.StatusCode.Should().Be(HttpStatusCode.Created);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Contain("aGVhbHRoeS1nYXRld2F5").And.Contain("sha256");
        cleanupPreview.StatusCode.Should().Be(HttpStatusCode.OK);
        cleanup.StatusCode.Should().Be(HttpStatusCode.OK);
        unavailable.StatusCode.Should().Be(HttpStatusCode.Gone);
        app.Services.GetRequiredService<TestFileRegistry>().ReadPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/log");
        app.Services.GetRequiredService<TestFileRegistry>().StatPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/log");
        app.Services.GetRequiredService<TestArtifactStore>().Artifacts.Should().ContainSingle()
            .Which.Content.Should().BeNull();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Select(request => request.Operation)
            .Should().Equal("collect", "artifact_status", "download", "artifact_cleanup");
    }

    [Fact]
    public async Task ProductionStat_BindsTheExactDelegationAndReturnsMetadataWithoutFileContent()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/stat?path=%2Fvar%2Flog%2Fagent.log");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("agent.log").And.Contain("lastModifiedUtc").And.NotContain("contentBase64");
        app.Services.GetRequiredService<TestFileRegistry>().StatPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/log");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "stat");
    }

    [Fact]
    public async Task ProductionFileRead_WithoutAnExplicitReadRoot_IsRejectedBeforeAcceptedAuditOrGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: false);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/read?path=%2Fvar%2Flog%2Fagent.log");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        app.Services.GetRequiredService<TestFileRegistry>().ReadPolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionFileRead_RejectsASiblingPrefixBeforeGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/read?path=%2Fvar%2Flogger%2Fagent.log");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        app.Services.GetRequiredService<TestFileRegistry>().ReadPolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionWriteText_UsesExactPreviewConfirmationAndTheWriteRootBoundGateway()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/write-text";

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { path = "/var/lib/netratel/agent.txt", text = "healthy" });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path = "/var/lib/netratel/agent.txt",
            text = "healthy",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await preview.Content.ReadAsStringAsync()).Should().Contain("agent.txt").And.Contain("sha256");
        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        (await confirmation.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":false");
        app.Services.GetRequiredService<TestFileRegistry>().WritePolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/lib/netratel");
        Encoding.UTF8.GetString(app.Services.GetRequiredService<TestFileRegistry>().WrittenContent!).Should().Be("healthy");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "write_text");
    }

    [Fact]
    public async Task ProductionCreateDirectory_UsesExactPreviewConfirmationAndTheWriteRootBoundGateway()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/create-directory";

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { path = "/var/lib/netratel/exports" });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path = "/var/lib/netratel/exports",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await preview.Content.ReadAsStringAsync()).Should().Contain("exports").And.Contain("sha256");
        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        (await confirmation.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":false");
        app.Services.GetRequiredService<TestFileRegistry>().CreateDirectoryPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/lib/netratel");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "create_directory");
    }

    [Fact]
    public async Task ProductionCreateDirectory_ReplaysTheStoredReceiptWithoutANewGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/create-directory";
        var confirmation = new
        {
            path = "/var/lib/netratel/exports",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        (await client.PostAsJsonAsync($"{root}/preview", new { confirmation.path })).StatusCode.Should().Be(HttpStatusCode.OK);
        var initial = await client.PostAsJsonAsync($"{root}/confirm", confirmation);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", confirmation);

        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestFileRegistry>().CreateDirectoryPolicies.Should().ContainSingle();
    }

    [Fact]
    public async Task ProductionDelete_UsesExactDestructivePreviewConfirmationAndTheWriteRootBoundGateway()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete";

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { path = "/var/lib/netratel/exports/marker.txt" });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path = "/var/lib/netratel/exports/marker.txt",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await preview.Content.ReadAsStringAsync()).Should().Contain("marker.txt").And.Contain("\"confirmationClass\":3");
        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        (await confirmation.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":false");
        app.Services.GetRequiredService<TestFileRegistry>().DeletePolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/lib/netratel");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "delete");
    }

    [Fact]
    public async Task ProductionDelete_ReplaysTheStoredReceiptWithoutANewGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete";
        var confirmation = new
        {
            path = "/var/lib/netratel/exports/marker.txt",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        (await client.PostAsJsonAsync($"{root}/preview", new { confirmation.path })).StatusCode.Should().Be(HttpStatusCode.OK);
        var initial = await client.PostAsJsonAsync($"{root}/confirm", confirmation);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", confirmation);

        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestFileRegistry>().DeletePolicies.Should().ContainSingle();
    }

    [Fact]
    public async Task ProductionDelete_WithoutAnExplicitWriteRoot_IsRejectedBeforeIssuingAPlanOrDispatching()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: false);
        var client = AuthorizedClient(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete/preview",
            new { path = "/var/lib/netratel/exports/marker.txt" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        app.Services.GetRequiredService<TestFileRegistry>().DeletePolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionDelete_RejectsTheExactApprovedWriteRootBeforeIssuingAPlanOrDispatching()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete/preview",
            new { path = "/var/lib/netratel" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        app.Services.GetRequiredService<TestFileRegistry>().DeletePolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionDelete_RejectsAnInnerApprovedWriteRootEvenWhenItIsBelowAnotherWriteRoot()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        app.Services.GetRequiredService<TestAdmission>().WriteRootsOverride =
            ["/var/lib/netratel", "/var/lib/netratel/exports"];
        var client = AuthorizedClient(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete/preview",
            new { path = "/var/lib/netratel/exports" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        app.Services.GetRequiredService<TestFileRegistry>().DeletePolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("path_not_found", HttpStatusCode.NotFound)]
    [InlineData("directory_not_empty", HttpStatusCode.Conflict)]
    public async Task ProductionDelete_ReturnsTheGatewayTargetStateWithoutReclassifyingItAsAPolicyFailure(
        string gatewayFailure,
        HttpStatusCode expectedStatus)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete";
        app.Services.GetRequiredService<TestFileRegistry>().DeleteFailureCode = gatewayFailure;

        (await client.PostAsJsonAsync($"{root}/preview", new { path = "/var/lib/netratel/exports/marker.txt" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path = "/var/lib/netratel/exports/marker.txt",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        response.StatusCode.Should().Be(expectedStatus);
        (await response.Content.ReadAsStringAsync()).Should().Contain(gatewayFailure);
    }

    [Fact]
    public async Task ProductionCopy_UsesSeparateReadAndWriteRootPolicies_AndReplaysWithoutANewDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/copy";
        var confirmation = new
        {
            sourcePath = "/var/log/agent.log",
            destinationPath = "/var/lib/netratel/exports/agent-copy.log",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { confirmation.sourcePath, confirmation.destinationPath });
        var initial = await client.PostAsJsonAsync($"{root}/confirm", confirmation);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", confirmation);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await preview.Content.ReadAsStringAsync()).Should().Contain("sourcePath").And.Contain("destinationPath");
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestFileRegistry>().CopyPolicies.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new GatewayFileMoveCopyAccessPolicy(["/var/log"], ["/var/lib/netratel"]));
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "copy");
    }

    [Fact]
    public async Task ProductionMove_UsesWriteRootsAtBothEndsAndRejectsPolicyRootSources()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/move";

        var preview = await client.PostAsJsonAsync($"{root}/preview", new
        {
            sourcePath = "/var/lib/netratel/exports/old.log",
            destinationPath = "/var/lib/netratel/exports/new.log"
        });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            sourcePath = "/var/lib/netratel/exports/old.log",
            destinationPath = "/var/lib/netratel/exports/new.log",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });
        var rootSource = await client.PostAsJsonAsync($"{root}/preview", new
        {
            sourcePath = "/var/lib/netratel",
            destinationPath = "/var/lib/netratel/exports/newer.log"
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        rootSource.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        app.Services.GetRequiredService<TestFileRegistry>().MovePolicies.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new GatewayFileMoveCopyAccessPolicy(["/var/lib/netratel"], ["/var/lib/netratel"]));
    }

    [Fact]
    public async Task ProductionCopy_WithoutAnExplicitReadRoot_IsRejectedBeforeIssuingAPlanOrDispatching()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: false, includeWriteRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/copy/preview",
            new { sourcePath = "/var/log/agent.log", destinationPath = "/var/lib/netratel/exports/agent-copy.log" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        app.Services.GetRequiredService<TestFileRegistry>().CopyPolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionWriteText_ChangedContentAfterPreview_IsRejectedWithoutGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/write-text";

        await client.PostAsJsonAsync($"{root}/preview", new { path = "/var/lib/netratel/agent.txt", text = "healthy" });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path = "/var/lib/netratel/agent.txt",
            text = "changed",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        confirmation.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await confirmation.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        app.Services.GetRequiredService<TestFileRegistry>().WritePolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionWriteText_WithoutAnExplicitWriteRoot_IsRejectedBeforeIssuingAPlanOrDispatching()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: false);
        var client = AuthorizedClient(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/write-text/preview",
            new { path = "/var/lib/netratel/agent.txt", text = "healthy" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        app.Services.GetRequiredService<TestFileRegistry>().WritePolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionWriteText_ReplayReturnsTheReceiptWithoutASecondGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/write-text";
        var request = new
        {
            path = "/var/lib/netratel/agent.txt",
            text = "healthy",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        await client.PostAsJsonAsync($"{root}/preview", new { request.path, request.text });
        var initial = await client.PostAsJsonAsync($"{root}/confirm", request);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", request);

        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestFileRegistry>().WritePolicies.Should().ContainSingle();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle();
    }

    [Fact]
    public async Task ProductionUpload_UsesTheExactPreviewConfirmationAndBoundedBinaryGatewayWrite()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/upload";
        var contentBase64 = Convert.ToBase64String(new byte[] { 0, 1, 2, 255 });

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { path = "/var/lib/netratel/agent.bin", contentBase64 });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path = "/var/lib/netratel/agent.bin",
            contentBase64,
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmation.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Services.GetRequiredService<TestFileRegistry>().WritePolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal("/var/lib/netratel");
        app.Services.GetRequiredService<TestFileRegistry>().WrittenContent.Should().Equal((byte)0, 1, 2, 255);
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request =>
            request.Tool == "netratel_files" && request.Operation == "upload");
    }

    [Fact]
    public async Task ProductionUpload_RejectsNonCanonicalBase64BeforePolicyAuditOrGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var client = AuthorizedClient(app);

        var response = await client.PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/upload/preview",
            new { path = "/var/lib/netratel/agent.bin", contentBase64 = "AQI" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Services.GetRequiredService<TestFileRegistry>().WritePolicies.Should().BeEmpty();
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    private static HttpClient AuthorizedClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(Guid agentId, bool includeReadRoot, bool includeWriteRoot = true)
    {
        var client = new ClientKey(7, agentId);
        var options = new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            FileGatewayEnabled = true,
            FileBrowseAuthorityEnabled = true
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Production);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
                services.AddAuthorization(policyOptions => policyOptions.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton(options);
                services.AddSingleton(new TestPresence(client));
                services.AddSingleton<IClientPresenceRouter>(provider => provider.GetRequiredService<TestPresence>());
                services.AddSingleton(new TestAdmission(includeReadRoot, includeWriteRoot));
                services.AddSingleton<IMcpOperatorRouteAdmission>(provider => provider.GetRequiredService<TestAdmission>());
                services.AddSingleton<TestConfirmations>();
                services.AddSingleton<IMcpOperatorConfirmationService>(provider => provider.GetRequiredService<TestConfirmations>());
                services.AddSingleton<TestArtifactStore>();
                services.AddSingleton<IMcpOperatorFileArtifactStore>(provider => provider.GetRequiredService<TestArtifactStore>());
                services.AddSingleton<TestFileRegistry>();
                services.AddSingleton<IAgentFileGatewaySessionRegistry>(provider => provider.GetRequiredService<TestFileRegistry>());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.Use(async (http, next) =>
                {
                    var operation = http.Request.Path.Value! switch
                    {
                        var path when path.EndsWith("/files/browse", StringComparison.Ordinal) => "browse",
                        var path when path.EndsWith("/files/stat", StringComparison.Ordinal) => "stat",
                        var path when path.EndsWith("/files/read", StringComparison.Ordinal) => "read",
                        var path when path.EndsWith("/artifacts/collect/preview", StringComparison.Ordinal) => "preview_collect",
                        var path when path.EndsWith("/artifacts/collect/confirm", StringComparison.Ordinal) => "confirm_collect",
                        var path when path.EndsWith("/cleanup/preview", StringComparison.Ordinal) => "preview_artifact_cleanup",
                        var path when path.EndsWith("/cleanup/confirm", StringComparison.Ordinal) => "confirm_artifact_cleanup",
                        var path when path.EndsWith("/download", StringComparison.Ordinal) => "download",
                        var path when path.Contains("/artifacts/", StringComparison.Ordinal) => "artifact_status",
                        var path when path.EndsWith("/files/write-text/preview", StringComparison.Ordinal) => "preview_write_text",
                        var path when path.EndsWith("/files/write-text/confirm", StringComparison.Ordinal) => "confirm_write_text",
                        var path when path.EndsWith("/files/upload/preview", StringComparison.Ordinal) => "preview_upload",
                        var path when path.EndsWith("/files/upload/confirm", StringComparison.Ordinal) => "confirm_upload",
                        var path when path.EndsWith("/files/create-directory/preview", StringComparison.Ordinal) => "preview_create_directory",
                        var path when path.EndsWith("/files/create-directory/confirm", StringComparison.Ordinal) => "confirm_create_directory",
                        var path when path.EndsWith("/files/delete/preview", StringComparison.Ordinal) => "preview_delete",
                        var path when path.EndsWith("/files/delete/confirm", StringComparison.Ordinal) => "confirm_delete",
                        var path when path.EndsWith("/files/copy/preview", StringComparison.Ordinal) => "preview_copy",
                        var path when path.EndsWith("/files/copy/confirm", StringComparison.Ordinal) => "confirm_copy",
                        var path when path.EndsWith("/files/move/preview", StringComparison.Ordinal) => "preview_move",
                        _ => "confirm_move"
                    };
                    http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                        new McpOperatorDelegationIdentity("operator-1", "operator-client", null, [], ["Operator"], ["netratel.mcp.files", "netratel.mcp.write"]),
                        "netratel-mcp-http",
                        "netratel_files",
                        operation,
                        "request-1",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        "https://mcp.example",
                        "prod",
                        client.TenantId,
                        client.AgentId,
                        "correlation-1");
                    await next();
                });
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMcpOperatorClientFileEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "mcp-service")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class TestPresence(ClientKey client) : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey value, CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceSnapshot(
            value,
            value == client ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "1.0",
            ["files"],
            null,
            "test",
            true));
    }

    private sealed class TestAdmission(bool includeReadRoot, bool includeWriteRoot) : IMcpOperatorRouteAdmission
    {
        public ConcurrentQueue<McpOperatorRouteAccessRequest> Accepted { get; } = [];
        public IReadOnlyList<string>? WriteRootsOverride { get; set; }
        public IReadOnlyList<string>? ReadRootsOverride { get; set; }

        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            var access = new McpOperatorAccessRequest(
                request.Environment,
                request.Principal,
                request.TenantId,
                request.AgentId,
                null,
                IsFileWriteOperation(request.Operation) ? McpOperatorOperationFamily.FileWrite : McpOperatorOperationFamily.FileRead,
                $"{request.Tool}/{request.Operation}",
                request.RequiredScopes,
                request.Operation is "write_text" or "upload" or "move" or "artifact_cleanup" ? McpOperatorConfirmationClass.Destructive : request.Operation is "copy" or "collect" ? McpOperatorConfirmationClass.StandardMutation : McpOperatorConfirmationClass.None,
                request.CorrelationId,
                request.RequestId,
                McpResource: request.McpResource,
                McpInstance: request.McpInstance,
                Tool: request.Tool,
                TargetOnline: request.TargetOnline,
                CapabilityAvailable: request.CapabilityAvailable);
            var decision = new McpOperatorDecision(
                true,
                null,
                null,
                [Guid.Parse("1e31dfe8-a8ff-42f2-9236-21f7d1a8a78f")],
                new McpOperatorConstraints(
                    ReadRoots: ReadRootsOverride ?? (includeReadRoot ? ["/var/log"] : null),
                    WriteRoots: WriteRootsOverride ?? (includeWriteRoot ? ["/var/lib/netratel"] : null)),
                "target-digest",
                access,
                1);
            return Task.FromResult(new McpOperatorRouteAdmission(decision, McpOperatorOperationCatalog.Find(request.Tool, request.Operation)));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            Accepted.Enqueue(request);
            return Task.FromResult(new McpOperatorAcceptedAudit(
                Guid.NewGuid(),
                Guid.NewGuid(),
                request.Environment,
                request.ServicePrincipal,
                request.Principal.Subject,
                request.Principal.ClientId,
                request.Principal.AuthorizedParty,
                [],
                [],
                request.Principal.Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToArray(),
                request.McpResource,
                request.McpInstance,
                request.Tool,
                request.TenantId,
                request.AgentId,
                IsFileWriteOperation(request.Operation) ? McpOperatorOperationFamily.FileWrite : McpOperatorOperationFamily.FileRead,
                $"{request.Tool}/{request.Operation}",
                request.CorrelationId,
                request.RequestId,
                DateTimeOffset.UtcNow));
        }

        private static bool IsFileWriteOperation(string operation) => operation is
            "write_text" or "upload" or "create_directory" or "delete" or "copy" or "move" or "collect" or "artifact_cleanup";
    }

    private sealed class TestFileRegistry : IAgentFileGatewaySessionRegistry
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("healthy-gateway");

        public List<GatewayFileAccessPolicy> ListPolicies { get; } = [];
        public List<GatewayFileAccessPolicy> ReadPolicies { get; } = [];
        public List<GatewayFileAccessPolicy> StatPolicies { get; } = [];
        public List<GatewayFileAccessPolicy> CreateDirectoryPolicies { get; } = [];
        public List<GatewayFileAccessPolicy> DeletePolicies { get; } = [];
        public List<GatewayFileMoveCopyAccessPolicy> CopyPolicies { get; } = [];
        public List<GatewayFileMoveCopyAccessPolicy> MovePolicies { get; } = [];
        public List<GatewayFileAccessPolicy> WritePolicies { get; } = [];
        public byte[]? WrittenContent { get; private set; }
        public string? DeleteFailureCode { get; set; }
        public IReadOnlyList<GatewayFileEntry> ListedEntries { get; set; } = [new("agent.log", "/var/log/agent.log", false, Content.Length)];

        public AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(ListedEntries);
        public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            ListPolicies.Add(accessPolicy);
            return ListAsync(client, path, pageSize, cancellationToken);
        }

        public Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
            ReadAsync(client, path, new GatewayFileAccessPolicy([]), cancellationToken);

        public Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            ReadPolicies.Add(accessPolicy);
            var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
            channel.Writer.TryWrite(Content);
            channel.Writer.TryComplete();
            return Task.FromResult(new GatewayFileReadOperation(channel.Reader, Task.CompletedTask, (_, _) => Task.CompletedTask));
        }

        public Task<GatewayFileMetadata> StatAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayFileMetadata(path, false, Content.Length, new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero), "text/plain; charset=utf-8"));

        public Task<GatewayFileMetadata> StatAsync(ClientKey client, string path, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            StatPolicies.Add(accessPolicy);
            return StatAsync(client, path, cancellationToken);
        }

        public Task CreateDirectoryAsync(ClientKey client, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CreateDirectoryAsync(ClientKey client, string path, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            CreateDirectoryPolicies.Add(accessPolicy);
            return CreateDirectoryAsync(client, path, cancellationToken);
        }

        public Task DeleteAsync(ClientKey client, string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(ClientKey client, string path, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            DeletePolicies.Add(accessPolicy);
            if (DeleteFailureCode is { } failureCode)
                return Task.FromException(new AgentFileGatewayOperationException(failureCode));
            return DeleteAsync(client, path, cancellationToken);
        }

        public Task CopyAsync(ClientKey client, string sourcePath, string destinationPath, GatewayFileMoveCopyAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            CopyPolicies.Add(accessPolicy);
            return Task.CompletedTask;
        }

        public Task MoveAsync(ClientKey client, string sourcePath, string destinationPath, GatewayFileMoveCopyAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            MovePolicies.Add(accessPolicy);
            return Task.CompletedTask;
        }

        public Task WriteAsync(ClientKey client, string path, Stream source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task WriteAsync(ClientKey client, string path, Stream source, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            WritePolicies.Add(accessPolicy);
            await using var content = new MemoryStream();
            await source.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
            WrittenContent = content.ToArray();
        }
        public bool TryAccept(ClientKey client, FileRequestAccepted accepted) => false;
        public bool TryAddPage(ClientKey client, FileListPage page) => false;
        public Task<bool> TryAddReadChunkAsync(ClientKey client, FileTransferChunk chunk, CancellationToken cancellationToken) => Task.FromResult(false);
        public bool TryComplete(ClientKey client, FileRequestCompleted completed) => false;
        public bool TryFail(ClientKey client, FileRequestFailed failed) => false;
    }

    private sealed class TestConfirmations : IMcpOperatorConfirmationService
    {
        public const string PlanToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        public const string IdempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        private readonly Guid idempotencyId = Guid.Parse("b4f2fcc8-eb0a-43cb-bd77-31b1c00aee6a");
        private string? payloadHash;
        private readonly Dictionary<string, (McpOperatorIdempotencyOutcome Outcome, string? ResultReference)> completed = [];

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken)
        {
            payloadHash = request.PayloadHash;
            return Task.FromResult(new McpOperatorConfirmationPlan(
                PlanToken,
                IdempotencyKey,
                DateTimeOffset.UtcNow.AddMinutes(5),
                McpOperatorConfirmationClass.Destructive,
                request.PayloadHash,
                request.Decision.TargetSetDigest));
        }

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            if (!string.Equals(payloadHash, request.PayloadHash, StringComparison.Ordinal))
                return Task.FromResult(McpOperatorConfirmationAdmission.Denied("confirmation_plan_stale"));
            return Task.FromResult(completed.TryGetValue(request.PayloadHash, out var prior)
                ? new McpOperatorConfirmationAdmission(false, true, null, idempotencyId, prior.Outcome, prior.ResultReference)
                : new McpOperatorConfirmationAdmission(true, false, null, idempotencyId, McpOperatorIdempotencyOutcome.Pending, null));
        }

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        {
            completed[payloadHash!] = (outcome, resultReference);
            return Task.CompletedTask;
        }
    }

    private sealed class TestArtifactStore : IMcpOperatorFileArtifactStore
    {
        public List<McpOperatorFileArtifact> Artifacts { get; } = [];

        public Task<McpOperatorFileArtifact> CreateOrGetAsync(McpOperatorFileArtifactCreateRequest request, CancellationToken cancellationToken)
        {
            var existing = Artifacts.SingleOrDefault(artifact => artifact.IdempotencyId == request.IdempotencyId);
            if (existing is not null)
                return Task.FromResult(existing);

            var artifact = new McpOperatorFileArtifact(
                Guid.NewGuid(),
                request.Access.TenantId,
                request.Access.AgentId,
                request.Access.Principal.Subject,
                request.Access.Principal.ClientId ?? string.Empty,
                request.Access.McpResource!,
                request.Access.McpInstance!,
                request.ReadRootFingerprint,
                request.FileName,
                request.Content.Length,
                Convert.ToHexString(SHA256.HashData(request.Content)).ToLowerInvariant(),
                request.MimeType,
                request.Content,
                request.AcceptedAuditId,
                request.IdempotencyId,
                request.CreatedAtUtc,
                request.ExpiresAtUtc,
                null);
            Artifacts.Add(artifact);
            return Task.FromResult(artifact with { Content = null });
        }

        public Task<McpOperatorFileArtifact?> GetOwnedAsync(Guid artifactId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, bool includeContent, CancellationToken cancellationToken)
        {
            var artifact = Artifacts.SingleOrDefault(candidate =>
                candidate.ArtifactId == artifactId && candidate.TenantId == tenantId && candidate.AgentId == agentId &&
                candidate.Subject == principal.Subject && candidate.ClientId == (principal.ClientId ?? string.Empty) &&
                candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance);
            return Task.FromResult(artifact is null ? null : artifact with { Content = includeContent ? artifact.Content : null });
        }

        public Task<McpOperatorFileArtifact?> CleanupOwnedAsync(Guid artifactId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var index = Artifacts.FindIndex(candidate =>
                candidate.ArtifactId == artifactId && candidate.TenantId == tenantId && candidate.AgentId == agentId &&
                candidate.Subject == principal.Subject && candidate.ClientId == (principal.ClientId ?? string.Empty) &&
                candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance);
            if (index < 0)
                return Task.FromResult<McpOperatorFileArtifact?>(null);

            var cleaned = Artifacts[index] with { Content = null, DeletedAtUtc = Artifacts[index].DeletedAtUtc ?? now };
            Artifacts[index] = cleaned;
            return Task.FromResult<McpOperatorFileArtifact?>(cleaned);
        }

        public Task<int> PurgeExpiredAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken)
        {
            var count = 0;
            for (var index = 0; index < Artifacts.Count && count < maximumCount; index++)
            {
                if (Artifacts[index].DeletedAtUtc is null && Artifacts[index].ExpiresAtUtc <= now)
                {
                    Artifacts[index] = Artifacts[index] with { Content = null, DeletedAtUtc = now };
                    count++;
                }
            }

            return Task.FromResult(count);
        }
    }

    private sealed record ArtifactMetadata(Guid ArtifactId);
}
