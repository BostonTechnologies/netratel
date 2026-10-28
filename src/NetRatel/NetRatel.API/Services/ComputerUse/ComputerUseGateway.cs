using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.API.Services.ComputerUse;

public interface IComputerUseGateway
{
    Task<ComputerUseResult> ExecuteAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
        ComputerUseCommand command, CancellationToken cancellationToken);
}

/// <summary>Uses the authenticated Akka presence fence and the existing agent-only control transport.</summary>
public sealed class ComputerUseGateway(IAgentControlSessionRegistry controls, IClientPresenceRouter presence,
    TimeProvider timeProvider) : IComputerUseGateway
{
    public async Task<ComputerUseResult> ExecuteAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
        ComputerUseCommand command, CancellationToken cancellationToken)
    {
        if (ComputerUseValidation.ValidateCommand(command) is { } code)
            return ComputerUseResult.Refused(command, code);
        var now = timeProvider.GetUtcNow();
        if (command.ExpiresAtUtc <= now) return ComputerUseResult.Refused(command, "session_expired");
        var snapshot = await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (snapshot.Status != ClientPresenceStatus.Online || snapshot.ConnectionId != connectionId ||
            snapshot.ConnectionEpoch != checked((long)connectionEpoch))
            return ComputerUseResult.Refused(command, "presence_changed");
        var timeout = TimeSpan.FromSeconds(Math.Min(30, (command.ExpiresAtUtc - now).TotalSeconds));
        try
        {
            return await controls.RequestComputerUseAsync(client, connectionId, connectionEpoch, command,
                timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentControlSessionUnavailableException)
        {
            return ComputerUseResult.Refused(command, "computer_use_unavailable");
        }
        catch (OperationCanceledException)
        {
            return new(ComputerUseContract.Version, command.OperationId, command.SessionId,
                ComputerUseStatuses.Unknown, "execution_interrupted", Provider: command.Provider,
                MayHaveApplied: command.Operation == ComputerUseOperations.Act);
        }
    }
}
