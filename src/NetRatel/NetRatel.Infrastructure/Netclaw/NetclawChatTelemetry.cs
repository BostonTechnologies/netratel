using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace NetRatel.Infrastructure.Netclaw;

internal static class NetclawChatTelemetry
{
    private static readonly Meter Meter = new("NetRatel.Chat");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("netratel.chat.lifecycle.events");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("netratel.chat.lifecycle.elapsed", "s");

    public static void Record(string phase, string? outcome = null, double? seconds = null)
    {
        Events.Add(1, new KeyValuePair<string, object?>("phase", phase), new("outcome", outcome));
        if (seconds is >= 0) Duration.Record(seconds.Value, new KeyValuePair<string, object?>("phase", phase), new("outcome", outcome));
    }
}

public sealed partial class NetclawInvestigationWorker
{
    private static bool IsMeaningfulOutput(string? type) => type is "text" or "text_delta" or "buffer_flush" or "turn_completed" or
        "tool_call" or "tool_result" or "tool_interaction" or "tool_interaction_closed" or "error" or "subagent" or "file";

    private static string NativeErrorCategory(JsonElement output) => Value(output, "errorCategory") switch
    {
        "ProviderFailure" => "ProviderFailure",
        "ToolFailure" => "ToolFailure",
        "SessionFailure" => "SessionFailure",
        _ => "UnknownError"
    };

    private static string? NativeErrorCorrelation(JsonElement output) =>
        Guid.TryParse(Value(output, "errorCorrelationId"), out var reference) ? reference.ToString("N") : null;

    private void ObserveLifecycle(string phase, Guid id, SessionOwner owner, string? outcome = null, string? nativeCorrelation = null)
    {
        NetclawChatTelemetry.Record(phase, outcome, owner.AcceptedAtUtc == default ? null : (clock.GetUtcNow() - owner.AcceptedAtUtc).TotalSeconds);
        logger.LogInformation(
            "NetClaw chat {Phase}: investigation {InvestigationId}, tenant {TenantId}, message {MessageId}, native session {SessionId}, native turn {NativeTurn}, outcome {Outcome}, native correlation {NativeCorrelation}",
            phase, id, owner.Profile.TenantId, owner.MessageId, owner.SessionId, owner.LastTerminalOrdinal,
            outcome, nativeCorrelation);
    }
}
