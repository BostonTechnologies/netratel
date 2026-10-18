using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Mcp.Core;

public sealed partial class NetRatelMcpOperationalTools
{
    private static readonly TimeSpan HealthProbeBudget = TimeSpan.FromSeconds(10);

    private async Task<NetRatelToolResponse> HealthAsync(string operation, CancellationToken cancellationToken)
    {
        if (operation != "get") return Unsupported("netratel_health", operation, ["get"]);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthProbeBudget);
        var live = ObserveHealthAsync(McpApiHealthRoutes.Liveness, readiness: false, deadline.Token, cancellationToken);
        var ready = ObserveHealthAsync(McpApiHealthRoutes.Readiness, readiness: true, deadline.Token, cancellationToken);
        await Task.WhenAll(live, ready).ConfigureAwait(false);
        var observations = new[] { live.Result, ready.Result };
        var authenticated = observations.Any(probe => probe.Status == "healthy")
            ? "admitted"
            : observations.Any(probe => probe.Status == "unauthorized") ? "denied" : "unknown";
        var data = new JsonObject
        {
            ["scope"] = "configured_api_destination",
            ["components"] = new JsonObject
            {
                ["liveness"] = live.Result.ToJson(),
                ["readiness"] = ready.Result.ToJson(),
                ["authentication"] = new JsonObject
                {
                    ["status"] = authenticated,
                    ["scope"] = "protected_health_route",
                    ["note"] = "Observes HealthRead route admission only. Use netratel_auth/status and an approved tenant read to verify delegated authentication and business access."
                }
            },
            ["mcpHost"] = "Observe netratel://health separately; this tool probes the configured API destination."
        };
        var healthy = observations.All(probe => probe.Status == "healthy");
        return new NetRatelToolResponse(healthy, healthy ? "completed" : "failed",
            healthy ? "Configured API health probes completed." : "One or more configured API health components did not pass; inspect the retained component observations.",
            data, Error: healthy ? null : new NetRatelToolError("health_component_failed", observations.Any(probe => probe.Retryable)))
            .WithFailureContext("netratel_health", operation);
    }

    private async Task<HealthObservation> ObserveHealthAsync(string path, bool readiness,
        CancellationToken deadline, CancellationToken callerCancellation)
    {
        try
        {
            // API responses may contain dependency information. Only these bounded facts
            // cross the MCP boundary, including when the API returns an error body.
            var response = await client.GetAsync(path, deadline).ConfigureAwait(false);
            if (response is not JsonObject body || body["status"] is not JsonValue status ||
                !status.TryGetValue<string>(out var value) || value != (readiness ? "ready" : "alive"))
                return new("invalid_response", "health_probe_invalid_response", 200, false);
            return new("healthy", "health_probe_succeeded", 200, false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            return new("timed_out", "health_probe_timed_out", null, true);
        }
        catch (NetRatelMcpApiException error) when (error.Code == "remote_response_invalid")
        {
            return new("invalid_response", "health_probe_invalid_response", error.StatusCode, false);
        }
        catch (NetRatelMcpApiException error)
        {
            var status = error.StatusCode switch
            {
                401 or 403 => "unauthorized",
                404 => "unmapped",
                503 when readiness && error.RemoteCode == "api_readiness_unhealthy" => "unready",
                _ => "unavailable"
            };
            return new(status, readiness && error.StatusCode == 503 && status != "unready"
                ? "health_probe_readiness_503_backend_cause_unknown" : "health_probe_" + status, error.StatusCode, error.StatusCode >= 500);
        }
        catch (JsonException)
        {
            return new("invalid_response", "health_probe_invalid_response", null, false);
        }
        catch (HttpRequestException)
        {
            return new("unavailable", "health_probe_unavailable", null, true);
        }
    }

    private sealed record HealthObservation(string Status, string Code, int? HttpStatus, bool Retryable)
    {
        public JsonObject ToJson() => new()
        {
            ["status"] = Status, ["code"] = Code, ["httpStatus"] = HttpStatus, ["retryable"] = Retryable
        };
    }
}
