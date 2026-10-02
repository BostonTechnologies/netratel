using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Bounded typed HTTP client. The registered handler disables all redirects and cookies.</summary>
public sealed class RatelDeskHttpTransport(HttpClient http, IRatelDeskOriginPolicy origins, TimeProvider time, RatelDeskTransportLimiter limiter)
    : IRatelDeskConnectionTester, IRatelDeskIncidentTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid connectorId, RatelDeskConnectorConfiguration configuration,
        string credential, CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || connectorId == Guid.Empty || !origins.TryValidate(configuration.Origin, out var origin) || !RatelDeskConnectorService.ValidCredential(credential))
            return new(RatelDeskConnectionTestStatus.Unavailable, "invalid-origin-or-credential");
        using var operation = await limiter.TryAcquireAsync(tenantId, connectorId, cancellationToken).ConfigureAwait(false);
        if (operation is null) return new(RatelDeskConnectionTestStatus.Unavailable, "connector-busy");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var lookups = new List<(string Path, string Id, string? Organization)> {
                ("api/v1/ticketing/organizations?module=incident", configuration.OrganizationId, null),
                ("api/v1/ticketing/customers?module=incident&organizationId=" + Uri.EscapeDataString(configuration.OrganizationId), configuration.CustomerId, configuration.OrganizationId)
            };
            if (configuration.AssignedToId is { } assignee)
                lookups.Add(("api/v1/ticketing/assignees?module=incident&organizationId=" + Uri.EscapeDataString(configuration.OrganizationId), assignee, configuration.OrganizationId));
            foreach (var lookup in lookups)
            {
                using var request = Request(HttpMethod.Get, new Uri(origin!, lookup.Path), credential);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(RatelDeskConnectionTestStatus.AuthenticationRejected, "receiver-auth-or-target-denied");
                if ((int)response.StatusCode == 429) return new(RatelDeskConnectionTestStatus.Unavailable, "receiver-rate-limited", RetryAfterSeconds: RetryAfter(response));
                if (!response.IsSuccessStatusCode) return new(RatelDeskConnectionTestStatus.Unavailable, "receiver-read-unavailable");
                using var document = JsonDocument.Parse(await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false));
                var items = document.RootElement;
                if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > RatelDeskConnectorLimits.MaximumLookupItems)
                    return new(RatelDeskConnectionTestStatus.Unavailable, "receiver-read-contract-unverified");
                var found = items.EnumerateArray().Any(item => String(item, "id") == lookup.Id &&
                    (lookup.Organization is null || String(item, "organizationId") == lookup.Organization));
                if (!found) return new(RatelDeskConnectionTestStatus.MappingRejected, "receiver-target-not-authorized");
            }
            if (configuration.CategoryIds.Count != 0)
            {
                using var request = Request(HttpMethod.Get, new Uri(origin!, "api/v1/categories/?type=Incident"), credential);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new(RatelDeskConnectionTestStatus.MappingRejected, "receiver-category-not-authorized");
                using var document = JsonDocument.Parse(await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > RatelDeskConnectorLimits.MaximumLookupItems ||
                    configuration.CategoryIds.Any(id => !root.EnumerateArray().Any(item => String(item, "id") == id.ToString("D") &&
                        item.TryGetProperty("isActive", out var active) && active.ValueKind == JsonValueKind.True)))
                    return new(RatelDeskConnectionTestStatus.MappingRejected, "receiver-category-not-authorized");
            }
            // Actual current RatelDesk provides normal creation, but no replay/receipt/atomic confirmation capability.
            return new(RatelDeskConnectionTestStatus.MappingValidated, RatelDeskConnectorLimits.ReceiverUnavailableCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(RatelDeskConnectionTestStatus.Unavailable, "receiver-read-timeout"); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException)
        { return new(RatelDeskConnectionTestStatus.Unavailable, "receiver-read-contract-unverified"); }
    }

    public async Task<RatelDeskDeliveryResult> CreateAsync(int tenantId, Guid connectorId, string origin, string credential, RatelDeskCreateIncidentDto payload,
        CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || connectorId == Guid.Empty || !origins.TryValidate(origin, out var normalized) || !RatelDeskConnectorService.ValidCredential(credential)) return new(RatelDeskDeliveryStatus.Unavailable, "invalid-origin-or-credential");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        if (bytes.Length > RatelDeskConnectorLimits.MaximumRequestBytes) return new(RatelDeskDeliveryStatus.PayloadRejected, "incident-request-too-large");
        using var operation = await limiter.TryAcquireAsync(tenantId, connectorId, cancellationToken).ConfigureAwait(false);
        if (operation is null) return new(RatelDeskDeliveryStatus.Unavailable, "connector-busy");
        var sendStarted = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = Request(HttpMethod.Post, new Uri(normalized!, "api/v1/incidents/"), credential);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            cancellationToken.ThrowIfCancellationRequested();
            sendStarted = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(RatelDeskDeliveryStatus.AuthenticationRejected, "receiver-auth-rejected");
            if ((int)response.StatusCode == 429) return new(RatelDeskDeliveryStatus.RateLimited, "receiver-rate-limited", RetryAfterSeconds: RetryAfter(response));
            if ((int)response.StatusCode is >= 300 and < 400) return new(RatelDeskDeliveryStatus.DeliveryUnknown, "receiver-redirect-refused");
            if ((int)response.StatusCode is >= 400 and < 500) return new(RatelDeskDeliveryStatus.PayloadRejected, "receiver-payload-rejected");
            if (response.StatusCode != HttpStatusCode.Created) return new(RatelDeskDeliveryStatus.DeliveryUnknown, "receiver-commit-unconfirmed");
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false));
            var receipt = document.RootElement;
            var id = String(receipt, "id"); var tracking = String(receipt, "trackingId");
            if (!SafeReceiptValue(id) || !SafeReceiptValue(tracking) || String(receipt, "organizationId") != payload.OrganizationId || String(receipt, "customerId") != payload.CustomerId)
                return new(RatelDeskDeliveryStatus.DeliveryUnknown, "receiver-receipt-unverified");
            // The navigation route has not been verified for the intended deployment. Do not invent a browser link.
            return new(RatelDeskDeliveryStatus.Succeeded, "incident-created", new(id!, tracking!, null));
        }
        catch (OperationCanceledException) when (sendStarted) { return new(RatelDeskDeliveryStatus.DeliveryUnknown, "receiver-commit-unconfirmed"); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException)
        { return new(RatelDeskDeliveryStatus.DeliveryUnknown, "receiver-commit-unconfirmed"); }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string credential)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.Add(new("application/json"));
        return request;
    }
    private static string? String(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool SafeReceiptValue(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private int? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } at ? at - time.GetUtcNow() : null);
        return delay is null ? null : (int)Math.Clamp(Math.Ceiling(delay.Value.TotalSeconds), 1d, 300d);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > RatelDeskConnectorLimits.MaximumResponseBytes) throw new InvalidDataException("receiver-response-too-large");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[4_096];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > RatelDeskConnectorLimits.MaximumResponseBytes) throw new InvalidDataException("receiver-response-too-large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
