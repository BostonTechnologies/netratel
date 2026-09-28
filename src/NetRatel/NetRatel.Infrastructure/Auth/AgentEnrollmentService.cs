using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NetRatel.Application.ClientAuth;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Services;

namespace NetRatel.Infrastructure.Auth;

public sealed class AgentEnrollmentService : IAgentEnrollmentService
{
    private readonly HttpClient _http;
    private readonly IAgentDeviceKeyStore _deviceKeyStore;

    public AgentEnrollmentService(HttpClient http, IAgentDeviceKeyStore deviceKeyStore)
    {
        _http = http;
        _deviceKeyStore = deviceKeyStore;
    }

    public async Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(enrollmentCode))
        {
            throw new AgentClientAuthException("Enrollment code is required.");
        }

        var key = await _deviceKeyStore.GetOrCreateAsync(ct).ConfigureAwait(false);
        var deviceInfo = new
        {
            os = Environment.OSVersion.Platform.ToString(),
            osVersion = Environment.OSVersion.VersionString,
            hostName = Environment.MachineName,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        };

        var requestedScopes = new[] { "netratel:connect" };
        var deviceInfoJson = JsonSerializer.Serialize(deviceInfo);
        var body = JsonSerializer.Serialize(new
        {
            enrollmentCode = enrollmentCode.Trim(),
            publicKey = key.PublicKey,
            keyAlgorithm = key.Algorithm,
            requestedScopes,
            deviceInfoJson
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var bodyHash = PopSignatureService.ComputeEnrollmentBodyHash(
            enrollmentCode,
            key.PublicKey,
            key.Algorithm,
            deviceInfoJson,
            requestedScopes);
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/enroll", timestamp, nonce, bodyHash);
        var signature = PopSignatureService.Sign(key.Algorithm, key.PrivateKey, message);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/enroll")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-NetRatel-Signature", signature);
        request.Headers.Add("X-NetRatel-Nonce", nonce);
        request.Headers.Add("X-NetRatel-Timestamp", timestamp.UtcDateTime.ToString("O"));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await BuildErrorDetailAsync(response, ct).ConfigureAwait(false);
            throw new AgentClientAuthException(detail, (int)response.StatusCode);
        }

        var dto = await response.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (dto is null || dto.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(dto.RefreshToken))
        {
            throw new AgentClientAuthException("Enrollment response was invalid.");
        }

        return (dto.AgentId.ToString(), dto.RefreshToken);
    }

    private static async Task<string> BuildErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? correlationId = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("correlationId", out var directCorrelation))
                {
                    correlationId = directCorrelation.GetString();
                }
                if (string.IsNullOrWhiteSpace(correlationId) &&
                    doc.RootElement.TryGetProperty("extensions", out var ext) &&
                    ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("correlationId", out var corr))
                {
                    correlationId = corr.GetString();
                }
            }

            var safeCorrelation = SafeDiagnosticToken(correlationId);
            var detail = response.StatusCode == HttpStatusCode.Unauthorized
                ? "Enrollment was rejected."
                : "Enrollment failed.";
            return $"status={(int)response.StatusCode}; detail={detail}" +
                   (safeCorrelation is null ? string.Empty : $"; correlationId={safeCorrelation}");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return $"status={(int)response.StatusCode} {response.ReasonPhrase}; detail={(response.StatusCode == HttpStatusCode.Unauthorized ? "Unauthorized." : "Enrollment failed.")}";
        }
    }

    private sealed record EnrollResponse(Guid AgentId, string RefreshToken, int ExpiresInDays);

    private static string? SafeDiagnosticToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? value
            : null;
}
