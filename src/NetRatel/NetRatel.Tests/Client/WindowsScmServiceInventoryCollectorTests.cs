using System.ComponentModel;
using System.Runtime.InteropServices;
using NetRatel.Client.Service.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class WindowsScmServiceInventoryCollectorTests
{
    [Theory]
    [InlineData(1, 0, 0, ClientServiceState.Stopped, "SERVICE_STOPPED")]
    [InlineData(1, 5, 0, ClientServiceState.Failed, "SERVICE_STOPPED")]
    [InlineData(1, 1066, 7, ClientServiceState.Failed, "SERVICE_STOPPED")]
    [InlineData(2, 0, 0, ClientServiceState.Starting, "SERVICE_START_PENDING")]
    [InlineData(3, 0, 0, ClientServiceState.Stopping, "SERVICE_STOP_PENDING")]
    [InlineData(4, 0, 0, ClientServiceState.Running, "SERVICE_RUNNING")]
    [InlineData(5, 0, 0, ClientServiceState.Starting, "SERVICE_CONTINUE_PENDING")]
    [InlineData(6, 0, 0, ClientServiceState.Unknown, "SERVICE_PAUSE_PENDING")]
    [InlineData(7, 0, 0, ClientServiceState.Paused, "SERVICE_PAUSED")]
    [InlineData(999, 0, 0, ClientServiceState.Unknown, "999")]
    public void Native_enumeration_fixture_preserves_names_raw_state_and_failure_status(
        uint nativeState, uint exitCode, uint specificExitCode, ClientServiceState expected, string raw)
    {
        using var fixture = new NativeEnumerationFixture("Spooler", "Print Spooler", nativeState, exitCode, specificExitCode);
        var read = WindowsScmServiceInventoryAdapter.ParseEnumerationRecord(fixture.Pointer);
        var timestamp = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
        var observation = WindowsScmServiceInventoryCollector.Map(read, timestamp);

        Assert.Equal("Spooler", observation.Name);
        Assert.Equal("Print Spooler", observation.DisplayName);
        Assert.Equal(ClientServicePlatform.Windows, observation.Platform);
        Assert.Equal(expected, observation.State);
        Assert.Equal(raw, observation.RawState);
        Assert.Equal(timestamp, observation.ObservedAtUtc);
        Assert.Null(observation.StartMode);
        Assert.Null(observation.UnitFileState);
        Assert.False(observation.AuthoritativeMissing);
    }

    [Theory]
    [InlineData(0, "Boot")]
    [InlineData(1, "System")]
    [InlineData(2, "Automatic")]
    [InlineData(3, "Manual")]
    [InlineData(4, "Disabled")]
    [InlineData(9, null)]
    public void Start_mode_is_independent_from_stopped_state(uint startType, string? mode)
    {
        var observation = WindowsScmServiceInventoryCollector.Map(new("svc", "Service", 1, StartType: startType), DateTimeOffset.UtcNow);
        Assert.Equal(ClientServiceState.Stopped, observation.State);
        Assert.Equal(mode, observation.StartMode);
    }

    [Fact]
    public async Task Partial_watch_permission_failure_demotes_missing_to_unknown()
    {
        var adapter = new FixtureAdapter(new([
            new("absent", "absent", 0, AuthoritativeMissing: true),
            new("restricted", "restricted", 0)
        ], false, "scm_access_denied"));
        var collector = Create(adapter);

        var result = await collector.CollectWatchAsync(["absent", "restricted"], 42, CancellationToken.None);

        Assert.Equal(ServiceSnapshotKind.Watch, result.Kind);
        Assert.Equal((ulong)42, result.WatchPolicyRevision);
        Assert.Equal(ServiceCollectionStatus.Partial, result.Status);
        Assert.Equal(ClientServiceState.Unknown, result.Services[0].State);
        Assert.False(result.Services[0].AuthoritativeMissing);
        Assert.Equal(ClientServiceState.Unknown, result.Services[1].State);
        Assert.False(result.Services[1].AuthoritativeMissing);
        Assert.True(ClientServiceContractValidator.TryValidateResult(result, out _));
    }

    [Fact]
    public async Task Complete_explicit_lookup_missing_is_authoritative()
    {
        var collector = Create(new FixtureAdapter(new([new("absent", "absent", 0, AuthoritativeMissing: true)], true)));
        var result = await collector.CollectWatchAsync(["absent"], 42, CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Complete, result.Status);
        Assert.Equal(ClientServiceState.Missing, result.Services[0].State);
        Assert.True(result.Services[0].AuthoritativeMissing);
        Assert.True(ClientServiceContractValidator.TryValidateResult(result, out _));
    }

    [Fact]
    public async Task Complete_inventory_includes_stopped_and_running_services()
    {
        var collector = Create(new FixtureAdapter(new([
            new("disabled", "Disabled service", 1, StartType: 4),
            new("running", "Running service", 4, StartType: 2)
        ], true)));
        var result = await collector.CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Complete, result.Status);
        Assert.Equal(2, result.Services.Count);
        Assert.Equal(ClientServiceState.Stopped, result.Services[0].State);
        Assert.Equal("Disabled", result.Services[0].StartMode);
        Assert.Equal(ClientServiceState.Running, result.Services[1].State);
    }

    [Fact]
    public async Task Truncated_and_partial_inventory_never_claims_complete_or_infers_missing()
    {
        var rows = Enumerable.Range(0, ClientServicesLimits.MaximumServices + 1)
            .Select(index => new WindowsScmService($"svc{index}", "Service", 1)).ToArray();
        var result = await Create(new FixtureAdapter(new(rows, true))).CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Partial, result.Status);
        Assert.Equal(ClientServicesLimits.MaximumServices, result.Services.Count);
        Assert.All(result.Services, row => Assert.False(row.AuthoritativeMissing));
    }

    [Fact]
    public async Task Oversized_native_name_is_rejected_without_changing_its_identity()
    {
        using var fixture = new NativeEnumerationFixture(new string('s', ClientServicesLimits.MaximumNameLength + 1), "Service", 4);
        var parsed = WindowsScmServiceInventoryAdapter.ParseEnumerationRecord(fixture.Pointer);
        var result = await Create(new FixtureAdapter(new([parsed], true))).CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Error, result.Status);
        Assert.Empty(result.Services);
    }

    [Fact]
    public async Task Invalid_native_name_is_rejected_and_display_controls_are_bounded()
    {
        var collector = Create(new FixtureAdapter(new([
            new("invalid\nname", "Invalid", 4),
            new("valid", "Label\twith\ncontrols" + new string('s', ClientServicesLimits.MaximumDisplayNameLength), 4)
        ], true)));
        var result = await collector.CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Partial, result.Status);
        var observation = Assert.Single(result.Services);
        Assert.Equal("valid", observation.Name);
        Assert.Equal(ClientServicesLimits.MaximumDisplayNameLength, observation.DisplayName.Length);
        Assert.DoesNotContain(observation.DisplayName, char.IsControl);
        Assert.True(ClientServiceContractValidator.TryValidateResult(result, out _));
    }

    [Fact]
    public async Task Display_truncation_preserves_complete_Unicode_scalars()
    {
        var label = new string('d', ClientServicesLimits.MaximumDisplayNameLength - 1) + "\U0001F600";
        var collector = Create(new FixtureAdapter(new([new("valid", label, 4)], true)));
        var result = await collector.CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Partial, result.Status);
        var observation = Assert.Single(result.Services);
        Assert.Equal(ClientServicesLimits.MaximumDisplayNameLength - 1, observation.DisplayName.Length);
        Assert.False(char.IsHighSurrogate(observation.DisplayName[^1]));
    }

    [Theory]
    [InlineData(5, "scm_access_denied")]
    [InlineData(1722, "scm_query_failed")]
    public async Task Inventory_error_produces_no_missing_observations(int error, string code)
    {
        var adapter = new FixtureAdapter(new([], true)) { Failure = new Win32Exception(error) };
        var result = await Create(adapter).CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Error, result.Status);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(result.Services);
    }

    [Fact]
    public async Task Unsupported_platform_does_not_call_native_adapter()
    {
        var adapter = new FixtureAdapter(new([], true));
        var result = await new WindowsScmServiceInventoryCollector(adapter, isWindows: () => false)
            .CollectInventoryAsync(CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Unsupported, result.Status);
        Assert.Equal(0, adapter.ReadCount);
    }

    [Fact]
    public async Task Watch_selection_is_bounded_and_case_insensitive_duplicates_are_queried_once()
    {
        var adapter = new FixtureAdapter(new([new("Spooler", "Spooler", 4)], true));
        var collector = Create(adapter);
        var result = await collector.CollectWatchAsync(["Spooler", "spooler"], 1, CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Complete, result.Status);
        Assert.Equal(["Spooler"], adapter.Selected);
        var tooMany = Enumerable.Range(0, ClientServicesLimits.MaximumWatchServices + 1).Select(index => $"svc{index}").ToArray();
        var rejected = await collector.CollectWatchAsync(tooMany, 1, CancellationToken.None);
        Assert.Equal("invalid_watch_selection", rejected.ErrorCode);
        Assert.Equal(1, adapter.ReadCount);
    }

    [Fact]
    public async Task Watch_observation_preserves_exact_server_selected_casing()
    {
        var adapter = new FixtureAdapter(new([
            new("Spooler", "Print Spooler", 4),
            new("MissingService", "MissingService", 0, AuthoritativeMissing: true)
        ], true));
        var result = await Create(adapter).CollectWatchAsync(["spooler", "missingService"], 7, CancellationToken.None);
        Assert.Equal(ServiceCollectionStatus.Complete, result.Status);
        Assert.Equal("spooler", result.Services[0].Name);
        Assert.Equal("missingService", result.Services[1].Name);
        Assert.Equal(ClientServiceState.Missing, result.Services[1].State);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var adapter = new FixtureAdapter(new([], true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(adapter).CollectInventoryAsync(cancelled.Token));
        Assert.Equal(0, adapter.ReadCount);
    }

    [Fact]
    public async Task Cancellation_interrupts_an_in_progress_platform_read()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FixtureAdapter(new([], true))
        {
            TokenRead = token =>
            {
                started.TrySetResult();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
            }
        };
        var pending = Create(adapter).CollectInventoryAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Timed_out_native_worker_keeps_gate_until_exit()
    {
        using var release = new ManualResetEventSlim();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FixtureAdapter(new([], true))
        {
            BeforeRead = () => { release.Wait(); exited.TrySetResult(); }
        };
        var collector = new WindowsScmServiceInventoryCollector(adapter, isWindows: () => true, timeout: TimeSpan.FromMilliseconds(30));
        try
        {
            var first = await collector.CollectInventoryAsync(CancellationToken.None);
            Assert.Equal("collection_timeout", first.ErrorCode);
            var second = await collector.CollectInventoryAsync(CancellationToken.None);
            Assert.Equal("collection_busy", second.ErrorCode);
        }
        finally
        {
            release.Set();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static WindowsScmServiceInventoryCollector Create(FixtureAdapter adapter) => new(adapter, isWindows: () => true);

    private sealed class FixtureAdapter(WindowsScmReadResult result) : IWindowsScmServiceInventoryAdapter
    {
        public int ReadCount { get; private set; }
        public IReadOnlyList<string>? Selected { get; private set; }
        public Exception? Failure { get; init; }
        public Action? BeforeRead { get; init; }
        public Action<CancellationToken>? TokenRead { get; init; }

        public WindowsScmReadResult ReadInventory(int maximumServices, CancellationToken cancellationToken) => Read(cancellationToken);
        public WindowsScmReadResult ReadSelected(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            Selected = names;
            return Read(cancellationToken);
        }
        private WindowsScmReadResult Read(CancellationToken cancellationToken)
        {
            ReadCount++;
            BeforeRead?.Invoke();
            TokenRead?.Invoke(cancellationToken);
            if (Failure is not null) throw Failure;
            return result;
        }
    }

    private sealed class NativeEnumerationFixture : IDisposable
    {
        private readonly IntPtr _name;
        private readonly IntPtr _displayName;
        public IntPtr Pointer { get; }

        public NativeEnumerationFixture(string name, string displayName, uint state, uint exitCode = 0, uint specificExitCode = 0)
        {
            _name = Marshal.StringToHGlobalUni(name);
            _displayName = Marshal.StringToHGlobalUni(displayName);
            var statusOffset = 2 * IntPtr.Size;
            var recordSize = (statusOffset + 9 * sizeof(uint) + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
            Pointer = Marshal.AllocHGlobal(recordSize);
            for (var index = 0; index < recordSize; index++) Marshal.WriteByte(Pointer, index, 0);
            Marshal.WriteIntPtr(Pointer, _name);
            Marshal.WriteIntPtr(Pointer, IntPtr.Size, _displayName);
            Marshal.WriteInt32(Pointer, statusOffset + sizeof(uint), unchecked((int)state));
            Marshal.WriteInt32(Pointer, statusOffset + 3 * sizeof(uint), unchecked((int)exitCode));
            Marshal.WriteInt32(Pointer, statusOffset + 4 * sizeof(uint), unchecked((int)specificExitCode));
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_name);
            Marshal.FreeHGlobal(_displayName);
        }
    }
}
