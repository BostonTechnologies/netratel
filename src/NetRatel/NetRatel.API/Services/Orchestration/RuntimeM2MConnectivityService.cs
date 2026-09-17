using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetRatel.Application.Abstractions;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Services.Orchestration;

public sealed class RuntimeM2MConnectivityService(
    IHttpClientFactory httpClientFactory,
    INetRatelSystemTokenService tokenService,
    IOptions<NetRatelExternalServiceCallbackOptions> options) : IM2MConnectivityService
{
    private const string DefaultRemoteAudience = "external-service.api";
    private const string DefaultRemoteSystemName = "ExternalService";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly INetRatelSystemTokenService _tokenService = tokenService;
    private readonly NetRatelExternalServiceCallbackOptions _options = options.Value;

    public Task<M2MConnectivitySettingsDto> GetAsync(CancellationToken ct)
        => Task.FromResult(ResolveSettings());

    public Task SaveAsync(M2MConnectivitySettingsDto dto, CancellationToken ct)
        => throw new NotSupportedException("ExternalService connectivity is configured through runtime options.");

    public async Task<M2MConnectivityTestResultDto> TestAsync(M2MConnectivityTestRequestDto req, CancellationToken ct)
    {
        var settings = ResolveSettings();
        var results = new List<M2MConnectivityProbeResultDto>();
        string? token = null;

        if (req.IncludeTokenAcquire)
        {
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RemoteAudience))
            {
                results.Add(new(
                    "AcquireToken",
                    "(runtime signing key)",
                    TrafficLight.Amber,
                    null,
                    null,
                    "Connectivity is disabled or the ExternalService audience is missing.",
                    settings.RemoteSystemName,
                    DateTimeOffset.UtcNow));
            }
            else
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    token = await _tokenService.GetTokenAsync(settings.RemoteAudience, ct);
                    sw.Stop();
                    results.Add(new(
                        "AcquireToken",
                        "(runtime signing key)",
                        TrafficLight.Green,
                        null,
                        sw.ElapsedMilliseconds,
                        string.IsNullOrWhiteSpace(token) ? "Token was empty." : "Token acquired successfully.",
                        settings.RemoteSystemName,
                        DateTimeOffset.UtcNow));
                }
                catch (Exception ex)
                {
                    results.Add(new(
                        "AcquireToken",
                        "(runtime signing key)",
                        TrafficLight.Red,
                        null,
                        null,
                        Truncate(ex.Message, 256),
                        settings.RemoteSystemName,
                        DateTimeOffset.UtcNow));
                }
            }
        }

        if (req.IncludeRemoteHealth)
        {
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RemoteBaseUrl))
            {
                results.Add(new(
                    "RemoteHealth",
                    "(disabled)",
                    TrafficLight.Amber,
                    null,
                    null,
                    "Connectivity is disabled or the ExternalService API base URL is missing.",
                    settings.RemoteSystemName,
                    DateTimeOffset.UtcNow));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(settings.RemoteAudience))
                {
                    token = await _tokenService.GetTokenAsync(settings.RemoteAudience, ct);
                }

                results.Add(await ProbeExternalServicePingAsync(settings, token, ct));
            }
        }

        return new(results);
    }

    private async Task<M2MConnectivityProbeResultDto> ProbeExternalServicePingAsync(
        M2MConnectivitySettingsDto settings,
        string? token,
        CancellationToken ct)
    {
        var endpoint = $"{settings.RemoteBaseUrl!.TrimEnd('/')}/api/v1/orchestration/netratel/m2m/ping";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var client = _httpClientFactory.CreateClient("ConnectivityProbe");
            var sw = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, ct);
            sw.Stop();
            var content = await response.Content.ReadAsStringAsync(ct);

            var remoteName = settings.RemoteSystemName;
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(content))
            {
                remoteName = TryReadRemoteName(content) ?? remoteName;
            }

            return new(
                "RemoteHealth",
                endpoint,
                response.IsSuccessStatusCode
                    ? TrafficLight.Green
                    : (int)response.StatusCode is 401 or 403 ? TrafficLight.Amber : TrafficLight.Red,
                (int)response.StatusCode,
                sw.ElapsedMilliseconds,
                response.IsSuccessStatusCode
                    ? (string.IsNullOrWhiteSpace(content) ? "OK" : Truncate(content, 400))
                    : $"HTTP {(int)response.StatusCode}: {Truncate(content, 200)}",
                remoteName,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new(
                "RemoteHealth",
                endpoint,
                TrafficLight.Red,
                null,
                null,
                Truncate(ex.Message, 256),
                settings.RemoteSystemName,
                DateTimeOffset.UtcNow);
        }
    }

    private M2MConnectivitySettingsDto ResolveSettings()
    {
        var baseUrl = Normalize(_options.BaseUrl);
        var audience = Normalize(_options.Audience) ?? DefaultRemoteAudience;
        return new(
            !string.IsNullOrWhiteSpace(baseUrl),
            baseUrl,
            audience,
            DefaultRemoteSystemName);
    }

    private static string? TryReadRemoteName(string content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.TryGetProperty("service", out var service)
                && service.ValueKind == JsonValueKind.String)
            {
                return service.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
