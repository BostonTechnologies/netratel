using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Gateway;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Actual product request -> OAuth ingest -> native command -> durable provider callback -> recorded task/worklog.</summary>
[Collection(ServiceLinkRotationPeerCollection.Name)]
[Trait("category", "manual-integration")]
public sealed class ServiceLinkOrchestrationRoundTripTests
{
    private const string JobName = "Synthetic actual native command";
    private const string TaskName = "Bounded real agent command";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory(Timeout = 300_000)]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "ServiceLinkPhysicalProof")]
    public async Task Real_recorded_form_task_executes_on_authenticated_native_agent_and_receives_provider_callback(bool netRatelInitiates)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = lifetime.Token;
        _ = await ServiceLinkNativeArtifacts.ResolveAsync(ct); // Fail stale/dirty artifact provenance before starting products.
        using var nativeListener = new ServiceLinkNativeListener();
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates, nativeListener: nativeListener);
        var peerMetadata = await GetJsonAsync(pair.RatelDesk.Anonymous, "/api/integrations/service-link/metadata", ct);
        Assert.Equal(ServiceLinkPublishedRatelDeskPeer.PublishedVersion, peerMetadata.GetProperty("product_version").GetString());
        await using var native = await ServiceLinkNativeProcess.StartAsync(pair.NetRatel, nativeListener, ct);
        await native.WaitAdmittedAsync(ct);
        var localMetadata = await GetJsonAsync(pair.NetRatel.Anonymous, "/api/integrations/service-link/metadata", ct);
        Assert.Equal(native.ClientProductVersion, localMetadata.GetProperty("product_version").GetString());
        var tenant = int.Parse(pair.NetRatel.TenantId, CultureInfo.InvariantCulture);
        Assert.True(pair.NetRatel.Services.GetRequiredService<IAgentJobGatewaySessionRegistry>()
            .IsAvailable(new ClientKey(tenant, native.AgentId)), "The real authenticated product job gateway session was not admitted.");
        await pair.NetRatel.CreateNativeJobAndSelectTargetAsync(native.AgentId, JobName, ct);
        var definitionId = pair.NetRatel.RequestDefinitionId.ToString(CultureInfo.InvariantCulture);
        using (var step = await pair.NetRatel.Administrator.PostAsJsonAsync($"/api/v1/jobs/{definitionId}/steps", new
        { Ordinal = 1, Type = 0, Runner = "bash", Command = native.Command, ScriptId = (ulong?)null, PayloadJson = (string?)null, Enabled = true }, ct))
            Assert.True(step.IsSuccessStatusCode, $"The actual bounded job step returned HTTP {(int)step.StatusCode}.");

        await pair.ActivateAsync(ct); // Both human consents, exchanged actual credentials, verification, durable commit.
        await pair.NetRatel.WaitForInitialSensitiveWindowAsync(ct);
        var link = pair.Review.GrantSummary.LinkId;
        Assert.Equal(native.AgentId, pair.NetRatel.ResourceId);
        using (var settings = await pair.RatelDesk.Administrator.GetAsync("/api/v1/admin/orchestration/", ct))
        {
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            var profile = await settings.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.True(profile.GetProperty("enabled").GetBoolean());
            Assert.Equal(pair.NetRatel.BaseUrl, profile.GetProperty("baseUrl").GetString());
        }

        var templateId = Guid.NewGuid().ToString("D");
        var service = await PostJsonAsync(pair.RatelDesk.Administrator, "/api/v1/services/", new
        { Name = "Synthetic orchestration round trip", Description = "bounded disposable proof", AllowedCustomerIds = new[] { pair.RatelDesk.CustomerId }, AllowedOrganizationIds = new[] { pair.RatelDesk.OrganizationId } }, ct);
        var schema = JsonSerializer.Serialize(new
        {
            Fields = Array.Empty<object>(), Tasks = new[] { new
            { Id = templateId, Name = TaskName, Order = 1, Type = "automation", AutoStart = true, DependsOn = Array.Empty<string>(), ExpectedRuntimeMinutes = 1, GraceRuntimeMinutes = 1 } }
        }, Json);
        var form = await PostJsonAsync(pair.RatelDesk.Administrator, "/api/v1/request-forms/", new
        {
            ServiceId = Id(service), Title = "Synthetic orchestration round trip", Description = "bounded disposable proof",
            OrganizationId = pair.RatelDesk.OrganizationId, AllowedOrganizationIds = new[] { pair.RatelDesk.OrganizationId }, ReleaseStatus = 0, JsonSchema = schema
        }, ct);
        var binding = await PostJsonAsync(pair.RatelDesk.Administrator, "/api/v1/admin/orchestration/bindings", new
        {
            RequestFormId = Id(form), TaskTemplateId = templateId, OrchestrationRequestDefinitionId = definitionId,
            OrchestrationRequestDefinitionName = JobName, OrchestrationJobDefinitionId = definitionId, OrchestrationJobDefinitionName = JobName
        }, ct);
        Assert.True(binding.GetProperty("enabled").GetBoolean());

        var correlation = "physical-" + Guid.NewGuid().ToString("N");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/requests/")
        {
            Content = JsonContent.Create(new
            {
                Title = "Synthetic actual automation proof", Description = "bounded disposable request", Priority = 0,
                OrganizationId = pair.RatelDesk.OrganizationId, CustomerId = pair.RatelDesk.CustomerId,
                ServiceId = Id(service), RequestFormId = Id(form), PayloadJson = "{}"
            })
        };
        create.Headers.Add("X-Correlation-Id", correlation);
        using var created = await pair.RatelDesk.Administrator.SendAsync(create, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var parent = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var parentId = Id(parent);
        // Actual HTTP creation generated this task and invoked published StartAsync; no task/status/stamps were seeded.
        var tasks = await GetJsonAsync(pair.RatelDesk.Administrator, "/api/v1/request-tasks/?requestId=" + Uri.EscapeDataString(parentId), ct);
        var generated = Assert.Single(tasks.GetProperty("items").EnumerateArray());
        Assert.Equal(TaskName, generated.GetProperty("name").GetString());
        var taskId = Id(generated);
        var acknowledged = await GetJsonAsync(pair.RatelDesk.Administrator, "/api/v1/request-tasks/" + taskId, ct);
        Assert.Equal(templateId, acknowledged.GetProperty("templateId").GetString());
        Assert.Equal(2, acknowledged.GetProperty("status").GetInt32());
        Assert.Equal(link, acknowledged.GetProperty("orchestrationLinkId").GetString());
        Assert.Equal(pair.NetRatel.InstanceId.ToString("D"), acknowledged.GetProperty("orchestrationPeerInstanceId").GetString());
        Assert.Equal(pair.Review.GrantSummary.ProposedLinkRevision, acknowledged.GetProperty("orchestrationLinkRevision").GetInt64());
        Assert.Equal(Id(binding), acknowledged.GetProperty("automationBindingId").GetString());
        var localRequestId = int.Parse(acknowledged.GetProperty("orchestrationExternalRequestId").GetString()!, CultureInfo.InvariantCulture);
        var execution = acknowledged.GetProperty("orchestratorExecutionId").GetString()!;
        Assert.Equal(execution, acknowledged.GetProperty("orchestrationExternalRunId").GetString());
        var runId = ulong.Parse(execution, CultureInfo.InvariantCulture);

        var ingest = Assert.Single(pair.NetRatel.Proxy.BusinessObservations, observation =>
            observation.Path == "/internal/ingest" && observation.StatusCode is >= 200 and < 300);
        using (var transmitted = JsonDocument.Parse(ingest.RequestJson))
        {
            Assert.Equal(parentId, transmitted.RootElement.GetProperty("RequestId").GetString());
            Assert.Equal(taskId, transmitted.RootElement.GetProperty("RequestTaskId").GetString());
            Assert.Equal(correlation, transmitted.RootElement.GetProperty("CorrelationId").GetString());
            Assert.Equal(definitionId, transmitted.RootElement.GetProperty("NetRatelJobDefinitionId").GetString());
            Assert.Equal(definitionId, transmitted.RootElement.GetProperty("NetRatelRequestDefinitionId").GetString());
        }
        using (var ack = JsonDocument.Parse(ingest.ResponseJson!))
        {
            Assert.Equal(localRequestId.ToString(CultureInfo.InvariantCulture), ack.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(execution, ack.RootElement.GetProperty("runId").GetString());
            Assert.Equal(execution, ack.RootElement.GetProperty("executionId").GetString());
        }

        var recorded = await RecordedBindingAsync(pair, parentId, taskId, ct);
        Assert.Equal(localRequestId, recorded.RequestId); Assert.Equal(execution, recorded.ExecutionId);
        Assert.Equal(tenant, recorded.TenantId); Assert.Equal(native.AgentId, recorded.AgentId);
        Assert.Equal(definitionId, recorded.JobDefinitionId); Assert.Equal(correlation, recorded.CorrelationId);
        Assert.Equal(link, recorded.LinkId); Assert.Equal(pair.Review.GrantHash, recorded.GrantHash);
        Assert.Equal(pair.RatelDesk.InstanceId.ToString("D"), recorded.PeerInstanceId);
        Assert.Equal(pair.RatelDesk.OrganizationId, recorded.PeerTenantId);
        Assert.Equal(pair.RatelDesk.ApiBaseUrl + "/api/v1/orchestration/provider/callback", recorded.CallbackUrl);

        await WaitAsync(async poll =>
        {
            var current = await DetailsAsync(pair, runId, poll);
            return current.Run.Status == JobRunState.Running && current.Steps.Count == 1 && current.Activities.Count == 1;
        }, TimeSpan.FromSeconds(10), ct);
        var beforeCompletion = await DetailsAsync(pair, runId, ct);
        Assert.Equal(JobRunState.Running, beforeCompletion.Run.Status);
        Assert.Equal(recorded.SourceSystem, beforeCompletion.Run.StartedBy);
        Assert.Equal(native.AgentId, beforeCompletion.Run.AgentId);
        Assert.Single(beforeCompletion.Steps); Assert.Single(beforeCompletion.Activities);
        Assert.False(File.Exists(native.ReleaseFile));
        await native.ReleaseCommandAsync(ct); // Actual persisted ACK exists before the physical executor can finish.

        await WaitAsync(async poll => (await DetailsAsync(pair, runId, poll)).Run.Status == JobRunState.Succeeded,
            TimeSpan.FromSeconds(40), ct);
        var completed = await DetailsAsync(pair, runId, ct);
        Assert.Equal(JobStepRunState.Succeeded, Assert.Single(completed.Steps).Status);
        var activity = Assert.Single(completed.Activities);
        Assert.Equal(native.AgentId, activity.AgentId);
        using (var result = JsonDocument.Parse(activity.ResultJson!))
        {
            Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(native.ProofMarker, result.RootElement.GetProperty("stdout").EnumerateArray().Select(line => line.GetString()));
        }
        await WaitAsync(async poll =>
        {
            var current = await GetJsonAsync(pair.RatelDesk.Administrator, "/api/v1/request-tasks/" + taskId, poll);
            return current.GetProperty("status").GetInt32() == 3 && current.GetProperty("lastAutomationStatus").GetString() == "succeeded" &&
                await TerminalDeliveredAsync(pair, localRequestId, poll);
        }, TimeSpan.FromSeconds(40), ct);
        var finalTask = await GetJsonAsync(pair.RatelDesk.Administrator, "/api/v1/request-tasks/" + taskId, ct);
        Assert.Equal(3, finalTask.GetProperty("status").GetInt32());
        Assert.Equal(6, finalTask.GetProperty("state").GetInt32()); // Published TicketState.Resolved.
        Assert.NotEqual(JsonValueKind.Null, finalTask.GetProperty("completedAt").ValueKind);
        Assert.Equal(link, finalTask.GetProperty("orchestrationLinkId").GetString());
        Assert.Equal(execution, finalTask.GetProperty("orchestratorExecutionId").GetString());
        Assert.Equal(localRequestId.ToString(CultureInfo.InvariantCulture), finalTask.GetProperty("orchestrationExternalRequestId").GetString());
        using (var callbackResult = JsonDocument.Parse(finalTask.GetProperty("resultJson").GetString()!))
        {
            Assert.Equal("succeeded", callbackResult.RootElement.GetProperty("status").GetString());
            Assert.Equal(execution, callbackResult.RootElement.GetProperty("orchestrationRunId").GetString());
            Assert.Contains(native.ProofMarker, callbackResult.RootElement.GetProperty("resultJson").GetString()!, StringComparison.Ordinal);
        }
        var callback = Assert.Single(pair.RatelDesk.Proxy.BusinessObservations, observation =>
            observation.Path == "/api/v1/orchestration/provider/callback" && observation.StatusCode == 204 &&
            JsonSerializer.Deserialize<JsonElement>(observation.RequestJson).GetProperty("status").GetString() == "succeeded");
        var worklogs = await GetJsonAsync(pair.RatelDesk.Administrator, $"/api/v1/requests/{parentId}/worklogs", ct);
        var success = Assert.Single(worklogs.EnumerateArray(), log =>
            log.GetProperty("notesText").GetString()?.Contains("reported 'succeeded'", StringComparison.Ordinal) == true);
        Assert.Contains(Id(binding), success.GetProperty("notesText").GetString()!, StringComparison.Ordinal);
        Assert.Contains(execution, success.GetProperty("notesText").GetString()!, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, success.GetProperty("technicianId").ValueKind); // Machine subject is not a human FK.
        var originalWorklogs = worklogs.EnumerateArray().Select(Id).Order(StringComparer.Ordinal).ToArray();

        // Replay only the actual terminal provider body after the genuine result/receipt is complete.
        var callbackToken = await pair.TokenAsync(netRatelIssuer: false, "rateldesk.orchestration.callback", ct);
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/provider/callback")
        { Content = new StringContent(callback.RequestJson, Encoding.UTF8, "application/json") };
        replay.Headers.Authorization = new AuthenticationHeaderValue("Bearer", callbackToken);
        replay.Headers.Add("X-Correlation-Id", correlation);
        using (var replayed = await pair.RatelDesk.Anonymous.SendAsync(replay, ct)) Assert.Equal(HttpStatusCode.NoContent, replayed.StatusCode);
        var unchanged = await GetJsonAsync(pair.RatelDesk.Administrator, $"/api/v1/requests/{parentId}/worklogs", ct);
        Assert.Equal(originalWorklogs, unchanged.EnumerateArray().Select(Id).Order(StringComparer.Ordinal).ToArray());
        await using (var scope = pair.NetRatel.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Equal(1, await db.Set<ManagedOrchestrationRequestBinding>().CountAsync(row => row.ParentRequestId == parentId && row.RequestTaskId == taskId, ct));
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
            Assert.Single(await runs.ListAsync(ct), run => run.JobId.ToString(CultureInfo.InvariantCulture) == definitionId);
        }
        WriteReceipt(pair, native, nativeListener, parentId, taskId, Id(binding), localRequestId, completed, Id(success), callback.StatusCode);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, ct);
        Assert.True(response.IsSuccessStatusCode, $"Actual human product command {path} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }
    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }
    private static string Id(JsonElement item) => item.GetProperty("id").GetString()!;
    private static async Task<ManagedOrchestrationRequestBinding> RecordedBindingAsync(ServiceLinkPair pair, string parent, string task, CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ManagedOrchestrationRequestBinding>()
            .AsNoTracking().SingleAsync(row => row.ParentRequestId == parent && row.RequestTaskId == task, ct);
    }
    private static async Task<JobRunDetails> DetailsAsync(ServiceLinkPair pair, ulong run, CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(run, ct)
            ?? throw new InvalidOperationException("The actual persisted job run disappeared.");
    }
    private static async Task<bool> TerminalDeliveredAsync(ServiceLinkPair pair, int request, CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<OrchestrationCallbackDelivery>()
            .AsNoTracking().AnyAsync(row => row.RequestId == request && row.Phase == "succeeded" && row.DeliveredAtUtc != null, ct);
    }
    private static async Task WaitAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        while (!await condition(deadline.Token)) await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
    }
    private static void WriteReceipt(ServiceLinkPair pair, ServiceLinkNativeProcess native, ServiceLinkNativeListener listener,
        string parent, string task, string binding, int request, JobRunDetails details, string worklog, int callbackStatus)
    {
        var directory = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_EVIDENCE_DIRECTORY") ?? Path.Combine("TestResults", "service-link-proof");
        Directory.CreateDirectory(directory);
        var receipt = new
        {
            phase = "actual-native-orchestration-completed", candidateSource = native.SourceSha,
            reviewedSource = Environment.GetEnvironmentVariable("NETRATEL_REVIEW_SOURCE_SHA") ?? native.SourceSha,
            clientAssemblySha256 = native.ClientAssemblySha256, clientProductVersion = native.ClientProductVersion,
            native.TrustVerified, native.UntrustedCaRejected, caSha256 = listener.CaSha256,
            netRatelInitiates = pair.NetRatelInitiates, provider = "PostgreSQL", nativeExecution = "production enrollment/token/presence/job clients and real bounded bash executor",
            ratelDeskVersion = ServiceLinkPublishedRatelDeskPeer.PublishedVersion, ratelDeskSource = ServiceLinkPublishedRatelDeskPeer.PublishedSource,
            ratelDeskApiImage = ServiceLinkPublishedRatelDeskPeer.ApiImage, ratelDeskWebImage = ServiceLinkPublishedRatelDeskPeer.WebImage,
            linkId = pair.Review.GrantSummary.LinkId, semanticRevision = pair.Review.GrantSummary.ProposedLinkRevision, pair.Review.GrantHash,
            parentRequestId = parent, requestTaskId = task, automationBindingId = binding, netRatelRequestId = request,
            runId = details.Run.Id.ToString(CultureInfo.InvariantCulture), agentId = native.AgentId, jobId = details.Run.JobId.ToString(CultureInfo.InvariantCulture),
            stepId = details.Steps.Single().Id.ToString(CultureInfo.InvariantCulture), activityId = details.Activities.Single().Id.ToString(CultureInfo.InvariantCulture),
            proofMarker = native.ProofMarker, callbackStatus, finalTaskStatus = "Completed", successWorklogId = worklog, replay = "204, same worklog IDs and single run/binding",
            boundary = "isolated actual candidate and immutable published companion; no production deployment interoperability claim"
        };
        File.WriteAllText(Path.Combine(directory, "physical-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }
}
