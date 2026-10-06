using System.Text;
using System.Text.Json;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskReceiverTransport(RatelDeskReceiverHttpPipeline http, TimeProvider time)
    : IRatelDeskReceiverTransport
{
    public async Task<RatelDeskVerifiedCapability> CapabilitiesAsync(RatelDeskSemanticPeer peer, string bearer, CancellationToken ct)
    {
        var reply = await Read(peer, bearer, HttpMethod.Get,
            ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CapabilitiesPath), null, ct);
        RatelDeskReceiverReadException.RequireJsonSuccess(reply, "capability");
        return ReceiverWireValidation.Capability(reply.Body, peer, time.GetUtcNow());
    }
    public async Task ValidateTargetsAsync(RatelDeskSemanticPeer peer,
        RatelDeskVerifiedCapability capability, string bearer, CancellationToken ct)
    {
        if (!ReceiverWireValidation.ValidCapability(capability, peer))
            throw new InvalidDataException("receiver-capability-binding-unverified");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            organizationId = peer.OrganizationId, customerId = peer.CustomerId,
            assignedToId = peer.AssignedToId, categoryIds = peer.CategoryIds
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reply = await Read(peer, bearer, HttpMethod.Post, capability.Endpoints.TargetValidation, body, ct);
        RatelDeskReceiverReadException.RequireJsonSuccess(reply, "target-validation");
        ReceiverWireValidation.Target(reply.Body, peer);
    }
    public async Task<RatelDeskReceiverObservation> LookupAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
    {
        if (!ReceiverWireValidation.ValidCapability(prepared.Capability, prepared.Peer) ||
            !RatelDeskReceiverKey.IsConforming(prepared.ReceiverIdempotencyKey))
            return new(RatelDeskReceiverObservationKind.TransientReadFailure, "receiver-preparation-unverified");
        var endpoint = RatelDeskReceiverKey.ReceiptEndpoint(prepared.Capability.Endpoints.ReceiptTemplate, prepared.ReceiverIdempotencyKey);
        try
        {
            var reply = await Read(prepared.Peer, bearer, HttpMethod.Get, endpoint, null, ct, preserveDotKey: true);
            if (reply.Status == 200) return Committed(reply, prepared);
            if ((reply.Status is 404 or 410) && !reply.NoStore)
                throw new InvalidDataException("receiver-receipt-headers-unverified");
            if (reply.Status == 404) return new(RatelDeskReceiverObservationKind.Missing, "receiver-receipt-missing");
            if (reply.Status == 410) return new(RatelDeskReceiverObservationKind.Gone, "receiver-receipt-expired");
            return Classify(reply, false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or RatelDeskReceiverReadException)
        { return new(RatelDeskReceiverObservationKind.TransientReadFailure, "receiver-receipt-unverified"); }
    }
    public async Task<RatelDeskReceiverObservation> CreateAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
    {
        if (!ReceiverWireValidation.ValidCapability(prepared.Capability, prepared.Peer))
            return new(RatelDeskReceiverObservationKind.Unavailable, "receiver-preparation-unverified");
        if (!RatelDeskReceiverKey.IsConforming(prepared.ReceiverIdempotencyKey))
            return new(RatelDeskReceiverObservationKind.Unavailable, "receiver-key-invalid");
        var bytes = Encoding.UTF8.GetBytes(prepared.ExactCreateBodyJson);
        if (bytes.Length > RatelDeskConnectorLimits.MaximumRequestBytes)
            return new(RatelDeskReceiverObservationKind.PayloadRejected, "incident-request-too-large");
        var mayHaveEnteredHttp = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            // Caller already committed MarkPostAttemptAsync. Conservatively preserve that uncertainty
            // even if a later admission/network/read error prevents knowing whether HttpClient sent.
            mayHaveEnteredHttp = true;
            var reply = await Read(prepared.Peer, bearer, HttpMethod.Post, prepared.Capability.Endpoints.Create,
                bytes, ct, prepared.ReceiverIdempotencyKey);
            if (reply.Status is 200 or 201) return Committed(reply, prepared);
            return Classify(reply, true);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or RatelDeskReceiverReadException)
        {
            return new(mayHaveEnteredHttp ? RatelDeskReceiverObservationKind.PossibleCommit : RatelDeskReceiverObservationKind.Unavailable,
                mayHaveEnteredHttp ? "receiver-commit-unconfirmed" : "receiver-create-not-started");
        }
    }
    private Task<RatelDeskReceiverReply> Read(RatelDeskSemanticPeer peer, string bearer,
        HttpMethod method, string endpoint, byte[]? body, CancellationToken ct,
        string? key = null, bool preserveDotKey = false) => http.ReadAsync(peer.Mode, peer.LocalTenantId,
            peer.ConnectorId, peer.ApiBaseUrl, peer.SourceInstanceId, bearer, method, endpoint, body, ct, key, preserveDotKey);
    private static RatelDeskReceiverObservation Committed(RatelDeskReceiverReply reply, RatelDeskReceiverPreparationV2 prepared)
    {
        if (!string.Equals(reply.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) || !reply.NoStore)
            throw new InvalidDataException("receiver-receipt-headers-unverified");
        return new(RatelDeskReceiverObservationKind.Committed, "receiver-receipt-verified",
            ReceiverWireValidation.Receipt(reply.Body, reply.Location, prepared));
    }
    private static RatelDeskReceiverObservation Classify(RatelDeskReceiverReply reply, bool sentWrite)
    {
        if (reply.Status is 401 or 403) return new(RatelDeskReceiverObservationKind.AuthenticationRejected, "receiver-auth-rejected");
        if (reply.Status == 429) return new(RatelDeskReceiverObservationKind.RateLimited, "receiver-rate-limited", RetryAfter: reply.RetryAfter);
        string? code = null;
        try { code = ReceiverWireValidation.ProblemCode(reply.Body); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { }
        if (reply.Status == 409 && code == "idempotency-payload-conflict")
            return new(RatelDeskReceiverObservationKind.FingerprintConflict, code);
        if ((reply.Status is 400 or 422) && (code is "invalid-target-request" or "invalid-incident-target" or "invalid-incident-request" or "invalid-integration-headers"))
            return new(RatelDeskReceiverObservationKind.PayloadRejected, code);
        return new(sentWrite ? RatelDeskReceiverObservationKind.PossibleCommit : RatelDeskReceiverObservationKind.TransientReadFailure,
            sentWrite ? "receiver-commit-unconfirmed" : "receiver-read-unavailable");
    }
}
