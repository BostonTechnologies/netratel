using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.Web.Services.Terminal;

internal static class TerminalAccountHandover
{
    public static bool IsOpen(TerminalSessionDto session) => session.State is "active" or "opened" or "open";

    public static async Task<TerminalSessionDto> SwitchAsync(ITerminalService terminal, TerminalSessionDto original,
        ExecutionIdentitySetting requested, CancellationToken ct)
    {
        var request = new SwitchTerminalAccountRequest(original.Generation!.Value, Guid.NewGuid(), original.AttachmentLeaseId!, requested);
        TerminalSessionDto replacement;
        try { replacement = await terminal.SwitchGatewayAccountAsync(original.SessionId, request, ct); }
        catch (HttpRequestException error) when (error.StatusCode is null)
        {
            // The server may have accepted the switch before the reply was lost.
            var latest = await terminal.GetGatewaySessionAsync(original.SessionId, ct);
            if (latest?.ReplacementSessionId is not { } id || await terminal.GetGatewaySessionAsync(id, ct) is not { } found)
                throw;
            replacement = found;
        }

        while (!IsOpen(replacement))
        {
            if (replacement.State is "closed" or "failed")
                throw new InvalidOperationException(replacement.CloseReason ?? "The client could not start the requested shell.");
            await Task.Delay(TimeSpan.FromMilliseconds(350), ct);
            replacement = await terminal.GetGatewaySessionAsync(replacement.SessionId, ct)
                ?? throw new InvalidOperationException("The replacement terminal is unavailable.");
        }
        return replacement;
    }
}
