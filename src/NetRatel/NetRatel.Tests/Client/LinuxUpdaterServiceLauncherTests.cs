using NetRatel.Client.Service.Updates;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LinuxUpdaterServiceLauncherTests
{
    [Fact]
    public async Task SuccessfulHandoverWaitsForJobAcknowledgementWithoutWaitingForTheUpdater()
    {
        var calls = new List<string[]>();
        await LinuxUpdaterServiceLauncher.StartAsync("netratel-update.service", Path.GetTempPath(), Guid.NewGuid(),
            run: (arguments, _) =>
            {
                calls.Add(arguments);
                return Task.FromResult((0, string.Empty));
            });
        Assert.Equal(new[] { "start", "--no-block", "--no-ask-password", "netratel-update.service" }, Assert.Single(calls));
    }

    [Theory]
    [InlineData("ActiveState=inactive\nJob=\n", true)]
    [InlineData("ActiveState=failed\nJob=\n", true)]
    [InlineData("ActiveState=active\nJob=\n", false)]
    [InlineData("ActiveState=activating\nJob=5\n", false)]
    [InlineData("ActiveState=inactive\nJob=5\n", false)]
    [InlineData("ActiveState=inactive\n", false)]
    public async Task ARejectedStartCanOnlyRetryWhenSystemdProvesThereIsNoRunningOrQueuedJob(string state, bool retrySafe)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-launch-test-" + Guid.NewGuid().ToString("N"));
        var calls = new List<string[]>();
        var failure = await Assert.ThrowsAsync<NativeUpdaterLaunchException>(() =>
            LinuxUpdaterServiceLauncher.StartAsync("netratel-update.service", directory, Guid.NewGuid(),
                run: (arguments, _) =>
                {
                    calls.Add(arguments);
                    return Task.FromResult(arguments[0] == "start" ? (1, string.Empty) : (0, state));
                }));
        Assert.Equal(retrySafe, failure.RetrySafe);
        Assert.Equal("linux_updater_start_rejected", failure.Code);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "show", "--property=ActiveState,Job", "netratel-update.service" }, calls[1]);
    }

    [Theory]
    [InlineData("state.json")]
    [InlineData("result.json")]
    public async Task AStoppedUnitWithNativeAttemptEvidenceCannotBeAutomaticallyRelaunched(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-launch-state-" + Guid.NewGuid().ToString("N"));
        var attemptId = Guid.NewGuid();
        try
        {
            ProtectedUpdateState.AtomicWriteJson(Path.Combine(directory, fileName), new { attemptId, state = "applying" });
            var failure = await Assert.ThrowsAsync<NativeUpdaterLaunchException>(() =>
                LinuxUpdaterServiceLauncher.StartAsync("netratel-update.service", directory, attemptId,
                    run: (arguments, _) => Task.FromResult(arguments[0] == "start"
                        ? (1, string.Empty) : (0, "ActiveState=inactive\nJob=\n"))));
            Assert.False(failure.RetrySafe);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnknownUnitStateKeepsAnUncertainHandoverFenced()
    {
        var failure = await Assert.ThrowsAsync<NativeUpdaterLaunchException>(() =>
            LinuxUpdaterServiceLauncher.StartAsync("netratel-update.service", Path.GetTempPath(), Guid.NewGuid(),
                run: (_, _) => Task.FromResult((1, string.Empty))));
        Assert.False(failure.RetrySafe);
    }
}
