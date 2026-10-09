namespace NetRatel.Shared.SystemPairing;

public static class PairingProtocol
{
    public const string Contract = "bostec.pairing.v1";
    public const string Route = "/api/pairing/v1";
    public const string AdminRoute = "/api/v1/admin/system-connections";
}
public sealed record PairingMetadata(string Contract, string Product, string InstallationId, string Name,
    string WebOrigin, string ApiOrigin, string? ProducerInstanceId, string SigningPublicKey = "", string? ReceiverInstanceId = null);
public sealed record PairingMetadataProof(PairingMetadata Metadata, string Nonce, string Signature);
public sealed record PairingExchangeRequest(string Code, Guid OperationId, PairingMetadata Peer, string InboundSecret, string Signature = "");
public sealed record PairingExchangeResponse(string PairId, PairingMetadata Peer, string InboundSecret, string Signature = "");
public sealed record PairingChoice(string Id, string Name, string? ParentId = null);
public sealed record PairingDirectory(PairingChoice[] Tenants, PairingChoice[] Customers);
public sealed record PairingDirectories(PairingChoice[] NetRatelTenants, PairingChoice[] RatelDeskOrganizations,
    PairingChoice[] Customers);
public sealed record PairingMapping(Guid Id, string PairId, string Name, string NetRatelTenantId,
    string RatelDeskOrganizationId, string? RatelDeskCustomerId, bool CreateIncidents, bool RunAutomation);
public sealed record PairingBusinessCredential(string ClientId, string ClientSecret, string TokenEndpoint,
    string Audience, string Issuer, string[] Scopes, string? SourceInstanceId, string? SourceNamespaceId);
public sealed record PairingSaveRequest(Guid OperationId, long Revision, PairingMapping Mapping,
    PairingBusinessCredential? Credential);
public sealed record PairingSaveResponse(PairingMapping Mapping, PairingBusinessCredential? Credential);
public sealed record PairingTestResult(bool Success, string Message, DateTimeOffset TestedAtUtc,
    PairingReadinessDiagnostic? Diagnostic = null);
public sealed record PairingConnectRequest(string Address, string PairingCode, Guid OperationId);
public sealed record PairingCodeResponse(string Code, DateTimeOffset ExpiresAtUtc);
public sealed record PairingConnectionDto(string Id, string PairId, PairingMapping? Mapping,
    PairingMetadata Peer, string Status, PairingTestResult? LastTest = null);
public sealed record PairingResourceConstraints
{
    [System.Text.Json.Serialization.JsonPropertyName("organization_id")] public string? OrganizationId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("customer_ids")] public string[] CustomerIds { get; init; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("request_ids")] public string[] RequestIds { get; init; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("task_ids")] public string[] TaskIds { get; init; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("tenant_id")] public string? TenantId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("resource_ids")] public string[] ResourceIds { get; init; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("request_definition_ids")] public string[] RequestDefinitionIds { get; init; } = [];
}
public sealed class PairingException(int statusCode, string code, string message, PairingReadinessDiagnostic? diagnostic = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public PairingReadinessDiagnostic? Diagnostic { get; } = PairingReadinessDiagnostics.IsValid(diagnostic) ? diagnostic : null;
}
