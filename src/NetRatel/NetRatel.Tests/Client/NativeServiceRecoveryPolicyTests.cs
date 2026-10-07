using AwesomeAssertions;
using NetRatel.Client.Service;
using NetRatel.Client.Service.Updates;
using NetRatel.Shared.Client;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class NativeServiceRecoveryPolicyTests
{
    [Fact]
    public async Task OwnedMigration_IsIdempotent_AndRequiresEffectiveReadback()
    {
        var adapter = new FixtureAdapter(new(true, true, false), new(true, true, true));
        (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(adapter, TestContext.Current.CancellationToken)).Should().Be(ServicePolicyStatus.Applied);
        (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(adapter, TestContext.Current.CancellationToken)).Should().Be(ServicePolicyStatus.AlreadyManaged);
        adapter.Writes.Should().Be(1);
        var ineffective = new FixtureAdapter(new(true, true, false), new(true, true, false));
        (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(ineffective, TestContext.Current.CancellationToken)).Should().Be(ServicePolicyStatus.PermissionOrVerificationFailure);
    }

    [Theory]
    [InlineData(false, true, (int)ServicePolicyStatus.NotApplicable)]
    [InlineData(true, false, (int)ServicePolicyStatus.AdministratorOverride)]
    public async Task UnownedService_AndAdministratorOverrides_ArePreserved(bool owned, bool recognized, int expected)
    {
        var adapter = new FixtureAdapter(new(owned, recognized, false), new(true, true, true));
        (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(adapter, TestContext.Current.CancellationToken)).Should().Be((ServicePolicyStatus)expected);
        adapter.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData("[Service]\nRestart=always\nRestartPreventExitStatus=78\n", true)]
    [InlineData("[Service]\nRestart=always\nRestartSec=4s\n", false)]
    [InlineData("[Unit]\nStartLimitBurst=10\n[Service]\nRestart=always\n", false)]
    [InlineData("[Service]\nRestart=on-failure\n", false)]
    public void LinuxGeneratedPolicy_RecognizesLegacyDefaults_AndPreservesExplicitOverrides(string unit, bool recognized)
    {
        LinuxRecoveryPolicyAdapter.RecognizesGeneratedRecovery(unit).Should().Be(recognized);
        LinuxRecoveryPolicyAdapter.RecognizesGeneratedRecovery(ManagedServiceRecovery.LinuxDropIn).Should().BeTrue();
    }

    [Fact]
    public async Task LinuxCandidateStartup_RetrofitsOwnedLegacyUnit_WithoutChangingIdentityEnvironmentOrResettingLimits()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The Unix adapter fixture requires Linux.");
        var directory = Path.Combine(Path.GetTempPath(), "netratel-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fragment = Path.Combine(directory, "netratel-client.service");
            var launcher = Path.Combine(directory, "netratel-client-start.sh");
            var original = "[Unit]\nDescription=NetRatel Client\n[Service]\nRestart=always\nRestartPreventExitStatus=78\nUser=owned-account\nEnvironment=NetRatelCLIENT__Client__ApiBaseUrl=https://existing.invalid\n";
            await File.WriteAllTextAsync(fragment, original, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(launcher, "#!/usr/bin/env bash\nset -euo pipefail\nexec '/custom root/current/NetRatel.Client' --service\n", TestContext.Current.CancellationToken);
            var dropIn = Path.Combine(directory, "netratel-client.service.d", "50-netratel-recovery.conf");
            var calls = new List<string>();
            Task<string> Run(string file, string[] arguments, CancellationToken stopping)
            {
                calls.Add(file + " " + string.Join(' ', arguments));
                if (file == "readlink") return Task.FromResult(Environment.ProcessPath!);
                if (arguments[0] == "daemon-reload") return Task.FromResult("");
                var applied = File.Exists(dropIn);
                return Task.FromResult($"MainPID={Environment.ProcessId}\nFragmentPath={fragment}\nDropInPaths={(applied ? dropIn : "")}\nExecStart={{ path={launcher} ; argv[]={launcher} ; }}\nRestart=always\nRestartUSec={(applied ? "30s" : "100ms")}\nStartLimitIntervalUSec={(applied ? "0" : "10s")}\nRestartPreventExitStatus=78\n");
            }
            var adapter = new LinuxRecoveryPolicyAdapter(Run);
            (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(adapter, TestContext.Current.CancellationToken)).Should().Be(ServicePolicyStatus.Applied);
            (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(adapter, TestContext.Current.CancellationToken)).Should().Be(ServicePolicyStatus.AlreadyManaged);
            (await File.ReadAllTextAsync(fragment, TestContext.Current.CancellationToken)).Should().Be(original);
            (await File.ReadAllTextAsync(dropIn, TestContext.Current.CancellationToken)).Should().Be(ManagedServiceRecovery.LinuxDropIn);
            calls.Count(x => x == "systemctl daemon-reload").Should().Be(1);
            calls.Should().NotContain(x => x.Contains("reset-failed", StringComparison.Ordinal));
            await File.WriteAllTextAsync(dropIn, "[Service]\nRestartSec=7s\n", TestContext.Current.CancellationToken);
            var custom = new LinuxRecoveryPolicyAdapter((file, arguments, stopping) =>
                file == "systemctl" && arguments[0] == "show" ? Task.FromResult($"MainPID={Environment.ProcessId}\nFragmentPath={fragment}\nDropInPaths={dropIn}\nExecStart={{ path={launcher} ; }}\nRestart=always\nRestartUSec=7s\nStartLimitIntervalUSec=0\nRestartPreventExitStatus=78\n") : Run(file, arguments, stopping));
            (await NativeServiceRecoveryPolicy.ApplyOwnedAsync(custom, TestContext.Current.CancellationToken)).Should().Be(ServicePolicyStatus.AdministratorOverride);
            (await File.ReadAllTextAsync(dropIn, TestContext.Current.CancellationToken)).Should().Contain("RestartSec=7s");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeWorker_StopWins_AndUnexpectedReturnOrFaultIsFailure()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = NativeServiceWorker.ObserveAsync(async stopping =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stopping);
        }, stop.Token, () => 0);
        await entered.Task;
        stop.Cancel();
        (await observed).Should().BeNull();
        (await NativeServiceWorker.ObserveAsync(_ => Task.CompletedTask, CancellationToken.None, () => 0)).Should().Be(1);
        (await NativeServiceWorker.ObserveAsync(_ => Task.FromException(new InvalidOperationException()), CancellationToken.None, () => 0)).Should().Be(1);
        (await NativeServiceWorker.ObserveAsync(_ => Task.FromException(new InvalidOperationException()), stop.Token, () => 1)).Should().BeNull();
    }

    [Fact]
    public void WindowsManagedActions_RepeatFinalRestart_AndPreserveCustomRecovery()
    {
        var current = "C:\\custom root\\versions\\1.2.3\\NetRatel.Client.exe";
        WindowsRecoveryPolicyAdapter.IsOwnedImage('"' + current + "\" --service", current).Should().BeTrue();
        WindowsRecoveryPolicyAdapter.IsOwnedImage('"' + current + '"', current).Should().BeTrue("legacy service mode can be implicit");
        WindowsRecoveryPolicyAdapter.IsOwnedImage('"' + current + "\" --service --enroll foreign", current).Should().BeFalse();
        WindowsRecoveryPolicyAdapter.IsOwnedImage("\"C:\\other\\NetRatel.Client.exe\" --service", current).Should().BeFalse();
        WindowsRecoveryPolicyAdapter.ActionEntry[] managed = [new(1, 30000), new(1, 60000), new(1, 300000)];
        WindowsRecoveryPolicyAdapter.EvaluatePolicy(0, [], null, null).Recognized.Should().BeTrue("an absent old recovery policy is eligible for retrofit");
        WindowsRecoveryPolicyAdapter.EvaluatePolicy(86400, managed, null, null).Desired.Should().BeTrue();
        WindowsRecoveryPolicyAdapter.EvaluatePolicy(86400, [.. managed, new(0, 0)], null, null).Recognized.Should().BeFalse("a NONE tail is an administrator override");
        WindowsRecoveryPolicyAdapter.EvaluatePolicy(3600, managed, null, null).Recognized.Should().BeFalse();
        WindowsRecoveryPolicyAdapter.EvaluatePolicy(86400, managed, "custom-command", null).Recognized.Should().BeFalse();
        WindowsRecoveryPolicyAdapter.EvaluatePolicy(86400, managed, null, "custom reboot message").Recognized.Should().BeFalse();
        ManagedServiceRecovery.WindowsActions.Should().Be("restart/30000/restart/60000/restart/300000");
    }

    [Fact]
    public void PendingHandover_RequiresProtectedCurrentPendingState_AndAuthenticatedReadinessEndsIt()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Unix file-protection fixture requires Linux.");
        var directory = Path.Combine(Path.GetTempPath(), "netratel-activation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var requestPath = Path.Combine(directory, "request.json");
            var statePath = Path.Combine(directory, "state.json");
            var resultPath = Path.Combine(directory, "result.json");
            var readyPath = Path.Combine(directory, "ready.json");
            var now = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
            var attemptId = Guid.NewGuid();
            var releaseId = Guid.NewGuid();
            void Write(string path, object value)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(value));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            Write(requestPath, new { schema = "netratel.update.request.v2", attemptId, releaseId, admissionNonce = "fixture-only", toVersion = "1.2.3", runtimeId = "linux-x64", requestedAtUtc = now });
            Write(statePath, new { state = "verifying", version = "1.2.3", updatedAtUtc = now, attemptId });
            PendingUpdateActivation? Read(string version = "1.2.3", DateTimeOffset? at = null) =>
                AkkaClientAutoUpdateCoordinator.ReadPendingActivationCore(requestPath, statePath, resultPath, readyPath, version, "linux-x64", at ?? now);
            Read()!.AttemptId.Should().Be(attemptId);
            Read("1.2.2").Should().BeNull();
            Read(at: now.AddSeconds(181)).Should().BeNull();
            Write(resultPath, new { schema = "netratel.update.result.v2", attemptId = Guid.NewGuid(), state = "RolledBack" });
            Read().Should().NotBeNull("a previous attempt's result must not complete the current handover");
            Write(readyPath, new { schema = "netratel.update.ready.v2", attemptId, releaseId, version = "1.2.3", confirmationId = Guid.NewGuid() });
            Read().Should().BeNull();
            File.Delete(readyPath);
            File.SetUnixFileMode(requestPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            Read().Should().BeNull("untrusted local update files cannot authorize an immediate attempt");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FixtureAdapter(NativeRecoverySnapshot before, NativeRecoverySnapshot after) : INativeRecoveryPolicyAdapter
    {
        public int Writes { get; private set; }
        public Task<NativeRecoverySnapshot> ReadAsync(CancellationToken stopping) => Task.FromResult(Writes == 0 ? before : after);
        public Task ApplyAsync(CancellationToken stopping) { Writes++; return Task.CompletedTask; }
    }
}
