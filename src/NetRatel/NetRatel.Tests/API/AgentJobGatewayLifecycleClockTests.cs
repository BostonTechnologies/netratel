using AwesomeAssertions;
using NetRatel.AgentGateway.Contracts.V1;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class AgentCommandJobControlGatewayServiceTests
{
    [Theory]
    [InlineData(-120)]
    [InlineData(120)]
    public async Task JobLifecycle_UsesServerReceiptTime_WithoutComparingIndependentClocks(int agentOffsetSeconds)
    {
        var requestedAt = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        var receivedAt = requestedAt.AddSeconds(1);
        using var fixture = await Fixture.CreateAsync("job", new JobReceiptClock(receivedAt));
        fixture.EnableJobReceipts = true;
        fixture.JobRequestedAt = requestedAt;
        fixture.JobStatusAt = requestedAt.AddSeconds(agentOffsetSeconds);
        using var call = fixture.Connect();
        await call.SendHello();
        (await call.Read(CancellationToken.None)).Should().BeTrue();
        call.CurrentJobFrame!().Accepted.Capabilities.Should().Contain("lifecycle-receipts-v1");
        await call.SendLifecycle();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await call.Read(timeout.Token)).Should().BeTrue();

        call.CurrentJobFrame!().PayloadCase.Should().Be(GatewayJobFrame.PayloadOneofCase.Receipt);
        fixture.RecordedJobLifecycle!.RequestedAtUtc.Should().Be(requestedAt);
        fixture.RecordedJobLifecycle.StatusAtUtc.Should().Be(requestedAt.AddSeconds(agentOffsetSeconds));
        fixture.RecordedJobLifecycle.ReceivedAtUtc.Should().Be(receivedAt);
        fixture.RecordedJobLifecycle.StartedAtUtc.Should().BeNull();
        fixture.RecordedJobLifecycle.ExitCode.Should().BeNull();
        fixture.RecordedJobLifecycle.AuthenticatedOwner!.Value.ConnectionId.Should().Be(fixture.ConnectionId);
    }

    [Fact]
    public async Task JobLifecycle_CommitAcknowledgementWaitsForAuthority()
    {
        using var fixture = await Fixture.CreateAsync("job");
        fixture.EnableJobReceipts = true;
        fixture.BlockLifecycle = true;
        using var call = fixture.Connect();
        await call.SendHello();
        (await call.Read(CancellationToken.None)).Should().BeTrue();
        await call.SendLifecycle();
        await fixture.LifecycleBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nextResponse = call.Read(timeout.Token);
        nextResponse.IsCompleted.Should().BeFalse("transport receipt must wait for the durable authority boundary");
        fixture.ReleaseLifecycle.TrySetResult();
        (await nextResponse).Should().BeTrue();
        var receipt = call.CurrentJobFrame!().Receipt;
        receipt.JobRunId.Should().Be(1);
        receipt.JobStepRunId.Should().Be(3);
        receipt.RequestId.Should().Be("request-1");
        receipt.Version.Should().Be(1);
        receipt.LifecycleSequence.Should().Be(1);
    }

    private sealed class JobReceiptClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
