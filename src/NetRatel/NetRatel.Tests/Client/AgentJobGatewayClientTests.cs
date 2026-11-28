using System.Threading.Channels;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentJobGatewayClientTests
{
    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public void ProtectedDispatchUsesServerTimeAndRetainsItsDeadlineAcrossClockCorrections(int offsetSeconds)
    {
        var serverTime = DateTimeOffset.Parse("2026-11-28T17:00:00Z");
        var clock = new JobClock(serverTime.AddSeconds(offsetSeconds));
        var helloSentAt = clock.GetTimestamp();
        clock.MonotonicTicks += TimeSpan.FromSeconds(2).Ticks;
        var expires = serverTime + ProtectedJobParameterRules.DispatchLifetime;

        ProtectedJobDispatchLease.FromServerExpiry(expires, clock, null, helloSentAt).Should().BeNull();
        var lease = ProtectedJobDispatchLease.FromServerExpiry(expires, clock, serverTime, helloSentAt);
        lease.Should().NotBeNull();
        lease!.Value.Remaining.Should().Be(TimeSpan.FromSeconds(118));
        clock.UtcNow = serverTime.AddDays(1);
        lease.Value.IsExpired.Should().BeFalse();
        clock.UtcNow = serverTime.AddDays(-1);
        clock.MonotonicTicks += lease.Value.Remaining.Ticks;
        lease.Value.IsExpired.Should().BeTrue();
    }

    [Fact]
    public async Task ExpiredProtectedDispatchReportsTerminalFailureAndKeepsTheGatewayUsable()
    {
        var now = DateTimeOffset.Parse("2026-11-28T17:00:00Z");
        var clock = new JobClock(now);
        using var writer = NewWriter(clock);
        var stream = new RecordingWriter();
        await writer.AttachAsync(stream, CancellationToken.None);
        var client = new AgentJobGatewayClient(new(), _ => { });
        using var manager = new ClientTaskManager(writer.PublishTaskStatusAsync, _ => { });
        var dispatch = Dispatch(now.AddMinutes(-3));
        dispatch.TaskType = TaskKinds.ExecShellCommand;
        var credential = new RunAsCredential { Username = "synthetic", Password = "synthetic-password" };
        var protectedDispatch = new ProtectedJobStepDispatch
        {
            Step = dispatch, ExpiresAtUtc = Timestamp.FromDateTimeOffset(now.AddMinutes(-1)), Credential = credential
        };

        client.HandleProtectedDispatch(protectedDispatch, manager, writer, new HashSet<string>(), clock, now, 0, CancellationToken.None);

        var failed = await stream.ReadAsync();
        failed.Lifecycle.Status.Should().Be(JobLifecycleStatus.Failed);
        failed.Lifecycle.StartedAtUtc.Should().BeNull();
        failed.Lifecycle.ResultJson.Should().Contain("protected_dispatch_expired").And.NotContain("synthetic");
        credential.Password.Should().BeEmpty();
        protectedDispatch.Credential.Should().BeNull();
        writer.Acknowledge(Receipt(failed));
        writer.TryRegisterDispatch(dispatch).Should().BeFalse();

        var next = Dispatch(now);
        next.RequestId = "request-2";
        writer.TryRegisterDispatch(next).Should().BeTrue();
        var accepted = writer.PublishLifecycleAsync(next.RequestId, JobLifecycleStatus.Accepted, null, null, 0);
        writer.Acknowledge(Receipt(await stream.ReadAsync()));
        await accepted;
    }

    [Fact]
    public async Task IndependentClocksAndBackwardCorrection_PreserveEventTimeAndOrderedCommittedEvidence()
    {
        var requestedAt = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        var clock = new JobClock(requestedAt.AddMinutes(-2));
        using var writer = NewWriter(clock);
        var stream = new RecordingWriter();
        await writer.AttachAsync(stream, CancellationToken.None);
        writer.TryRegisterDispatch(Dispatch(requestedAt)).Should().BeTrue();

        var admission = writer.PublishLifecycleAsync("request-1", JobLifecycleStatus.Accepted, null, null, 0);
        var accepted = await stream.ReadAsync();
        accepted.Lifecycle.StatusAtUtc.ToDateTimeOffset().Should().Be(requestedAt.AddMinutes(-2));
        accepted.Lifecycle.StartedAtUtc.Should().BeNull();
        accepted.Lifecycle.HasExitCode.Should().BeFalse();
        admission.IsCompleted.Should().BeFalse();
        writer.Acknowledge(Receipt(accepted));
        await admission;

        var acceptedPublication = writer.PublishTaskStatusAsync("request-1", "started", null, null);
        var started = await stream.ReadAsync();
        started.Lifecycle.StatusAtUtc.ToDateTimeOffset().Should().Be(requestedAt.AddMinutes(-2));
        started.Lifecycle.StartedAtUtc.Should().Be(started.Lifecycle.StatusAtUtc);
        started.Lifecycle.HasExitCode.Should().BeFalse();
        acceptedPublication.IsCompleted.Should().BeFalse("gRPC write completion is not a committed lifecycle receipt");
        clock.UtcNow = requestedAt.AddMinutes(-3);
        var progressPublication = writer.PublishTaskStatusAsync("request-1", "progress", null, null);
        clock.UtcNow = requestedAt.AddMinutes(-5);
        writer.Acknowledge(Receipt(started));
        await acceptedPublication;
        var progress = await stream.ReadAsync();
        progress.Lifecycle.StatusAtUtc.ToDateTimeOffset().Should().Be(requestedAt.AddMinutes(-3),
            "the event time is captured before a prior acknowledgement and wall-clock correction");
        writer.Acknowledge(Receipt(progress));
        await progressPublication;

        var completedPublication = writer.PublishTaskStatusAsync("request-1", "completed", "{\"stdout\":[\"done\"]}", 0);
        var completed = await stream.ReadAsync();
        completed.Lifecycle.StatusAtUtc.ToDateTimeOffset().Should().Be(clock.UtcNow);
        completed.Lifecycle.StartedAtUtc.Should().Be(started.Lifecycle.StartedAtUtc);
        completed.Lifecycle.RequestedAtUtc.Should().Be(Timestamp.FromDateTimeOffset(requestedAt));
        completed.Lifecycle.Version.Should().Be(progress.Lifecycle.Version + 1);
        completed.Lifecycle.LifecycleSequence.Should().Be(progress.Lifecycle.LifecycleSequence + 1);
        completed.Lifecycle.HasExitCode.Should().BeTrue();
        completed.Lifecycle.ExitCode.Should().Be(0);
        writer.TryGetRequestId(1, out _).Should().BeTrue("terminal evidence remains pending until commit acknowledgement");
        writer.Acknowledge(Receipt(completed));
        await completedPublication;
        writer.TryGetRequestId(1, out _).Should().BeFalse();
        writer.TryRegisterDispatch(Dispatch(requestedAt)).Should().BeFalse("the observed side effect cannot be repeated after completion");
    }

    [Fact]
    public async Task LostTerminalAcknowledgement_ReplaysIdenticalEvidenceOnReconnect_WithoutExecutingAgain()
    {
        var clock = new JobClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        using var writer = NewWriter(clock);
        var oldStream = new RecordingWriter();
        await writer.AttachAsync(oldStream, CancellationToken.None);
        writer.TryRegisterDispatch(Dispatch(clock.UtcNow)).Should().BeTrue();
        await PublishAndAcknowledgeAsync(writer, oldStream, JobLifecycleStatus.Accepted);
        await PublishAndAcknowledgeAsync(writer, oldStream, JobLifecycleStatus.Started);
        var publication = writer.PublishTaskStatusAsync("request-1", "completed", "{\"stdout\":[\"effect applied\"]}", 0);
        var original = await oldStream.ReadAsync();
        writer.Detach(oldStream);
        clock.UtcNow = clock.UtcNow.AddMinutes(-10);
        var replacement = new RecordingWriter();
        await writer.AttachAsync(replacement, CancellationToken.None);
        var replay = await replacement.ReadAsync();

        replay.Lifecycle.Should().Be(original.Lifecycle, "retry preserves the immutable event including event time and result");
        replay.Sequence.Should().Be(1, "physical frame ordering restarts for the new transport");
        publication.IsCompleted.Should().BeFalse();
        writer.TryRegisterDispatch(Dispatch(clock.UtcNow)).Should().BeFalse();
        writer.Acknowledge(Receipt(replay));
        await publication;
        writer.Acknowledge(Receipt(replay));
        writer.TryRegisterDispatch(Dispatch(clock.UtcNow)).Should().BeFalse();
    }

    [Fact]
    public async Task FailedTransportWrite_RetainsPreExecutionFailureWithNullableStartAndExit()
    {
        var clock = new JobClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        using var writer = NewWriter(clock);
        var admissionStream = new RecordingWriter();
        await writer.AttachAsync(admissionStream, CancellationToken.None);
        writer.TryRegisterDispatch(Dispatch(clock.UtcNow)).Should().BeTrue();
        await PublishAndAcknowledgeAsync(writer, admissionStream, JobLifecycleStatus.Accepted);
        writer.Detach(admissionStream);
        await writer.AttachAsync(new FaultingWriter(), CancellationToken.None);
        var publication = writer.PublishTaskStatusAsync("request-1", "failed", "{\"error\":\"interpreter unavailable\"}", null);
        publication.IsCompleted.Should().BeFalse();
        var replacement = new RecordingWriter();
        await writer.AttachAsync(replacement, CancellationToken.None);
        var replay = await replacement.ReadAsync();
        replay.Lifecycle.StartedAtUtc.Should().BeNull();
        replay.Lifecycle.HasExitCode.Should().BeFalse();
        replay.Lifecycle.Status.Should().Be(JobLifecycleStatus.Failed);
        writer.Acknowledge(Receipt(replay));
        await publication;
    }

    [Fact]
    public async Task WrongReceiptAndFencedSession_CannotAdvancePendingCursor()
    {
        var clock = new JobClock(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        using var writer = NewWriter(clock);
        var stream = new RecordingWriter();
        await writer.AttachAsync(stream, CancellationToken.None);
        writer.TryRegisterDispatch(Dispatch(clock.UtcNow)).Should().BeTrue();
        await PublishAndAcknowledgeAsync(writer, stream, JobLifecycleStatus.Accepted);
        var publication = writer.PublishTaskStatusAsync("request-1", "started", null, null);
        var sent = await stream.ReadAsync();
        var wrongReceipt = Receipt(sent); wrongReceipt.JobStepRunId++;
        Assert.Throws<RpcException>(() => writer.Acknowledge(wrongReceipt)).StatusCode.Should().Be(StatusCode.DataLoss);
        publication.IsCompleted.Should().BeFalse();
        var fenced = Accepted(); fenced.ConnectionEpoch++;
        Assert.Throws<RpcException>(() => writer.ValidateInbound(fenced)).StatusCode.Should().Be(StatusCode.DataLoss);
        writer.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
    }

    [Fact]
    public void AdmissionRequiresCommitReceiptSupport_BeforeAnyExecution()
    {
        using var writer = NewWriter(new JobClock(DateTimeOffset.UtcNow));
        var accepted = Accepted();
        Assert.Throws<RpcException>(() => writer.ValidateAccepted(accepted)).StatusCode.Should().Be(StatusCode.FailedPrecondition);
        accepted.Accepted.Capabilities.Add("lifecycle-receipts-v1");
        writer.ValidateAccepted(accepted);
    }

    private static GatewayPresenceSession Presence() => new(3, Guid.Parse("9ed6041f-6e52-4ebb-bb56-21d43e183e7d"),
        4, Guid.Parse("dbdb2f03-b078-4b80-9788-ca7d0dde8ec8"));
    private static AgentJobGatewayClient.JobGatewayWriter NewWriter(TimeProvider clock) => new(Presence(), "1.0", clock, _ => { }, CancellationToken.None);
    private static GatewayJobFrame Accepted() => new()
    {
        ProtocolVersion = "1.0", TenantId = Presence().TenantId, ClientId = Presence().AgentId.ToString("D"),
        ConnectionEpoch = Presence().ConnectionEpoch, ConnectionId = Presence().ConnectionId.ToString("D"),
        Accepted = new JobConnectAccepted { JobAuthority = "akka" }
    };
    private static JobStepDispatch Dispatch(DateTimeOffset requestedAt) => new()
    {
        JobRunId = 1, JobStepId = 2, JobStepRunId = 3, TenantId = 3, Ordinal = 0,
        RequestId = "request-1", CorrelationId = "correlation-1", TaskType = "ExecShellCommand", PayloadJson = "{}",
        NextVersion = 2, NextSequence = 2, RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt)
    };
    private static JobLifecycleReceipt Receipt(AgentJobFrame frame) => new()
    {
        JobRunId = frame.Lifecycle.JobRunId, JobStepRunId = frame.Lifecycle.JobStepRunId, RequestId = frame.Lifecycle.RequestId,
        Version = frame.Lifecycle.Version, LifecycleSequence = frame.Lifecycle.LifecycleSequence
    };
    private static async Task PublishAndAcknowledgeAsync(AgentJobGatewayClient.JobGatewayWriter writer, RecordingWriter stream, JobLifecycleStatus status)
    {
        var publication = writer.PublishLifecycleAsync("request-1", status, null, null, 0);
        writer.Acknowledge(Receipt(await stream.ReadAsync()));
        await publication;
    }
    private sealed class JobClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public long MonotonicTicks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => MonotonicTicks;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class RecordingWriter : IClientStreamWriter<AgentJobFrame>
    {
        private readonly Channel<AgentJobFrame> _frames = Channel.CreateUnbounded<AgentJobFrame>();
        public WriteOptions? WriteOptions { get; set; }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task WriteAsync(AgentJobFrame message) { _frames.Writer.TryWrite(message.Clone()); return Task.CompletedTask; }
        public Task WriteAsync(AgentJobFrame message, CancellationToken cancellationToken) => WriteAsync(message);
        public Task<AgentJobFrame> ReadAsync() => _frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private sealed class FaultingWriter : IClientStreamWriter<AgentJobFrame>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task WriteAsync(AgentJobFrame message) => Task.FromException(new IOException("transport lost before commit receipt"));
        public Task WriteAsync(AgentJobFrame message, CancellationToken cancellationToken) => WriteAsync(message);
    }
}
