using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Client.Service.Gateway;

internal sealed class GatewayNetworkCounters(TimeProvider time, Action<string> log)
{
    private readonly Dictionary<string, NetworkRead> _baselines = new(StringComparer.Ordinal);

    public void Sample(TelemetryFrame frame, int maximumScopes, int windowLimit)
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (PlatformNotSupportedException)
        {
            _baselines.Clear();
            frame.NetworkQuality = TelemetryMeasurementQuality.Unsupported;
            return;
        }
        catch (NetworkInformationException exception)
        {
            _baselines.Clear();
            frame.NetworkQuality = TelemetryMeasurementQuality.Failed;
            log($"Telemetry network inventory failed: {exception.Message}");
            return;
        }

        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nic in interfaces.Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                     nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                 .OrderBy(nic => nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown)
                 .ThenBy(nic => nic.Name, StringComparer.Ordinal).ThenBy(nic => nic.Id, StringComparer.Ordinal))
        {
            var name = nic.Name.Trim();
            if (frame.Networks.Count == maximumScopes || name.Length is 0 or > 128 || name.Any(char.IsControl) || !scopes.Add(name))
            {
                frame.NetworkOmittedCount++;
                continue;
            }

            string identity = Hash($"name:{name}");
            try
            {
                var strongIdentity = AdapterIdentity(nic);
                var fingerprint = nic.GetPhysicalAddress().ToString();
                if (OperatingSystem.IsLinux() && !name.Contains('/') && name is not ("." or ".."))
                    fingerprint += ":" + File.GetCreationTimeUtc("/sys/class/net/" + name).Ticks.ToString(CultureInfo.InvariantCulture);
                if (strongIdentity is not null) identity = Hash(strongIdentity);
                if (!seen.Add(identity))
                {
                    frame.NetworkOmittedCount++;
                    continue;
                }
                var statistics = nic.GetIPv4Statistics();
                var received = statistics.BytesReceived;
                var sent = statistics.BytesSent;
                var clock = CounterReadClock.Read(time);
                var evidence = strongIdentity is not null && received >= 0 && sent >= 0
                    ? Observe(identity, name, fingerprint, (ulong)received, (ulong)sent, clock, windowLimit)
                    : WithoutWindow(identity, name, TelemetryMeasurementQuality.Unsupported);
                frame.Networks.Add(evidence);
            }
            catch (Exception exception) when (exception is NetworkInformationException or NotSupportedException or InvalidOperationException)
            {
                _baselines.Remove(identity);
                frame.Networks.Add(WithoutWindow(identity, name, exception is NotSupportedException
                    ? TelemetryMeasurementQuality.Unsupported : TelemetryMeasurementQuality.Failed));
                log($"Telemetry network counters unavailable for '{name}': {exception.Message}");
            }
        }

        ForgetAbsent(seen);
        frame.NetworkQuality = frame.NetworkOmittedCount > 0 ? TelemetryMeasurementQuality.Partial
            : frame.Networks.Count == 0 ? TelemetryMeasurementQuality.Missing : TelemetryMeasurementQuality.Valid;
    }

    internal void ForgetAbsent(IReadOnlySet<string> visible)
    {
        foreach (var identity in _baselines.Keys.Where(key => !visible.Contains(key)).ToArray()) _baselines.Remove(identity);
    }

    internal TelemetryNetwork Observe(string id, string name, string fingerprint, ulong rx, ulong tx, CounterReadClock clock,
        int windowLimit = MetricMeasurementWindow.DefaultWindowLimitSeconds)
    {
        var next = new NetworkRead(fingerprint, rx, tx, Guid.NewGuid(), clock);
        var quality = TelemetryMeasurementQuality.WarmUp;
        var sample = WithoutWindow(id, name, quality, next.Generation);
        if (_baselines.TryGetValue(id, out var previous))
        {
            sample.Quality = TelemetryMeasurementQuality.Reset;
            if (previous.Fingerprint == fingerprint && rx >= previous.Received && tx >= previous.Sent &&
                clock.MeasureFrom(previous.Clock, time, out var attributed, windowLimit) is { } window)
            {
                next = next with { Generation = previous.Generation, Clock = attributed };
                sample.CounterGeneration = previous.Generation.ToString("D");
                sample.Quality = TelemetryMeasurementQuality.Valid;
                sample.Window = window;
                sample.RxBytes = rx - previous.Received;
                sample.TxBytes = tx - previous.Sent;
                sample.RxBytesPerSec = Math.Round(sample.RxBytes * 1_000_000d / window.ElapsedMicroseconds, 1);
                sample.TxBytesPerSec = Math.Round(sample.TxBytes * 1_000_000d / window.ElapsedMicroseconds, 1);
            }
        }
        _baselines[id] = next;
        return sample;
    }

    private static TelemetryNetwork WithoutWindow(string id, string name, TelemetryMeasurementQuality quality, Guid generation = default) =>
        new() { Scope = name, DisplayName = name, InterfaceId = id, Quality = quality, CounterGeneration = generation.ToString("D") };

    private static string? AdapterIdentity(NetworkInterface nic)
    {
        if (OperatingSystem.IsWindows() && Guid.TryParse(nic.Id, out var adapterId)) return $"windows:{adapterId:D}";
        var properties = nic.GetIPProperties();
        var index = properties.GetIPv4Properties()?.Index ?? properties.GetIPv6Properties()?.Index;
        return index is > 0 ? $"ifindex:{index.Value.ToString(CultureInfo.InvariantCulture)}" : null;
    }

    private static string Hash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    private readonly record struct NetworkRead(string Fingerprint, ulong Received, ulong Sent, Guid Generation, CounterReadClock Clock);
}
