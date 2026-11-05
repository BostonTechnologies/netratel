using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

public sealed record FlowScriptExecutionTerminalDto(FlowExecutionOutcome Outcome, int? ExitCode);

/// <summary>Classifies only a committed native terminal result. No elapsed-time, stale-agent or error-text inference.</summary>
public static class FlowScriptExecutionOutcomes
{
    public static FlowExecutionOutcome? FromCommittedResult(string? rawStatus, int? exitCode, string? nativeResultJson) =>
        ClassifyCommittedResult(rawStatus, exitCode, nativeResultJson)?.Outcome;

    public static FlowScriptExecutionTerminalDto? ClassifyCommittedResult(string? rawStatus, int? exitCode, string? nativeResultJson)
    {
        var status = FlowExecutionOutcomes.FromAuthoritativeStatus(rawStatus);
        if (status is null) return null;
        if (status is FlowExecutionOutcome.Denied or FlowExecutionOutcome.Unsupported) return new(status.Value, exitCode);
        var unknown = false;
        var timedOut = false;
        var cancelled = false;
        int? payloadExitCode = null;
        if (!string.IsNullOrWhiteSpace(nativeResultJson))
        {
            try
            {
                using var document = JsonDocument.Parse(nativeResultJson, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) return InvalidBody(status.Value, exitCode);
                var root = document.RootElement;
                unknown = True(root, "outcomeUnknown");
                timedOut = True(root, "timedOut");
                cancelled = True(root, "cancelled");
                if (root.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number &&
                    code.TryGetInt32(out var number)) payloadExitCode = number;
            }
            catch (JsonException)
            {
                // A malformed body cannot prove that an otherwise finished process had a known outcome.
                // Native authoritative timeout/cancellation/unknown states remain decisive.
                return InvalidBody(status.Value, exitCode);
            }
        }
        var acceptedExitCode = exitCode ?? payloadExitCode;
        if (status == FlowExecutionOutcome.Unknown || unknown ||
            exitCode is not null && payloadExitCode is not null && exitCode != payloadExitCode)
            return new(FlowExecutionOutcome.Unknown, acceptedExitCode);
        if (status == FlowExecutionOutcome.TimedOut || timedOut) return new(FlowExecutionOutcome.TimedOut, acceptedExitCode);
        if (status == FlowExecutionOutcome.Cancelled || cancelled) return new(FlowExecutionOutcome.Cancelled, acceptedExitCode);
        if (status == FlowExecutionOutcome.Failed || acceptedExitCode is not null and not 0)
            return new(FlowExecutionOutcome.Failed, acceptedExitCode);
        return new(acceptedExitCode == 0 ? FlowExecutionOutcome.Succeeded : FlowExecutionOutcome.Unknown, acceptedExitCode);
    }

    private static FlowScriptExecutionTerminalDto InvalidBody(FlowExecutionOutcome status, int? exitCode) =>
        new(status is FlowExecutionOutcome.TimedOut or FlowExecutionOutcome.Cancelled or FlowExecutionOutcome.Unknown
            ? status : FlowExecutionOutcome.Unknown, exitCode);

    private static bool True(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
