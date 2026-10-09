using System.Globalization;
using System.Text.Json;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public static class ReceiverWireValidation
{
    public const string Contract = "rateldesk.incident-create.v1";
    public const string CapabilitiesPath = "/api/v1/integrations/netratel/capabilities";
    public const string CreatePath = "/api/v1/incidents/";
    public const string ReceiptPath = "/api/v1/integrations/netratel/incident-receipts/{key}";
    public const string TargetsPath = "/api/v1/integrations/netratel/targets/validate";
    public const long MinimumRetention = 7_776_000;
    public const long MaximumReplay = 2_592_000;
    public const string KeyPattern = "^[A-Za-z0-9._~-]{1,256}$";

    public static string Endpoint(string apiBase, string path) => apiBase.TrimEnd('/') + "/" + path.TrimStart('/');

    public static bool ValidCapability(RatelDeskVerifiedCapability? capability, RatelDeskSemanticPeer? peer)
    {
        if (capability is null || peer is null || capability.Endpoints is null) return false;
        try { RequirePeer(peer); }
        catch (InvalidDataException) { return false; }
        return capability.ContractVersion == Contract &&
            capability.ReceiverInstanceId == peer.ReceiverInstanceId &&
            capability.SourceInstanceId == peer.SourceInstanceId &&
            capability.SourceNamespaceId == peer.SourceNamespaceId &&
            capability.Endpoints.Capabilities == Endpoint(peer.ApiBaseUrl, CapabilitiesPath) &&
            capability.Endpoints.Create == Endpoint(peer.ApiBaseUrl, CreatePath) &&
            capability.Endpoints.ReceiptTemplate == Endpoint(peer.ApiBaseUrl, ReceiptPath) &&
            capability.Endpoints.TargetValidation == Endpoint(peer.ApiBaseUrl, TargetsPath) &&
            capability.MinimumReceiptRetentionSeconds >= MinimumRetention &&
            capability.MaximumAutomaticReplaySeconds is >= 86_400 and <= MaximumReplay &&
            capability.ObservedAtUtc != default && capability.ObservedAtUtc.Offset == TimeSpan.Zero;
    }

    private static void RequirePeer(RatelDeskSemanticPeer peer)
    {
        if (peer.Mode != RatelDeskAuthenticationMode.PairedSystem ||
            peer.LocalTenantId <= 0 || peer.ConnectorId == Guid.Empty ||
            peer.SourceInstanceId == Guid.Empty || peer.SourceNamespaceId == Guid.Empty ||
            peer.OrganizationId is not { Length: > 0 and <= 64 } || string.IsNullOrWhiteSpace(peer.OrganizationId) || peer.OrganizationId.Any(char.IsControl) ||
            peer.CustomerId is not { Length: > 0 and <= 64 } || string.IsNullOrWhiteSpace(peer.CustomerId) || peer.CustomerId.Any(char.IsControl) ||
            peer.AssignedToId is { } assignee && (assignee.Length is 0 or > 64 || string.IsNullOrWhiteSpace(assignee) || assignee.Any(char.IsControl)) ||
            peer.CategoryIds is null || peer.CategoryIds.Length > RatelDeskConnectorLimits.MaximumCategories)
            throw new InvalidDataException("receiver-semantic-peer-unverified");
        _ = CanonicalGuid(peer.ReceiverInstanceId);
        foreach (var category in peer.CategoryIds) _ = CanonicalGuid(category);
        if (!peer.CategoryIds.SequenceEqual(peer.CategoryIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("receiver-semantic-peer-unverified");
        try
        {
            if (RatelDeskApiBase.Canonical(peer.ApiBaseUrl) != peer.ApiBaseUrl)
                throw new InvalidDataException("receiver-api-base-unverified");
        }
        catch (ArgumentException) { throw new InvalidDataException("receiver-api-base-unverified"); }
    }

    private static Guid CanonicalGuid(string? value) => Guid.TryParseExact(value, "D", out var id) &&
        id != Guid.Empty && id.ToString("D") == value ? id : throw new InvalidDataException("receiver-identity-unverified");

    public static JsonDocument Parse(byte[] body)
    {
        if (body.Length > RatelDeskConnectorLimits.MaximumResponseBytes) throw new InvalidDataException("receiver-response-too-large");
        var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        try { ValidateDuplicates(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }
    private static void ValidateDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("duplicate-receiver-json-field");
                ValidateDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateDuplicates(child);
    }

    public static RatelDeskVerifiedCapability Capability(byte[] body,
        RatelDeskSemanticPeer peer, DateTimeOffset observedAtUtc)
    {
        RequirePeer(peer);
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("receiver-observation-time-invalid");
        using var doc = Parse(body); var root = doc.RootElement;
        Equal(Text(root, "contractVersion"), Contract);
        ReceiverIdentity(root, peer.ReceiverInstanceId);
        Equal(Text(root, "sourceInstanceId"), peer.SourceInstanceId.ToString("D"));
        Equal(Text(root, "sourceNamespaceId"), peer.SourceNamespaceId.ToString("D"));
        Equal(Text(root, "keyHeader"), "Idempotency-Key");
        Equal(Text(root, "sourceHeader"), "X-NetRatel-Source-Instance");
        Equal(Text(root, "keyPattern"), KeyPattern);
        if (Integer(root, "maxKeyLength") != 256 ||
            Integer(root, "minimumReceiptRetentionSeconds") < MinimumRetention ||
            Integer(root, "maximumAutomaticReplaySeconds") is < 86_400 or > MaximumReplay ||
            Flag(root, "receiptEvictionEnabled") || !Flag(root, "atomicIncidentReceiptAndEffects") ||
            !Flag(root, "supportsReceiptLookup") || !Flag(root, "supportsSafeSameKeyReplay"))
            throw new InvalidDataException("receiver-replay-guarantees-unverified");
        const string mode = "oauth_client_credentials";
        var modes = Required(root, "authenticationModes", JsonValueKind.Array);
        if (modes.GetArrayLength() is < 1 or > 8 ||
            modes.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String ||
                x.GetString() is not ("api_bearer" or "oauth_client_credentials")) ||
            modes.EnumerateArray().Select(x => x.GetString()).Distinct(StringComparer.Ordinal).Count() != modes.GetArrayLength() ||
            !modes.EnumerateArray().Any(x => x.GetString() == mode))
            throw new InvalidDataException("receiver-authentication-mode-unverified");
        var endpoints = new RatelDeskReceiverEndpoints(Endpoint(peer.ApiBaseUrl, CapabilitiesPath),
            Text(root, "createEndpoint"), Text(root, "receiptEndpointTemplate"), Text(root, "targetValidationEndpoint"));
        Equal(endpoints.Create, Endpoint(peer.ApiBaseUrl, CreatePath));
        Equal(endpoints.ReceiptTemplate, Endpoint(peer.ApiBaseUrl, ReceiptPath));
        Equal(endpoints.TargetValidation, Endpoint(peer.ApiBaseUrl, TargetsPath));
        return new(Contract, peer.ReceiverInstanceId, peer.SourceInstanceId, peer.SourceNamespaceId,
            endpoints, Integer(root, "minimumReceiptRetentionSeconds"),
            Integer(root, "maximumAutomaticReplaySeconds"), observedAtUtc)
        { OrganizationName = DisplayName(root, "organizationName"), CustomerName = DisplayName(root, "customerName") };
    }

    private static string? DisplayName(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 256 } name && !name.Any(char.IsControl) ? name : null;

    public static void Target(byte[] body, RatelDeskSemanticPeer peer)
    {
        RequirePeer(peer);
        using var doc = Parse(body); var root = doc.RootElement;
        Equal(Text(root, "contractVersion"), Contract);
        ReceiverIdentity(root, peer.ReceiverInstanceId);
        Equal(Text(root, "sourceInstanceId"), peer.SourceInstanceId.ToString("D"));
        Equal(Text(root, "sourceNamespaceId"), peer.SourceNamespaceId.ToString("D"));
        if (!Flag(root, "valid")) throw new InvalidDataException("receiver-target-unverified");
        var mapping = Required(root, "mapping", JsonValueKind.Object);
        Equal(Text(mapping, "organizationId"), peer.OrganizationId);
        Equal(Text(mapping, "customerId"), peer.CustomerId);
        if (!mapping.TryGetProperty("assignedToId", out var assignee))
            throw new InvalidDataException("receiver-target-unverified");
        if (peer.AssignedToId is null ? assignee.ValueKind != JsonValueKind.Null :
                assignee.ValueKind != JsonValueKind.String || assignee.GetString() != peer.AssignedToId)
            throw new InvalidDataException("receiver-target-unverified");
        var categories = Required(mapping, "categoryIds", JsonValueKind.Array);
        if (categories.GetArrayLength() != peer.CategoryIds.Length ||
            !categories.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                .SequenceEqual(peer.CategoryIds, StringComparer.Ordinal))
            throw new InvalidDataException("receiver-target-unverified");
    }

    public static RatelDeskVerifiedReceipt Receipt(byte[] body, string? locationHeader,
        RatelDeskReceiverPreparationV2 prepared)
    {
        RequirePeer(prepared.Peer);
        if (!RatelDeskReceiverKey.IsConforming(prepared.ReceiverIdempotencyKey) ||
            prepared.ReceiverFingerprint is not { Length: 64 } ||
            !prepared.ReceiverFingerprint.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new InvalidDataException("receiver-preparation-unverified");
        using var doc = Parse(body); var root = doc.RootElement;
        var receipt = Required(root, "integrationReceipt", JsonValueKind.Object);
        Equal(Text(receipt, "contractVersion"), Contract);
        Equal(Text(receipt, "receiverInstanceId"), prepared.Peer.ReceiverInstanceId);
        Equal(Text(receipt, "sourceInstanceId"), prepared.Peer.SourceInstanceId.ToString("D"));
        Equal(Text(receipt, "sourceNamespaceId"), prepared.Peer.SourceNamespaceId.ToString("D"));
        Equal(Text(receipt, "key"), prepared.ReceiverIdempotencyKey);
        Equal(Text(receipt, "fingerprint"), prepared.ReceiverFingerprint);
        Equal(Text(receipt, "outcome"), "committed");
        Equal(Text(receipt, "organizationId"), prepared.Peer.OrganizationId);
        Equal(Text(receipt, "customerId"), prepared.Peer.CustomerId);
        var incident = SafeId(Text(receipt, "incidentId")); var tracking = SafeId(Text(receipt, "trackingId"));
        Equal(Text(root, "id"), incident); Equal(Text(root, "trackingId"), tracking);
        Equal(Text(root, "organizationId"), prepared.Peer.OrganizationId);
        Equal(Text(root, "customerId"), prepared.Peer.CustomerId);
        var location = Text(receipt, "location");
        var api = new Uri(RatelDeskApiBase.Canonical(prepared.Peer.ApiBaseUrl), UriKind.Absolute);
        var expectedLocation = api.AbsolutePath.TrimEnd('/') + "/api/v1/incidents/" + incident;
        // Capture the actual API resource path, never invent a Web navigation URL.
        Equal(location, expectedLocation); Equal(locationHeader, location);
        var stamp = Text(receipt, "committedAtUtc");
        // Receiver permits fractional UTC and +00:00. B whole-second parsing is inapplicable.
        if (!System.Text.RegularExpressions.Regex.IsMatch(stamp,
                @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|\+00:00)\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var committed) || committed.Offset != TimeSpan.Zero)
            throw new InvalidDataException("receiver-commit-time-unverified");
        return new(Contract, prepared.Peer.ReceiverInstanceId, prepared.Peer.SourceNamespaceId,
            prepared.Peer.SourceInstanceId, prepared.ReceiverIdempotencyKey, prepared.ReceiverFingerprint,
            "committed", incident, tracking, prepared.Peer.OrganizationId, prepared.Peer.CustomerId,
            committed, location, System.Text.Encoding.UTF8.GetString(body));
    }

    public static string? ProblemCode(byte[] body)
    {
        using var doc = Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return null;
        var value = code.GetString();
        return value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? value : null;
    }
    private static JsonElement Required(JsonElement root, string name, JsonValueKind kind) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value : throw new InvalidDataException("receiver-field-unverified");
    private static string Text(JsonElement root, string name)
    {
        var value = Required(root, name, JsonValueKind.String).GetString();
        return value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl)
            ? value : throw new InvalidDataException("receiver-field-unverified");
    }
    private static long Integer(JsonElement root, string name) =>
        Required(root, name, JsonValueKind.Number).TryGetInt64(out var value) && value >= 0
            ? value : throw new InvalidDataException("receiver-number-unverified");
    private static bool Flag(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("receiver-boolean-unverified");
        return value.GetBoolean();
    }
    private static string SafeId(string value) => value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value :
            throw new InvalidDataException("receiver-identity-unverified");
    private static void Equal(string? actual, string expected)
    { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new InvalidDataException("receiver-binding-unverified"); }

    private static void ReceiverIdentity(JsonElement root, string expected)
    {
        var actual = CanonicalGuid(Text(root, "receiverInstanceId")).ToString("D");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new RatelDeskReceiverReadException("receiver-identity-mismatch", null, null);
    }
}
