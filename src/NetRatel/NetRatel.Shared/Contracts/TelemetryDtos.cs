using System;
using System.Collections.Generic;

namespace NetRatel.Shared.Contracts;

public static class AgentTelemetryMetricTypes
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Disk = "disk";
    public const string Network = "network";
    public const string Health = "health";
}

public sealed record AgentTelemetryPointDto(
    DateTimeOffset Timestamp,
    double Value);

public sealed record AgentCpuTelemetryDto(
    double UsagePercent,
    double? LoadAverage = null,
    int? ProcessCount = null);

public sealed record AgentMemoryTelemetryDto(
    long TotalMb,
    long UsedMb,
    long AvailableMb,
    double UsagePercent);

public sealed record AgentDiskTelemetryDto(
    string Scope,
    double TotalGb,
    double UsedGb,
    double FreeGb,
    double UsagePercent);

public sealed record AgentNetworkTelemetryDto(
    string Scope,
    double RxBytesPerSec,
    double TxBytesPerSec);

public sealed record AgentHealthTelemetryDto(
    long UptimeSeconds,
    string? AgentVersion,
    string? OsVersion,
    DateTimeOffset? LastHeartbeat);

public sealed record AgentTelemetrySnapshotDto(
    string ClientIdentity,
    DateTimeOffset Timestamp,
    DateTimeOffset? LastUpdatedUtc,
    AgentCpuTelemetryDto? Cpu,
    AgentMemoryTelemetryDto? Memory,
    IReadOnlyList<AgentDiskTelemetryDto> Disks,
    IReadOnlyList<AgentNetworkTelemetryDto> Networks,
    AgentHealthTelemetryDto? Health,
    IReadOnlyList<AgentTelemetryPointDto> CpuHistory,
    IReadOnlyList<AgentTelemetryPointDto> MemoryHistory,
    IReadOnlyList<AgentTelemetryPointDto> NetworkRxHistory,
    IReadOnlyList<AgentTelemetryPointDto> NetworkTxHistory);
