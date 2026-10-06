using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Client.Service.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Publishes telemetry through the authoritative V2 gateway stream.
/// This publisher has no SpacetimeDB dependency.
/// </summary>
public sealed class AgentGatewayTelemetryPublisher(
    GatewayClientOptions options,
    string agentVersion,
    Action<string> log,
    TimeProvider? timeProvider = null,
    IServiceInventoryCollector? serviceInventoryCollector = null,
    Func<Uri, GrpcChannel>? channelFactory = null)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunForPresenceSessionAsync(
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            log("Telemetry gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
            return;
        }

        var collector = new GatewayTelemetrySnapshotCollector(agentVersion, log);
        using var services = new ServiceCollectionCoordinator(serviceInventoryCollector ?? ServiceInventoryCollectorFactory.Create(_timeProvider), _timeProvider);
        var sequenceCursor = new PresenceTelemetrySequenceCursor();
        var retryDelay = InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunV2StreamAsync(endpoint, session, accessToken, collector, services, sequenceCursor, stoppingToken).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
            {
                log($"Telemetry V2 stream failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunV2StreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken, GatewayTelemetrySnapshotCollector collector, ServiceCollectionCoordinator services, PresenceTelemetrySequenceCursor sequenceCursor, CancellationToken stoppingToken)
    {
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var streamToken = streamCancellation.Token;
        services.BeginStream();
        using var channel = channelFactory?.Invoke(endpoint) ?? GrpcChannel.ForAddress(endpoint);
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Client(channel);
        using var call = client.Connect(new Metadata { { "Authorization", $"Bearer {session.GetAccessToken(accessToken)}" } }, cancellationToken: streamToken);
        var accepted = new TaskCompletionSource<TelemetryConnectAccepted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policies = System.Threading.Channels.Channel.CreateBounded<TelemetrySamplingPolicy>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var ackGate = new object();
        var servicePolicies = System.Threading.Channels.Channel.CreateBounded<ServiceWatchPolicy>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        TaskCompletionSource<ulong>? pendingAcknowledgement = null;
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await call.ResponseStream.MoveNext(streamToken).ConfigureAwait(false))
                {
                    var frame = call.ResponseStream.Current;
                    switch (frame.PayloadCase)
                    {
                        case GatewayTelemetryFrame.PayloadOneofCase.Accepted:
                            accepted.TrySetResult(frame.Accepted);
                            break;
                        case GatewayTelemetryFrame.PayloadOneofCase.SnapshotAccepted:
                            lock (ackGate)
                            {
                                pendingAcknowledgement?.TrySetResult(frame.SnapshotAccepted.AcceptedSequence);
                            }
                            break;
                        case GatewayTelemetryFrame.PayloadOneofCase.TelemetrySamplingPolicy:
                            policies.Writer.TryWrite(frame.TelemetrySamplingPolicy);
                            break;
                        case GatewayTelemetryFrame.PayloadOneofCase.ServiceWatchPolicy:
                            servicePolicies.Writer.TryWrite(frame.ServiceWatchPolicy);
                            break;
                    }
                }
                accepted.TrySetException(new RpcException(new Status(StatusCode.Unavailable, "Telemetry gateway closed before admission.")));
                lock (ackGate) pendingAcknowledgement?.TrySetException(new RpcException(new Status(StatusCode.Unavailable, "Telemetry gateway closed before acknowledgement.")));
            }
            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
            {
                accepted.TrySetCanceled(streamToken);
                lock (ackGate) pendingAcknowledgement?.TrySetCanceled(streamToken);
            }
            catch (Exception exception)
            {
                accepted.TrySetException(exception);
                lock (ackGate) pendingAcknowledgement?.TrySetException(exception);
            }
            finally
            {
                policies.Writer.TryComplete();
                servicePolicies.Writer.TryComplete();
            }
        }, CancellationToken.None);
        try
        {
            await call.RequestStream.WriteAsync(new AgentTelemetryFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = session.TenantId,
                ClientId = session.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Hello = new AgentTelemetryHello { AgentVersion = agentVersion, Capabilities = { "telemetry-rate-control-v1", ClientServicesLimits.Capability } }
            }).ConfigureAwait(false);
            var admission = await accepted.Task.WaitAsync(streamToken).ConfigureAwait(false);
            if (!GatewayWireProtocol.HasAkkaAuthority(admission.TelemetryAuthority))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Telemetry gateway returned an unsupported authority token."));
            }

            var slowInterval = TimeSpan.FromSeconds(Math.Clamp(options.TelemetrySlowIntervalSeconds, 5, 300));
            var servicesEnabled = admission.AcceptedCapabilities.Contains(ClientServicesLimits.Capability);
            var nextSlowSampleAtUtc = DateTimeOffset.MinValue;
            ulong policyRevision = 0;
            var activeFastInterval = BaselineFastInterval();
            var policyExpiresAtUtc = DateTimeOffset.MinValue;
            // This is the only stream writer for metrics and services; both consume the same ACK credit.
            async Task SendAcknowledgedAsync(AgentTelemetryFrame frame)
            {
                var acknowledgement = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (ackGate) pendingAcknowledgement = acknowledgement;
                await call.RequestStream.WriteAsync(frame).ConfigureAwait(false);
                if (await acknowledgement.Task.WaitAsync(streamToken).ConfigureAwait(false) != frame.Sequence)
                    throw new RpcException(new Status(StatusCode.DataLoss, "Telemetry gateway returned an invalid acknowledgement."));
                lock (ackGate) pendingAcknowledgement = null;
            }
            while (!streamToken.IsCancellationRequested)
            {
                while (policies.Reader.TryRead(out var policy))
                {
                    if (IsValidNewerPolicy(policy, policyRevision))
                    {
                        policyRevision = policy.Revision;
                        activeFastInterval = policy.Interactive
                            ? TimeSpan.FromMilliseconds(ClampInteractiveInterval(policy.FastIntervalMilliseconds))
                            : BaselineFastInterval();
                        slowInterval = TimeSpan.FromSeconds(Math.Clamp((int)Math.Min(policy.SlowIntervalSeconds, 300u), 5, 300));
                        policyExpiresAtUtc = policy.ExpiresAtUtc.ToDateTimeOffset();
                    }
                }
                while (servicePolicies.Reader.TryRead(out var servicePolicy))
                {
                    if (servicesEnabled && TryConvertServicePolicy(servicePolicy, out var converted)) services.ApplyPolicy(converted!);
                }
                var now = _timeProvider.GetUtcNow();
                if (servicesEnabled) services.StartDueCollection(streamCancellation.Token);
                if (policyExpiresAtUtc <= now) activeFastInterval = BaselineFastInterval();
                var includeSlow = now >= nextSlowSampleAtUtc;
                if (includeSlow) nextSlowSampleAtUtc = now.Add(slowInterval);
                var sequence = sequenceCursor.Next();
                var snapshot = collector.CreateFrame(session, sequence, now, includeSlow);
                await SendAcknowledgedAsync(new AgentTelemetryFrame
                {
                    ProtocolVersion = options.ProtocolVersion,
                    TenantId = session.TenantId,
                    ClientId = session.AgentId.ToString("D"),
                    ConnectionEpoch = session.ConnectionEpoch,
                    ConnectionId = session.ConnectionId.ToString("D"),
                    Sequence = sequence,
                    Snapshot = snapshot
                }).ConfigureAwait(false);
                if (servicesEnabled && services.TryTakeCompleted(out var collection) && collection is not null)
                {
                    foreach (var chunk in ServiceSnapshotSerializer.CreateChunks(collection, session, options.ProtocolVersion))
                        await SendAcknowledgedAsync(ServiceSnapshotSerializer.CreateEnvelope(chunk, session, options.ProtocolVersion, sequenceCursor.Next())).ConfigureAwait(false);
                }
                if (servicesEnabled) services.StartDueCollection(streamToken);
                using var selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(streamToken);
                var waitInterval = activeFastInterval;
                if (servicesEnabled && services.DelayUntilNextCollection() is { } serviceDelay && serviceDelay < waitInterval) waitInterval = serviceDelay;
                var wait = Task.Delay(waitInterval, _timeProvider, selectionCancellation.Token);
                var policySignal = policies.Reader.WaitToReadAsync(selectionCancellation.Token).AsTask();
                var servicePolicySignal = servicePolicies.Reader.WaitToReadAsync(selectionCancellation.Token).AsTask();
                var collectionSignal = servicesEnabled ? services.PendingCollection : null;
                var signals = collectionSignal is null
                    ? new Task[] { wait, policySignal, servicePolicySignal }
                    : new Task[] { wait, policySignal, servicePolicySignal, collectionSignal };
                var completed = await Task.WhenAny(signals).ConfigureAwait(false);
                if (completed == policySignal)
                {
                    if (!await policySignal.ConfigureAwait(false))
                    {
                        throw new RpcException(new Status(StatusCode.Unavailable, "Telemetry gateway closed its policy stream."));
                    }
                    selectionCancellation.Cancel();
                    await ObserveSelectionCancellationAsync(wait).ConfigureAwait(false);
                    await ObserveSelectionCancellationAsync(servicePolicySignal).ConfigureAwait(false);
                    continue;
                }
                if (completed == servicePolicySignal || completed == collectionSignal)
                {
                    if (completed == servicePolicySignal && !await servicePolicySignal.ConfigureAwait(false))
                        throw new RpcException(new Status(StatusCode.Unavailable, "Telemetry gateway closed its service policy stream."));
                    selectionCancellation.Cancel();
                    await ObserveSelectionCancellationAsync(wait).ConfigureAwait(false);
                    await ObserveSelectionCancellationAsync(policySignal).ConfigureAwait(false);
                    await ObserveSelectionCancellationAsync(servicePolicySignal).ConfigureAwait(false);
                    continue;
                }

                await wait.ConfigureAwait(false);
                selectionCancellation.Cancel();
                await ObserveSelectionCancellationAsync(policySignal).ConfigureAwait(false);
                await ObserveSelectionCancellationAsync(servicePolicySignal).ConfigureAwait(false);
            }
        }
        finally
        {
            streamCancellation.Cancel();
            call.Dispose();
            try { await reader.ConfigureAwait(false); }
            catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
            {
                log("Telemetry V2 reader stopped after stream cancellation.");
            }
        }
    }

    private TimeSpan BaselineFastInterval() => TimeSpan.FromSeconds(Math.Clamp(options.TelemetryFastIntervalSeconds, 1, 60));
    private sealed class PresenceTelemetrySequenceCursor
    {
        private ulong _sequence;
        // Reserve before writing. An ambiguous write/ACK can consume a number but never reuse it on retry.
        public ulong Next() => checked(++_sequence);
    }
    internal static bool TryConvertServicePolicy(ServiceWatchPolicy policy, out ClientServiceWatchPolicyDto? converted)
    {
        converted = null;
        if (policy.ExpiresAtUtc is null || (!string.IsNullOrEmpty(policy.RefreshRequestId) && !Guid.TryParse(policy.RefreshRequestId, out _))) return false;
        try
        {
            converted = new ClientServiceWatchPolicyDto(policy.Revision, policy.ServiceNames.ToArray(), policy.WatchIntervalSeconds, policy.InventoryIntervalSeconds,
                policy.ExpiresAtUtc.ToDateTimeOffset(), Guid.TryParse(policy.RefreshRequestId, out var refresh) ? refresh : null);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    private int ClampInteractiveInterval(uint value) => Math.Clamp((int)Math.Min(value, int.MaxValue), Math.Max(1000, options.TelemetryMinimumIntervalMilliseconds), 60_000);
    private bool IsValidNewerPolicy(TelemetrySamplingPolicy policy, ulong currentRevision) =>
        policy.Revision > currentRevision && policy.FastIntervalMilliseconds > 0 && policy.SlowIntervalSeconds > 0 &&
        policy.ExpiresAtUtc is not null && policy.ExpiresAtUtc.ToDateTimeOffset() > _timeProvider.GetUtcNow() &&
        policy.ExpiresAtUtc.ToDateTimeOffset() <= _timeProvider.GetUtcNow().AddSeconds(Math.Clamp(options.TelemetryPolicyMaximumLifetimeSeconds, 30, 300));

    private static async Task ObserveSelectionCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine("Telemetry scheduler selection was cancelled after its competing wait completed.");
        }
    }
}

internal sealed class GatewayTelemetrySnapshotCollector(string agentVersion, Action<string> log)
{
    private const int MaximumScopesPerFrame = 64;
    private GatewayCpuSample? _previousCpu;
    private readonly Dictionary<string, GatewayNetworkSample> _previousNetworks = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TelemetryDisk> _disks = [];
    private TelemetryTransportHealth? _health;

    public TelemetryFrame CreateFrame(
        GatewayPresenceSession session,
        ulong sequence,
        DateTimeOffset observedAtUtc,
        bool includeSlowMetrics)
    {
        if (includeSlowMetrics)
        {
            _disks = SampleDisks();
            _health = SampleHealth(observedAtUtc);
        }

        var frame = new TelemetryFrame
        {
            ProtocolVersion = "1.0",
            TenantId = session.TenantId,
            ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"),
            Sequence = sequence,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(observedAtUtc),
            TransportHealth = _health
        };

        if (TrySampleCpu(out var cpu))
        {
            frame.Cpu = cpu;
        }

        if (TrySampleMemory(out var memory))
        {
            frame.Memory = memory;
        }

        frame.Disks.AddRange(_disks);
        frame.Networks.AddRange(SampleNetworks(observedAtUtc));
        return frame;
    }

    private bool TrySampleCpu(out TelemetryCpu cpu)
    {
        cpu = new TelemetryCpu();
        try
        {
            if (!TryReadCpuSample(out var current))
            {
                return false;
            }

            var usagePercent = 0d;
            if (_previousCpu is GatewayCpuSample previous)
            {
                var idleDelta = current.IdleTicks - previous.IdleTicks;
                var totalDelta = current.TotalTicks - previous.TotalTicks;
                if (totalDelta > 0)
                {
                    usagePercent = Math.Clamp((1d - ((double)idleDelta / totalDelta)) * 100d, 0d, 100d);
                }
            }

            _previousCpu = current;
            cpu = new TelemetryCpu { UsagePercent = Math.Round(usagePercent, 1) };
            var loadAverage = ReadLoadAverage();
            if (loadAverage.HasValue)
            {
                cpu.LoadAverage = loadAverage.Value;
            }

            var processCount = TryGetProcessCount();
            if (processCount.HasValue)
            {
                cpu.ProcessCount = processCount.Value;
            }

            return true;
        }
        catch (Exception exception)
        {
            log($"Telemetry shadow CPU sample failed: {exception.Message}");
            return false;
        }
    }

    private static bool TryReadCpuSample(out GatewayCpuSample sample)
    {
        sample = default;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var first = File.ReadLines("/proc/stat").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first) || !first.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return false;
            }

            var values = first.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(static value => ulong.TryParse(value, out var parsed) ? parsed : 0UL)
                .ToArray();
            if (values.Length < 4)
            {
                return false;
            }

            var idle = values[3] + (values.Length > 4 ? values[4] : 0UL);
            sample = new GatewayCpuSample(values.Aggregate(0UL, static (total, value) => total + value), idle);
            return true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return false;
        }

        var totalTicks = idleTime + kernelTime + userTime;
        if (totalTicks == 0)
        {
            return false;
        }

        sample = new GatewayCpuSample(totalTicks, idleTime);
        return true;
    }

    private bool TrySampleMemory(out TelemetryMemory memory)
    {
        memory = new TelemetryMemory();
        try
        {
            long totalKb;
            long availableKb;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var values = File.ReadAllLines("/proc/meminfo")
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Trim(), parts => ParseMemInfoKb(parts[1]), StringComparer.OrdinalIgnoreCase);
                if (!values.TryGetValue("MemTotal", out totalKb))
                {
                    return false;
                }

                availableKb = values.TryGetValue("MemAvailable", out var available)
                    ? available
                    : values.GetValueOrDefault("MemFree");
            }
            else if (TryGetGlobalMemoryStatus(out var memoryStatus))
            {
                totalKb = (long)(memoryStatus.TotalPhys / 1024UL);
                availableKb = (long)(memoryStatus.AvailPhys / 1024UL);
            }
            else
            {
                return false;
            }

            var usedKb = Math.Max(0, totalKb - availableKb);
            memory = new TelemetryMemory
            {
                TotalMb = totalKb / 1024d,
                UsedMb = usedKb / 1024d,
                AvailableMb = availableKb / 1024d,
                UsagePercent = totalKb <= 0 ? 0d : Math.Round((double)usedKb / totalKb * 100d, 1)
            };
            return true;
        }
        catch (Exception exception)
        {
            log($"Telemetry shadow memory sample failed: {exception.Message}");
            return false;
        }
    }

    private IReadOnlyList<TelemetryDisk> SampleDisks()
    {
        var disks = new List<TelemetryDisk>();
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var totalGb = drive.TotalSize / 1024d / 1024d / 1024d;
                var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                var usedGb = Math.Max(0, totalGb - freeGb);
                var scope = NormalizeDiskScope(string.IsNullOrWhiteSpace(drive.Name) ? drive.RootDirectory.FullName : drive.Name);
                if (string.IsNullOrEmpty(scope) || !scopes.Add(scope) || disks.Count >= MaximumScopesPerFrame)
                {
                    continue;
                }

                disks.Add(new TelemetryDisk
                {
                    Scope = scope,
                    TotalGb = Math.Round(totalGb, 1),
                    UsedGb = Math.Round(usedGb, 1),
                    FreeGb = Math.Round(freeGb, 1),
                    UsagePercent = totalGb <= 0 ? 0d : Math.Round(usedGb / totalGb * 100d, 1)
                });
            }
            catch (Exception exception)
            {
                log($"Telemetry shadow disk sample skipped for '{drive.Name}': {exception.Message}");
            }
        }

        return disks;
    }

    internal static string NormalizeDiskScope(string? scope)
    {
        return RemoteFilePath.TryNormalize(scope, out var path) ? path : string.Empty;
    }

    private IReadOnlyList<TelemetryNetwork> SampleNetworks(DateTimeOffset now)
    {
        var networks = new List<TelemetryNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var stats = nic.GetIPv4Statistics();
                var current = new GatewayNetworkSample(stats.BytesReceived, stats.BytesSent, now);
                var rx = 0d;
                var tx = 0d;
                if (_previousNetworks.TryGetValue(nic.Id, out var previous))
                {
                    var seconds = (current.Timestamp - previous.Timestamp).TotalSeconds;
                    if (seconds > 0)
                    {
                        rx = Math.Max(0, (current.BytesReceived - previous.BytesReceived) / seconds);
                        tx = Math.Max(0, (current.BytesSent - previous.BytesSent) / seconds);
                    }
                }

                _previousNetworks[nic.Id] = current;
                networks.Add(new TelemetryNetwork
                {
                    Scope = nic.Name,
                    RxBytesPerSec = Math.Round(rx, 1),
                    TxBytesPerSec = Math.Round(tx, 1)
                });
            }
            catch (Exception exception)
            {
                log($"Telemetry shadow network sample skipped for '{nic.Name}': {exception.Message}");
            }
        }

        return networks;
    }

    private TelemetryTransportHealth SampleHealth(DateTimeOffset observedAtUtc) =>
        new()
        {
            UptimeSeconds = ReadUptimeSeconds(),
            AgentVersion = agentVersion,
            OsVersion = RuntimeInformation.OSDescription,
            LastHeartbeat = Timestamp.FromDateTimeOffset(observedAtUtc)
        };

    private static double? ReadLoadAverage()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? Math.Round(parsed, 2) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int? TryGetProcessCount()
    {
        try
        {
            return Process.GetProcesses().Length;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static long ReadUptimeSeconds()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var value = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (double.TryParse(value, CultureInfo.InvariantCulture, out var seconds))
                {
                    return Math.Max(0, (long)seconds);
                }
            }
        }
        catch (IOException)
        {
            return Math.Max(0, Environment.TickCount64 / 1000);
        }

        return Math.Max(0, Environment.TickCount64 / 1000);
    }

    private static long ParseMemInfoKb(string value)
    {
        var numeric = new string(value.Where(char.IsDigit).ToArray());
        return long.TryParse(numeric, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref GatewayMemoryStatus memoryStatus);

    private static bool TryGetGlobalMemoryStatus(out GatewayMemoryStatus memoryStatus)
    {
        memoryStatus = new GatewayMemoryStatus { Length = (uint)Marshal.SizeOf<GatewayMemoryStatus>() };
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && GlobalMemoryStatusEx(ref memoryStatus);
    }

    private readonly record struct GatewayCpuSample(ulong TotalTicks, ulong IdleTicks);
    private readonly record struct GatewayNetworkSample(long BytesReceived, long BytesSent, DateTimeOffset Timestamp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct GatewayMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
