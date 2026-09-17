using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Realtime;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Agents;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorClientObservabilityEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientBinding_ReturnsOptionalMigrationProvenanceForAnExistingV2Client(bool hasBinding)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        if (hasBinding)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.PrimaryClientAgentBindings.Add(new PrimaryClientAgentBinding
            {
                Id = Guid.NewGuid(), TenantId = 7, AgentId = agentId, PrimaryClientIdentity = "migration-source",
                Status = PrimaryClientAgentBindingStatus.Bound, CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "test"
            });
            await db.SaveChangesAsync();
        }
        var response = await AuthorizedClient(app).GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/clients/binding");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("hasBinding").GetBoolean().Should().Be(hasBinding);
        json.RootElement.GetProperty("agentId").GetGuid().Should().Be(agentId);
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().ContainSingle(request => request.Operation == "binding");
    }

    [Fact]
    public async Task PolicyAdmittedClientGet_ReturnsOnlyTheCuratedExactTargetIdentity()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/clients");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"identity\":{")
            .And.Contain("\"displayName\":\"test-agent\"")
            .And.Contain("\"hostName\":\"test-agent-host\"")
            .And.Contain("\"operatingSystem\":\"Linux\"")
            .And.Contain("\"architecture\":\"x64\"")
            .And.NotContain("deviceInfoJson");
        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.Evaluated.Select(request => (request.Tool, request.Operation)).Should().Equal(("netratel_clients", "get"));
        admission.Accepted.Select(request => (request.Tool, request.Operation)).Should().Equal(("netratel_clients", "get"));
    }

    [Fact]
    public async Task DeniedClientGet_DoesNotReadOrReturnTheExactTargetIdentity()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: false);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/clients");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing").And.NotContain("test-agent-host");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyAdmittedObservabilityReads_BindExactDelegationAndWriteAcceptedAuditEvidence()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}";

        var sources = await client.GetAsync($"{root}/logs/sources");
        var history = await client.GetAsync($"{root}/logs/history?sourceId=runtime&pageSize=10");
        var search = await client.GetAsync($"{root}/logs/search?sourceId=runtime&text=history");
        var telemetry = await client.GetAsync($"{root}/telemetry/snapshot");
        var clientTelemetry = await client.GetAsync($"{root}/clients/telemetry");

        sources.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sources.Content.ReadAsStringAsync()).Should().Contain("mcp-operator-policy").And.Contain("correlation-1");
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        (await history.Content.ReadAsStringAsync()).Should().Contain("history-record");
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        (await search.Content.ReadAsStringAsync()).Should().Contain("history-record");
        telemetry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await telemetry.Content.ReadAsStringAsync()).Should().Contain("akka-shadow");
        clientTelemetry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await clientTelemetry.Content.ReadAsStringAsync()).Should().Contain("akka-shadow");

        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.Evaluated.Select(request => (request.Tool, request.Operation, request.RequiredScopes.Single()))
            .Should().Equal(
                ("netratel_client_logs", "sources", "netratel.mcp.observe"),
                ("netratel_client_logs", "history", "netratel.mcp.observe"),
                ("netratel_client_logs", "search", "netratel.mcp.observe"),
                ("netratel_client_telemetry", "snapshot", "netratel.mcp.observe"),
                ("netratel_clients", "telemetry", "netratel.mcp.observe"));
        admission.Accepted.Select(request => (request.Tool, request.Operation))
            .Should().Equal(
                ("netratel_client_logs", "sources"),
                ("netratel_client_logs", "history"),
                ("netratel_client_logs", "search"),
                ("netratel_client_telemetry", "snapshot"),
                ("netratel_clients", "telemetry"));
    }

    [Fact]
    public async Task DeniedPolicy_PreventsGatewayDispatchAndReturnsTheSafePolicyFailure()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: false);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/history?sourceId=runtime");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing").And.Contain("correlation-1");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidRequest_IsRejectedBeforePolicyEvaluationOrGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/history?sourceId=runtime&pageSize=101");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_log_query");
        app.Services.GetRequiredService<TestAdmission>().Evaluated.Should().BeEmpty();
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DelegationTargetMismatch_IsRejectedBeforePolicyEvaluationOrGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/8/{agentId:D}/logs/sources");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_invalid");
        app.Services.GetRequiredService<TestAdmission>().Evaluated.Should().BeEmpty();
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OfflineAndCapabilityAbsentTargets_AreRejectedBeforeAcceptedAuditOrGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}";
        var presence = app.Services.GetRequiredService<TestPresence>();
        var options = app.Services.GetRequiredService<NetRatelAkkaMigrationOptions>();

        presence.Online = false;
        var offline = await client.GetAsync($"{root}/logs/sources");
        presence.Online = true;
        options.LogAuthorityEnabled = false;
        var unavailable = await client.GetAsync($"{root}/logs/sources");

        offline.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await offline.Content.ReadAsStringAsync()).Should().Contain("target_offline");
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await unavailable.Content.ReadAsStringAsync()).Should().Contain("capability_unavailable");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DisabledTarget_IsRejectedBeforeAcceptedAuditOrGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        app.Services.GetRequiredService<TestAdmission>().TargetEnabled = false;

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/sources");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target_disabled");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DataAbsentAfterAdmission_ReturnsNoContentFreeFailure()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        app.Services.GetRequiredService<TestTelemetry>().DataAvailable = false;

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/telemetry/snapshot");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("telemetry_unavailable").And.NotContain("akka-shadow");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Select(request => request.Operation).Should().Equal("snapshot");
    }

    [Fact]
    public async Task ProductionClientPing_UsesTheClientToolExecuteScopeAndReplaysWithoutASecondGatewayAction()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/clients/ping";

        var preview = await client.PostAsync($"{root}/preview", null);
        var confirmed = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });
        var replay = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestControlSessions>().Calls.Should().ContainSingle();
        app.Services.GetRequiredService<TestAdmission>().Evaluated
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => (candidate.Operation, candidate.RequiredScopes.Single()))
            .Should().Equal(
                ("ping", "netratel.mcp.execute"),
                ("ping", "netratel.mcp.execute"),
                ("ping", "netratel.mcp.execute"));
        app.Services.GetRequiredService<TestAdmission>().Accepted
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => candidate.Operation)
            .Should().Equal("ping");
    }

    [Fact]
    public async Task ProductionClientSoftwareUpdate_UsesTheClientToolExecuteScopeAndReplaysWithoutASecondResume()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/clients/software-update";
        var request = new { planToken = TestConfirmations.PlanToken, idempotencyKey = TestConfirmations.IdempotencyKey };

        var preview = await client.PostAsync($"{root}/preview", null);
        var confirmed = await client.PostAsJsonAsync($"{root}/confirm", request);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", request);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestUpdateAuthority>().Calls.Should().Equal("resume");
        app.Services.GetRequiredService<TestAdmission>().Evaluated
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => (candidate.Operation, candidate.RequiredScopes.Single()))
            .Should().Equal(
                ("software_update", "netratel.mcp.execute"),
                ("software_update", "netratel.mcp.execute"),
                ("software_update", "netratel.mcp.execute"));
        app.Services.GetRequiredService<TestAdmission>().Accepted
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => candidate.Operation)
            .Should().Equal("software_update");
    }

    [Fact]
    public async Task ProductionClientDisable_UsesTheClientToolAdminScopeAndReplaysWithoutASecondLifecycleAction()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/clients/disable";
        var request = new
        {
            reason = "Approved maintenance window",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { reason = request.reason });
        var confirmed = await client.PostAsJsonAsync($"{root}/confirm", request);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", request);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestAgentManagement>().Calls.Should().Equal("disable");
        app.Services.GetRequiredService<TestAdmission>().Evaluated
            .Where(request => request.Tool == "netratel_clients")
            .Select(request => (request.Operation, request.RequiredScopes.Single()))
            .Should().Equal(
                ("disable", "netratel.mcp.admin"),
                ("disable", "netratel.mcp.admin"),
                ("disable", "netratel.mcp.admin"));
        app.Services.GetRequiredService<TestAdmission>().Accepted
            .Where(request => request.Tool == "netratel_clients")
            .Select(request => request.Operation)
            .Should().Equal("disable");
    }

    [Fact]
    public async Task ProductionClientEnable_UsesTheClientToolAdminScopeAndReplaysWithoutASecondLifecycleAction()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true, initiallyEnabled: false);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/clients/enable";
        var request = new { planToken = TestConfirmations.PlanToken, idempotencyKey = TestConfirmations.IdempotencyKey };

        var preview = await client.PostAsync($"{root}/preview", null);
        var confirmed = await client.PostAsJsonAsync($"{root}/confirm", request);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", request);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestAgentManagement>().Calls.Should().Equal("enable");
        app.Services.GetRequiredService<TestAdmission>().Evaluated
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => (candidate.Operation, candidate.RequiredScopes.Single()))
            .Should().Equal(
                ("enable", "netratel.mcp.admin"),
                ("enable", "netratel.mcp.admin"),
                ("enable", "netratel.mcp.admin"));
        app.Services.GetRequiredService<TestAdmission>().Accepted
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => candidate.Operation)
            .Should().Equal("enable");
    }

    [Fact]
    public async Task ProductionClientDelete_UsesTheClientToolAdminScopeAndReplaysWithoutASecondLifecycleAction()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/clients/delete";
        var request = new
        {
            reason = "Machine retired",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { request.reason });
        var confirmed = await client.PostAsJsonAsync($"{root}/confirm", request);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", request);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        app.Services.GetRequiredService<TestAgentManagement>().Calls.Should().Equal("delete");
        app.Services.GetRequiredService<TestAdmission>().Evaluated
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => (candidate.Operation, candidate.RequiredScopes.Single()))
            .Should().Equal(
                ("delete", "netratel.mcp.admin"),
                ("delete", "netratel.mcp.admin"),
                ("delete", "netratel.mcp.admin"));
        app.Services.GetRequiredService<TestAdmission>().Accepted
            .Where(candidate => candidate.Tool == "netratel_clients")
            .Select(candidate => candidate.Operation)
            .Should().Equal("delete");
    }

    [Fact]
    public async Task PolicyRevocationDuringBoundedWindows_DiscardsCollectedObservabilityData()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}";
        var admission = app.Services.GetRequiredService<TestAdmission>();
        var dispatcher = app.Services.GetRequiredService<TestLogDispatcher>();
        var telemetry = app.Services.GetRequiredService<TestTelemetry>();

        dispatcher.OnStartLive = () => admission.IsAllowed = false;
        var tail = await client.GetAsync($"{root}/logs/tail?sourceId=runtime&windowSeconds=1&maxRecords=1");

        admission.IsAllowed = true;
        telemetry.OnSnapshotRead = () => admission.IsAllowed = false;
        var window = await client.GetAsync($"{root}/telemetry/stream-window?windowSeconds=1&maxSamples=1");

        tail.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tail.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing").And.NotContain("history-record");
        window.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await window.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing").And.NotContain("akka-shadow");
        dispatcher.Calls.Should().Equal(LogQueryOperation.StartLive, LogQueryOperation.StopLive);
        admission.Accepted.Select(request => (request.Tool, request.Operation)).Should().Equal(
            ("netratel_client_logs", "tail"),
            ("netratel_client_telemetry", "stream_window"));
    }

    [Fact]
    public async Task TargetGoingOfflineDuringATailWindow_DiscardsCollectedObservabilityData()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}";
        var presence = app.Services.GetRequiredService<TestPresence>();
        app.Services.GetRequiredService<TestLogDispatcher>().OnStartLive = () => presence.Online = false;

        var response = await client.GetAsync($"{root}/logs/tail?sourceId=runtime&windowSeconds=1&maxRecords=1");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("target_offline").And.NotContain("history-record");
    }

    [Fact]
    public async Task CallerCancellationDuringATailWindow_PropagatesAfterTheGatewayStopSignal()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        using var scope = app.Services.CreateScope();
        var observability = scope.ServiceProvider.GetRequiredService<McpOperatorClientObservabilityService>();
        var dispatcher = app.Services.GetRequiredService<TestLogDispatcher>();
        dispatcher.StartLiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.WaitForStartLiveCancellation = true;
        using var cancellation = new CancellationTokenSource();

        var operation = observability.GetTailAsync(
            new ClientKey(7, agentId),
            "runtime",
            windowSeconds: 15,
            maxRecords: 1,
            currentAdmission: null,
            cancellationToken: cancellation.Token);
        await dispatcher.StartLiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Func<Task> awaitOperation = async () => await operation;
        await awaitOperation.Should().ThrowAsync<OperationCanceledException>();
        dispatcher.Calls.Should().Equal(LogQueryOperation.StartLive, LogQueryOperation.StopLive);
    }

    [Fact]
    public async Task ConcurrentTailWindows_RemainIndependentAndStopEveryLiveQuery()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        using var scope = app.Services.CreateScope();
        var observability = scope.ServiceProvider.GetRequiredService<McpOperatorClientObservabilityService>();
        var dispatcher = app.Services.GetRequiredService<TestLogDispatcher>();
        dispatcher.ExpectedLiveStarts = 2;
        dispatcher.LiveStartsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ReleaseLiveStarts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = observability.GetTailAsync(new ClientKey(7, agentId), "runtime", 1, 1, null, CancellationToken.None);
        var second = observability.GetTailAsync(new ClientKey(7, agentId), "runtime", 1, 1, null, CancellationToken.None);
        await dispatcher.LiveStartsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatcher.ReleaseLiveStarts.TrySetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => result.IsSuccess && result.Value!.Records.Count == 1);
        dispatcher.Calls.Count(operation => operation == LogQueryOperation.StartLive).Should().Be(2);
        dispatcher.Calls.Count(operation => operation == LogQueryOperation.StopLive).Should().Be(2);
    }

    [Fact]
    public async Task GatewayReconnectGap_IsReportedAsABoundedResyncRequirement()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        app.Services.GetRequiredService<TestLogDispatcher>().StartLiveResyncRequired = true;

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/tail?sourceId=runtime&windowSeconds=1&maxRecords=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"resyncRequired\":true").And.Contain("resyncGuidance");
    }

    [Fact]
    public async Task ProductionResync_UsesTargetBoundPreviewAndConfirmedIdempotentDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/resync";

        var preview = await client.PostAsJsonAsync($"{root}/preview", new { sourceId = "runtime" });
        var confirm = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            sourceId = "runtime",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await preview.Content.ReadAsStringAsync()).Should().Contain("runtime").And.Contain("\"confirmationClass\":1");
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmationContent = await confirm.Content.ReadAsStringAsync();
        confirmationContent.Should().Contain("\"replayed\":false").And.NotContain("history-record");

        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.Evaluated.Select(request => (request.Operation, request.RequiredScopes.Single()))
            .Should().Equal(
                ("resync", "netratel.mcp.write"),
                ("resync", "netratel.mcp.write"),
                ("resync", "netratel.mcp.write"),
                ("resync", "netratel.mcp.write"));
        admission.Accepted.Select(request => request.Operation).Should().Equal("resync");
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().Equal(LogQueryOperation.History);
        var confirmations = app.Services.GetRequiredService<TestConfirmations>();
        confirmations.Plans.Should().ContainSingle(plan => plan.Decision.Request.Operation == "netratel_client_logs/resync");
        var completed = confirmations.Completed.Should().ContainSingle().Which;
        completed.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        completed.ResultReference.Should().NotBeNull().And.NotContain("history-record");
    }

    [Fact]
    public async Task ProductionResync_ReplayReturnsThePersistedContentFreeReceiptWithoutRedispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/resync";
        var request = new
        {
            sourceId = "runtime",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        };

        await client.PostAsJsonAsync($"{root}/preview", new { sourceId = "runtime" });
        var first = await client.PostAsJsonAsync($"{root}/confirm", request);
        var replay = await client.PostAsJsonAsync($"{root}/confirm", request);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true").And.NotContain("history-record");
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().Equal(LogQueryOperation.History);
        app.Services.GetRequiredService<TestAdmission>().Accepted.Select(value => value.Operation).Should().Equal("resync");
    }

    [Fact]
    public async Task ProductionResync_ChangedSourceAfterPreviewIsRejectedAsAStalePlanBeforeAuditOrDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/resync";

        await client.PostAsJsonAsync($"{root}/preview", new { sourceId = "runtime" });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            sourceId = "secondary",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        confirmation.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await confirmation.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().BeEmpty();
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProductionResync_RevocationBeforeAcceptedAuditPreventsGatewayDispatchAndCompletesThePlanAsFailed()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, allowed: true);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/logs/resync";
        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.RejectOnAcceptedAudit = true;

        await client.PostAsJsonAsync($"{root}/preview", new { sourceId = "runtime" });
        var confirmation = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            sourceId = "runtime",
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        confirmation.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await confirmation.Content.ReadAsStringAsync()).Should().Contain("target_policy_missing");
        app.Services.GetRequiredService<TestLogDispatcher>().Calls.Should().BeEmpty();
        app.Services.GetRequiredService<TestConfirmations>().Completed.Should().ContainSingle(completion =>
            completion.Outcome == McpOperatorIdempotencyOutcome.Failed && completion.ResultReference == "target_policy_missing");
    }

    private static HttpClient AuthorizedClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(Guid agentId, bool allowed, bool initiallyEnabled = true)
    {
        var client = new ClientKey(7, agentId);
        var options = new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            ControlGatewayEnabled = true,
            PingAuthorityEnabled = true,
            LogGatewayEnabled = true,
            LogAuthorityEnabled = true,
            TelemetryShadowEnabled = true,
            TelemetryAuthorityEnabled = true
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
                services.AddScoped<McpOperatorClientObservabilityService>();
                services.AddDbContext<OrchestratorDbContext>(db => db.UseInMemoryDatabase($"mcp-operator-client-{agentId:N}"));
                services.AddSingleton<McpOperatorLocalAgentOptions>();
                services.AddSingleton(new TestAgentManagement(client, initiallyEnabled));
                services.AddSingleton<IAgentManagementService>(provider => provider.GetRequiredService<TestAgentManagement>());
                services.AddSingleton<TestUpdateAuthority>();
                services.AddSingleton<IClientUpdateOperatorAuthority>(provider => provider.GetRequiredService<TestUpdateAuthority>());
                services.AddSingleton<IClientPresenceReadModel>(_ => throw new NotSupportedException());
                services.AddScoped<IPrimaryClientAgentBindingService, NetRatel.Infrastructure.Services.PrimaryClientAgentBindingService>();
                services.AddSingleton(new TestPresence(client));
                services.AddSingleton<IClientPresenceRouter>(provider => provider.GetRequiredService<TestPresence>());
                services.AddSingleton<IAgentLogGatewaySessionRegistry>(new TestLogRegistry(client));
                services.AddSingleton(new TestLogDispatcher());
                services.AddSingleton<IAgentLogGatewayQueryDispatcher>(provider => provider.GetRequiredService<TestLogDispatcher>());
                services.AddSingleton(new TestTelemetry(client));
                services.AddSingleton<IClientTelemetryRouter>(provider => provider.GetRequiredService<TestTelemetry>());
                services.AddSingleton<IGatewayTelemetryLiveRegistry, GatewayTelemetryLiveRegistry>();
                services.AddSingleton(new TestControlSessions(client));
                services.AddSingleton<IAgentControlSessionRegistry>(provider => provider.GetRequiredService<TestControlSessions>());
                services.AddSingleton(new TestAdmission(allowed));
                services.AddSingleton<IMcpOperatorRouteAdmission>(provider => provider.GetRequiredService<TestAdmission>());
                services.AddSingleton<TestConfirmations>();
                services.AddSingleton<IMcpOperatorConfirmationService>(provider => provider.GetRequiredService<TestConfirmations>());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.Use(async (http, next) =>
                {
                    var (tool, operation) = http.Request.Path.Value! switch
                    {
                        var path when path.EndsWith("/logs/sources", StringComparison.Ordinal) => ("netratel_client_logs", "sources"),
                        var path when path.EndsWith("/logs/history", StringComparison.Ordinal) => ("netratel_client_logs", "history"),
                        var path when path.EndsWith("/logs/search", StringComparison.Ordinal) => ("netratel_client_logs", "search"),
                        var path when path.EndsWith("/logs/tail", StringComparison.Ordinal) => ("netratel_client_logs", "tail"),
                        var path when path.EndsWith("/logs/resync/preview", StringComparison.Ordinal) => ("netratel_client_logs", "preview_resync"),
                        var path when path.EndsWith("/logs/resync/confirm", StringComparison.Ordinal) => ("netratel_client_logs", "confirm_resync"),
                        var path when path.EndsWith("/telemetry/snapshot", StringComparison.Ordinal) => ("netratel_client_telemetry", "snapshot"),
                        var path when path.EndsWith("/clients/telemetry", StringComparison.Ordinal) => ("netratel_clients", "telemetry"),
                        var path when path.EndsWith("/clients", StringComparison.Ordinal) => ("netratel_clients", "get"),
                        var path when path.EndsWith("/clients/binding", StringComparison.Ordinal) => ("netratel_clients", "binding"),
                        var path when path.EndsWith("/clients/ping/preview", StringComparison.Ordinal) => ("netratel_clients", "preview_ping"),
                        var path when path.EndsWith("/clients/ping/confirm", StringComparison.Ordinal) => ("netratel_clients", "ping"),
                        var path when path.EndsWith("/clients/software-update/preview", StringComparison.Ordinal) => ("netratel_clients", "preview_software_update"),
                        var path when path.EndsWith("/clients/software-update/confirm", StringComparison.Ordinal) => ("netratel_clients", "software_update"),
                        var path when path.EndsWith("/clients/disable/preview", StringComparison.Ordinal) => ("netratel_clients", "preview_disable"),
                        var path when path.EndsWith("/clients/disable/confirm", StringComparison.Ordinal) => ("netratel_clients", "disable"),
                        var path when path.EndsWith("/clients/enable/preview", StringComparison.Ordinal) => ("netratel_clients", "preview_enable"),
                        var path when path.EndsWith("/clients/enable/confirm", StringComparison.Ordinal) => ("netratel_clients", "enable"),
                        var path when path.EndsWith("/clients/delete/preview", StringComparison.Ordinal) => ("netratel_clients", "preview_delete"),
                        var path when path.EndsWith("/clients/delete/confirm", StringComparison.Ordinal) => ("netratel_clients", "delete"),
                        _ => ("netratel_client_telemetry", "stream_window")
                    };
                    http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                        new McpOperatorDelegationIdentity("operator-1", "operator-client", null, [], ["Administrator", "Operator"], ["netratel.mcp.observe", "netratel.mcp.execute", "netratel.mcp.admin"]),
                        "netratel-mcp-http",
                        tool,
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
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcpOperatorClientObservabilityEndpoints();
                    endpoints.MapMcpOperatorClientAdministrationEndpoints();
                });
            });
        });
        var app = await builder.StartAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = client.TenantId,
            Name = "test-agent",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            DeviceInfoJson = """{"hostName":"test-agent-host","os":"Linux","architecture":"x64","privateValue":"must-not-be-returned"}"""
        });
        await db.SaveChangesAsync();
        return app;
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
        public bool Online { get; set; } = true;

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey value, CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceSnapshot(
            value,
            value == client && Online ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "1.0",
            ["logs", "telemetry"],
            null,
            "test",
            true));
    }

    private sealed class TestLogRegistry(ClientKey client) : IAgentLogGatewaySessionRegistry
    {
        public event Action<GatewayLogBatchEvent>? LogBatchAccepted
        {
            add { }
            remove { }
        }

        public AgentLogRegistration Register(ClientKey value, Guid connectionId, ulong connectionEpoch, AgentLogHello hello, bool provisional = false) => throw new NotSupportedException();
        public bool TryCompleteResync(ClientKey value, string sourceId) => false;
        public GatewayLogPageDto Query(ClientKey value, GatewayLogPageRequest request) => new([], null, null, false, 0, false);

        public IReadOnlyList<GatewayLogSourceDescriptorDto> GetSources(ClientKey value) => value == client
            ? [
                new("runtime", "runtime", "Runtime", "linux", true, null, true, true, true, ["text"]),
                new("secondary", "secondary", "Secondary", "linux", true, null, true, true, true, ["text"])
            ]
            : [];
    }

    private sealed class TestLogDispatcher : IAgentLogGatewayQueryDispatcher
    {
        private int _liveStartCount;

        public ConcurrentQueue<LogQueryOperation> Calls { get; } = [];
        public Action? OnStartLive { get; set; }
        public TaskCompletionSource? StartLiveStarted { get; set; }
        public bool WaitForStartLiveCancellation { get; set; }
        public int ExpectedLiveStarts { get; set; }
        public TaskCompletionSource? LiveStartsReached { get; set; }
        public TaskCompletionSource? ReleaseLiveStarts { get; set; }
        public bool StartLiveResyncRequired { get; set; }

        public AgentLogQueryRegistration Register(AgentLogRegistration registration, bool provisional = false) => throw new NotSupportedException();

        public async Task<GatewayLogPageDto> QueryAsync(ClientKey client, GatewayLogPageRequest request, LogQueryOperation operation, CancellationToken cancellationToken)
        {
            Calls.Enqueue(operation);
            if (operation == LogQueryOperation.StartLive)
            {
                OnStartLive?.Invoke();
                StartLiveStarted?.TrySetResult();
                if (ReleaseLiveStarts is { } release)
                {
                    if (Interlocked.Increment(ref _liveStartCount) == ExpectedLiveStarts)
                        LiveStartsReached?.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                if (WaitForStartLiveCancellation)
                {
                    var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var registration = cancellationToken.Register(
                        static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                        cancellationSignal);
                    await cancellationSignal.Task.ConfigureAwait(false);
                }
            }

            return new GatewayLogPageDto(
                [new("cursor-1", 1, DateTimeOffset.UtcNow, "Information", request.SourceId, null, null, null, null, null, null, "history-record", null, false)],
                null,
                null,
                false,
                0,
                operation == LogQueryOperation.StartLive && StartLiveResyncRequired);
        }
    }

    private sealed class TestTelemetry(ClientKey client) : IClientTelemetryRouter
    {
        public bool DataAvailable { get; set; } = true;
        public Action? OnSnapshotRead { get; set; }

        public Task<TelemetryMessageResult> RecordAsync(RecordTelemetrySnapshot message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientTelemetryState> GetSnapshotAsync(ClientKey value, CancellationToken cancellationToken)
        {
            OnSnapshotRead?.Invoke();
            return Task.FromResult(new ClientTelemetryState(
                value,
                value == client && DataAvailable
                ? new TelemetrySnapshot(value, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, [], [], null, "akka-shadow", true)
                : null));
        }
    }

    private sealed class TestControlSessions(ClientKey client) : IAgentControlSessionRegistry
    {
        public ConcurrentQueue<ClientKey> Calls { get; } = [];

        public AgentControlSessionRegistration Register(ClientKey value, Guid connectionId, ulong connectionEpoch, bool provisional = false) => throw new NotSupportedException();

        public Task<AgentControlPingResult> RequestPingAsync(ClientKey value, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Enqueue(value);
            var sent = DateTimeOffset.UtcNow;
            return Task.FromResult(new AgentControlPingResult(value == client ? value : client, Guid.NewGuid(), sent, sent.AddMilliseconds(3), sent.AddMilliseconds(2)));
        }

        public bool TryGetLatestPing(ClientKey value, out AgentControlPingResult result)
        {
            result = default!;
            return false;
        }
    }

    private sealed class TestAgentManagement(ClientKey client, bool initiallyEnabled) : IAgentManagementService
    {
        public ConcurrentQueue<string> Calls { get; } = [];
        private bool _enabled = initiallyEnabled;

        public Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();

        public Task<AgentDetailDto?> GetAsync(int tenantId, Guid agentId, CancellationToken ct) =>
            Task.FromResult<AgentDetailDto?>(tenantId == client.TenantId && agentId == client.AgentId
                ? new AgentDetailDto(client.TenantId, client.AgentId, "test-agent", _enabled, _enabled ? null : "Approved maintenance window", DateTimeOffset.UtcNow, "test", null, null, _enabled ? null : DateTimeOffset.UtcNow)
                : null);

        public Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct)
        {
            if (tenantId != client.TenantId || agentId != client.AgentId) throw new AgentAuthException(404, "Agent not found.", "agent_not_found");
            _enabled = false;
            Calls.Enqueue("disable");
            return Task.CompletedTask;
        }

        public Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken ct)
        {
            if (tenantId != client.TenantId || agentId != client.AgentId) throw new AgentAuthException(404, "Agent not found.", "agent_not_found");
            _enabled = true;
            Calls.Enqueue("enable");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct)
        {
            if (tenantId != client.TenantId || agentId != client.AgentId) throw new AgentAuthException(404, "Agent not found.", "agent_not_found");
            _enabled = false;
            Calls.Enqueue("delete");
            return Task.CompletedTask;
        }
    }

    private sealed class TestUpdateAuthority : IClientUpdateOperatorAuthority
    {
        private bool _eligible = true;
        public ConcurrentQueue<string> Calls { get; } = [];

        public Task<ClientUpdateResumeEligibility> GetResumeEligibilityAsync(int tenantId, Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_eligible
                ? new ClientUpdateResumeEligibility(true, null, 7)
                : new ClientUpdateResumeEligibility(false, "client_update_not_suspended", 8));

        public Task<ClientUpdateResumeResult> ResumeAutomaticUpdatesAsync(int tenantId, Guid agentId, string resumedBy, CancellationToken cancellationToken)
        {
            if (!_eligible) return Task.FromResult(new ClientUpdateResumeResult(false, "client_update_not_suspended", 8));
            _eligible = false;
            Calls.Enqueue("resume");
            return Task.FromResult(new ClientUpdateResumeResult(true, null, 8));
        }
    }

    private sealed class TestAdmission(bool allowed) : IMcpOperatorRouteAdmission
    {
        public ConcurrentQueue<McpOperatorRouteAccessRequest> Evaluated { get; } = [];
        public ConcurrentQueue<McpOperatorRouteAccessRequest> Accepted { get; } = [];
        public bool IsAllowed { get; set; } = allowed;
        public bool TargetEnabled { get; set; } = true;
        public bool RejectOnAcceptedAudit { get; set; }

        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            Evaluated.Enqueue(request);
            var access = new McpOperatorAccessRequest(
                request.Environment,
                request.Principal,
                request.TenantId,
                request.AgentId,
                null,
                McpOperatorOperationFamily.Observability,
                $"{request.Tool}/{request.Operation}",
                request.RequiredScopes,
                McpOperatorConfirmationClass.None,
                request.CorrelationId,
                request.RequestId,
                McpResource: request.McpResource,
                McpInstance: request.McpInstance,
                Tool: request.Tool,
                TargetOnline: request.TargetOnline,
                CapabilityAvailable: request.CapabilityAvailable);
            var decision = !TargetEnabled
                ? McpOperatorDecision.Denied(access, "target_disabled", McpOperatorAuthorizationLayer.Target)
                : !request.TargetOnline
                    ? McpOperatorDecision.Denied(access, "target_offline", McpOperatorAuthorizationLayer.Target)
                    : !request.CapabilityAvailable
                        ? McpOperatorDecision.Denied(access, "capability_unavailable", McpOperatorAuthorizationLayer.Capability)
                        : IsAllowed
                            ? new McpOperatorDecision(true, null, null, [], new McpOperatorConstraints(), null, access)
                            : McpOperatorDecision.Denied(access, "target_policy_missing", McpOperatorAuthorizationLayer.Policy);
            return Task.FromResult(new McpOperatorRouteAdmission(decision, McpOperatorOperationCatalog.Find(request.Tool, request.Operation)));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            if (RejectOnAcceptedAudit)
                throw new McpOperatorAdmissionRejectedException("target_policy_missing");

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
                McpOperatorOperationFamily.Observability,
                $"{request.Tool}/{request.Operation}",
                request.CorrelationId,
                request.RequestId,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class TestConfirmations : IMcpOperatorConfirmationService
    {
        public const string PlanToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        public const string IdempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        private static readonly Guid AdmissionId = Guid.Parse("9cb863f4-ff02-43c8-b60d-6703e8ca508f");

        public List<McpOperatorConfirmationPlanRequest> Plans { get; } = [];
        public List<McpOperatorConfirmationRequest> Confirmed { get; } = [];
        public List<(Guid IdempotencyId, McpOperatorIdempotencyOutcome Outcome, string? ResultReference)> Completed { get; } = [];

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken)
        {
            Plans.Add(request);
            return Task.FromResult(new McpOperatorConfirmationPlan(
                PlanToken,
                IdempotencyKey,
                DateTimeOffset.UtcNow.AddMinutes(5),
                McpOperatorConfirmationClass.StandardMutation,
                request.PayloadHash,
                request.Decision.TargetSetDigest));
        }

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            Confirmed.Add(request);
            if (Plans.LastOrDefault() is not { } plan || !string.Equals(plan.PayloadHash, request.PayloadHash, StringComparison.Ordinal))
                return Task.FromResult(McpOperatorConfirmationAdmission.Denied("confirmation_plan_stale"));

            var completed = Completed.LastOrDefault();
            return Task.FromResult(completed.IdempotencyId == Guid.Empty
                ? new McpOperatorConfirmationAdmission(true, false, null, AdmissionId, McpOperatorIdempotencyOutcome.Pending, null)
                : new McpOperatorConfirmationAdmission(false, true, null, completed.IdempotencyId, completed.Outcome, completed.ResultReference));
        }

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        {
            Completed.Add((idempotencyId, outcome, resultReference));
            return Task.CompletedTask;
        }
    }
}
