using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Web.Services.Notifications;

public interface INotificationConnectorApiService
{
    Task<IReadOnlyList<NotificationConnectorTenantDto>> GetTenantsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NotificationConnectorDto>> ListAsync(int tenantId, CancellationToken ct = default);
    Task<NotificationConnectorDto> SaveAsync(int tenantId, Guid id, SaveNotificationConnectorRequest request, CancellationToken ct = default);
    Task DeleteAsync(int tenantId, Guid id, long expectedRevision, CancellationToken ct = default);
    Task<NotificationDeliveryResult> SendTestAsync(int tenantId, Guid id, CancellationToken ct = default);
}

public sealed class NotificationConnectorApiService(IHttpClientFactory factory) : INotificationConnectorApiService
{
    private static string Root(int tenantId) => $"/api/v2/tenants/{tenantId}/connectors/notifications";
    public Task<IReadOnlyList<NotificationConnectorTenantDto>> GetTenantsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<NotificationConnectorTenantDto>>(HttpMethod.Get, "/api/v2/connectors/notifications/tenants", null, ct);
    public Task<IReadOnlyList<NotificationConnectorDto>> ListAsync(int tenantId, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<NotificationConnectorDto>>(HttpMethod.Get, Root(tenantId), null, ct);
    public Task<NotificationConnectorDto> SaveAsync(int tenantId, Guid id, SaveNotificationConnectorRequest request, CancellationToken ct = default) =>
        SendAsync<NotificationConnectorDto>(HttpMethod.Put, $"{Root(tenantId)}/{id:D}", JsonContent.Create(request), ct);
    public async Task DeleteAsync(int tenantId, Guid id, long expectedRevision, CancellationToken ct = default)
    {
        using var response = await SendRequestAsync(HttpMethod.Delete, $"{Root(tenantId)}/{id:D}?expectedRevision={expectedRevision}", null, ct).ConfigureAwait(false);
    }
    public Task<NotificationDeliveryResult> SendTestAsync(int tenantId, Guid id, CancellationToken ct = default) =>
        SendAsync<NotificationDeliveryResult>(HttpMethod.Post, $"{Root(tenantId)}/{id:D}/test-notification", null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var response = await SendRequestAsync(method, path, body, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("The notification connector response was empty.");
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, path) { Content = body };
        if (method != HttpMethod.Get) request.Headers.Add("X-NetRatel-Account-Request", "1");
        var response = await factory.CreateClient("OrchestratorApi").SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(status switch
        {
            HttpStatusCode.Forbidden => "You do not have permission to manage or use notification credentials in this tenant.",
            HttpStatusCode.Conflict => "This notification target changed. Reload it before saving again.",
            HttpStatusCode.NotFound => "This notification target is no longer available.",
            HttpStatusCode.BadRequest => "Check the name, provider options and webhook URL. A new target needs its provider's incoming webhook URL.",
            HttpStatusCode.TooManyRequests => "The notification target limit was reached.",
            _ => "The notification connector request could not be completed."
        }, null, status);
    }
}
