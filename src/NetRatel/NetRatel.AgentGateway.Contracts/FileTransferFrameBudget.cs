namespace NetRatel.AgentGateway.Contracts.V1;

/// <summary>
/// Reserves space for protobuf and gRPC framing around file-transfer payloads.
/// The API gateway enforces a 16 KiB gRPC message limit in development.
/// </summary>
public static class FileTransferFrameBudget
{
    public const int PayloadBytes = 12 * 1024;
}
