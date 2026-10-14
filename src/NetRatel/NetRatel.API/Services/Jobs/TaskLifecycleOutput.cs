using System.Text.Json;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;

namespace NetRatel.API.Services.Jobs;

/// <summary>Projects bounded native output into the existing ordered task log stream.</summary>
internal static class TaskLifecycleOutput
{
    public static async Task AppendAsync(IJobRunService tasks, ClientKey client, string requestId, string? resultJson,
        DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("outputStreamed", out var streamed) && streamed.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("stdout", out _) && !root.TryGetProperty("stderr", out _)) return;
            var activity = await tasks.GetActivityByRequestIdAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (activity is null) return;
            if (activity.TenantId != client.TenantId || activity.AgentId != client.AgentId)
                throw new InvalidOperationException("The output activity does not match the authenticated execution target.");
            var sequence = root.TryGetProperty("outputSequence", out var nativeSequence) && nativeSequence.TryGetInt64(out var value) && value > 0
                ? value : (await tasks.GetLogsByRequestIdAsync(requestId, cancellationToken).ConfigureAwait(false)).Select(x => x.Sequence).DefaultIfEmpty().Max() + 1;
            var remainingLines = 1024;
            foreach (var stream in new[] { "stdout", "stderr" })
            {
                if (!root.TryGetProperty(stream, out var lines) || lines.ValueKind != JsonValueKind.Array) continue;
                foreach (var line in lines.EnumerateArray())
                {
                    if (remainingLines-- <= 0) return;
                    if (line.ValueKind != JsonValueKind.String) continue;
                    var message = line.GetString() ?? string.Empty;
                    await tasks.AppendTaskLogAsync(new(requestId, activity.Id, activity.ClientIdentity, activity.TenantId,
                        stream, message.Length <= 4096 ? message : message[..4096], sequence++, receivedAtUtc), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (JsonException) { }
    }
}
