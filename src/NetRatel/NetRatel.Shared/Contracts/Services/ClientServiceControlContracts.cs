using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Services;

[JsonConverter(typeof(JsonStringEnumConverter<ClientServiceControlAction>))]
public enum ClientServiceControlAction { Start = 1, Stop = 2, Restart = 3 }

public sealed record ClientServiceControlRequest(
    Guid OperationId,
    string ServiceName,
    ClientServicePlatform Platform,
    ClientServiceControlAction Action,
    string? Reason = null);

/// <summary>An exact service operation, with a deadline that also covers time spent queued.</summary>
public sealed record ClientServiceControlPayload(
    Guid OperationId,
    string ServiceName,
    ClientServicePlatform Platform,
    ClientServiceControlAction Action,
    DateTimeOffset ExpiresAtUtc);

public sealed record ClientServiceControlResult(
    Guid OperationId,
    string ServiceName,
    ClientServicePlatform Platform,
    ClientServiceControlAction Action,
    bool Success,
    ClientServiceState State,
    DateTimeOffset ObservedAtUtc,
    string? ErrorCode = null);

public sealed record ClientServiceOperationDto(
    Guid OperationId,
    int TenantId,
    Guid AgentId,
    string ServiceName,
    ClientServicePlatform Platform,
    ClientServiceControlAction Action,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ClientServiceControlResult? Result,
    string? FailureCode = null,
    bool MayHaveExecuted = false);

public static class ClientServiceControlRules
{
    public const string Capability = "service-control-v1";
    public const string TaskType = "service-control";
    public static readonly TimeSpan DispatchLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(45);

    public static bool IsValidSelection(string? name, ClientServicePlatform platform, ClientServiceControlAction action) =>
        name is not null && Enum.IsDefined(action) && platform is ClientServicePlatform.Windows or ClientServicePlatform.LinuxSystemd &&
        ClientServiceContractValidator.IsValidServiceName(name, platform) &&
        (platform != ClientServicePlatform.LinuxSystemd || !name.Any(character => character is '*' or '?' or '[' or ']')) &&
        (platform != ClientServicePlatform.LinuxSystemd || !name.EndsWith("@.service", StringComparison.Ordinal));

    public static bool IsProtectedService(string name) =>
        string.Equals(name, "NetRatel.Client", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "netratel-client.service", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "netratel.client.service", StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminal(string state) => state is "Completed" or "Failed" or "Cancelled";
}
