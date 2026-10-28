using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.ComputerUse;

/// <summary>Additive, provider-neutral desktop contracts. Desktop content is transient, never audit metadata.</summary>
public static class ComputerUseContract
{
    public const int Version = 1;
    public const string Capability = "computer-use-v1";
    public const int MaximumRequestBytes = 32 * 1024;
    public const int MaximumResponseBytes = 3 * 1024 * 1024;
    public const int MaximumTextLength = 4096;
    public const int MaximumStateLength = 128 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public static class ComputerUseOperations
{
    public const string Capabilities = "capabilities";
    public const string ListWindows = "list_windows";
    public const string Open = "open";
    public const string Observe = "observe";
    public const string Act = "act";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Close = "close";

    public static bool IsKnown(string? operation) => operation is
        Capabilities or ListWindows or Open or Observe or Act or Pause or Resume or Close;
}

public static class ComputerUseProviders
{
    public const string CuaDriver = "cua_driver";
    public const string NativeWindows = "native_windows";
    public static bool IsKnown(string? provider) => provider is CuaDriver or NativeWindows;
}

public static class ComputerUseActionKinds
{
    public const string Invoke = "invoke";
    public const string Click = "click";
    public const string Type = "type";
    public const string Key = "key";
    public const string Scroll = "scroll";
    public static bool IsKnown(string? kind) => kind is Invoke or Click or Type or Key or Scroll;
}

public static class ComputerUseStatuses
{
    public const string Completed = "completed";
    public const string Observed = "observed";
    public const string AppliedUnverified = "applied_unverified";
    public const string Verified = "verified";
    public const string Refused = "refused";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

/// <summary>Windows session identity comes from Remote Support inventory. Window IDs are provider-specific opaque identifiers.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseTarget(
    int WindowsSessionId,
    string UserSidHash,
    int? ProcessId = null,
    string? WindowId = null)
{
    public bool IsSessionTarget => WindowsSessionId > 0 &&
        !string.IsNullOrWhiteSpace(UserSidHash) && UserSidHash.Length <= 128 &&
        !UserSidHash.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
    public bool IsWindowTarget => IsSessionTarget && ProcessId is > 0 &&
        !string.IsNullOrWhiteSpace(WindowId) && WindowId.Length <= 256 && !WindowId.Any(char.IsControl);
}

/// <summary>Exactly one bounded action. Pixel coordinates are window-relative and require the named fresh observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseAction(
    string Kind,
    string? ElementId = null,
    string? Text = null,
    string? Key = null,
    int? X = null,
    int? Y = null,
    int? ScrollX = null,
    int? ScrollY = null,
    string? Button = null);

/// <summary>Server-issued command. Owners, tenant and presence fences remain outside caller-controlled action data.</summary>
public sealed record ComputerUseCommand(
    int ContractVersion,
    Guid OperationId,
    Guid SessionId,
    string Operation,
    ComputerUseTarget Target,
    string Provider,
    bool ControlAllowed,
    DateTimeOffset ExpiresAtUtc,
    string? ObservationId = null,
    ComputerUseAction? Action = null,
    bool IncludeScreenshot = false,
    long SessionRevision = 0);

public sealed record ComputerUseWindow(int ProcessId, string WindowId, string Title, string? Application = null);
public sealed record ComputerUseElement(string ElementId, string Role, string? Name = null, string? Value = null);
public sealed record ComputerUseScreenshot(string MimeType, string Base64, int Width, int Height);

/// <summary>A fresh read-back; observing a changed tree alone does not prove the requested effect occurred.</summary>
public sealed record ComputerUseObservation(
    string ObservationId,
    DateTimeOffset ObservedAtUtc,
    ComputerUseTarget Target,
    string State,
    IReadOnlyList<ComputerUseElement>? Elements = null,
    ComputerUseScreenshot? Screenshot = null);

public sealed record ComputerUseCapabilities(
    string Provider,
    bool Available,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> Actions,
    string? ProviderVersion = null,
    string? ProtocolVersion = null,
    string? UnavailableReason = null);

/// <summary>Structured outcomes distinguish dispatch, observed state, uncertain effects and refusal.</summary>
public sealed record ComputerUseResult(
    int ContractVersion,
    Guid OperationId,
    Guid SessionId,
    string Status,
    string? Code = null,
    string? Message = null,
    string? Provider = null,
    ComputerUseObservation? Observation = null,
    IReadOnlyList<ComputerUseWindow>? Windows = null,
    ComputerUseCapabilities? Capabilities = null,
    bool MayHaveApplied = false)
{
    public static ComputerUseResult Refused(ComputerUseCommand command, string code, string? message = null) =>
        new(ComputerUseContract.Version, command.OperationId, command.SessionId,
            ComputerUseStatuses.Refused, code, message, command.Provider);
}
