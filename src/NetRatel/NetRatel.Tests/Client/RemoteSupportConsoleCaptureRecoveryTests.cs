using FluentAssertions;
using NetRatel.Client.Service.RemoteSupport;
using System.Runtime.Versioning;
using Xunit;

namespace NetRatel.Tests.Client;

[SupportedOSPlatform("windows")]
public sealed class RemoteSupportConsoleCaptureRecoveryTests
{
    [Fact]
    public void InitialFailures_AreReprobedUntilAFrameSucceeds()
    {
        var now = DateTimeOffset.UtcNow;
        var runner = new FakeRunner(FailedMatrix(), FailedMatrix(), SuccessfulMatrix("backend-c"));
        using var provider = Provider(runner, () => now);

        Action first = () => provider.CaptureFrame(1024, 576);
        first.Should().Throw<RemoteSupportCaptureException>()
            .Which.DeterministicFailure.Should().BeFalse();
        now = now.AddMilliseconds(500);
        Action second = () => provider.CaptureFrame(1024, 576);
        second.Should().Throw<RemoteSupportCaptureException>()
            .Which.DeterministicFailure.Should().BeFalse();
        now = now.AddMilliseconds(500);

        provider.CaptureFrame(1024, 576).BackendName.Should().Be("backend-c");
        runner.RunCount.Should().Be(3);
    }

    [Fact]
    public void InitialFailure_BecomesTerminalOnlyAfterDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var runner = new FakeRunner(FailedMatrix(), FailedMatrix());
        using var provider = Provider(runner, () => now, initialDeadline: TimeSpan.FromSeconds(30));

        Action first = () => provider.CaptureFrame(1024, 576);
        first.Should().Throw<RemoteSupportCaptureException>()
            .Which.DeterministicFailure.Should().BeFalse();
        now = now.AddSeconds(30);

        Action terminal = () => provider.CaptureFrame(1024, 576);
        var failure = terminal.Should().Throw<RemoteSupportCaptureException>().Which;
        failure.DeterministicFailure.Should().BeTrue();
        failure.CaptureState.Should().Be("desktop_capture_failed");
        failure.AttemptCount.Should().Be(2);
        failure.BackendResults.Should().NotBeEmpty();
    }

    [Fact]
    public void EstablishedBackendFailure_InvalidatesAndReprobesMatrix()
    {
        var now = DateTimeOffset.UtcNow;
        var runner = new FakeRunner(SuccessfulMatrix("backend-a"), SuccessfulMatrix("backend-b"));
        runner.Captures.Enqueue((FailedBackend("backend-a"), null));
        using var provider = Provider(runner, () => now);

        provider.CaptureFrame(1024, 576).BackendName.Should().Be("backend-a");
        now = now.AddSeconds(1);
        Action failedFreshCapture = () => provider.CaptureFrame(1024, 576);
        failedFreshCapture.Should().Throw<RemoteSupportCaptureException>()
            .Which.CaptureState.Should().Be("desktop_transition_recovering");
        now = now.AddMilliseconds(500);

        provider.CaptureFrame(1024, 576).BackendName.Should().Be("backend-b");
        runner.RunCount.Should().Be(2);
    }

    [Fact]
    public void Refresh_InvalidatesSelectedBackendAndForcesImmediateMatrixProbe()
    {
        var runner = new FakeRunner(SuccessfulMatrix("backend-a"), SuccessfulMatrix("backend-b"));
        using var provider = Provider(runner, () => DateTimeOffset.UtcNow);

        provider.CaptureFrame(1024, 576).BackendName.Should().Be("backend-a");
        provider.Refresh();
        provider.CaptureFrame(1024, 576).BackendName.Should().Be("backend-b");

        runner.RunCount.Should().Be(2);
    }

    [Theory]
    [InlineData((ushort)0, false, "console")]
    [InlineData((ushort)2, true, "rdp")]
    [InlineData(null, true, "console")]
    [InlineData(null, false, "unknown")]
    public void SessionProtocol_DistinguishesConsoleAndRdp(ushort? protocol, bool activeConsole, string expected)
    {
        RemoteSupportWindowsSessionInventory.ResolveSessionType(protocol, activeConsole)
            .Should().Be(expected);
    }

    private static ConsoleSecureDesktopCaptureProvider Provider(
        IConsoleCaptureBackendRunner runner,
        Func<DateTimeOffset> utcNow,
        TimeSpan? initialDeadline = null) =>
        new(
            "test-session",
            runner,
            utcNow,
            initialDeadline ?? TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(15));

    private static RemoteSupportCaptureBackendMatrixResult SuccessfulMatrix(string backendName)
    {
        var backend = SuccessfulBackend(backendName);
        return new RemoteSupportCaptureBackendMatrixResult(
            [backend],
            backend,
            new RemoteSupportCaptureFrame([0, 0, 0], 1, 1, 1, 1, backendName),
            null);
    }

    private static RemoteSupportCaptureBackendMatrixResult FailedMatrix()
    {
        var backend = FailedBackend("backend-failed");
        return new RemoteSupportCaptureBackendMatrixResult([backend], null, null, null);
    }

    private static RemoteSupportCaptureBackendResult SuccessfulBackend(string backendName) => new()
    {
        BackendName = backendName,
        BackendAvailable = true,
        BackendInitSucceeded = true,
        FirstFrameAttempted = true,
        FirstFrameSucceeded = true,
        TargetDesktop = "Winlogon"
    };

    private static RemoteSupportCaptureBackendResult FailedBackend(string backendName) => new()
    {
        BackendName = backendName,
        BackendAvailable = true,
        BackendInitSucceeded = true,
        FirstFrameAttempted = true,
        FirstFrameSucceeded = false,
        TargetDesktop = "Winlogon",
        Win32Error = 5,
        HResult = unchecked((int)0x80070005)
    };

    private sealed class FakeRunner(params RemoteSupportCaptureBackendMatrixResult[] probes) : IConsoleCaptureBackendRunner
    {
        private readonly Queue<RemoteSupportCaptureBackendMatrixResult> _probes = new(probes);
        public Queue<(RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame)> Captures { get; } = new();
        public int RunCount { get; private set; }

        public RemoteSupportCaptureBackendMatrixResult Run(string sessionId, int maxWidth, int maxHeight, bool writeSuccessfulJpeg)
        {
            RunCount++;
            return _probes.Dequeue();
        }

        public (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) CaptureWithBackend(
            string backendName,
            int maxWidth,
            int maxHeight) => Captures.Count > 0
                ? Captures.Dequeue()
                : (SuccessfulBackend(backendName), new RemoteSupportCaptureFrame([0, 0, 0], 1, 1, 1, 1, backendName));
    }
}
