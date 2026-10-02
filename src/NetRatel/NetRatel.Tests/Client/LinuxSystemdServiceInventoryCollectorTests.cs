using FluentAssertions;
using NetRatel.Client.Service.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LinuxSystemdServiceInventoryCollectorTests
{
    [Fact]
    public async Task Inventory_UnionsInstalledStoppedAndLoadedTransientUnits_WithoutCommandsOrEnvironment()
    {
        var adapter = new FixtureAdapter(arguments => arguments[0] switch
        {
            "list-unit-files" => new(0, "stopped.service disabled disabled\noneshot.service enabled enabled\nalias.service alias -\n"),
            "list-units" => new(0, "transient.service loaded active running Transient\noneshot.service loaded active exited One shot\n"),
            "show" => new(0, Block("canonical.service", "inactive", "dead", "alias", names: "canonical.service alias.service") +
                Block("oneshot.service", "active", "exited", "enabled") + Block("stopped.service", "inactive", "dead", "disabled") +
                Block("transient.service", "active", "running", "transient")),
            _ => throw new InvalidOperationException()
        });
        var collector = new LinuxSystemdServiceInventoryCollector(adapter);

        var result = await collector.CollectInventoryAsync(CancellationToken.None);

        result.Status.Should().Be(ServiceCollectionStatus.Complete);
        result.Services.Select(service => service.Name).Should().BeEquivalentTo("alias.service", "oneshot.service", "stopped.service", "transient.service");
        result.Services.Single(service => service.Name == "stopped.service").State.Should().Be(ClientServiceState.Stopped);
        var oneshot = result.Services.Single(service => service.Name == "oneshot.service");
        oneshot.State.Should().Be(ClientServiceState.Running);
        oneshot.SubState.Should().Be("exited");
        oneshot.UnitFileState.Should().Be("enabled");
        var show = adapter.Commands.Single(command => command[0] == "show");
        show.Should().Contain("--property=Id,Names,Description,LoadState,ActiveState,SubState,UnitFileState");
        show.Should().Contain("--");
        show.Should().NotContain(argument => argument.Contains("Exec", StringComparison.Ordinal) || argument.Contains("Environment", StringComparison.Ordinal));
        ClientServiceContractValidator.TryValidateResult(result, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("loaded", "active", "running", false, ClientServiceState.Running)]
    [InlineData("loaded", "active", "exited", false, ClientServiceState.Running)]
    [InlineData("loaded", "inactive", "dead", false, ClientServiceState.Stopped)]
    [InlineData("loaded", "failed", "failed", false, ClientServiceState.Failed)]
    [InlineData("loaded", "activating", "start", false, ClientServiceState.Starting)]
    [InlineData("loaded", "deactivating", "stop", false, ClientServiceState.Stopping)]
    [InlineData("not-found", "inactive", "dead", false, ClientServiceState.Unknown)]
    [InlineData("not-found", "inactive", "dead", true, ClientServiceState.Missing)]
    [InlineData("error", "inactive", "dead", false, ClientServiceState.Unknown)]
    public void Normalization_PreservesSeparateSystemdState(string load, string active, string sub, bool missing, ClientServiceState expected) =>
        LinuxSystemdServiceInventoryCollector.NormalizeState(load, active, sub, missing).Should().Be(expected);

    [Theory]
    [InlineData(0, false, ServiceCollectionStatus.Complete, ClientServiceState.Missing)]
    [InlineData(1, false, ServiceCollectionStatus.Partial, ClientServiceState.Unknown)]
    [InlineData(0, true, ServiceCollectionStatus.Partial, ClientServiceState.Unknown)]
    public async Task Missing_RequiresSuccessfulUntruncatedExactLookup(int exitCode, bool truncated, ServiceCollectionStatus status, ClientServiceState state)
    {
        var adapter = new FixtureAdapter(_ => new(exitCode, Block("absent.service", "inactive", "dead", "", load: "not-found"), truncated));
        var result = await new LinuxSystemdServiceInventoryCollector(adapter).CollectWatchAsync(["absent.service"], 7, CancellationToken.None);

        result.Status.Should().Be(status);
        result.Services.Single().State.Should().Be(state);
        result.Services.Single().AuthoritativeMissing.Should().Be(state == ClientServiceState.Missing);
        result.WatchPolicyRevision.Should().Be(7);
        ClientServiceContractValidator.TryValidateResult(result, out _).Should().BeTrue();
    }

    [Fact]
    public async Task PartialExactLookup_DemotesMissingEvidenceWhenAnotherServiceCannotBeRead()
    {
        var adapter = new FixtureAdapter(_ => new(0, Block("absent.service", "inactive", "dead", "", load: "not-found")));
        var result = await new LinuxSystemdServiceInventoryCollector(adapter).CollectWatchAsync(["absent.service", "unreadable.service"], 1, CancellationToken.None);
        result.Status.Should().Be(ServiceCollectionStatus.Partial);
        result.Services.Should().OnlyContain(service => service.State == ClientServiceState.Unknown && !service.AuthoritativeMissing);
    }

    [Fact]
    public async Task DiscoveryFailureOrMalformedOutput_CannotBecomeCompleteEmptyInventory()
    {
        var failure = new FixtureAdapter(_ => new(1, ""));
        var result = await new LinuxSystemdServiceInventoryCollector(failure).CollectInventoryAsync(CancellationToken.None);
        result.Status.Should().Be(ServiceCollectionStatus.Error);
        result.Services.Should().BeEmpty();

        var malformed = new FixtureAdapter(arguments => arguments[0] == "list-unit-files" ? new(0, "invalid output") : new(0, ""));
        result = await new LinuxSystemdServiceInventoryCollector(malformed).CollectInventoryAsync(CancellationToken.None);
        result.Status.Should().Be(ServiceCollectionStatus.Partial);
    }

    [Fact]
    public async Task LinuxWatchIdentifiers_StayCaseSensitive_AndAreArgumentValues()
    {
        var adapter = new FixtureAdapter(_ => new(0, Block("Foo.service", "active", "running", "enabled") + Block("foo.service", "inactive", "dead", "disabled")));
        var result = await new LinuxSystemdServiceInventoryCollector(adapter).CollectWatchAsync(["Foo.service", "foo.service"], 8, CancellationToken.None);
        result.Services.Should().HaveCount(2);
        adapter.Commands.Single().SkipWhile(argument => argument != "--").Skip(1).Should().Equal("Foo.service", "foo.service");
    }

    [Fact]
    public async Task WatchSelection_IsBoundedAndRejectsInvalidNamesBeforeQuery()
    {
        var adapter = new FixtureAdapter(_ => throw new InvalidOperationException("must not query"));
        var collector = new LinuxSystemdServiceInventoryCollector(adapter);
        var result = await collector.CollectWatchAsync(Enumerable.Range(0, 65).Select(index => $"unit{index}.service").ToArray(), 1, CancellationToken.None);
        result.Status.Should().Be(ServiceCollectionStatus.Error);
        result = await collector.CollectWatchAsync(["../bad.service"], 1, CancellationToken.None);
        result.Status.Should().Be(ServiceCollectionStatus.Error);
        adapter.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_ReachesOutstandingOSQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new BlockingAdapter(entered);
        using var cancellation = new CancellationTokenSource();
        var task = new LinuxSystemdServiceInventoryCollector(adapter).CollectInventoryAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static string Block(string name, string active, string sub, string enabled, string load = "loaded", string? names = null) =>
        $"Id={name}\nNames={names ?? name}\nDescription={name} display\nLoadState={load}\nActiveState={active}\nSubState={sub}\nUnitFileState={enabled}\n\n";

    private sealed class FixtureAdapter(Func<IReadOnlyList<string>, SystemdCommandResult> query) : ISystemdServiceAdapter
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public Task<SystemdCommandResult> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments.ToArray());
            return Task.FromResult(query(arguments));
        }
    }

    private sealed class BlockingAdapter(TaskCompletionSource entered) : ISystemdServiceAdapter
    {
        public async Task<SystemdCommandResult> QueryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(0, "");
        }
    }
}
