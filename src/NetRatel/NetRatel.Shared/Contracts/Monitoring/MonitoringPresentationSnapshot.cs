using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>Forward-only, immutable incident presentation. Missing on pre-upgrade occurrences and intents.</summary>
public sealed record MonitoringPresentationSnapshotDto(int Version, int TenantId, Guid RuleId, Guid AgentId,
    Guid OccurrenceId, string TenantName, string ClientName, string? Hostname,
    string Title, string Details, string FormattedValue, string Condition, string RecoveryCondition,
    DateTimeOffset ObservedAtUtc, DateTimeOffset RaisedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AlertUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitoringDiskFactsDto? Disk = null);

public static class MonitoringPresentationSnapshot
{
    public const int CurrentVersion = 1;
    public const int MaximumBytes = 4096;
    public const int MaximumLinkLength = 1024;

    public static bool IsValid(MonitoringPresentationSnapshotDto value) => value.Version == CurrentVersion &&
        value.TenantId > 0 && value.RuleId != Guid.Empty && value.OccurrenceId != Guid.Empty &&
        Text(value.TenantName, 128) && Text(value.ClientName, 256) &&
        (value.Hostname is null || Text(value.Hostname, 256)) &&
        Text(value.Title, 200) && Text(value.Details, 3000, multiline: true) &&
        Text(value.FormattedValue, 256) && Text(value.Condition, 256) && Text(value.RecoveryCondition, 256) &&
        value.ObservedAtUtc != default && value.RaisedAtUtc != default && Link(value.ClientUrl) && Link(value.AlertUrl) &&
        (value.Disk is null || value.Disk.TotalBytes > 0 && value.Disk.FreeBytes <= value.Disk.TotalBytes) &&
        JsonSerializer.SerializeToUtf8Bytes(value).Length <= MaximumBytes;

    private static bool Text(string? value, int maximum, bool multiline = false) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));
    private static bool Link(string? value) => value is null || value.Length <= MaximumLinkLength &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !value.Any(char.IsControl);
}
