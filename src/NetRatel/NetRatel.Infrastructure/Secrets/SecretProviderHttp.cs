using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.SystemPairing.Network;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class SecretProviderHttp(HttpClient http, SecretProviderNetworkPolicy policy)
{
    private const int MaximumBodySize = 2 * 1024 * 1024;
    private static readonly HttpRequestOptionsKey<Uri> BoundOrigin = new("NetRatel.SecretProvider.Origin");

    public async Task<JsonDocument?> SendAsync(SecretProviderContext context, HttpMethod method, string route,
        CancellationToken ct, object? body = null, ProviderAccessToken? token = null, bool allowNotFound = false, bool readBody = true,
        bool openBaoCompareAndSwap = false)
    {
        var origin = policy.ValidateOrigin(context.Configuration.Endpoint);
        using var request = new HttpRequestMessage(method, new Uri(origin, route));
        Bind(request, origin);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (token is not null)
            request.Headers.Add(token.Header, token.HeaderValue);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
                return null;
            if (response.StatusCode == HttpStatusCode.BadRequest && openBaoCompareAndSwap &&
                await IsOpenBaoConflictAsync(response.Content, timeout.Token))
                throw Conflict();
            if (!response.IsSuccessStatusCode)
                throw Failure((int)response.StatusCode, authenticating: token is null);
            if (!readBody || response.StatusCode == HttpStatusCode.NoContent)
                return null;
            await response.Content.LoadIntoBufferAsync(MaximumBodySize, timeout.Token);
            return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (IOException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
    }

    public HttpClient CreateOriginBoundClient(Uri origin) => new(new OriginBindingHandler(origin)
    {
        InnerHandler = CreateHandler(policy)
    }) { Timeout = TimeSpan.FromSeconds(15) };

    internal static SocketsHttpHandler CreateHandler(SecretProviderNetworkPolicy policy) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.Zero,
        MaxResponseHeadersLength = 32,
        ConnectCallback = (context, ct) => ConnectAsync(policy, context, ct)
    };

    internal static SecretProviderException Failure(int statusCode, bool authenticating = false) => statusCode switch
    {
        400 when authenticating => new(SecretProviderFailure.Authentication, "The provider rejected the configured authentication."),
        400 => Invalid("The provider rejected the requested operation or data."),
        401 => new(SecretProviderFailure.Authentication, "The provider rejected the configured authentication."),
        403 when authenticating => new(SecretProviderFailure.Authentication, "The provider rejected the configured authentication."),
        403 => new(SecretProviderFailure.Forbidden, "The provider denied this operation. Check the connection's data-plane permissions."),
        404 => Missing(),
        409 or 412 => Conflict(),
        429 => new(SecretProviderFailure.Throttled, "The provider limited requests. Try again after its rate limit resets."),
        _ => Unavailable()
    };

    internal static JsonElement Property(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
        ? value : throw Unavailable();

    internal static string Text(JsonElement obj, string name) => OptionalString(obj, name) ?? throw Unavailable();
    internal static string? OptionalString(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static long Number(JsonElement obj, string name) => Property(obj, name) is { ValueKind: JsonValueKind.Number } number &&
        number.TryGetInt64(out var value) ? value : throw Unavailable();

    internal static string TokenText(JsonElement obj, string name)
    {
        var token = Text(obj, name);
        if (token.Length is < 1 or > 8192 || token.Any(c => !char.IsAscii(c) || char.IsControl(c) || char.IsWhiteSpace(c)))
            throw Unavailable();
        return token;
    }

    internal static DateTimeOffset? OptionalTimestamp(JsonElement obj, string name) =>
        DateTimeOffset.TryParse(OptionalString(obj, name), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static async Task<bool> IsOpenBaoConflictAsync(HttpContent response, CancellationToken ct)
    {
        await response.LoadIntoBufferAsync(4096, ct);
        using var json = JsonDocument.Parse(await response.ReadAsStreamAsync(ct));
        return json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array &&
            errors.EnumerateArray().Any(element => element.ValueKind == JsonValueKind.String &&
                element.GetString() == "check-and-set parameter did not match the current version");
    }

    private static void Bind(HttpRequestMessage request, Uri origin)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            Uri.Compare(uri, origin, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw Unavailable();
        request.Options.Set(BoundOrigin, origin);
    }

    private static async ValueTask<Stream> ConnectAsync(SecretProviderNetworkPolicy policy, SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(BoundOrigin, out var origin) ||
            !string.Equals(context.DnsEndPoint.Host, origin.IdnHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != origin.Port)
            throw Unavailable();
        IPAddress[] addresses = IPAddress.TryParse(origin.IdnHost, out var address)
            ? [address] : await Dns.GetHostAddressesAsync(origin.IdnHost, ct);
        PairingEndpointPolicy.ValidateResolvedAddresses(origin, addresses, "Secret provider endpoint", policy.PrivateOriginAllowed(origin));
        foreach (var candidate in addresses)
        {
            var socket = new Socket(candidate.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(candidate, origin.Port), ct);
                PairingEndpointPolicy.ValidateResolvedAddresses(origin, addresses, "Secret provider endpoint", policy.PrivateOriginAllowed(origin));
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw Unavailable();
    }

    private sealed class OriginBindingHandler(Uri origin) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bind(request, origin);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
