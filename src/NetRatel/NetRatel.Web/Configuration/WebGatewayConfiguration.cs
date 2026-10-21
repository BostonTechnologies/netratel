using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace NetRatel.Web.Configuration;

/// <summary>
/// Composes generated defaults and operator layers before YARP sees one configuration.
/// Scalar fields merge by identity. Destinations and lists replace as complete collections
/// per configuration provider; explicit removals apply after all layers. YARP validates
/// the resulting routes/transforms before activation and retains its previous valid config.
/// </summary>
public static class WebGatewayConfiguration
{
    public static ConfigurationRoot Create(IConfiguration configuration, WebGatewayOptions options,
        Action<Exception>? reportReloadFailure = null) =>
        (ConfigurationRoot)new ConfigurationBuilder().Add(new GatewaySource(configuration, options, reportReloadFailure)).Build();

    private sealed record GatewaySource(IConfiguration Configuration, WebGatewayOptions Options,
        Action<Exception>? ReportReloadFailure) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new GatewayProvider(Configuration, Options, ReportReloadFailure);
    }

    private sealed class GatewayProvider(IConfiguration source, WebGatewayOptions options,
        Action<Exception>? reportReloadFailure) : ConfigurationProvider, IDisposable
    {
        private readonly object _gate = new();
        private IDisposable? _subscription;

        public override void Load()
        {
            Data = Compose(source, options);
            _subscription = ChangeToken.OnChange(source.GetReloadToken, Reload);
        }

        private void Reload()
        {
            lock (_gate)
            {
                try
                {
                    var candidate = Compose(source, options);
                    Data = candidate;
                    OnReload();
                }
                catch (Exception exception)
                {
                    reportReloadFailure?.Invoke(exception);
                }
            }
        }

        public void Dispose() => _subscription?.Dispose();
    }

    private static Dictionary<string, string?> Compose(IConfiguration source, WebGatewayOptions options)
    {
        var data = Defaults(options);
        if (source is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                var layer = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                ReadProvider(provider, "ReverseProxy", layer);
                Overlay(data, layer);
            }
        }
        else
        {
            Overlay(data, source.GetSection("ReverseProxy").AsEnumerable()
                .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase));
        }
        RemoveSelected(data, "RemovedRoutes", "Routes");
        RemoveSelected(data, "RemovedClusters", "Clusters");
        using var composed = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        var clusters = composed.GetSection("ReverseProxy:Clusters").GetChildren()
            .Select(section => section.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in composed.GetSection("ReverseProxy:Routes").GetChildren())
        {
            if (route["ClusterId"] is not { Length: > 0 } cluster || !clusters.Contains(cluster))
                throw new InvalidOperationException($"Gateway route '{route.Key}' must reference an existing cluster.");
        }
        foreach (var cluster in composed.GetSection("ReverseProxy:Clusters").GetChildren())
        {
            foreach (var destination in cluster.GetSection("Destinations").GetChildren())
                WebGatewayOptions.ValidateBackendUri(destination["Address"], $"ReverseProxy cluster '{cluster.Key}' destination '{destination.Key}' Address");
        }
        return data;
    }

    private static void ReadProvider(IConfigurationProvider provider, string parent, Dictionary<string, string?> values)
    {
        if (provider.TryGet(parent, out var value)) values[parent] = value;
        foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
            ReadProvider(provider, $"{parent}:{child}", values);
    }

    private static void Overlay(Dictionary<string, string?> data, Dictionary<string, string?> layer)
    {
        // A changed destination dictionary or list never leaves obsolete generated/file entries.
        var replacements = layer.Keys.Select(CollectionRoot).Where(key => key is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var replacement in replacements) RemoveSubtree(data, replacement!);
        foreach (var pair in layer) data[pair.Key] = pair.Value;
    }

    private static string? CollectionRoot(string key)
    {
        var parts = key.Split(':');
        for (var index = 1; index < parts.Length; index++)
        {
            // Route/cluster IDs and dictionary keys may themselves be numeric.
            // Only supported collection fields have replacement semantics.
            if (parts[index].Equals("RemovedRoutes", StringComparison.OrdinalIgnoreCase) ||
                parts[index].Equals("RemovedClusters", StringComparison.OrdinalIgnoreCase) ||
                (index >= 3 && parts[index].Equals("Destinations", StringComparison.OrdinalIgnoreCase)) ||
                (index == 3 && parts[index].Equals("Transforms", StringComparison.OrdinalIgnoreCase)) ||
                (index == 4 && parts[index - 1].Equals("Match", StringComparison.OrdinalIgnoreCase) &&
                 new[] { "Methods", "Hosts", "Headers", "QueryParameters" }.Contains(parts[index], StringComparer.OrdinalIgnoreCase)))
                return string.Join(':', parts.Take(index + 1));
        }
        return null;
    }

    private static void RemoveSelected(Dictionary<string, string?> data, string removalName, string collection)
    {
        var prefix = $"ReverseProxy:{removalName}:";
        foreach (var id in data.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray())
            RemoveSubtree(data, $"ReverseProxy:{collection}:{id}");
    }

    private static void RemoveSubtree(Dictionary<string, string?> data, string prefix)
    {
        foreach (var key in data.Keys.Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)).ToArray()) data.Remove(key);
    }

    private static Dictionary<string, string?> Defaults(WebGatewayOptions options)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Route(string id, string path, bool apiPrefix = false, params string[] methods)
        {
            var key = $"ReverseProxy:Routes:{id}";
            data[$"{key}:ClusterId"] = "apiCluster";
            data[$"{key}:Match:Path"] = path;
            for (var index = 0; index < methods.Length; index++) data[$"{key}:Match:Methods:{index}"] = methods[index];
            if (apiPrefix)
            {
                data[$"{key}:Transforms:0:RequestHeader"] = "X-Forwarded-Prefix";
                data[$"{key}:Transforms:0:Set"] = "/api";
                // YARP's implicit prefix forwarder otherwise removes the configured
                // prefix when Web is hosted at /. Keep its other trusted forwarders.
                data[$"{key}:Transforms:1:X-Forwarded"] = "Set";
                data[$"{key}:Transforms:1:Prefix"] = "Off";
            }
        }
        Route("api-openapi-route", "/api/openapi/{**catch-all}");
        data["ReverseProxy:Routes:api-openapi-route:Transforms:0:PathRemovePrefix"] = "/api";
        data["ReverseProxy:Routes:api-openapi-route:Transforms:1:RequestHeader"] = "X-Forwarded-Prefix";
        data["ReverseProxy:Routes:api-openapi-route:Transforms:1:Set"] = "/api";
        data["ReverseProxy:Routes:api-openapi-route:Transforms:2:X-Forwarded"] = "Set";
        data["ReverseProxy:Routes:api-openapi-route:Transforms:2:Prefix"] = "Off";
        Route("api-health-live-route", "/health/live", false, "GET");
        Route("api-health-ready-route", "/health/ready", false, "GET");
        Route("api-v1-route", "/api/v1/{**catch-all}", true);
        Route("public-client-install-route", "/clients/install/{**catch-all}");
        Route("agent-telemetry-route", "/api/v2/agents/{**catch-all}", true);
        Route("agent-updates-route", "/api/v2/agent-updates/{**catch-all}", true);
        Route("bootstrap-setup-route", "/api/v2/setup/{**catch-all}", true);
        Route("deployment-branding-route", "/api/v2/branding/{**catch-all}", true);
        Route("local-auth-route", "/api/v2/local-auth/{**catch-all}", true);
        Route("service-oauth-metadata-route", "/.well-known/oauth-authorization-server");
        Route("service-jwks-route", "/.well-known/service-jwks.json");
        Route("service-token-route", "/connect/token");
        Route("pairing-metadata-route", "/api/pairing/v1/metadata", false, "GET");
        data["ReverseProxy:Routes:pairing-metadata-route:Transforms:0:RequestHeadersAllowed"] = "X-Pairing-Nonce";
        Route("orchestration-health-route", "/internal/health");
        Route("orchestration-jobs-route", "/internal/catalog/jobs");
        Route("orchestration-tenants-route", "/internal/catalog/tenants");
        Route("orchestration-request-definitions-route", "/internal/catalog/request-definitions");
        Route("orchestration-ingest-route", "/internal/ingest");
        Route("integrationPairingCapabilitiesRoute", "/api/integration-pairing/v1/capabilities", false, "GET");
        Route("httpHostSetupCapabilitiesRoute", "/api/http-host-setup/v1/capabilities", false, "GET");
        foreach (var operation in new[] { "start", "poll", "cancel" })
            Route($"httpHostSetup{operation}Route", $"/api/http-host-setup/v1/{operation}", false, "POST");
        foreach (var operation in new[] { "start", "poll", "cancel", "acknowledge" })
            Route($"integrationPairing{operation}Route", $"/api/integration-pairing/v1/{operation}", false, "POST");
        Route("integrationCredentialsRoute", "/api/v2/account/integration-credentials/{**catch-all}", true);
        foreach (var operation in new[] { "authenticate", "exchange" })
            Route($"mcpLocalDelegation{operation}Route", $"/api/v2/mcp/local-delegation/{operation}", false, "POST");
        Route("mcpReviewedAccessStartRoute", "/api/v2/mcp/local-delegation/access/start", false, "POST");
        // Account-backed administration used by the same-origin Scalar client.
        // Keep these explicit: the rest of /api/v2 remains private by default.
        Route("mcpPolicyAdministrationRoute", "/api/v2/mcp-operator/policies/{**catch-all}", true, "GET", "POST", "PUT");
        Route("mcpTargetAdministrationRoute", "/api/v2/mcp-operator/targets/{tenantId:int}/{agentId:guid}", true, "GET", "PUT");
        Route("mcpPolicyAuditsRoute", "/api/v2/mcp-operator/audits", true, "GET");
        Route("mcpAcceptedAuditsRoute", "/api/v2/mcp-operator/accepted-audits", true, "GET");
        Route("mcpOperatorApiRoute", "/api/v2/mcp/operator/{**catch-all}", true);
        Route("mcpDevelopmentApiRoute", "/api/v2/development/mcp/{**catch-all}", true);
        Route("agentTasksRoute", "/api/v2/tasks/{**catch-all}", true);
        Route("clientPresenceRoute", "/api/v2/client-presence/{**catch-all}", true, "GET");
        Route("primaryClientBindingRoute", "/api/v2/tenants/{tenantId:int}/primary-client-bindings/agents/{agentId:guid}", true);
        data["ReverseProxy:Clusters:apiCluster:Destinations:api1:Address"] = options.ApiBaseUri.AbsoluteUri;
        if (options.Mcp.Enabled)
        {
            // MapMcp hosts exactly /mcp (with optional trailing slash), not arbitrary subpaths.
            data["ReverseProxy:Routes:mcpRoute:ClusterId"] = "mcpCluster";
            data["ReverseProxy:Routes:mcpRoute:Match:Path"] = "/mcp";
            data["ReverseProxy:Routes:mcpReviewedSetupRoute:ClusterId"] = "mcpCluster";
            data["ReverseProxy:Routes:mcpReviewedSetupRoute:Match:Path"] = "/mcp/setup/access/start";
            data["ReverseProxy:Routes:mcpReviewedSetupRoute:Match:Methods:0"] = "POST";
            data["ReverseProxy:Clusters:mcpCluster:Destinations:mcp1:Address"] = options.Mcp.BackendUri!.AbsoluteUri;
            data["ReverseProxy:Clusters:mcpCluster:HttpRequest:AllowResponseBuffering"] = "false";
            data["ReverseProxy:Clusters:mcpCluster:HttpRequest:ActivityTimeout"] = options.Mcp.ActivityTimeout.ToString("c", CultureInfo.InvariantCulture);
            if (options.Mcp.AuthenticationMode == "Oidc")
            {
                data["ReverseProxy:Routes:mcpMetadataRoute:ClusterId"] = "mcpCluster";
                data["ReverseProxy:Routes:mcpMetadataRoute:Match:Path"] = "/.well-known/oauth-protected-resource/mcp";
                data["ReverseProxy:Routes:mcpMetadataRoute:Match:Methods:0"] = "GET";
            }
        }
        return data;
    }
}
