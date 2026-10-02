using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Presence;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class PresenceReadModelPointTests
{
    [Fact]
    public async Task Exact_client_projection_read_does_not_wait_for_blocked_epoch_allocation_or_create_clients()
    {
        var system = ActorSystem.Create("presence-point-" + Guid.NewGuid().ToString("N"));
        var allocator = new PausedEpochStore();
        try
        {
            var client = new ClientKey(90, Guid.NewGuid());
            var readModel = system.ActorOf(PresenceReadModelActor.Props());
            var presence = system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), readModel, allocator));
            var admission = presence.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(client, Guid.NewGuid(), Guid.NewGuid(),
                "v1", "point-test", [], null, DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await allocator.Entered.Task.WaitAsync(timeout.Token);
            var before = await readModel.Ask<ClientPresenceReadModelPointSnapshot>(new GetClientPresenceReadModelByKey(client), TimeSpan.FromSeconds(1));
            before.Snapshot.Should().BeNull();
            (await readModel.Ask<ClientPresenceReadModelSnapshot>(new GetClientPresenceReadModel())).Items.Should().BeEmpty();
            allocator.Resume.TrySetResult(1);
            (await admission).ConnectionEpoch.Should().Be(1);
            var point = await readModel.Ask<ClientPresenceReadModelPointSnapshot>(new GetClientPresenceReadModelByKey(client));
            point.Client.Should().Be(client); point.Snapshot!.ConnectionEpoch.Should().Be(1);
            var foreign = new ClientKey(client.TenantId + 1, client.AgentId);
            (await readModel.Ask<ClientPresenceReadModelPointSnapshot>(new GetClientPresenceReadModelByKey(foreign))).Snapshot.Should().BeNull();
            (await readModel.Ask<ClientPresenceReadModelSnapshot>(new GetClientPresenceReadModel())).Items.Should().HaveCount(1);
        }
        finally { allocator.Resume.TrySetResult(1); await system.Terminate(); }
    }
    private sealed class PausedEpochStore : IClientConnectionEpochStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> AllocateAsync(ClientKey client, long minimumEpoch, CancellationToken ct)
        { Entered.TrySetResult(); return Resume.Task.WaitAsync(ct); }
    }
}
