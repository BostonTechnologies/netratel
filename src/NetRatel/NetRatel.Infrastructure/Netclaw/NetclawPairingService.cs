// Native Netclaw protocol adapted from BostonTechnologies/rateldesk (Apache-2.0).
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed class NetclawPairingService : INetclawPairingService
{
    private const int MaximumResponseBytes = 4 * 1024;
    private const int MaximumTokenLength = 2048;
    private const int MaximumNameAttempts = 3;
    private readonly HttpClient httpClient;
    private readonly TimeSpan requestDeadline;

    public NetclawPairingService(HttpClient httpClient)
        : this(httpClient, TimeSpan.FromSeconds(15))
    {
    }

    internal NetclawPairingService(HttpClient httpClient, TimeSpan requestDeadline)
    {
        this.httpClient = httpClient;
        this.requestDeadline = requestDeadline;
    }

    public async Task<string> ExchangeCodeAsync(
        NetclawPairingTarget target,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(pairingCode) || pairingCode.Length > 1024)
            throw new NetclawPairingException("invalid_pairing_code", 400, "Enter a valid one-time pairing code.");

        var exchangeEndpoint = BuildExchangeEndpoint(target.SessionEndpoint, target.AllowPrivateHttp);
        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(requestDeadline);
            using var request = new HttpRequestMessage(HttpMethod.Post, exchangeEndpoint)
            {
                Content = JsonContent.Create(new PairExchangeRequest(
                    pairingCode,
                    $"netratel-api-{Guid.NewGuid():N}"))
            };
            request.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, target.AllowPrivateHttp);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NetclawPairingException("pairing_timeout", 504, "Netclaw pairing timed out. Check the saved profile before retrying.");
            }
            catch (HttpRequestException)
            {
                throw new NetclawPairingException("pairing_outcome_uncertain", 502, "Netclaw pairing could not be confirmed. Check the saved profile before requesting another code.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Conflict && attempt + 1 < MaximumNameAttempts)
                    continue;

                if (!response.IsSuccessStatusCode)
                    throw CreateStatusException(response.StatusCode);

                try
                {
                    return await ReadTokenAsync(response.Content, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new NetclawPairingException("pairing_timeout", 504, "Netclaw pairing timed out. Check the saved profile before retrying.");
                }
                catch (HttpRequestException)
                {
                    throw new NetclawPairingException("pairing_outcome_uncertain", 502, "Netclaw pairing could not be confirmed. Check the saved profile before requesting another code.");
                }
                catch (IOException)
                {
                    throw new NetclawPairingException("pairing_outcome_uncertain", 502, "Netclaw pairing could not be confirmed. Check the saved profile before requesting another code.");
                }
            }
        }

        throw new NetclawPairingException(
            "pairing_device_name_conflict",
            409,
            "Netclaw could not accept a unique NetRatel device name. Retry with a fresh pairing code.");
    }

    private static Uri BuildExchangeEndpoint(Uri sessionEndpoint, bool allowPrivateHttp)
    {
        try
        {
            return NetclawEndpointNormalizer.BuildPairingExchangeEndpoint(sessionEndpoint, allowPrivateHttp);
        }
        catch (ArgumentException)
        {
            throw new NetclawPairingException("invalid_pairing_target", 400, "The Netclaw daemon address is not allowed for pairing.");
        }
    }

    private static NetclawPairingException CreateStatusException(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized => new("pairing_code_rejected", 400, "Netclaw rejected the pairing code. Check that it is current and unused."),
            HttpStatusCode.NotFound => new("pairing_code_not_found", 400, "Netclaw has no active pairing code. Generate a new code and try again."),
            HttpStatusCode.TooManyRequests => new("pairing_rate_limited", 429, "Netclaw is rate limiting pairing. Wait before trying again."),
            HttpStatusCode.Conflict => new("pairing_device_name_conflict", 409, "Netclaw could not accept a unique NetRatel device name. Retry with a fresh pairing code."),
            _ => new("pairing_service_error", 502, "Netclaw did not complete the pairing exchange.")
        };

    private static async Task<string> ReadTokenAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw InvalidResponse();

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaximumResponseBytes + 1];
        var totalBytes = 0;
        while (totalBytes < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytes), cancellationToken);
            if (bytesRead == 0) break;
            totalBytes += bytesRead;
        }

        if (totalBytes > MaximumResponseBytes)
            throw InvalidResponse();

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, totalBytes));
            if (!document.RootElement.TryGetProperty("token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
                throw InvalidResponse();

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
                throw InvalidResponse();
            return token;
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static NetclawPairingException InvalidResponse()
        => new("invalid_pairing_response", 502, "Netclaw returned an invalid pairing response.");

    private sealed record PairExchangeRequest(string Code, string DeviceName);
}
