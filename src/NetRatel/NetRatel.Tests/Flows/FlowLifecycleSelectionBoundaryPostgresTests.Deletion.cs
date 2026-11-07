using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed partial class FlowLifecycleSelectionBoundaryPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_and_real_ingress_have_one_tenant_fenced_order(bool deletionFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var firstGate = new BoundaryGate(pauseAfterFence: true); var secondGate = new BoundaryGate();
        var protection = new EphemeralDataProtectionProvider();
        await using var first = Provider(connection, protection, firstGate);
        await using var second = Provider(connection, protection, secondGate);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var flowId = Guid.NewGuid(); var versionId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.MigrateAsync(ct); db.Tenants.Add(new Tenant { Id = 7, Name = "Synthetic race tenant" });
            db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 7, Name = "Synthetic race flow", Revision = 1, Enabled = true,
                DraftJson = FlowGraphSerialization.Serialize(FlowTestData.Graph()), CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync(ct);
            var graph = FlowTestData.Graph();
            db.FlowVersions.Add(new() { Id = versionId, FlowId = flowId, TenantId = 7, VersionNumber = 1,
                GraphJson = FlowGraphSerialization.Serialize(graph), ConfigurationHash = FlowGraphSerialization.Hash(graph),
                PublishedAtUtc = now, PublishedBy = "synthetic-operator" });
            await db.SaveChangesAsync(ct);
        }
        var input = new FlowEventEnvelope(7, Guid.NewGuid(), Guid.NewGuid(), versionId, now, FlowTestData.EventData("Synthetic endpoint", 90), FlowTestData.Authority);
        var deletionProvider = deletionFirst ? first : second;
        var ingressProvider = deletionFirst ? second : first;
        Task<FlowDeleteResultDto> Delete() => deletionProvider.GetRequiredService<IFlowDefinitionService>().DeleteAsync(7, flowId, new(1), "synthetic-deleter", ct);
        Task<FlowIngressResult> Ingress() => ingressProvider.GetRequiredService<IFlowEventIngress>().EnqueueAsync(input, ct);
        Task<FlowDeleteResultDto> deletion; Task<FlowIngressResult> ingress;
        if (deletionFirst)
        {
            deletion = Delete(); await firstGate.Paused.WaitAsync(ct); ingress = Ingress();
        }
        else
        {
            ingress = Ingress(); await firstGate.Paused.WaitAsync(ct); deletion = Delete();
        }
        await AssertWaitingForFenceAsync(options, await secondGate.FenceAttempted.WaitAsync(ct), ct);
        firstGate.Release(); await Task.WhenAll(deletion, ingress);
        Assert.Equal(deletionFirst ? FlowDeleteDisposition.Deleted : FlowDeleteDisposition.ActiveWork, (await deletion).Disposition);
        Assert.Equal(deletionFirst ? FlowIngressDisposition.Disabled : FlowIngressDisposition.Enqueued, (await ingress).Disposition);
        await using var verify = new OrchestratorDbContext(options);
        Assert.Equal(deletionFirst ? 0 : 1, await verify.FlowRuns.CountAsync(run => run.Status == FlowRunStatus.Queued, ct));
        Assert.Equal(!deletionFirst, (await verify.FlowDefinitions.SingleAsync(ct)).Enabled);
    }
}
