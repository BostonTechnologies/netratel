using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;

namespace NetRatel.API.Gateway;

public sealed record AgentFrameValidationResult(
    bool IsValid,
    StatusCode StatusCode,
    string Error)
{
    public static AgentFrameValidationResult Success { get; } =
        new(true, StatusCode.OK, string.Empty);
}

public static class AgentGatewayProtocolValidator
{
    public static AgentFrameValidationResult ValidateHello(
        AgentFrame frame,
        AuthenticatedAgentIdentity identity,
        string supportedProtocolVersion)
    {
        var common = ValidateCommon(frame, identity, supportedProtocolVersion);
        if (!common.IsValid)
        {
            return common;
        }

        if (frame.PayloadCase != AgentFrame.PayloadOneofCase.Hello)
        {
            return Invalid("The first frame must contain a connect hello.");
        }

        if (frame.ConnectionEpoch != 0 || frame.Sequence != 0)
        {
            return Invalid("The connect hello must use connection_epoch=0 and sequence=0.");
        }

        if (frame.Hello.AgentVersion.Length > 128)
        {
            return Invalid("agent_version exceeds 128 characters.");
        }

        if (frame.Hello.Capabilities.Count > 64 ||
            frame.Hello.Capabilities.Any(capability => capability.Length > 128))
        {
            return Invalid("The capability list exceeds the Phase 1 limits.");
        }

        var legacyIdentity = frame.Hello.LegacySpacetimeIdentity;
        if (!string.IsNullOrWhiteSpace(legacyIdentity) &&
            (legacyIdentity.Length != 64 || !legacyIdentity.All(Uri.IsHexDigit)))
        {
            return Invalid("legacy_spacetime_identity must be a 64-character hexadecimal value when supplied.");
        }

        return AgentFrameValidationResult.Success;
    }

    public static AgentFrameValidationResult ValidateHeartbeat(
        AgentFrame frame,
        AuthenticatedAgentIdentity identity,
        string supportedProtocolVersion,
        Guid connectionId,
        long connectionEpoch)
    {
        var common = ValidateCommon(frame, identity, supportedProtocolVersion);
        if (!common.IsValid)
        {
            return common;
        }

        if (frame.PayloadCase != AgentFrame.PayloadOneofCase.Heartbeat)
        {
            return Invalid("Phase 1 accepts presence heartbeat frames only after the connect hello.");
        }

        if (!Guid.TryParse(frame.ConnectionId, out var parsedConnectionId) || parsedConnectionId != connectionId)
        {
            return new AgentFrameValidationResult(false, StatusCode.Aborted, "The connection_id does not match the active stream.");
        }

        if (frame.ConnectionEpoch != checked((ulong)connectionEpoch))
        {
            return new AgentFrameValidationResult(false, StatusCode.Aborted, "The connection_epoch is stale or invalid.");
        }

        if (frame.Sequence == 0)
        {
            return Invalid("Heartbeat sequence numbers must start at one.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static AgentFrameValidationResult ValidateCommon(
        AgentFrame frame,
        AuthenticatedAgentIdentity identity,
        string supportedProtocolVersion)
    {
        if (!string.Equals(frame.ProtocolVersion, supportedProtocolVersion, StringComparison.Ordinal))
        {
            return new AgentFrameValidationResult(
                false,
                StatusCode.FailedPrecondition,
                $"Unsupported protocol version '{frame.ProtocolVersion}'.");
        }

        if (frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var clientId) ||
            clientId != identity.AgentId)
        {
            return new AgentFrameValidationResult(
                false,
                StatusCode.PermissionDenied,
                "Frame tenant_id/client_id does not match the authenticated principal.");
        }

        if (!Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty)
        {
            return Invalid("connection_id must contain a non-empty GUID.");
        }

        if (!Guid.TryParse(frame.OperationId, out var operationId) || operationId == Guid.Empty)
        {
            return Invalid("operation_id must contain a non-empty GUID.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static AgentFrameValidationResult Invalid(string error) =>
        new(false, StatusCode.InvalidArgument, error);
}
