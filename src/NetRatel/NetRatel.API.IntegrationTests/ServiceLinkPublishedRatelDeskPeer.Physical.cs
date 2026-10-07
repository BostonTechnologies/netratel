using System.Text.Json;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

// Private fixture facts from the actual published receiver's PostgreSQL database.
// Raw accepted bodies stay in memory and are never exported as public proof.
internal sealed record PhysicalPublishedIncidentCommit(Guid SourceInstanceId, Guid NamespaceId,
    string Key, string Fingerprint, Guid ReceiptId, string IncidentId, string OrganizationId,
    string CustomerId, string AcceptedJson, DateTimeOffset CommittedAtUtc, long IncidentRows,
    Guid[] ConfirmationIds);

internal sealed partial class ServiceLinkPublishedRatelDeskPeer
{
    public Task<string> StopPhysicalApiAsync(CancellationToken ct) => ComposeAsync(["stop", "api"], ct);
    public async Task RestorePhysicalApiAsync(CancellationToken ct)
    {
        await ComposeAsync(["up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "api"], ct);
        await LoginAsync();
    }

    public async Task<(long Receipts, long Incidents, long Confirmations)> ReadPhysicalNamespaceCountsAsync(
        Guid source, Guid sourceNamespace, CancellationToken ct)
    {
        if (source == Guid.Empty || sourceNamespace == Guid.Empty) throw new ArgumentException("The actual namespace identity is required.");
        var sql = $$"""
            SELECT json_build_object('receipts', count(*),
              'incidents', count(DISTINCT t."Id"),
              'confirmations', (SELECT count(*) FROM "MailboxOutboxEffect" e JOIN "IncidentCreateReceipts" er ON er."Id"=e."ReceiptId"
                WHERE er."SourceNamespaceId"='{{sourceNamespace:D}}' AND e."Kind"=0 AND e."Payload"::jsonb ? 'SupportDeliveryId'
                  AND e."Payload"::jsonb ->> 'SupportDeliveryId' IS NULL))
            FROM "IncidentCreateReceipts" r JOIN "IncidentReceiverSources" s ON s."SourceNamespaceId"=r."SourceNamespaceId"
            JOIN "Tickets" t ON t."Id"=r."IncidentId" AND t."Discriminator"='Incident'
            WHERE s."SourceInstanceId"='{{source:D}}' AND r."SourceNamespaceId"='{{sourceNamespace:D}}';
            """;
        var output = await ComposeAsync(["exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", "rateldesk", "-d", "rateldesk", "-At", "-c", sql], ct);
        using var result = JsonDocument.Parse(output.Trim());
        return (result.RootElement.GetProperty("receipts").GetInt64(), result.RootElement.GetProperty("incidents").GetInt64(), result.RootElement.GetProperty("confirmations").GetInt64());
    }

    public async Task<PhysicalPublishedIncidentCommit> ReadPhysicalIncidentCommitAsync(
        Guid source, Guid sourceNamespace, string key, CancellationToken ct)
    {
        if (source == Guid.Empty || sourceNamespace == Guid.Empty ||
            key is not { Length: > 0 and <= 256 } ||
            !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '-'))
            throw new ArgumentException("A canonical current source/namespace/key is required.");
        // Every interpolated value has been restricted above. No caller-supplied SQL,
        // title search, seeded receipt or transaction shared with HTTP is supported.
        var sql = $$"""
            SELECT json_build_object(
              'sourceInstanceId', s."SourceInstanceId", 'namespaceId', r."SourceNamespaceId",
              'key', r."Key", 'fingerprint', r."Fingerprint", 'receiptId', r."Id",
              'incidentId', r."IncidentId", 'organizationId', r."OrganizationId",
              'customerId', r."CustomerId", 'acceptedJson', r."AcceptedJson",
              'committedAtUtc', r."CommittedAtUtc",
              'incidentRows', (SELECT count(*) FROM "Tickets" t
                WHERE t."Discriminator" = 'Incident' AND t."Id" = r."IncidentId"
                  AND t."OrganizationId" = r."OrganizationId" AND t."CustomerId" = r."CustomerId"),
              'confirmationIds', (SELECT COALESCE(json_agg(e."Id" ORDER BY e."Id"), '[]'::json)
                FROM "MailboxOutboxEffect" e
                WHERE e."ReceiptId" = r."Id" AND e."Kind" = 0
                  AND e."Payload"::jsonb ? 'SupportDeliveryId'
                  AND e."Payload"::jsonb ->> 'SupportDeliveryId' IS NULL
                  AND e."Payload"::jsonb ->> 'TicketId' = r."IncidentId"
                  AND e."Payload"::jsonb ->> 'OrganizationId' = r."OrganizationId"))
            FROM "IncidentCreateReceipts" r JOIN "IncidentReceiverSources" s
              ON s."SourceNamespaceId" = r."SourceNamespaceId"
            WHERE s."SourceInstanceId" = '{{source:D}}'
              AND r."SourceNamespaceId" = '{{sourceNamespace:D}}' AND r."Key" = '{{key}}';
            """;
        var output = await ComposeAsync(["exec", "-T", "postgres", "psql", "-v",
            "ON_ERROR_STOP=1", "-U", "rateldesk", "-d", "rateldesk", "-At", "-c", sql], ct);
        // Unique source/namespace/key means exactly one JSON row. Reject missing,
        // duplicated, truncated and malformed output with a fixed nonsecret error.
        PhysicalPublishedIncidentCommit? value;
        try
        {
            value = JsonSerializer.Deserialize<PhysicalPublishedIncidentCommit>(output.Trim(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The published receiver committed-row read was invalid.");
        }
        if (value is null || value.SourceInstanceId != source || value.NamespaceId != sourceNamespace ||
            value.Key != key || value.ReceiptId == Guid.Empty || value.CommittedAtUtc == default ||
            value.IncidentRows != 1 || value.ConfirmationIds is not { Length: 1 } ||
            value.ConfirmationIds[0] == Guid.Empty || string.IsNullOrEmpty(value.IncidentId) ||
            string.IsNullOrEmpty(value.AcceptedJson) || value.AcceptedJson.Length > 131_072)
            throw new InvalidOperationException("The actual committed incident/receipt/confirmation identity did not match.");
        return value;
    }
}
