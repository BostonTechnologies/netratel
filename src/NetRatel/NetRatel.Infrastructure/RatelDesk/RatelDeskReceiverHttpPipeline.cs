using System.Net.Http.Headers;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed record RatelDeskReceiverReply(int Status, byte[] Body, string? Location,
    TimeSpan? RetryAfter, string? MediaType, bool NoStore);

public sealed class RatelDeskReceiverHttpPipeline(IHttpClientFactory clients,
    RatelDeskReceiverNetworkPolicy network, TimeProvider time, RatelDeskTransportLimiter limiter)
{
    public const string ManagedClient = "RatelDesk.Receiver.PairedSystem";
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public async Task<RatelDeskReceiverReply> ReadAsync(RatelDeskAuthenticationMode mode,
        int tenantId, Guid connectorId, string approvedApiBase, Guid source, string bearer,
        HttpMethod method, string endpoint, byte[]? body, CancellationToken ct,
        string? idempotencyKey = null, bool preserveDotKey = false)
    {
        if (tenantId <= 0 || connectorId == Guid.Empty || source == Guid.Empty || string.IsNullOrEmpty(bearer))
            throw new UnauthorizedAccessException("receiver-operation-identity-invalid");
        if (idempotencyKey is not null && !RatelDeskReceiverKey.IsConforming(idempotencyKey))
            throw new InvalidDataException("receiver-key-invalid");
        network.ValidateEndpoint(mode, approvedApiBase, endpoint);
        using var admission = await limiter.TryAcquireAsync(tenantId, connectorId, ct);
        if (admission is null) throw new RatelDeskReceiverReadException("connector-busy", null, null);
        using var operationDeadline = new CancellationTokenSource(OperationTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, operationDeadline.Token);
        Uri destination = new(endpoint, UriKind.Absolute);
        var isReceipt = RatelDeskReceiverNetworkPolicy.IsReceiptEndpoint(
            RatelDeskApiBase.Canonical(approvedApiBase), endpoint);
        if (preserveDotKey && !isReceipt)
            throw new InvalidDataException("receiver-raw-receipt-path-invalid");
        if (isReceipt)
        {
            // A conforming durable key can be '.' or '..'. Preserve its exact final segment.
            destination = new(endpoint, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }
        var canonicalApiBase = RatelDeskApiBase.Canonical(approvedApiBase);
        var expectedMethod = endpoint == ReceiverWireValidation.Endpoint(canonicalApiBase, ReceiverWireValidation.CreatePath) ||
            endpoint == ReceiverWireValidation.Endpoint(canonicalApiBase, ReceiverWireValidation.TargetsPath)
            ? HttpMethod.Post : HttpMethod.Get;
        var isCreate = endpoint == ReceiverWireValidation.Endpoint(canonicalApiBase, ReceiverWireValidation.CreatePath);
        if (method != expectedMethod || isCreate != (idempotencyKey is not null) ||
            expectedMethod == HttpMethod.Get && body is not null ||
            expectedMethod == HttpMethod.Post && body is null)
            throw new InvalidDataException("receiver-operation-endpoint-invalid");
        using var request = new HttpRequestMessage(method, destination);
        request.Options.Set(RatelDeskReceiverSafeHttpMessageHandler.ApprovedApiBaseOption, canonicalApiBase);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Add("X-NetRatel-Source-Instance", source.ToString("D"));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
        {
            if (body.Length > RatelDeskConnectorLimits.MaximumRequestBytes) throw new InvalidDataException("receiver-request-too-large");
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
        }
        // Options are read again after asynchronous admission and immediately before send.
        network.ValidateEndpoint(mode, approvedApiBase, endpoint);
        using var client = clients.CreateClient(mode switch
        {
            RatelDeskAuthenticationMode.PairedSystem => ManagedClient,
            _ => throw new UnauthorizedAccessException("unsupported-connector-authentication-mode")
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.Content.Headers.ContentLength > RatelDeskConnectorLimits.MaximumResponseBytes)
            throw new InvalidDataException("receiver-response-too-large");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, deadline.Token); if (count == 0) break;
            if (output.Length + count > RatelDeskConnectorLimits.MaximumResponseBytes) throw new InvalidDataException("receiver-response-too-large");
            output.Write(buffer, 0, count);
        }
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } at ? at - time.GetUtcNow() : null);
        var bounded = delay is null ? (TimeSpan?)null : TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(delay.Value.TotalSeconds), 1, 300));
        return new((int)response.StatusCode, output.ToArray(), response.Headers.Location?.OriginalString, bounded,
            response.Content.Headers.ContentType?.MediaType, response.Headers.CacheControl?.NoStore == true);
    }
}

// Safe diagnostics for read-only capture/preparation. No response body, credential or raw URL.
public sealed class RatelDeskReceiverReadException(string code, int? status, TimeSpan? retryAfter)
    : Exception(code)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static void RequireJsonSuccess(RatelDeskReceiverReply reply, string operation)
    {
        if (reply.Status != 200)
            throw new RatelDeskReceiverReadException(reply.Status switch
            {
                401 => "receiver-authentication-rejected", 403 => "receiver-current-grant-rejected",
                400 or 422 => "receiver-target-rejected", 429 => "receiver-rate-limited",
                >= 300 and < 400 => "receiver-redirect-refused", _ => "receiver-" + operation + "-unavailable"
            }, reply.Status, reply.RetryAfter);
        if (!string.Equals(reply.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) || !reply.NoStore)
            throw new InvalidDataException("receiver-read-headers-unverified");
    }
}
