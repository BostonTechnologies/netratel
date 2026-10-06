using AwesomeAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;
using Wire = NetRatel.AgentGateway.Contracts.V1;

namespace NetRatel.Tests.Services;

public sealed class ClientServicesContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CollectionFailureCannotClaimThatAnExpectedServiceIsMissing()
    {
        var missing = WindowsObservation() with { State = ClientServiceState.Missing, RawState = "not-found", AuthoritativeMissing = true };
        var result = Result([missing], ServiceSnapshotKind.Watch);
        ClientServiceContractValidator.TryValidateResult(result, out _).Should().BeTrue();

        foreach (var status in new[] { ServiceCollectionStatus.Partial, ServiceCollectionStatus.Error, ServiceCollectionStatus.Unsupported })
        {
            ClientServiceContractValidator.TryValidateResult(result with { Status = status }, out _).Should().BeFalse();
        }
        ClientServiceContractValidator.TryValidateResult(result with
        {
            Services = [missing with { AuthoritativeMissing = false }]
        }, out var error).Should().BeFalse();
        error.Should().Be("invalid_missing_evidence");
    }

    [Fact]
    public void ServiceIdentityUsesScmCaseInsensitivityAndExactSystemdNames()
    {
        ClientServiceContractValidator.TryValidateResult(Result(
            [WindowsObservation("Spooler"), WindowsObservation("spooler")]), out var error).Should().BeFalse();
        error.Should().Be("duplicate_service_name");

        ClientServiceContractValidator.TryValidateResult(Result(
            [LinuxObservation("Example.service"), LinuxObservation("example.service")]), out _).Should().BeTrue();
    }

    [Fact]
    public void SystemdEnablementIsSeparateFromActivityAndOneshotSubstate()
    {
        var stoppedEnabled = LinuxObservation("enabled.service") with
        {
            State = ClientServiceState.Stopped, RawState = "inactive/dead", ActiveState = "inactive", SubState = "dead", UnitFileState = "enabled"
        };
        var activeOneshot = LinuxObservation("oneshot.service") with { SubState = "exited", UnitFileState = "static" };
        ClientServiceContractValidator.TryValidateResult(Result([stoppedEnabled, activeOneshot]), out _).Should().BeTrue();
        stoppedEnabled.State.Should().Be(ClientServiceState.Stopped);
        activeOneshot.State.Should().Be(ClientServiceState.Running);
    }

    [Fact]
    public void SuccessfulEmptyInventoryRemainsDifferentFromUnsupportedCollection()
    {
        ClientServiceContractValidator.TryValidateResult(Result([]), out _).Should().BeTrue();
        ClientServiceContractValidator.TryValidateResult(Result([]) with { Status = ServiceCollectionStatus.Unsupported }, out _).Should().BeTrue();
        ClientServiceContractValidator.TryValidateResult(Result([WindowsObservation()]) with
        {
            Status = ServiceCollectionStatus.Unsupported
        }, out _).Should().BeFalse();
    }

    [Fact]
    public void InvalidObservationsReturnFixedCategoriesWithoutEchoingSuppliedText()
    {
        const string canary = "private-enrollment-canary";
        var observation = WindowsObservation() with { Name = canary + "\n" };
        ClientServiceContractValidator.TryValidateObservation(observation, out var error).Should().BeFalse();
        error.Should().Be("invalid_service_observation");
        error.Should().NotContain(canary);
    }

    [Theory]
    [InlineData("", ClientServicePlatform.Windows, false)]
    [InlineData(" Spooler", ClientServicePlatform.Windows, false)]
    [InlineData("path/service", ClientServicePlatform.Windows, false)]
    [InlineData("path\\service", ClientServicePlatform.Windows, false)]
    [InlineData("Spooler", ClientServicePlatform.Windows, true)]
    [InlineData("example.service", ClientServicePlatform.LinuxSystemd, true)]
    [InlineData("escaped\\x2dname.service", ClientServicePlatform.LinuxSystemd, true)]
    [InlineData("example.socket", ClientServicePlatform.LinuxSystemd, false)]
    [InlineData("--option.service", ClientServicePlatform.LinuxSystemd, false)]
    [InlineData("../../example.service", ClientServicePlatform.LinuxSystemd, false)]
    public void ServiceNamesAreBoundedStablePlatformIdentifiers(string name, ClientServicePlatform platform, bool accepted)
    {
        ClientServiceContractValidator.IsValidServiceName(name, platform).Should().Be(accepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MalformedUnicodeCannotChangeAnIdentifierDuringWireEncoding(int variant)
    {
        // Custom-attribute string serialization replaces unpaired surrogates, so
        // construct malformed UTF-16 at runtime to exercise the actual validator.
        var malformed = variant switch
        {
            0 => new string((char)0xD800, 1),
            1 => new string((char)0xDC00, 1),
            2 => new string((char)0xD800, 1) + "x",
            _ => new string([(char)0xDC00, (char)0xD800])
        };
        ClientServiceContractValidator.IsValidServiceName("Spooler" + malformed, ClientServicePlatform.Windows).Should().BeFalse();
        ClientServiceContractValidator.TryValidateObservation(WindowsObservation() with { DisplayName = malformed }, out _).Should().BeFalse();
    }

    [Fact]
    public void ValidUnicodePairsRemainSupportedWithoutClaimingAByteLimit()
    {
        ClientServiceContractValidator.IsValidServiceName("service-\uD83D\uDE00", ClientServicePlatform.Windows).Should().BeTrue();
    }

    [Fact]
    public void WatchAndInventoryCountsHaveSeparateLimits()
    {
        var observations = Enumerable.Range(0, ClientServicesLimits.MaximumWatchServices + 1)
            .Select(index => WindowsObservation($"service-{index}"))
            .ToArray();
        ClientServiceContractValidator.TryValidateResult(Result(observations), out _).Should().BeTrue();
        ClientServiceContractValidator.TryValidateResult(Result(observations, ServiceSnapshotKind.Watch), out var error).Should().BeFalse();
        error.Should().Be("service_count_exceeded");
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("expired")]
    [InlineData("lifetime")]
    [InlineData("watch-fast")]
    [InlineData("watch-slow")]
    [InlineData("inventory-fast")]
    [InlineData("inventory-slow")]
    [InlineData("count")]
    [InlineData("duplicates")]
    [InlineData("empty-refresh")]
    public void WatchPolicyRejectsUnboundedOrExpiredSelections(string invalid)
    {
        var policy = Policy();
        policy = invalid switch
        {
            "revision" => policy with { Revision = 0 },
            "expired" => policy with { ExpiresAtUtc = Now },
            "lifetime" => policy with { ExpiresAtUtc = Now + ClientServicesLimits.MaximumPolicyLifetime + TimeSpan.FromSeconds(1) },
            "watch-fast" => policy with { WatchIntervalSeconds = ClientServicesLimits.MinimumWatchIntervalSeconds - 1 },
            "watch-slow" => policy with { WatchIntervalSeconds = ClientServicesLimits.MaximumWatchIntervalSeconds + 1 },
            "inventory-fast" => policy with { InventoryIntervalSeconds = ClientServicesLimits.MinimumInventoryIntervalSeconds - 1 },
            "inventory-slow" => policy with { InventoryIntervalSeconds = ClientServicesLimits.MaximumInventoryIntervalSeconds + 1 },
            "count" => policy with { ServiceNames = Enumerable.Range(0, ClientServicesLimits.MaximumWatchServices + 1).Select(index => $"service-{index}").ToArray() },
            "duplicates" => policy with { ServiceNames = ["Spooler", "Spooler"] },
            "empty-refresh" => policy with { RefreshRequestId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        ClientServiceContractValidator.IsValidPolicy(policy, Now).Should().BeFalse();
    }

    [Fact]
    public void PolicyCanExplicitlySelectNoServicesWithoutInventingMonitors()
    {
        ClientServiceContractValidator.IsValidPolicy(Policy() with { ServiceNames = [] }, Now).Should().BeTrue();
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("connection")]
    [InlineData("epoch")]
    [InlineData("sequence")]
    [InlineData("size")]
    [InlineData("chunk-count")]
    [InlineData("watch-revision")]
    [InlineData("nonterminal-error")]
    public void ApplicationChunkRejectsInvalidTransportMetadata(string invalid)
    {
        var chunk = Chunk();
        chunk = invalid switch
        {
            "identity" => chunk with { Client = new(0, Guid.NewGuid()) },
            "connection" => chunk with { ConnectionId = Guid.Empty },
            "epoch" => chunk with { ConnectionEpoch = 0 },
            "sequence" => chunk with { Sequence = 0 },
            "size" => chunk with { PayloadBytes = ClientServicesLimits.MaximumChunkPayloadBytes + 1 },
            "chunk-count" => chunk with { ChunkIndex = ClientServicesLimits.MaximumChunks },
            "watch-revision" => chunk with { Kind = ServiceSnapshotKind.Watch, WatchPolicyRevision = 0 },
            "nonterminal-error" => chunk with { IsFinal = false, Status = ServiceCollectionStatus.Error, Services = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        ClientServicesValidation.IsValidChunk(chunk).Should().BeFalse();
    }

    [Fact]
    public void AdditiveWireFieldsRoundtripWithoutChangingOrdinaryTelemetry()
    {
        var accepted = new Wire.TelemetryConnectAccepted { TelemetryAuthority = "akka", MaximumInFlightFrames = 1 };
        Wire.TelemetryConnectAccepted.Parser.ParseFrom(accepted.ToByteArray()).AcceptedCapabilities.Should().BeEmpty();
        accepted.AcceptedCapabilities.Add(ClientServicesLimits.Capability);
        Wire.TelemetryConnectAccepted.Parser.ParseFrom(accepted.ToByteArray()).AcceptedCapabilities.Should().Equal(ClientServicesLimits.Capability);

        var services = new Wire.AgentTelemetryFrame
        {
            TenantId = 2, ClientId = Guid.NewGuid().ToString("D"), ConnectionEpoch = 1,
            ConnectionId = Guid.NewGuid().ToString("D"), Sequence = 1,
            ServicesChunk = new Wire.ServiceSnapshotChunk
            {
                CollectionId = Guid.NewGuid().ToString("D"), IsFinal = true,
                Kind = Wire.ServiceSnapshotType.Inventory, Status = Wire.ServiceCollectionCompleteness.Complete,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now)
            }
        };
        var restored = Wire.AgentTelemetryFrame.Parser.ParseFrom(services.ToByteArray());
        restored.PayloadCase.Should().Be(Wire.AgentTelemetryFrame.PayloadOneofCase.ServicesChunk);
        restored.ServicesChunk.Should().Be(services.ServicesChunk);

        services.Snapshot = new Wire.TelemetryFrame { Sequence = 2 };
        restored = Wire.AgentTelemetryFrame.Parser.ParseFrom(services.ToByteArray());
        restored.PayloadCase.Should().Be(Wire.AgentTelemetryFrame.PayloadOneofCase.Snapshot);
        restored.ServicesChunk.Should().BeNull();
        restored.Snapshot.Sequence.Should().Be(2);
    }

    private static ClientServiceObservation WindowsObservation(string name = "Spooler") =>
        new(name, name, ClientServicePlatform.Windows, ClientServiceState.Running, "Running", "Automatic", null, null, null, null, Now);

    private static ClientServiceObservation LinuxObservation(string name) =>
        new(name, name, ClientServicePlatform.LinuxSystemd, ClientServiceState.Running, "active/running", null, "loaded", "active", "running", "enabled", Now);

    private static ServiceCollectionResult Result(IReadOnlyList<ClientServiceObservation> observations, ServiceSnapshotKind kind = ServiceSnapshotKind.Inventory) =>
        new(Guid.NewGuid(), kind, ServiceCollectionStatus.Complete, Now, observations, WatchPolicyRevision: kind == ServiceSnapshotKind.Watch ? 1UL : 0UL);

    private static ClientServiceWatchPolicyDto Policy() =>
        new(1, ["Spooler"], ClientServicesLimits.DefaultWatchIntervalSeconds, ClientServicesLimits.DefaultInventoryIntervalSeconds, Now.AddMinutes(10));

    private static ClientServicesChunk Chunk() =>
        new(new ClientKey(2, Guid.NewGuid()), Guid.NewGuid(), 1, 1, Guid.NewGuid(), 0, true,
            ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete, Now, Now, 0, [WindowsObservation()], 100);
}
