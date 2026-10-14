using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.Web.Configuration;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class WebGatewayConfigurationTests
{
    [Fact]
    public void Shipped_defaults_resolve_one_API_destination_and_keep_selective_routes()
    {
        var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(WebRoot, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "https://api.example.invalid/" }).Build();
        var options = WebGatewayOptions.Resolve(source);
        using var effective = WebGatewayConfiguration.Create(source, options);
        effective["ReverseProxy:Clusters:apiCluster:Destinations:api1:Address"].Should().Be(options.ApiBaseUri.AbsoluteUri);
        effective["ReverseProxy:Routes:api-v1-route:Match:Path"].Should().Be("/api/v1/{**catch-all}");
        effective["ReverseProxy:Routes:api-openapi-route:Transforms:0:PathRemovePrefix"].Should().Be("/api");
        effective["ReverseProxy:Routes:public-client-install-route:Match:Path"].Should().Be("/clients/install/{**catch-all}");
        effective["ReverseProxy:Routes:agent-updates-route:Match:Path"].Should().Be("/api/v2/agent-updates/{**catch-all}");
        effective.GetSection("ReverseProxy:Routes").GetChildren().Should().NotContain(route =>
            route["Match:Path"] == "/api/{**catch-all}" || route["Match:Path"] == "/api/v2/{**catch-all}");
        effective.GetSection("ReverseProxy:Routes:mcpRoute").Exists().Should().BeFalse();
    }

    [Fact]
    public void Operator_layers_replace_destinations_and_lists_preserving_other_fields_and_custom_routes()
    {
        var source = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiBaseUrl"] = "https://clients.example.invalid/",
                ["ReverseProxy:Clusters:apiCluster:Destinations:old:Address"] = "https://old.example.invalid/",
                ["ReverseProxy:Routes:custom:ClusterId"] = "apiCluster",
                ["ReverseProxy:Routes:custom:Match:Path"] = "/custom",
                ["ReverseProxy:Routes:custom:Transforms:0:RequestHeader"] = "X-Custom",
                ["ReverseProxy:Routes:custom:Transforms:0:Set"] = "kept",
                ["ReverseProxy:Routes:custom:Transforms:1:PathPrefix"] = "/old"
            }).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:apiCluster:Destinations:primary:Address"] = "https://proxy.example.invalid/",
                ["ReverseProxy:Routes:custom:Transforms:0:PathPrefix"] = "/new",
                ["ReverseProxy:RemovedRoutes:0"] = "orchestration-ingest-route"
            }).Build();
        var options = WebGatewayOptions.Resolve(source);
        using var effective = WebGatewayConfiguration.Create(source, options);
        options.ApiBaseUri.AbsoluteUri.Should().Be("https://clients.example.invalid/");
        effective.GetSection("ReverseProxy:Clusters:apiCluster:Destinations").GetChildren().Select(x => x.Key)
            .Should().Equal("primary");
        effective["ReverseProxy:Routes:custom:Match:Path"].Should().Be("/custom");
        effective["ReverseProxy:Routes:custom:Transforms:0:PathPrefix"].Should().Be("/new");
        effective.GetSection("ReverseProxy:Routes:custom:Transforms").GetChildren().Should().HaveCount(1);
        effective["ReverseProxy:Routes:custom:Transforms:0:RequestHeader"].Should().BeNull();
        effective.GetSection("ReverseProxy:Routes:orchestration-ingest-route").Exists().Should().BeFalse();
    }

    [Fact]
    public void Disabled_MCP_preserves_an_explicit_operator_route_and_invalid_file_reload_keeps_destination()
    {
        var source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:mcpRoute:ClusterId"] = "privateMcp",
            ["ReverseProxy:Routes:mcpRoute:Match:Path"] = "/mcp",
            ["ReverseProxy:Clusters:privateMcp:Destinations:one:Address"] = "https://mcp.example.invalid/"
        }).Build();
        Exception? rejected = null;
        using var effective = WebGatewayConfiguration.Create(source, WebGatewayOptions.Resolve(source), exception => rejected = exception);
        effective["ReverseProxy:Routes:mcpRoute:ClusterId"].Should().Be("privateMcp");
        source["ReverseProxy:Clusters:privateMcp:Destinations:one:Address"] = "invalid";
        source.Reload();
        rejected.Should().BeOfType<InvalidOperationException>();
        effective["ReverseProxy:Clusters:privateMcp:Destinations:one:Address"].Should().Be("https://mcp.example.invalid/");
        source["ReverseProxy:Clusters:privateMcp:Destinations:one:Address"] = "https://replacement.example.invalid/";
        source.Reload();
        effective["ReverseProxy:Clusters:privateMcp:Destinations:one:Address"].Should().Be("https://replacement.example.invalid/");
    }

    [Theory]
    [InlineData("LocalCredential", false)]
    [InlineData("Oidc", true)]
    public void MCP_metadata_is_exact_and_OIDC_only(string mode, bool metadataExpected)
    {
        var source = McpConfiguration("http://mcp-http:9224/", mode);
        using var effective = WebGatewayConfiguration.Create(source, WebGatewayOptions.Resolve(source));
        effective["ReverseProxy:Routes:mcpRoute:Match:Path"].Should().Be("/mcp");
        effective.GetSection("ReverseProxy:Routes:mcpMetadataRoute").Exists().Should().Be(metadataExpected);
        if (metadataExpected)
            effective["ReverseProxy:Routes:mcpMetadataRoute:Match:Path"].Should().Be("/.well-known/oauth-protected-resource/mcp");
        effective["ReverseProxy:Routes:service-oauth-metadata-route:ClusterId"].Should().Be("apiCluster");
        effective["ReverseProxy:Clusters:mcpCluster:HttpRequest:AllowResponseBuffering"].Should().Be("false");
        effective["ReverseProxy:Clusters:mcpCluster:HttpRequest:ActivityTimeout"].Should().Be("00:05:00");
    }

    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact(Skip = "The shipped Docker entrypoint requires POSIX sh.", SkipUnless = nameof(IsPosix))]
    public async Task Shell_entrypoint_preserves_new_environment_override_IDs()
    {
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(RepositoryRoot, "docker/web/docker-entrypoint.sh"));
        start.ArgumentList.Add("/bin/sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf '%s\\n' \"$ReverseProxy__Clusters__mcpCluster__Destinations__mcp1__Address\" \"$ReverseProxy__Routes__mcpRoute__Match__Path\" \"$ASPNETCORE_HTTP_PORTS\"");
        start.Environment["NetRatel_HTTP_PORT"] = "9111";
        start.Environment["ReverseProxy__Clusters__mcpCluster__Destinations__mcp1__Address"] = "http://custom-mcp:9555/";
        start.Environment["ReverseProxy__Routes__mcpRoute__Match__Path"] = "/mcp";
        using var process = Process.Start(start)!;
        var lines = (await process.StandardOutput.ReadToEndAsync()).Trim().Split('\n');
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
        lines.Should().Equal("http://custom-mcp:9555/", "/mcp", "9111");
        // Bind the values that survived the real entrypoint with the .NET environment
        // provider. A unique prefix keeps process-global test variables isolated.
        var prefix = "NETRATEL_GATEWAY_TEST_" + Guid.NewGuid().ToString("N") + "_";
        var addressKey = prefix + "ReverseProxy__Clusters__mcpCluster__Destinations__mcp1__Address";
        var routeKey = prefix + "ReverseProxy__Routes__mcpRoute__Match__Path";
        try
        {
            Environment.SetEnvironmentVariable(addressKey, lines[0]);
            Environment.SetEnvironmentVariable(routeKey, lines[1]);
            var source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebGateway:Mcp:Enabled"] = "true",
                ["WebGateway:Mcp:BackendUrl"] = "http://mcp-http:9224/"
            }).AddEnvironmentVariables(prefix).Build();
            using var effective = WebGatewayConfiguration.Create(source, WebGatewayOptions.Resolve(source));
            effective["ReverseProxy:Clusters:mcpCluster:Destinations:mcp1:Address"].Should().Be(lines[0]);
            effective["ReverseProxy:Routes:mcpRoute:Match:Path"].Should().Be(lines[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(addressKey, null);
            Environment.SetEnvironmentVariable(routeKey, null);
        }
    }

    [Fact]
    public async Task Proxy_streams_incrementally_preserves_bearer_and_propagates_cancellation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? authorization = null;
        await using var backend = await Backend(async context =>
        {
            authorization = context.Request.Headers.Authorization.ToString();
            context.Response.ContentType = "text/event-stream";
            context.RequestAborted.Register(() => aborted.TrySetResult());
            await context.Response.WriteAsync("data: first\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: second\n\n", context.RequestAborted);
        });
        await using var proxy = await Proxy(McpConfiguration(Address(backend)));
        using var client = proxy.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new("Bearer", "explicit-integration-credential");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);
        (await reader.ReadLineAsync(cancellation.Token)).Should().Be("data: first");
        release.Task.IsCompleted.Should().BeFalse("the first event must arrive before backend completion");
        authorization.Should().Be("Bearer explicit-integration-credential");
        cancellation.Cancel();
        response.Dispose();
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Public_routes_reach_API_preserve_transforms_and_unknown_paths_never_return_HTML()
    {
        await using var backend = await Backend(context => context.Response.WriteAsync(
            context.Request.Path + "|" + context.Request.Headers["X-Forwarded-Prefix"]));
        var source = McpConfiguration(Address(backend), "Oidc");
        source["ApiBaseUrl"] = Address(backend);
        await using var proxy = await Proxy(source);
        using var client = proxy.GetTestClient();
        foreach (var path in new[] { "/mcp", "/mcp/", "/api/integration-pairing/v1/capabilities", "/api/v1/tenants", "/api/v1/auth/ai-agent/status",
                     "/api/v2/account/integration-credentials/pairing/lookup", "/api/v2/mcp/operator/auth/status",
                     "/api/v2/agent-updates/check", "/clients/install/capability",
                     "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-authorization-server" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await response.Content.ReadAsStringAsync()).Should().StartWith(path + "|");
        }
        foreach (var path in new[] { "/api/integration-pairing/v1/start", "/api/integration-pairing/v1/poll",
                     "/api/integration-pairing/v1/cancel", "/api/integration-pairing/v1/acknowledge",
                     "/api/v2/mcp/local-delegation/authenticate", "/api/v2/mcp/local-delegation/exchange" })
        {
            using var response = await client.PostAsync(path, new StringContent("{}"));
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await response.Content.ReadAsStringAsync()).Should().StartWith(path + "|");
        }
        (await client.GetStringAsync("/api/openapi/v1.json")).Should().Be("/openapi/v1.json|/api");
        (await client.GetStringAsync("/api/docs")).Should().Be("local docs");
        foreach (var path in new[] { "/api/v2/unlisted", "/api/unlisted", "/mcp/unrelated",
                     "/.well-known/oauth-protected-resource/mcp/unrelated" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("Blazor");
        }
    }

    [Fact]
    public async Task Selected_unavailable_MCP_returns_502_and_disabled_MCP_returns_404()
    {
        var backend = await Backend(context => context.Response.WriteAsync("ready"));
        var address = Address(backend);
        await backend.StopAsync();
        await backend.DisposeAsync();
        await using var selected = await Proxy(McpConfiguration(address));
        using var selectedClient = selected.GetTestClient();
        using var unavailable = await selectedClient.PostAsync("/mcp", new StringContent("{}"));
        unavailable.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await unavailable.Content.ReadAsStringAsync()).Should().NotContain("Blazor");
        await using var disabled = await Proxy(new ConfigurationBuilder().Build());
        using var disabledClient = disabled.GetTestClient();
        using var absent = await disabledClient.PostAsync("/mcp", new StringContent("{}"));
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var metadata = await disabledClient.GetAsync("/.well-known/oauth-protected-resource/mcp");
        metadata.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static IConfigurationRoot McpConfiguration(string address, string mode = "LocalCredential") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebGateway:Mcp:Enabled"] = "true",
            ["WebGateway:Mcp:BackendUrl"] = address,
            ["WebGateway:Mcp:AuthenticationMode"] = mode
        }).Build();

    private static async Task<WebApplication> Backend(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static async Task<WebApplication> Proxy(IConfiguration source)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var config = WebGatewayConfiguration.Create(source, WebGatewayOptions.Resolve(source));
        builder.Services.AddReverseProxy().LoadFromConfig(config.GetSection("ReverseProxy"));
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(config.Dispose);
        app.UseRouting();
        app.UseApiRouteGuard();
        app.MapGet("/api/docs", () => "local docs");
        app.MapReverseProxy();
        app.MapFallback(() => "Blazor fallback HTML");
        await app.StartAsync();
        return app;
    }

    private static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web"));
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(WebRoot, "../../.."));
}
