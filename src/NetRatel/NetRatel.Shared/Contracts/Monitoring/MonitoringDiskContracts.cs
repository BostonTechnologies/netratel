using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>Bounded facts from mountinfo in the collecting agent's own mount namespace. Absent means unclassified legacy inventory.</summary>
public sealed record TelemetryDiskMetadataDto(string FileSystem, string Source, string MountRoot,
    string CapacityKey, bool ReadOnly,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultExclusionReason = null);

/// <summary>Exact facts captured from the same accepted physical collection as the monitoring value.</summary>
public sealed record MonitoringDiskFactsDto(ulong TotalBytes, ulong FreeBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TelemetryDiskMetadataDto? Metadata = null);

/// <summary>Whole-string glob matching: '*' matches zero or more characters, '?' one character. Excludes win.</summary>
public sealed record MonitoringDiskPolicyDto(ImmutableArray<string> IncludeMounts, ImmutableArray<string> ExcludeMounts,
    ImmutableArray<string> IncludeFileSystems, ImmutableArray<string> ExcludeFileSystems);

public sealed record MonitoringDiskPreviewDto(string Mount, bool Eligible, string Reason, bool Classified,
    bool Fresh, DateTimeOffset? CollectedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TelemetryDiskMetadataDto? Metadata = null);
