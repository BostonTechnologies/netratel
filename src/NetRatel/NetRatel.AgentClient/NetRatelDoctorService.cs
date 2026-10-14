using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

public sealed record ConfigurationDoctorCheck(string Name, bool Completed, bool Passed, string Message, int? HttpStatus = null);
public sealed record ConfigurationDoctorResult(bool IsValid, ConfigurationDiagnostic Configuration, IReadOnlyList<ConfigurationDoctorCheck> Checks)
{
    public int ExitCode => IsValid ? 0 : 2;
}

/// <summary>Local validation is always separate from an explicitly requested, bounded read-only auth probe.</summary>
public sealed class NetRatelDoctorService(Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string AuthenticationStatusPath = "/api/v1/auth/ai-agent/status";

    public async Task<ConfigurationDoctorResult> CheckAsync(EffectiveAgentClientConfiguration effective,
        bool connectivity = false, CancellationToken cancellationToken = default)
    {
        var diagnostic = effective.Diagnostics();
        var checks = new List<ConfigurationDoctorCheck>
        {
            new("configuration", true, diagnostic.IsValid, diagnostic.IsValid ? "Local configuration is valid." : "Local configuration has errors; review the redacted diagnostics.")
        };
        if (!connectivity || !diagnostic.IsValid)
            return new(diagnostic.IsValid, diagnostic, checks);
        if (effective.Surface == ConfigurationSurface.HttpMcpClient)
        {
            checks.Add(new("authentication", false, false, "HTTP MCP requires an MCP initialize and an authorized read-only tool check with the selected client. Local configuration does not establish those checks."));
            return new(false, diagnostic, checks);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var client = new NetRatelAgentClient(effective.Configuration, handlerFactory);
            var status = await client.GetAsync(AuthenticationStatusPath, ct: bounded.Token).ConfigureAwait(false);
            var validStatus = IsAuthenticationStatus(status);
            checks.Add(new("authentication", true, validStatus, validStatus
                ? "The selected API accepted its configured identity on a read-only status endpoint. Tool authorization is separate."
                : "The response was not a valid NetRatel authentication status document. Check the public API route; an HTML page or unrelated JSON does not authenticate setup.", 200));
        }
        catch (AgentClientRemoteException exception)
        {
            checks.Add(new("authentication", true, false,
                exception.StatusCode is 401 or 403 ? "The selected API rejected authentication or authorization. Check this identity's purpose, grants and lifecycle; no alternate identity was tried." : "The selected API did not complete the read-only authentication check.", exception.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new("authentication", true, false, "The selected API authentication check timed out. Check DNS, TLS and reachability."));
        }
        catch (HttpRequestException)
        {
            checks.Add(new("authentication", true, false, "The selected API could not be reached securely. Check DNS, TLS and reachability."));
        }
        catch (JsonException)
        {
            checks.Add(new("authentication", true, false, "The selected route returned an invalid authentication status document. Check that it reaches the API rather than an HTML page.", 200));
        }
        return new(checks.All(check => check.Completed && check.Passed), diagnostic, checks);
    }

    private static bool IsAuthenticationStatus(JsonNode? status)
        => status is JsonObject document && document["authenticated"] is JsonValue authenticated &&
           authenticated.TryGetValue<bool>(out var accepted) && accepted &&
           document["authMode"] is JsonValue mode && mode.TryGetValue<string>(out var modeName) && !string.IsNullOrWhiteSpace(modeName) &&
           document["groups"] is JsonArray groups && IsStringArray(groups) &&
           document["roles"] is JsonArray roles && IsStringArray(roles);

    private static bool IsStringArray(JsonArray values) => values.All(item =>
        item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text));
}
