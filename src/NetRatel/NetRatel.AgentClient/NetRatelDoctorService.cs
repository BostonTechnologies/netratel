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
    public const string ServiceAuthenticationStatusPath = "/api/v1/auth/http-host/status";

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
        var mode = NetRatelConfigurationService.AuthenticationModeOf(effective.Configuration);
        var serviceIdentity = mode == AgentClientAuthenticationMode.ApiM2MClientSecret;
        var checkName = mode == AgentClientAuthenticationMode.IntegrationCredential ? "authentication" : "token acquisition";
        try
        {
            var client = new NetRatelAgentClient(effective.Configuration, handlerFactory);
            if (mode != AgentClientAuthenticationMode.IntegrationCredential)
            {
                await client.GetAccessTokenAsync(bounded.Token).ConfigureAwait(false);
                checks.Add(new(checkName, true, true,
                    "The configured token endpoint issued an access token. API authorization and delegated MCP access have not yet been verified."));
            }
            checkName = serviceIdentity ? "service API authorization" : "authentication";
            var status = await client.GetAsync(serviceIdentity ? ServiceAuthenticationStatusPath : AuthenticationStatusPath, ct: bounded.Token).ConfigureAwait(false);
            var validStatus = IsAuthenticationStatus(status, serviceIdentity);
            checks.Add(new(checkName, true, validStatus, validStatus
                ? serviceIdentity
                    ? "The API accepted the configured service identity on its read-only service status endpoint. Complete HTTP MCP setup verification to check caller delegation, tool access and tenant grants."
                    : "The selected API accepted its configured identity on a read-only status endpoint. Tool authorization is separate."
                : "The response was not a valid NetRatel authentication status document. Check the public API route; an HTML page or unrelated JSON does not authenticate setup.", 200));
        }
        catch (AgentClientRemoteException exception)
        {
            var message = checkName == "token acquisition"
                ? exception.Code == "auth_token_invalid"
                    ? "The configured token endpoint returned an invalid token document. Check the token URL and provider; no credential or token response is shown."
                    : "The configured token endpoint did not issue an access token. Check the selected client registration, secret source, scope and lifecycle; the API status request was not attempted."
                : exception.StatusCode is 401 or 403
                    ? serviceIdentity
                        ? "Token acquisition succeeded, but the API rejected the service status request. Check API token validation, the allowed service caller and its current grants. This does not establish that the client secret is wrong; no alternate identity was tried."
                        : "The selected API rejected the read-only account status request. Check this identity's purpose, grants and lifecycle; no alternate identity was tried."
                    : serviceIdentity && exception.StatusCode == 404
                        ? "The API does not expose the service status endpoint. Upgrade the API with the HTTP MCP server, then rerun doctor. Token issuance alone does not verify the connection."
                        : "The selected API did not complete the read-only identity check.";
            checks.Add(new(checkName, true, false, message, exception.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new(checkName, true, false, checkName == "token acquisition"
                ? "The token request timed out. Check DNS, TLS and reachability of the configured token endpoint; API authorization was not attempted."
                : "The read-only API identity check timed out. Check DNS, TLS and reachability."));
        }
        catch (HttpRequestException)
        {
            checks.Add(new(checkName, true, false, checkName == "token acquisition"
                ? "The configured token endpoint could not be reached securely. Check DNS, TLS and reachability; API authorization was not attempted."
                : "The selected API could not be reached securely. Check DNS, TLS and reachability."));
        }
        catch (JsonException)
        {
            checks.Add(new(checkName, true, false, "The selected route returned an invalid authentication status document. Check that it reaches the API rather than an HTML page.", 200));
        }
        return new(checks.All(check => check.Completed && check.Passed), diagnostic, checks);
    }

    private static bool IsAuthenticationStatus(JsonNode? status, bool serviceIdentity)
        => status is JsonObject document && document["authenticated"] is JsonValue authenticated &&
           authenticated.TryGetValue<bool>(out var accepted) && accepted &&
           document["authMode"] is JsonValue mode && mode.TryGetValue<string>(out var modeName) && !string.IsNullOrWhiteSpace(modeName) &&
           (!serviceIdentity || (modeName is "m2m" or "service") &&
               document["delegatedMcpVerified"] is JsonValue delegated && delegated.TryGetValue<bool>(out var verified) && !verified) &&
           document["groups"] is JsonArray groups && IsStringArray(groups) &&
           document["roles"] is JsonArray roles && IsStringArray(roles);

    private static bool IsStringArray(JsonArray values) => values.All(item =>
        item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text));
}
