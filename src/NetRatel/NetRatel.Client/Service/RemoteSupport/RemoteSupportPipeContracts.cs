using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Client.Service.RemoteSupport;

internal static class RemoteSupportPipeKinds
{
    public const string Offer = "remote-support-offer";
    public const string Ice = "remote-support-ice";
    public const string Close = "remote-support-close";
    public const string Signal = "remote-support-signal";
    public const string SasRequest = "remote-support-sas-request";
    public const string SasResponse = "remote-support-sas-response";
}

internal sealed record RemoteSupportPipeSignal(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("signalType")] string SignalType,
    [property: JsonPropertyName("payloadJson")] string PayloadJson);

internal sealed record RemoteSupportPipeOffer(
    [property: JsonPropertyName("offerJson")] string OfferJson,
    [property: JsonPropertyName("iceServers")] IReadOnlyList<RemoteSupportIceServerDto>? IceServers = null,
    [property: JsonPropertyName("mediaProfile")] RemoteSupportMediaProfileDto? MediaProfile = null,
    [property: JsonPropertyName("providerGeneration")] int ProviderGeneration = 1,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("fromProvider")] string? FromProvider = null,
    [property: JsonPropertyName("toProvider")] string? ToProvider = null,
    [property: JsonPropertyName("handoverReason")] string? HandoverReason = null,
    [property: JsonPropertyName("targetWindowsSessionId")] int? TargetWindowsSessionId = null,
    [property: JsonPropertyName("targetUserSidHash")] string? TargetUserSidHash = null);

internal sealed record RemoteSupportProviderSignalEnvelope(
    [property: JsonPropertyName("providerGeneration")] int ProviderGeneration,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("fromProvider")] string? FromProvider,
    [property: JsonPropertyName("toProvider")] string? ToProvider,
    [property: JsonPropertyName("handoverReason")] string? HandoverReason,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("providerPid")] int? ProviderPid,
    [property: JsonPropertyName("providerSessionId")] int? ProviderSessionId,
    [property: JsonPropertyName("activeConsoleSessionId")] uint? ActiveConsoleSessionId,
    [property: JsonPropertyName("desktopState")] string? DesktopState,
    [property: JsonPropertyName("payload")] JsonElement Payload);

internal sealed record RemoteSupportSasPipeRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("providerProcessSessionId")] int ProviderProcessSessionId,
    [property: JsonPropertyName("activeConsoleSessionId")] uint? ActiveConsoleSessionId,
    [property: JsonPropertyName("providerMatchesActiveConsole")] bool ProviderMatchesActiveConsole,
    [property: JsonPropertyName("desktopState")] string? DesktopState,
    [property: JsonPropertyName("captureFrameHash")] string? CaptureFrameHash);

internal sealed record RemoteSupportSasPipeResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("statusCode")] string StatusCode,
    [property: JsonPropertyName("message")] string Message);

internal sealed record RemoteSupportSignalMetadata(
    int ProviderGeneration,
    string? Reason,
    string? FromProvider,
    string? ToProvider,
    string? HandoverReason,
    JsonElement? Payload);

internal sealed record RemoteSupportProviderMetadata(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("desktopState")] string DesktopState,
    [property: JsonPropertyName("supportLevel")] string SupportLevel,
    [property: JsonPropertyName("inputDesktopName")] string? InputDesktopName,
    [property: JsonPropertyName("reconnectRequired")] bool ReconnectRequired)
{
    public static RemoteSupportProviderMetadata InteractiveUserHelper { get; } = new(
        RemoteSupportProviderKinds.InteractiveUserHelper,
        RemoteSupportDesktopStates.Unlocked,
        RemoteSupportSupportLevels.MediaPreview,
        "Default",
        false);

    public bool IsConsoleProvider =>
        string.Equals(Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, System.StringComparison.Ordinal);
}
