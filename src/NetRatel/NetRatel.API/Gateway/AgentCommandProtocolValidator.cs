using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;

namespace NetRatel.API.Gateway;

public static class AgentCommandProtocolValidator
{
    private const int MaxIdentifierLength = 256;

    public static AgentFrameValidationResult Validate(
        CommandShadowFrame frame,
        AuthenticatedAgentIdentity identity,
        string supportedProtocolVersion)
    {
        if (!string.Equals(frame.ProtocolVersion, supportedProtocolVersion, StringComparison.Ordinal))
        {
            return Invalid(
                StatusCode.FailedPrecondition,
                $"Unsupported protocol version '{frame.ProtocolVersion}'.");
        }

        if (frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var clientId) ||
            clientId != identity.AgentId)
        {
            return Invalid(
                StatusCode.PermissionDenied,
                "Frame tenant_id/client_id does not match the authenticated principal.");
        }

        if (!Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty)
        {
            return Invalid(StatusCode.InvalidArgument, "connection_id must contain a non-empty GUID.");
        }

        if (frame.ConnectionEpoch == 0 || frame.ConnectionEpoch > long.MaxValue)
        {
            return Invalid(StatusCode.InvalidArgument, "connection_epoch must contain a positive signed 64-bit value.");
        }

        if (string.IsNullOrWhiteSpace(frame.CommandId) || frame.CommandId.Length > MaxIdentifierLength)
        {
            return Invalid(StatusCode.InvalidArgument, "command_id must contain 1 to 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(frame.CorrelationId) || frame.CorrelationId.Length > MaxIdentifierLength)
        {
            return Invalid(StatusCode.InvalidArgument, "correlation_id must contain 1 to 256 characters.");
        }

        if (frame.Version == 0 || frame.Sequence == 0)
        {
            return Invalid(StatusCode.InvalidArgument, "Command version and sequence numbers must start at one.");
        }

        if (!IsSupportedStatus(frame.Status))
        {
            return Invalid(StatusCode.InvalidArgument, "A supported command lifecycle status is required.");
        }

        if (!TryReadTimestamp(frame.RequestTimestamp, out var requestTimestamp) ||
            !TryReadTimestamp(frame.StatusTimestamp, out var statusTimestamp) ||
            statusTimestamp < requestTimestamp)
        {
            return Invalid(
                StatusCode.InvalidArgument,
                "request_timestamp and status_timestamp must be valid and chronologically ordered.");
        }

        return AgentFrameValidationResult.Success;
    }

    private static bool IsSupportedStatus(CommandShadowStatus status) =>
        status is CommandShadowStatus.Created or
            CommandShadowStatus.Dispatched or
            CommandShadowStatus.Accepted or
            CommandShadowStatus.Started or
            CommandShadowStatus.Completed or
            CommandShadowStatus.Failed or
            CommandShadowStatus.Cancelled;

    internal static bool TryReadTimestamp(
        Google.Protobuf.WellKnownTypes.Timestamp? timestamp,
        out DateTimeOffset value)
    {
        value = default;
        if (timestamp is null)
        {
            return false;
        }

        try
        {
            value = timestamp.ToDateTimeOffset();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static AgentFrameValidationResult Invalid(StatusCode statusCode, string error) =>
        new(false, statusCode, error);
}
