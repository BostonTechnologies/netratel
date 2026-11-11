using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Requests;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobAggregatePostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issued_pairing_token_admits_one_durable_parent_and_callbacks_wait_for_the_complete_target_plan(bool ha)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        await using var h = await AggregateHarness.CreateAsync(connection, 2, ct, JobNodeFailurePolicy.ContinueRemaining);
        if (ha) await UseHaGroupAsync(h, 5, ct);
        var directory = Path.Combine(Path.GetTempPath(), "managed-aggregate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create();
        try
        {
            await using var app = await OrchestrationManagedServiceHttpTests.StartAsync(connection, directory, key, services =>
            {
                services.AddSingleton<TimeProvider>(h.Clock);
                services.AddSingleton<IClientConnectionEpochStore>(h.Services.GetRequiredService<IClientConnectionEpochStore>());
                services.AddOptions<JobAggregateOptions>(); services.AddNetRatelJobObservationPersistence();
                services.AddSingleton<IJobAggregateWakeup, NoWakeup>();
                services.AddScoped<IScriptService, NoScripts>(); services.AddScoped<IJobExecutionSnapshotCapture, PinnedJobExecutionSnapshotCapture>();
                services.AddScoped<IJobNodeDirectory, JobNodeDirectory>(); services.AddScoped<IJobTargetResolver, JobTargetResolver>();
                services.AddScoped<HaGroupWorkerSelector>(); services.AddScoped<JobAggregateOrchestrator>();
                services.AddScoped<IAkkaJobAuthorityService>(provider => new AkkaJobAuthorityService(
                    provider.GetRequiredService<IJobDefinitionService>(), provider.GetRequiredService<IJobRunService>(),
                    new JobTaskBridge(provider.GetRequiredService<IScriptService>(), NullLogger<JobTaskBridge>.Instance), h.Gateway,
                    h.NativeRouter, new RecordingRealtimeFanoutSink(), provider.GetRequiredService<JobAuthorityIdGenerator>(), new FixtureEnvironment(),
                    managedGuard: provider.GetRequiredService<ManagedOrchestrationInvocationGuard>(),
                    db: provider.GetRequiredService<OrchestratorDbContext>(), clock: h.Clock,
                    snapshotCapture: provider.GetRequiredService<IJobExecutionSnapshotCapture>(), aggregates: provider.GetRequiredService<JobAggregateOrchestrator>()));
            });
            h.UseServices((ServiceProvider)app.Services);
            CreatedServiceClient credential;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>().Database.MigrateAsync(ct);
                await PairingBusinessAuthorityFixture.SeedAdministratorAsync(scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>(), ct: ct);
                credential = await PairingBusinessAuthorityFixture.CreateAsync(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>(), 7, ServiceIdentityScopes.Business, ct: ct,
                    protection: scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
            }
            var client = app.GetTestClient();
            var token = await OrchestrationManagedServiceHttpTests.TokenAsync(client, credential, string.Join(' ', ServiceIdentityScopes.Business));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("X-NetRatel-Automation-Contract", "2");
            var capabilities = await client.GetFromJsonAsync<NetRatelAutomationCapabilities>("/internal/catalog/capabilities", ct);
            capabilities!.AggregateExecution.Should().BeTrue();
            var choices = await client.GetFromJsonAsync<NetRatelAutomationTargetChoice[]>("/internal/catalog/target-choices", ct);
            choices!.Count(choice => choice.Kind == "explicitNodes").Should().Be(5);
            // A real canonical job created after pairing is immediately in the refreshed effective grant.
            using var create = await client.PostAsJsonAsync("/internal/catalog/request-definitions", new CreateNetRatelRequestDefinitionDto
            {
                Name = "Created through paired authoring", TenantId = 7, Target = JobTargetDefinition.FromLegacy(h.AgentIds[0]),
                Runner = "bash", Command = "printf '%s' @message@",
                Inputs = [new() { Key = "message", Label = "Message", Type = "text", Required = true }]
            }, ct);
            create.EnsureSuccessStatusCode();
            var authored = (await create.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!;
            await using (var independent = app.Services.CreateAsyncScope())
            {
                var details = await independent.ServiceProvider.GetRequiredService<IJobDefinitionService>().GetDetailsAsync(ulong.Parse(authored.RequestDefinitionId), ct);
                details!.Steps.Should().ContainSingle(step => step.Command == "printf '%s' @message@");
                details.Job.Target!.AgentIds.Should().Equal(h.AgentIds[0]);
            }
            var catalogue = (await client.GetFromJsonAsync<NetRatelCatalogRequestDefinitionDto[]>("/internal/catalog/request-definitions", ct))!;
            catalogue.Should().Contain(item => item.RequestDefinitionId == "41" && item.ClientIdentity == "" && item.Target != null);
            catalogue.Should().Contain(item => item.RequestDefinitionId == authored.RequestDefinitionId);
            using var sync = await client.PostAsJsonAsync($"/internal/catalog/request-definitions/{authored.RequestDefinitionId}/inputs/sync",
                new SyncNetRatelRequestDefinitionInputsDto { Inputs = [new() { Key = "message", Label = "Message", Type = "text", DefaultValue = "synthetic" }] }, ct);
            sync.EnsureSuccessStatusCode();
            (await sync.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!.Target!.Definition!.AgentIds.Should().Equal(h.AgentIds[0]);
            NetRatelIngestRequest Ingest(string job = "41", string correlation = "attempt-one", string payload = "{\"input\":{\"message\":\"synthetic\"}}") => new()
            { RequestId = "synthetic-request", RequestTaskId = "synthetic-task", CorrelationId = correlation,
                NetRatelRequestDefinitionId = job, NetRatelJobDefinitionId = job, PayloadJson = payload };
            using var accepted = await client.PostAsJsonAsync("/internal/ingest", Ingest(), ct);
            accepted.EnsureSuccessStatusCode();
            var receipt = (await accepted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!;
            var parent = ulong.Parse(receipt.ExecutionId!);
            (await h.ReadAsync(parent, ct)).TargetCount.Should().Be(ha ? 1 : 5);
            h.Gateway.Dispatches.Should().BeEmpty();
            using var duplicate = await client.PostAsJsonAsync("/internal/ingest", Ingest(), ct);
            duplicate.EnsureSuccessStatusCode();
            (await duplicate.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId.Should().Be(receipt.ExecutionId);
            (await client.PostAsJsonAsync("/internal/ingest", Ingest(payload: "{\"changed\":true}"), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await client.PostAsJsonAsync("/internal/ingest", Ingest(correlation: "attempt-too-early"), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            await h.FillAsync(parent, ct);
            var first = h.Gateway.Active.Keys.Min();
            await h.SucceedNodeAsync(first, ct); await h.FillAsync(parent, ct);
            if (!ha)
            {
                (await h.ReadAsync(parent, ct)).Status.Should().Be(JobRunState.Running);
                await using var proof = app.Services.CreateAsyncScope();
                var request = await proof.ServiceProvider.GetRequiredService<IRequestService>().GetAsync(int.Parse(receipt.RequestId!), ct);
                request!.Status.Should().NotBe("Completed");
            }
            while (h.Gateway.Active.Count > 0)
            { await h.SucceedNodeAsync(h.Gateway.Active.Keys.Min(), ct); await h.FillAsync(parent, ct); }
            (await h.ReadAsync(parent, ct)).Status.Should().Be(JobRunState.Succeeded);
            h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Count().Should().Be(ha ? 1 : 5);
            await using (var proof = app.Services.CreateAsyncScope())
            {
                var db = proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(ct);
                binding.AgentId.Should().BeNull(); binding.TargetPlanDigest.Should().NotBeNull();
                var request = await ManagedOrchestrationRecovery.RecoverAsync(db, proof.ServiceProvider.GetRequiredService<IRequestService>(),
                    proof.ServiceProvider.GetRequiredService<IJobRunService>(), binding, ct);
                var details = (await proof.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(parent, ct))!;
                OrchestrationCallbackProjection.Matches(request!, details.Run, binding).Should().BeTrue();
                (await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct)).Should().BeTrue();
                var callback = OrchestrationCallbackProjection.Build(request!, details, binding, await JobRunNodeProjection.ReadAsync(db, checked((long)parent), ct));
                callback.Status.Should().Be("succeeded"); callback.ResultJson.Should().Contain("parentRunId");
                (await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)parent), ct)).TerminalResultHash = "invalid";
                await db.SaveChangesAsync(ct);
                (await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct)).Should().BeFalse();
                // Restore the original committed hash to verify a legitimate subsequent attempt.
                (await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)parent), ct)).TerminalResultHash =
                    JobRunProjectionHash.ComputeAggregate(details, await JobRunNodeProjection.ReadAsync(db, checked((long)parent), ct));
                await db.SaveChangesAsync(ct);
            }
            using var next = await client.PostAsJsonAsync("/internal/ingest", Ingest(authored.RequestDefinitionId, "attempt-two"), ct);
            next.EnsureSuccessStatusCode();
            var nextId = ulong.Parse((await next.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            nextId.Should().NotBe(parent); await h.FillAsync(nextId, ct);
            await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(nextId, ct);
            (await h.ReadAsync(nextId, ct)).Status.Should().Be(JobRunState.Succeeded);
            using var runtimeCreate = await client.PostAsJsonAsync("/internal/catalog/request-definitions", new CreateNetRatelRequestDefinitionDto
            {
                Name = "Declared runtime workers", TenantId = 7,
                Target = new() { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientIds, "workers") },
                Runner = "bash", Command = "printf runtime"
            }, ct);
            runtimeCreate.EnsureSuccessStatusCode();
            var runtimeJob = (await runtimeCreate.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!;
            runtimeJob.Target!.Readiness.Should().Be("requires-input");
            runtimeJob.Inputs.Should().Contain(input => input.Key == "workers" && input.Required);
            using var runtimeAccepted = await client.PostAsJsonAsync("/internal/ingest", Ingest(runtimeJob.RequestDefinitionId, "runtime-attempt",
                JsonSerializer.Serialize(new { input = new { workers = new[] { h.AgentIds[0].ToString("D") } } })), ct);
            runtimeAccepted.EnsureSuccessStatusCode();
            var runtimeId = ulong.Parse((await runtimeAccepted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            await h.FillAsync(runtimeId, ct); await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(runtimeId, ct);
            (await h.ReadAsync(runtimeId, ct)).Status.Should().Be(JobRunState.Succeeded);
            using var drifted = await client.PostAsJsonAsync("/internal/ingest", Ingest(correlation: "drift-attempt"), ct);
            drifted.EnsureSuccessStatusCode();
            var driftId = ulong.Parse((await drifted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            var beforeDrift = h.Gateway.Dispatches.Count;
            await using (var edit = app.Services.CreateAsyncScope())
            {
                var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                if (ha) (await db.Set<NetRatel.Infrastructure.SystemPairing.PairingConnectionRecord>().SingleAsync(ct)).Active = false;
                else (await db.Jobs.SingleAsync(row => row.Id == 41, ct)).Description = "Changed after admission";
                await db.SaveChangesAsync(ct);
            }
            await h.FillAsync(driftId, ct);
            h.Gateway.Dispatches.Count.Should().Be(beforeDrift);
            (await h.ReadAsync(driftId, ct)).Status.Should().Be(JobRunState.Failed);
        }
        finally { Directory.Delete(directory, true); }
    }
}
