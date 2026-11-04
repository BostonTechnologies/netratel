using AwesomeAssertions;
using NetRatel.Client.Service;
using NetRatel.Client.Service.Updates;
using NetRatel.Client;
using NetRatel.Application.ClientAuth;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Client;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class NativeServiceRecoveryPolicyTests
{
    [Fact]
    public void LegacyProtectedHandover_ReadsWebDefaults_AndMatchingReadinessEndsIt()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Unix file-protection fixture requires Linux.");
        var directory = Path.Combine(Path.GetTempPath(), "netratel-activation-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var request = Path.Combine(directory, "request.json");
            var state = Path.Combine(directory, "state.json");
            var ready = Path.Combine(directory, "ready.json");
            void Write(string path, string json)
            {
                File.WriteAllText(path, json);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            Write(request, """
                {"SCHEMA":"netratel.update.request.v2","ATTEMPTID":"11111111-2222-4333-8444-555555555555","RELEASEID":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","ADMISSIONNONCE":"fixture-only","TOVERSION":"1.2.3","RUNTIMEID":"linux-x64","REQUESTEDATUTC":"2026-11-04T12:34:56+00:00","future":true}
                """);
            // Native legacy state can omit the optional attempt identity.
            Write(state, """
                {"STATE":"verifying","VERSION":"1.2.3","UPDATEDATUTC":"2026-11-04T12:34:56+00:00","future":null}
                """);
            PendingUpdateActivation? Read() => AkkaClientAutoUpdateCoordinator.ReadPendingActivationCore(request, state,
                Path.Combine(directory, "result.json"), ready, "1.2.3", "linux-x64", DateTimeOffset.Parse("2026-11-04T12:34:56Z"));
            Read()!.AttemptId.Should().Be(Guid.Parse("11111111-2222-4333-8444-555555555555"));
            Write(ready, """
                {"SCHEMA":"netratel.update.ready.v2","ATTEMPTID":"11111111-2222-4333-8444-555555555555","RELEASEID":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","VERSION":"1.2.3","CONFIRMATIONID":"12345678-90ab-4cde-8123-456789abcdef","readyAtUtc":"2026-11-04T12:34:56Z"}
                """);
            Read().Should().BeNull("matching protected readiness ends this handover");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NoOfferAcknowledgement_WritesPresence_AndPreservesLegacyPolicyRevision()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-update-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "policy.json"), """
                {"REVISION":"9","futureField":true}
                """);
            using var http = new HttpClient { BaseAddress = new Uri("https://api.example") };
            var token = new UnusedUpdateToken();
            await using var coordinator = new AkkaClientAutoUpdateCoordinator(new ClientOptions
            {
                ApiBaseUrl = "https://api.example", AutoUpdate = new() { StateDirectory = directory, RuntimeId = "linux-x64" }
            }, "1.2.3+fixture", token, http, TimeProvider.System,
                () => throw new InvalidOperationException("No updater may launch without an offer."), true, _ => { });
            var before = new ConnectHello(); coordinator.PopulateHello(before);
            before.UpdatePolicyRevision.Should().Be(9);
            coordinator.OnAcknowledgement(null, new ClientUpdatePolicy { Revision = 10, AutoUpdateEnabled = false }, null);
            var after = new ConnectHello(); coordinator.PopulateHello(after);
            after.UpdatePolicyRevision.Should().Be(10);
            using var policy = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "policy.json")));
            policy.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("revision", "resumeAttemptId", "resumedAtUtc", "resumeRevision");
            policy.RootElement.GetProperty("resumeAttemptId").ValueKind.Should().Be(JsonValueKind.Null);
            using var presence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "presence.json")));
            presence.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("version", "acknowledgedAtUtc");
            presence.RootElement.GetProperty("version").GetString().Should().Be("1.2.3");
            token.Calls.Should().Be(0);
            File.Exists(Path.Combine(directory, "request.json")).Should().BeFalse();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GatewayActivationCallbacks_RecordTheirOwnTimestamps_AndRetainRequestAndHelloTimes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-activation-callbacks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), """
                {"schema":"netratel.update.request.v2","attemptId":"11111111-2222-4333-8444-555555555555","releaseId":"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","admissionNonce":"fixture-only","toVersion":"1.2.3","runtimeId":"linux-x64"}
                """);
            using var http = new HttpClient { BaseAddress = new Uri("https://api.example") };
            var token = new UnusedUpdateToken();
            await using var coordinator = new AkkaClientAutoUpdateCoordinator(new ClientOptions
            {
                ApiBaseUrl = "https://api.example", AutoUpdate = new() { StateDirectory = directory, RuntimeId = "linux-x64" }
            }, "1.2.3+fixture", token, http, TimeProvider.System,
                () => throw new InvalidOperationException("No updater may launch during diagnostic callbacks."), true, _ => { });
            JsonElement ReadActivation()
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "activation.json")));
                return document.RootElement.Clone();
            }
            static DateTimeOffset? Timestamp(JsonElement record, string name) =>
                record.GetProperty(name).ValueKind == JsonValueKind.Null ? null : record.GetProperty(name).GetDateTimeOffset();
            var hello = new ConnectHello(); coordinator.PopulateHello(hello);
            var initial = ReadActivation();
            var requestAt = Timestamp(initial, "requestLoadedAtUtc");
            var attachedAt = Timestamp(initial, "activationContextAttachedAtUtc");
            Assert.NotNull(requestAt); Assert.NotNull(attachedAt);
            var snapshots = new List<(string Field, DateTimeOffset Before, DateTimeOffset After, JsonElement Record)>();
            foreach (var (field, callback) in new (string, Action)[]
            {
                ("connectAcceptedAtUtc", () => coordinator.OnPresenceConnected(42)),
                ("firstHeartbeatSentAtUtc", () => coordinator.OnActivationHeartbeatSent(42)),
                ("heartbeatAcceptedAtUtc", () => coordinator.OnActivationHeartbeatAccepted(42))
            })
            {
                var before = DateTimeOffset.UtcNow;
                callback();
                snapshots.Add((field, before, DateTimeOffset.UtcNow, ReadActivation()));
            }
            var observed = string.Join(Environment.NewLine, snapshots.Select(snapshot => snapshot.Field + ": " + snapshot.Record.GetRawText()));
            foreach (var snapshot in snapshots)
            {
                var timestamp = Timestamp(snapshot.Record, snapshot.Field);
                Assert.True(timestamp is { } actual && actual >= snapshot.Before && actual <= snapshot.After, observed);
                Timestamp(snapshot.Record, "requestLoadedAtUtc").Should().Be(requestAt);
                Timestamp(snapshot.Record, "activationContextAttachedAtUtc").Should().Be(attachedAt);
                snapshot.Record.GetProperty("attemptId").GetGuid().Should().Be(Guid.Parse(hello.UpdateActivation.AttemptId));
                snapshot.Record.GetProperty("releaseId").GetGuid().Should().Be(Guid.Parse(hello.UpdateActivation.ReleaseId));
                snapshot.Record.GetProperty("processId").GetInt32().Should().Be(Environment.ProcessId);
                snapshot.Record.GetProperty("connectionEpoch").GetInt64().Should().Be(42);
                Timestamp(snapshots[^1].Record, snapshot.Field).Should().Be(timestamp);
            }
            token.Calls.Should().Be(0);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class UnusedUpdateToken : IAgentTokenService
    {
        public int Calls { get; private set; }
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("No token request may run without an offer.");
        }
    }

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
