using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Tasks;

internal sealed record ShellResultEnvelope(int? ExitCode, IReadOnlyList<string> Stdout, IReadOnlyList<string> Stderr,
    bool OutputTruncated, bool OutputStreamed, bool ProcessStarted, bool Cancelled, bool TimedOut, bool OutcomeUnknown,
    ShellExecutionDiagnostics Diagnostics);
internal sealed record ShellExecutionDiagnostics(string? ShellPath, string? Arguments, string? WorkingDirectory,
    double DurationMs, bool Cancelled, bool TimedOut);
internal sealed record ShellProgressEnvelope(string?[] Stdout, string?[] Stderr, long OutputSequence, bool OutputTruncated,
    string? OutputProtectionBinding);
internal sealed record ShellFailureEnvelope(int? ExitCode, string[] Stdout, string[] Stderr, ShellFailureDiagnostics Diagnostics);
internal sealed record ShellFailureDiagnostics(string TaskType, string? Payload, string FailureMessage);
internal sealed record ShellCancelledEnvelope(int? ExitCode, string[] Stdout, string[] Stderr, ShellCancellationDiagnostics Diagnostics);
internal sealed record ShellCancellationDiagnostics(string TaskType, bool Cancelled);

// Outbound shell envelopes used General defaults; inbound payloads keep their
// separate Web context. Preserve compact output, field order and explicit nulls.
[JsonSourceGenerationOptions(JsonSerializerDefaults.General, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ShellResultEnvelope))]
[JsonSerializable(typeof(ShellProgressEnvelope))]
[JsonSerializable(typeof(ShellFailureEnvelope))]
[JsonSerializable(typeof(ShellCancelledEnvelope))]
[JsonSerializable(typeof(string))]
internal partial class ClientShellJsonContext : JsonSerializerContext;
