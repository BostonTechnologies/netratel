using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobRunOwnedBoundaryPostgresTests
{
    [Fact]
    public async Task Accepted_native_request_is_observed_after_its_definition_switches_target_mode_without_becoming_an_aggregate()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct, retainDefinitionSnapshot: true);
        var request = await h.SubmitCanonicalRequestAsync("{}", ct);
        var original = await h.StartRequestAsync(request, false, ct);
        var before = await h.DurableSnapshotAsync(original.Id, ct);
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var definition = await db.Jobs.SingleAsync(ct);
            definition.AgentId = null;
            definition.TargetJson = JsonSerializer.Serialize(new JobTargetDefinition
                { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") });
            definition.ExecutionPolicyJson = JsonSerializer.Serialize(new JobExecutionPolicy(2));
            (await db.JobSteps.SingleAsync(ct)).Command = "printf changed-definition";
            await db.SaveChangesAsync(ct);
        }
        var duplicate = await h.StartRequestAsync(request, true, ct);
        duplicate.Id.Should().Be(original.Id); duplicate.IsAggregate.Should().BeFalse(); duplicate.AgentId.Should().Be(h.AgentId);
        (await h.DurableSnapshotAsync(original.Id, ct)).Should().Be(before);
        h.Gateway.Dispatches.Should().Be(1);
        await using var proof = h.Services.CreateAsyncScope();
        var check = proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await check.Set<JobRunNodeRecord>().CountAsync(ct)).Should().Be(0);
        (await check.Set<JobAggregateControlRecord>().CountAsync(ct)).Should().Be(0);
    }
}
