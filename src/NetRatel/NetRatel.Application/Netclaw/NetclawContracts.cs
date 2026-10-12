namespace NetRatel.Application.Netclaw;

public sealed record NetclawRuntimeProfile(int TenantId, Guid Id, long Revision, string Endpoint, string DeviceToken,
    bool AllowPrivateHttp, string ProfileFingerprint);
public sealed record NetclawPairingTarget(Uri SessionEndpoint, bool AllowPrivateHttp);
public interface INetclawPairingService
{
    Task<string> ExchangeCodeAsync(NetclawPairingTarget target, string pairingCode, CancellationToken cancellationToken = default);
}
public sealed class NetclawPairingException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public sealed class InvestigationConflictException(string message) : Exception(message);
