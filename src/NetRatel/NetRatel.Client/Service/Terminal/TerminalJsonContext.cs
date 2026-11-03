using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Client.Service.Terminal;

// Separate diagnostic shapes retain the original fields (including explicit nulls).
internal sealed record TerminalAvailabilityDiagnostic(string Stage, string AppBaseDirectory, string? ProcessPath, string? PythonPath, string? HelperPath, bool IsAvailable);
internal sealed record TerminalCandidateDiagnostic(string Backend, string Type);
internal sealed record TerminalCandidatesDiagnostic(string Stage, TerminalCandidateDiagnostic[] Values);
internal sealed record TerminalSelectionFailureDiagnostic(string Stage, bool ForceNative, bool ForcePython, string Error);
internal sealed record TerminalBackendFailureDiagnostic(string Stage, bool ForceNative, bool ForcePython, string ExpectedBackend, string ActualBackend);
internal sealed record TerminalStartedDiagnostic(string Stage, string Backend, bool ForceNative, bool ForcePython, bool Native);
internal sealed record TerminalStageDiagnostic(string Stage, string Result);
internal sealed record TerminalResizeDiagnostic(string Stage, string Result, int RequestedCols, int RequestedRows, int? AppliedCols, int? AppliedRows, string? Error);
internal sealed record TerminalPassedDiagnostic(string Stage, bool Reaped, string Output);
internal sealed record TerminalFailureDiagnostic(string Stage, string Error, string Output);

internal sealed record UnixPtyInputMessage(string Type, string Data);
internal sealed record UnixPtyResizeMessage(string Type, long Id, int Cols, int Rows);
internal sealed record UnixPtyCloseMessage(string Type, string? Reason);

internal sealed class UnixPtyHelperStatus
{
    public string? Type { get; set; }
    public long? Id { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int? Pid { get; set; }
    public int? Cols { get; set; }
    public int? Rows { get; set; }
}

// Web defaults preserve case-insensitive status reads and quoted numeric fields.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TerminalAvailabilityDiagnostic))]
[JsonSerializable(typeof(TerminalCandidatesDiagnostic))]
[JsonSerializable(typeof(TerminalSelectionFailureDiagnostic))]
[JsonSerializable(typeof(TerminalBackendFailureDiagnostic))]
[JsonSerializable(typeof(TerminalStartedDiagnostic))]
[JsonSerializable(typeof(TerminalStageDiagnostic))]
[JsonSerializable(typeof(TerminalResizeDiagnostic))]
[JsonSerializable(typeof(TerminalPassedDiagnostic))]
[JsonSerializable(typeof(TerminalFailureDiagnostic))]
[JsonSerializable(typeof(UnixPtyInputMessage))]
[JsonSerializable(typeof(UnixPtyResizeMessage))]
[JsonSerializable(typeof(UnixPtyCloseMessage))]
[JsonSerializable(typeof(UnixPtyHelperStatus))]
internal partial class TerminalJsonContext : JsonSerializerContext;
