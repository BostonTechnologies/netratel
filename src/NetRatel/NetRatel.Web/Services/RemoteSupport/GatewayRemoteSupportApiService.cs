using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Web.Services.RemoteSupport;

/// <summary>Web adapter for the additive Agent-ID remote-support gateway API.</summary>
public sealed class GatewayRemoteSupportApiService(IHttpClientFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");
    private readonly HttpClient _streaming = factory.CreateClient("OrchestratorApiStreaming");

    public async Task<RemoteSupportV2CapabilitySnapshot?> GetV2CapabilitiesAsync(
        int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(V2AgentUri(tenantId, agentId, "capabilities"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RemoteSupportV2CapabilitySnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new RemoteSupportApiException(response.StatusCode, "remote_support_capability_response_empty");
    }

    public async Task<RemoteSupportTargetInventorySnapshot?> GetV2InventoryAsync(
        int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(V2AgentUri(tenantId, agentId, "inventory"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RemoteSupportTargetInventorySnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new RemoteSupportApiException(response.StatusCode, "remote_support_inventory_response_empty");
    }

    public async Task RefreshV2InventoryAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(V2AgentUri(tenantId, agentId, "inventory/refresh"), null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GatewayRemoteSupportOpenResponse> OpenAsync(int tenantId, Guid agentId, OpenRemoteSupportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v2/agents/{tenantId}/{agentId:D}/remote-support/sessions", request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GatewayRemoteSupportOpenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The gateway remote-support API returned an empty response.");
    }

    public async Task SendSignalAsync(string sessionId, RemoteSupportSignalRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v2/gateway-remote-support/{Uri.EscapeDataString(sessionId)}/signals", request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSupportSessionSnapshot> OpenV2Async(
        int tenantId,
        Guid agentId,
        GatewayRemoteSupportV2LifecycleOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/api/v2/agents/{tenantId}/{agentId:D}/remote-support/v2/lifecycle/sessions", request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RemoteSupportSessionSnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The V2 lifecycle API returned an empty response.");
    }

    public async Task PrepareV2MediaAsync(
        RemoteSupportSessionSnapshot snapshot,
        GatewayRemoteSupportV2MediaPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(V2SessionUri(snapshot, "prepare"), request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendV2NegotiationAsync(
        RemoteSupportV2NegotiationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(V2SessionUri(envelope.Session, "negotiation"), envelope, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseV2Async(
        RemoteSupportSessionSnapshot snapshot,
        CancellationToken cancellationToken = default,
        bool requireCurrentRevision = true)
    {
        using var response = await _http.PostAsJsonAsync(
            V2SessionUri(snapshot, "close"),
            new GatewayRemoteSupportV2LifecycleControlRequest(
                Guid.NewGuid(),
                requireCurrentRevision ? snapshot.LifecycleRevision : null),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSupportSessionSnapshot?> GetV2LifecycleAsync(
        RemoteSupportSessionKey session, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(V2SessionUri(session, string.Empty), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RemoteSupportSessionSnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSupportSessionIceConfiguration> GetV2IceConfigurationAsync(
        RemoteSupportSessionKey session, long generation, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(V2SessionUri(session, $"ice-configuration?generation={generation}"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RemoteSupportSessionIceConfiguration>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new RemoteSupportApiException(response.StatusCode, "remote_support_ice_configuration_empty");
    }

    public Task SendV2SasAsync(RemoteSupportSessionSnapshot snapshot, CancellationToken cancellationToken = default) =>
        SendV2ControlAsync(snapshot, "sas", cancellationToken);

    public Task RepairV2HelperAsync(RemoteSupportSessionSnapshot snapshot, CancellationToken cancellationToken = default) =>
        SendV2ControlAsync(snapshot, "repair", cancellationToken);

    public async Task<GatewayRemoteSupportTransitionSelection?> GetV2TransitionSelectionAsync(
        RemoteSupportSessionKey session, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(V2SessionUri(session, "transition-target"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GatewayRemoteSupportTransitionSelection>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new RemoteSupportApiException(response.StatusCode, "remote_support_transition_selection_empty");
    }

    public async Task SelectV2TransitionTargetAsync(
        RemoteSupportSessionSnapshot snapshot,
        GatewayRemoteSupportTransitionSelection selection,
        GatewayRemoteSupportTransitionTarget candidate,
        CancellationToken cancellationToken = default)
    {
        var request = new GatewayRemoteSupportTransitionTargetSelectionRequest(
            Guid.NewGuid(), selection.TransitionId, selection.PresenceEpoch, selection.InventorySequence,
            candidate.WindowsSessionId, candidate.IdentityReference, snapshot.LifecycleRevision);
        using var response = await _http.PostAsJsonAsync(V2SessionUri(snapshot, "transition-target"), request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RemoteSupportSessionSnapshot> StreamV2LifecycleAsync(
        RemoteSupportSessionKey session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var lifecycle in StreamV2LifecycleEventsAsync(session, null, cancellationToken).ConfigureAwait(false))
        {
            yield return lifecycle.Event.Snapshot;
        }
    }

    public async IAsyncEnumerable<GatewayRemoteSupportLifecycleSseEvent> StreamV2LifecycleEventsAsync(
        RemoteSupportSessionKey session,
        long? afterAuditSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in StreamSseAsync(V2SessionUri(session, "events"), afterAuditSequence, cancellationToken).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize<GatewayRemoteSupportLifecycleEvent>(message.Data, JsonOptions) is { } lifecycle)
            {
                yield return new GatewayRemoteSupportLifecycleSseEvent(message.Id ?? lifecycle.AuditSequence, message.EventType, lifecycle);
            }
        }
    }

    public async IAsyncEnumerable<RemoteSupportV2NegotiationEnvelope> StreamV2NegotiationAsync(
        RemoteSupportSessionKey session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in StreamSseAsync(V2SessionUri(session, "negotiation"), null, cancellationToken).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize<RemoteSupportV2NegotiationEnvelope>(message.Data, JsonOptions) is { } envelope)
            {
                yield return envelope;
            }
        }
    }

    public async Task CloseAsync(string sessionId, string? reason, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v2/gateway-remote-support/{Uri.EscapeDataString(sessionId)}/close", new RemoteSupportCloseRequest(reason), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<GatewayRemoteSupportSignalDto> StreamSignalsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v2/gateway-remote-support/{Uri.EscapeDataString(sessionId)}/signals");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
        var data = new StringBuilder();
        string? line;
        while (!cancellationToken.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && JsonSerializer.Deserialize<GatewayRemoteSupportSignalDto>(data.ToString(), JsonOptions) is { } signal)
                {
                    yield return signal;
                }

                data.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Length > 5 ? line[5..].TrimStart() : string.Empty);
            }
        }
    }

    private async Task SendV2ControlAsync(RemoteSupportSessionSnapshot snapshot, string suffix, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            V2SessionUri(snapshot, suffix),
            new GatewayRemoteSupportV2LifecycleControlRequest(Guid.NewGuid(), snapshot.LifecycleRevision),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<RemoteSupportSseMessage> StreamSseAsync(
        string uri,
        long? afterAuditSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requestUri = afterAuditSequence is { } after ? $"{uri}?after={after}" : uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (afterAuditSequence is { } cursor)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await _streaming.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
        var data = new StringBuilder();
        long? eventId = null;
        string? eventType = null;
        string? line;
        while (!cancellationToken.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new RemoteSupportSseMessage(eventId, eventType, data.ToString());
                }

                data.Clear();
                eventId = null;
                eventType = null;
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Length > 5 ? line[5..].TrimStart() : string.Empty);
            }
            else if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(line.AsSpan(3).Trim(), System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out var parsedId))
            {
                eventId = parsedId;
            }
            else if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line.Length > 6 ? line[6..].Trim() : null;
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var code = body.Contains("stale", StringComparison.OrdinalIgnoreCase)
            ? "remote_support_stale"
            : response.StatusCode == System.Net.HttpStatusCode.Forbidden ? "remote_support_forbidden"
            : response.StatusCode == System.Net.HttpStatusCode.NotFound ? "remote_support_not_found"
            : "remote_support_request_failed";
        throw new RemoteSupportApiException(response.StatusCode, code);
    }

    private static string V2AgentUri(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/agents/{tenantId}/{agentId:D}/remote-support/v2/{suffix}";

    private static string V2SessionUri(RemoteSupportSessionSnapshot snapshot, string suffix) =>
        V2SessionUri(snapshot.Session, suffix);

    private static string V2SessionUri(RemoteSupportSessionKey session, string suffix) =>
        $"/api/v2/agents/{session.TenantId}/{session.AgentId:D}/remote-support/v2/lifecycle/sessions/{session.RemoteSupportSessionId:D}{(string.IsNullOrEmpty(suffix) ? string.Empty : $"/{suffix}")}";
}

public sealed record GatewayRemoteSupportLifecycleSseEvent(long AuditSequence, string? EventType, GatewayRemoteSupportLifecycleEvent Event);

public sealed record RemoteSupportSseMessage(long? Id, string? EventType, string Data);

public sealed record GatewayRemoteSupportTransitionSelection(
    Guid TransitionId,
    ulong PresenceEpoch,
    ulong InventorySequence,
    IReadOnlyList<GatewayRemoteSupportTransitionTarget> Candidates);

public sealed record GatewayRemoteSupportTransitionTarget(
    int WindowsSessionId,
    string IdentityReference,
    string State,
    bool IsConsole,
    bool IsAssistable);

public sealed record GatewayRemoteSupportTransitionTargetSelectionRequest(
    Guid RequestId,
    Guid TransitionId,
    ulong PresenceEpoch,
    ulong InventorySequence,
    int WindowsSessionId,
    string IdentityReference,
    long ExpectedLifecycleRevision);

public sealed class RemoteSupportApiException(System.Net.HttpStatusCode statusCode, string code)
    : InvalidOperationException(code)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record GatewayRemoteSupportV2LifecycleOpenRequest(
    Guid RequestId,
    RemoteSupportTargetDescriptor Target,
    IReadOnlyList<string> RequestedCapabilities,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record GatewayRemoteSupportV2MediaPrepareRequest(
    Guid RequestId,
    RemoteSupportTargetDescriptor Target,
    long? ExpectedLifecycleRevision = null);

public sealed record GatewayRemoteSupportV2LifecycleControlRequest(Guid RequestId, long? ExpectedLifecycleRevision = null);

public sealed record GatewayRemoteSupportLifecycleEvent(
    long AuditSequence,
    RemoteSupportSessionSnapshot Snapshot,
    string EventType);
