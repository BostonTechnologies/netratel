// src/NetRatel/NetRatel.Web/Services/Clients/ClientStreamService.cs
using System.Text.Json;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services;
using LegacySseClient = NetRatel.Web.Services.Sse.SseClient;
using LegacySseEvent = NetRatel.Web.Services.Sse.SseEvent;

namespace NetRatel.Web.Services.Clients;

public sealed class ClientStreamService : IAsyncDisposable
{
    private readonly LegacySseClient _sse;
    private Func<ClientDto, Task>? _onUpsert;
    private Func<string, Task>? _onDelete;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ClientStreamService(IHttpClientFactory factory, ITokenProvider tokens)
    {
        // ⬇️ Use the named client that already attaches bearer/redirect handling
        var http = factory.CreateClient("OrchestratorApi");
        _sse = new LegacySseClient(http, tokens.GetBearerAsync);
        _sse.OnEvent += OnSseAsync;
        _sse.OnError += ex =>
        {
            if (ex is OperationCanceledException or TaskCanceledException)
            {
                return;
            }
        };
    }

    // src/NetRatel/NetRatel.Web/Services/Clients/ClientStreamService.cs
    public async Task StartAsync(string pathOrAbsoluteUrl,
        Func<ClientDto, Task> onUpsert,
        Func<string, Task> onDelete,
        CancellationToken ct = default)
    {
        await StopAsync();
        _onUpsert = onUpsert;
        _onDelete = onDelete;

        // If caller passed an absolute URL, use it; otherwise resolve against HttpClient.BaseAddress
        var http = (HttpClient)typeof(LegacySseClient)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_sse)!;

        var target = Uri.TryCreate(pathOrAbsoluteUrl, UriKind.Absolute, out var abs)
            ? abs
            : new Uri(http.BaseAddress!, pathOrAbsoluteUrl.TrimStart('/'));

        await _sse.StartAsync(target, ct);
    }

    public Task StopAsync() => _sse.StopAsync();

    private async Task OnSseAsync(LegacySseEvent ev)
    {
        if (ev.Event?.Equals("client-upsert", StringComparison.OrdinalIgnoreCase) == true && ev.Data is not null)
        {
            var dto = JsonSerializer.Deserialize<ClientDto>(ev.Data, JsonOptions);
            if (dto is not null && _onUpsert is not null) await _onUpsert(dto);
            return;
        }

        if (ev.Event?.Equals("client-delete", StringComparison.OrdinalIgnoreCase) == true && ev.Data is not null)
        {
            using var doc = JsonDocument.Parse(ev.Data);
            if (TryGetClientIdentity(doc.RootElement, out var identity) && _onDelete is not null)
            {
                await _onDelete(identity);
            }
        }
    }

    public async ValueTask DisposeAsync() => await _sse.DisposeAsync();

    private static bool TryGetClientIdentity(JsonElement element, out string identity)
    {
        identity = string.Empty;

        if (element.TryGetProperty("clientIdentity", out var clientIdentityProp) && clientIdentityProp.GetString() is { Length: > 0 } newValue)
        {
            identity = newValue;
            return true;
        }

        if (element.TryGetProperty("identity", out var legacyProp) && legacyProp.GetString() is { Length: > 0 } legacyValue)
        {
            identity = legacyValue;
            return true;
        }

        return false;
    }
}
