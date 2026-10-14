using AwesomeAssertions;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ExecutionActivityBrokerTests
{
    [Fact]
    public void ExecutionInvalidations_IsolateTenantAndRequest_AndCoalesceRunChanges()
    {
        var broker = new ExecutionActivityBroker();
        using var task = broker.Subscribe(2, requestId: "task-a")!;
        using var run = broker.Subscribe(2, runId: 100)!;
        using var forbiddenTenant = broker.Subscribe(null, authorizedTenants: [9])!;
        broker.Publish(new(Guid.NewGuid(), 9, 100, "task-a"));
        task.Reader.TryRead(out _).Should().BeFalse();
        run.Reader.TryRead(out _).Should().BeFalse();
        forbiddenTenant.Reader.TryRead(out _).Should().BeTrue();
        broker.Publish(new(Guid.NewGuid(), 2, 100, "other-task"));
        task.Reader.TryRead(out _).Should().BeFalse();
        run.Reader.TryRead(out _).Should().BeTrue();
        ExecutionActivityNotification? last = null;
        for (var index = 0; index < 100; index++)
        { last = new(Guid.NewGuid(), 2, 100, "task-a"); broker.Publish(last); }
        task.Reader.TryRead(out var pending).Should().BeTrue();
        pending.Should().Be(last);
        task.Reader.TryRead(out _).Should().BeFalse();
        forbiddenTenant.Reader.TryRead(out _).Should().BeFalse();
        task.Dispose();
        broker.Publish(new(Guid.NewGuid(), 2, 100, "task-a"));
        task.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void ListenerLossAndRecovery_InvalidateBothTaskAndRunSnapshots()
    {
        var broker = new ExecutionActivityBroker();
        using var task = broker.Subscribe(2, requestId: "task-a")!;
        using var run = broker.Subscribe(2, runId: 100)!;
        foreach (var connected in new[] { true, false, true })
        {
            broker.SetConnected(connected);
            broker.IsConnected.Should().Be(connected);
            task.Reader.TryRead(out var taskChange).Should().BeTrue();
            run.Reader.TryRead(out var runChange).Should().BeTrue();
            taskChange!.TenantId.Should().BeNull();
            runChange!.RunId.Should().BeNull();
        }
    }
}
