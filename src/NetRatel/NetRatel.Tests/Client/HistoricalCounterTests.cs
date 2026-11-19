using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class HistoricalCounterTests
{
    [Theory]
    [InlineData(110, 110, 10, 0)]
    [InlineData(100, 110, 10, 100)]
    [InlineData(105, 107, 13, 50)]
    public void Windows_cpu_counts_idle_as_part_of_kernel(int idle, int kernel, int user, int expected)
    {
        var previous = GatewayTelemetrySnapshotCollector.FromWindowsCpuTimes(100, 100, 10);
        var current = GatewayTelemetrySnapshotCollector.FromWindowsCpuTimes((ulong)idle, (ulong)kernel, (ulong)user);
        Assert.True(GatewayTelemetrySnapshotCollector.TryCpuUsage(previous, current, out var usage));
        Assert.Equal(expected, usage);
        Assert.False(GatewayTelemetrySnapshotCollector.TryCpuUsage(current, previous, out _));
    }

    [Fact]
    public void Monotonic_windows_keep_traffic_when_utc_moves_and_clear_on_recreation()
    {
        var clock = new CounterClock();
        var collector = new GatewayNetworkCounters(clock, _ => { });
        TelemetryNetwork Read(ulong rx, ulong tx) => collector.Observe("adapter", "eth0", "fingerprint", rx, tx, CounterReadClock.Read(clock));

        var first = Read(10_000, 20_000);
        Assert.Equal(TelemetryMeasurementQuality.WarmUp, first.Quality);
        Assert.False(first.HasRxBytes);
        clock.Advance(5, 8);
        var shifted = Read(15_000, 20_000);
        Assert.Equal(TelemetryMeasurementQuality.Valid, shifted.Quality);
        Assert.Equal(5_000_000UL, shifted.Window.ElapsedMicroseconds);
        Assert.Equal(1_000, shifted.RxBytesPerSec);
        Assert.Equal(0UL, shifted.TxBytes);
        Assert.True(shifted.HasTxBytes);
        Assert.NotEqual(shifted.Window.AttributedEndUtc, shifted.Window.EndedAtUtc);

        clock.Advance(1, 1);
        var reset = Read(0, 10);
        Assert.Equal(TelemetryMeasurementQuality.Reset, reset.Quality);
        Assert.NotEqual(shifted.CounterGeneration, reset.CounterGeneration);
        Assert.False(reset.HasRxBytes);

        collector.ForgetAbsent(new HashSet<string>());
        clock.Advance(1, 1);
        var recreated = Read(50_000, 50_000);
        Assert.Equal(TelemetryMeasurementQuality.WarmUp, recreated.Quality);
        Assert.NotEqual(reset.CounterGeneration, recreated.CounterGeneration);
        clock.Advance(40, 40);
        Assert.Equal(TelemetryMeasurementQuality.Reset, Read(80_000, 75_000).Quality);
    }

    [Fact]
    public void Supported_sixty_second_fast_cadence_keeps_its_counter_window()
    {
        var clock = new CounterClock();
        var collector = new GatewayNetworkCounters(clock, _ => { });
        var windowLimit = CounterReadClock.WindowLimit(TimeSpan.FromSeconds(60));
        collector.Observe("adapter", "eth0", "fingerprint", 0, 0, CounterReadClock.Read(clock), windowLimit);
        clock.Advance(60, 60);
        var sample = collector.Observe("adapter", "eth0", "fingerprint", 60_000, 0, CounterReadClock.Read(clock), windowLimit);
        Assert.Equal(TelemetryMeasurementQuality.Valid, sample.Quality);
        Assert.Equal(1000, sample.RxBytesPerSec);
    }

    [Fact]
    public void Protobuf_keeps_integer_delta_and_measured_window()
    {
        var clock = new CounterClock();
        var collector = new GatewayNetworkCounters(clock, _ => { });
        collector.Observe("adapter", "eth0", "fingerprint", 0, 0, CounterReadClock.Read(clock));
        clock.Advance(1, 1);
        const ulong bytes = (1UL << 61) + 5;
        var current = collector.Observe("adapter", "eth0", "fingerprint", bytes, 0, CounterReadClock.Read(clock));
        var roundTrip = TelemetryNetwork.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(current));
        Assert.Equal(bytes, roundTrip.RxBytes);
        Assert.Equal(1_000_000UL, roundTrip.Window.ElapsedMicroseconds);
        Assert.Equal(current.CounterGeneration, roundTrip.CounterGeneration);
    }

    private sealed class CounterClock : TimeProvider
    {
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch.AddDays(20_000);
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;

        public void Advance(int elapsedSeconds, int wallSeconds)
        {
            _timestamp += elapsedSeconds * TimeSpan.TicksPerSecond;
            _utc = _utc.AddSeconds(wallSeconds);
        }
    }
}
