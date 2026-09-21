using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.AgentClient;
using NetRatel.API.Middleware;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Http;
using NetRatel.Shared.Operations;
using System.Net;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class NetRatelMcpHttpTests
{
    private static readonly SymmetricSecurityKey TestSigningKey = new(Encoding.UTF8.GetBytes("netratel-mcp-test-signing-key-for-http-protocol-discovery"));

    [Fact]
    public void Delegation_assertion_preserves_bounded_caller_identity_without_an_oauth_bearer()
    {
        var options = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes")),
            LifetimeSeconds = 90
        };
        var tokens = new McpOperatorDelegationTokenService(options);
        var agentId = Guid.Parse("f3beb72c-615c-457f-8047-11d4749fa845");
        var assertion = tokens.Create(
            new McpOperatorDelegationIdentity(
                "operator-123",
                "oauth-client",
                "oauth-client",
                ["netratel-operators"],
                ["McpOperator"],
                ["mcp:read", "mcp:operate"]),
            new McpOperatorDelegationRequest(
                "netratel_files",
                "collect",
                "request-123",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "trace-123"));

        assertion.Should().NotContain("Bearer");
        tokens.TryValidate(assertion, out var delegation).Should().BeTrue();
        delegation!.Identity.Subject.Should().Be("operator-123");
        delegation.Identity.Scopes.Should().BeEquivalentTo("mcp:operate", "mcp:read");
        delegation.ServicePrincipal.Should().Be("netratel-mcp-http-dev");
        delegation.Tool.Should().Be("netratel_files");
        delegation.Operation.Should().Be("collect");
        delegation.RequestId.Should().Be("request-123");
        delegation.Resource.Should().Be("https://mcp.dev.example/mcp");
        delegation.Instance.Should().Be("dev");
        delegation.TenantId.Should().Be(42);
        delegation.AgentId.Should().Be(agentId);
        delegation.CorrelationId.Should().Be("trace-123");
    }

    [Fact]
    public void Delegation_propagation_binds_policy_disable_to_the_explicit_verified_target_selector()
    {
        var options = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var tokens = new McpOperatorDelegationTokenService(options);
        var context = new McpOperatorDelegationContext();
        var propagation = new McpOperatorDelegationPropagation(options, tokens, context, HostContext("dev"), ValidOptions());
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "policy.admin@example.test"),
            new Claim("scope", NetRatelMcpHttpOptions.DefaultAdminScope),
            new Claim("roles", "PolicyAdministrator")
        ], "test"));
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["operation"] = JsonSerializer.SerializeToElement("preview_disable"),
            ["request"] = JsonSerializer.SerializeToElement(new
            {
                policyId = "68310e19-5f49-4e63-a73d-342e98940c26",
                expectedVersion = 1,
                target = new { kind = 2, tenantId = 42 }
            })
        };

        using (propagation.Begin(caller, "netratel_policy", arguments))
        {
            context.CurrentAssertion.Should().NotBeNullOrWhiteSpace();
            tokens.TryValidate(context.CurrentAssertion!, out var delegation).Should().BeTrue();
            delegation!.Tool.Should().Be("netratel_policy");
            delegation.Operation.Should().Be("preview_disable");
            delegation.TenantId.Should().Be(42);
            delegation.AgentId.Should().BeNull();
        }
    }

    [Fact]
    public void Delegation_propagation_keeps_production_tenant_administration_on_the_control_plane()
    {
        var options = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var tokens = new McpOperatorDelegationTokenService(options);
        var context = new McpOperatorDelegationContext();
        var propagation = new McpOperatorDelegationPropagation(options, tokens, context, HostContext("prod"), ValidOptions());
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "tenant.admin@example.test"),
            new Claim("tenant_id", "42"),
            new Claim("scope", NetRatelMcpHttpOptions.DefaultAdminScope),
            new Claim("roles", "Administrator")
        ], "test"));
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["operation"] = JsonSerializer.SerializeToElement("update"),
            ["request"] = JsonSerializer.SerializeToElement(new { tenantId = 42, expectedVersion = 3 })
        };

        using (propagation.Begin(caller, "netratel_tenants", arguments))
        {
            tokens.TryValidate(context.CurrentAssertion!, out var delegation).Should().BeTrue();
            delegation!.Tool.Should().Be("netratel_tenants");
            delegation.Operation.Should().Be("update");
            delegation.TenantId.Should().BeNull();
            delegation.AgentId.Should().BeNull();
            delegation.Identity.ActiveTenantId.Should().Be(42);
        }
    }

    [Fact]
    public void Delegation_assertion_rejects_tampering_and_is_removed_when_its_context_scope_ends()
    {
        var options = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var tokens = new McpOperatorDelegationTokenService(options);
        var assertion = tokens.Create(new McpOperatorDelegationIdentity("operator-123", null, null, [], [], ["mcp:read"]), "netratel_health", "get", "request-123");
        var context = new McpOperatorDelegationContext();

        using (context.Begin(assertion))
        {
            context.CurrentAssertion.Should().Be(assertion);
            tokens.TryValidate(assertion + "x", out _).Should().BeFalse();
        }

        context.CurrentAssertion.Should().BeNull();
    }

    [Fact]
    public async Task Api_delegation_middleware_exposes_only_a_valid_signed_assertion()
    {
        var options = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var tokens = new McpOperatorDelegationTokenService(options);
        var assertion = tokens.Create(new McpOperatorDelegationIdentity("operator-123", null, null, [], [], ["mcp:read"]), "netratel_health", "get", "request-123");
        var downstreamCalled = false;
        var middleware = new McpOperatorDelegationMiddleware(
            _ =>
            {
                downstreamCalled = true;
                return Task.CompletedTask;
            },
            options,
            tokens);
        var valid = new DefaultHttpContext();
        valid.Request.Headers[McpOperatorDelegationOptions.HeaderName] = assertion;

        await middleware.InvokeAsync(valid);

        downstreamCalled.Should().BeTrue();
        valid.TryGetMcpOperatorDelegation(out var delegation).Should().BeTrue();
        delegation!.Identity.Subject.Should().Be("operator-123");

        downstreamCalled = false;
        var tampered = new DefaultHttpContext();
        tampered.Request.Headers[McpOperatorDelegationOptions.HeaderName] = assertion + "x";

        await middleware.InvokeAsync(tampered);

        downstreamCalled.Should().BeFalse();
        tampered.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        tampered.TryGetMcpOperatorDelegation(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Health_and_protected_resource_metadata_are_anonymous()
    {
        await using var host = await CreateApplicationAsync();
        var client = host.Application.GetTestClient();

        var health = await client.GetAsync("/health/live");
        var metadata = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");

        health.StatusCode.Should().Be(HttpStatusCode.OK);
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await metadata.Content.ReadAsStringAsync();
        payload.Should().Contain("https://mcp.dev.example/mcp");
        payload.Should().Contain(NetRatelMcpHttpOptions.DefaultReadScope)
            .And.Contain(NetRatelMcpHttpOptions.DefaultObserveScope)
            .And.Contain(NetRatelMcpHttpOptions.DefaultFilesScope)
            .And.Contain(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope)
            .And.Contain(NetRatelMcpHttpOptions.DefaultExecuteScope)
            .And.Contain(NetRatelMcpHttpOptions.DefaultDevelopmentOnboardingScope)
            .And.Contain(NetRatelMcpHttpOptions.DefaultAdminScope)
            .And.Contain(NetRatelMcpHttpOptions.OfflineAccessScope)
            .And.Contain("\"openid\"")
            .And.Contain("\"profile\"");
    }

    [Fact]
    public async Task Local_credential_mode_starts_without_oidc_configuration_and_does_not_publish_oauth_metadata()
    {
        await using var host = await CreateApplicationAsync(localCredentialMode: true);
        var client = host.Application.GetTestClient();

        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Local_execution_assertion_keeps_only_non_secret_credential_identity_and_permission()
    {
        var options = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var tokens = new McpOperatorDelegationTokenService(options);
        var assertion = tokens.Create(
            new McpOperatorDelegationIdentity("local-owner", "credential-public-id", "netratel-local-http-mcp", [], ["Operator"], ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest("netratel_clients", "presence", "request-123", "https://mcp.dev.example/mcp", "dev", 42, Guid.NewGuid())
            {
                IngressCredentialId = "credential-internal-id",
                IngressPermission = "telemetry.read"
            });

        assertion.Should().NotContain("nrt_ic_");
        tokens.TryValidate(assertion, out var delegation).Should().BeTrue();
        delegation!.IngressCredentialId.Should().Be("credential-internal-id");
        delegation.IngressPermission.Should().Be("telemetry.read");
    }

    [Fact]
    public async Task Local_exchange_requires_a_paired_resource_and_returns_only_a_short_lived_execution_assertion()
    {
        var options = DelegationOptions();
        var tokens = new McpOperatorDelegationTokenService(options);
        var agentId = Guid.NewGuid();
        var pairing = tokens.Create(
            new McpOperatorDelegationIdentity("netratel-local-gateway", "netratel-local-gateway", null, [], [], []),
            new McpOperatorDelegationRequest("netratel_clients", "presence", "pairing-request", "https://mcp.dev.example/mcp", "dev", 42, agentId));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(IntegrationCredentialAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalExchangeAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("McpLocalDelegationExchange", policy =>
        {
            policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User.HasClaim("integration_credential_purpose", "http_mcp"));
        }));
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton<IEffectiveAccessService, AllowingEffectiveAccessService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcpLocalDelegationEndpoints();
        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, McpLocalDelegationEndpoints.ExchangePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nrt_ic_not-forwarded-to-business-api");
            request.Headers.Add(McpLocalDelegationEndpoints.PairingHeaderName, pairing);

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var assertion = payload.RootElement.GetProperty("assertion").GetString();
            assertion.Should().NotContain("nrt_ic_");
            tokens.TryValidate(assertion, out var execution).Should().BeTrue();
            execution!.Identity.Subject.Should().Be("local-owner");
            execution.IngressCredentialId.Should().Be("credential-id");
            execution.IngressPermission.Should().Be(NetRatelPermissions.TelemetryRead);
            execution.Resource.Should().Be("https://mcp.dev.example/mcp");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Local_exchange_allows_only_the_catalogued_instance_discovery_operations_without_an_invented_agent()
    {
        var options = DelegationOptions();
        var tokens = new McpOperatorDelegationTokenService(options);
        var pairing = tokens.Create(
            new McpOperatorDelegationIdentity("netratel-local-gateway", "netratel-local-gateway", null, [], [], []),
            new McpOperatorDelegationRequest("netratel_capabilities", "get", "pairing-request", "https://mcp.dev.example/mcp", "dev", null, null));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(IntegrationCredentialAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LocalExchangeAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("McpLocalDelegationExchange", policy =>
        {
            policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User.HasClaim("integration_credential_purpose", "http_mcp"));
        }));
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton<IEffectiveAccessService, AllowingEffectiveAccessService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcpLocalDelegationEndpoints();
        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, McpLocalDelegationEndpoints.ExchangePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nrt_ic_not-forwarded-to-business-api");
            request.Headers.Add(McpLocalDelegationEndpoints.PairingHeaderName, pairing);

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var assertion = (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement.GetProperty("assertion").GetString();
            tokens.TryValidate(assertion, out var execution).Should().BeTrue();
            execution!.TenantId.Should().BeNull();
            execution.AgentId.Should().BeNull();
            execution.IngressPermission.Should().Be(NetRatelPermissions.McpDiscoveryRead);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Mcp_route_rejects_an_unauthenticated_request_with_resource_metadata()
    {
        await using var host = await CreateApplicationAsync();
        var client = host.Application.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" })
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle(value => value.Contains("resource_metadata=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authenticated_http_protocol_publishes_the_shared_closed_catalog_resources_and_prompts()
    {
        await using var host = await CreateApplicationAsync();
        var client = host.Application.GetTestClient();

        using var initialize = await client.SendAsync(McpRequest(1, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "netratel-http-test", version = "1.0" }
        }));
        initialize.StatusCode.Should().Be(HttpStatusCode.OK);

        using var tools = await client.SendAsync(McpRequest(2, "tools/list", new { }));
        tools.StatusCode.Should().Be(HttpStatusCode.OK);
        using var toolsPayload = await ReadMcpPayloadAsync(tools);
        var publishedTools = toolsPayload.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        var expectedTools = NetRatelMcpToolDefinitions.CreateForHttp()
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        publishedTools.Select(tool => tool.GetProperty("name").GetString()).Should().BeEquivalentTo(expectedTools.Keys);
        foreach (var published in publishedTools)
        {
            var expected = expectedTools[published.GetProperty("name").GetString()!].ProtocolTool;
            published.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            published.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            published.GetProperty("annotations").ValueKind.Should().Be(JsonValueKind.Object);
            published.GetProperty("inputSchema").GetRawText().Should().Be(expected.InputSchema.GetRawText());
            published.GetProperty("outputSchema").GetRawText().Should().Be(expected.OutputSchema!.Value.GetRawText());
        }

        using var resources = await client.SendAsync(McpRequest(3, "resources/list", new { }));
        resources.StatusCode.Should().Be(HttpStatusCode.OK);
        using var resourcesPayload = await ReadMcpPayloadAsync(resources);
        var publishedResources = resourcesPayload.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray().ToArray();
        publishedResources.Select(resource => resource.GetProperty("uri").GetString())
            .Should().BeEquivalentTo(NetRatelMcpCatalog.Resources);
        foreach (var resource in publishedResources)
        {
            using var read = await client.SendAsync(McpRequest(4, "resources/read", new { uri = resource.GetProperty("uri").GetString() }));
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            using var readPayload = await ReadMcpPayloadAsync(read);
            readPayload.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray().Should().NotBeEmpty();
        }

        using var prompts = await client.SendAsync(McpRequest(5, "prompts/list", new { }));
        prompts.StatusCode.Should().Be(HttpStatusCode.OK);
        using var promptsPayload = await ReadMcpPayloadAsync(prompts);
        promptsPayload.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray()
            .Select(prompt => prompt.GetProperty("name").GetString())
            .Should().BeEquivalentTo(NetRatelMcpCatalog.Prompts);
        using var prompt = await client.SendAsync(McpRequest(6, "prompts/get", new { name = "controlled_remote_execution", arguments = new { } }));
        prompt.StatusCode.Should().Be(HttpStatusCode.OK);
        using var promptPayload = await ReadMcpPayloadAsync(prompt);
        promptPayload.RootElement.GetProperty("result").GetProperty("messages").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Authenticated_read_tool_remains_available_without_the_development_operation_scope()
    {
        await using var host = await CreateApplicationAsync();
        var client = host.Application.GetTestClient();

        using var response = await client.SendAsync(McpRequest(7, "tools/call", new
        {
            name = "netratel_capabilities",
            arguments = new { operation = "get" }
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = await ReadMcpPayloadAsync(response);
        var result = payload.RootElement.GetProperty("result");
        (!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean()).Should().BeTrue();
    }

    [Fact]
    public async Task Http_mcp_outbound_client_uses_its_explicit_budget_without_the_default_resilience_handler()
    {
        await using var host = await CreateApplicationAsync();
        var factory = host.Application.Services.GetRequiredService<IHttpClientFactory>();
        var handlers = HandlerTypeNames(host.Application.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(NetRatelMcpOutboundClient.ApiHttpClientName));

        factory.CreateClient(NetRatelMcpOutboundClient.ApiHttpClientName).Timeout.Should().Be(TimeSpan.FromSeconds(60));
        handlers.Should().NotContain(name => name.Contains("ResilienceHandler", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> HandlerTypeNames(HttpMessageHandler root)
    {
        var names = new List<string>();
        for (var current = root; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            names.Add(current.GetType().Name);
        }

        return names;
    }

    [Fact]
    public async Task Mcp_route_rejects_an_unapproved_browser_origin_before_authentication()
    {
        await using var host = await CreateApplicationAsync();
        var client = host.Application.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Add("Origin", "https://unapproved.example");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void Configuration_rejects_an_audience_other_than_the_public_mcp_resource()
    {
        var options = ValidOptions(audience: "https://different.dev.example/mcp");

        var action = () => NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(options);

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*Audience must equal PublicResourceUri*");
    }

    [Fact]
    public void Configuration_allows_an_http_authority_only_for_an_explicit_disposable_metadata_override()
    {
        var options = ValidOptions();
        options.Authority = "http://oidc.compose.test/default";
        options.RequireHttpsMetadata = false;

        var action = () => NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(options);

        action.Should().NotThrow();
    }

    [Fact]
    public void Production_host_rejects_the_disposable_http_metadata_override()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatel:Mcp:Http:Instance"] = "dev",
            ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://api.dev.example",
            ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.dev.example/mcp",
            ["NetRatel:Mcp:Http:Authority"] = "http://oidc.compose.test/default",
            ["NetRatel:Mcp:Http:RequireHttpsMetadata"] = "false",
            ["NetRatel:Mcp:Http:Audience"] = "https://mcp.dev.example/mcp",
            ["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read",
            ["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators"
        });

        var action = () => NetRatelMcpHttpApplication.ConfigureServices(builder);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*RequireHttpsMetadata=false is allowed only in the Development environment*");
    }

    [Fact]
    public void Configuration_rejects_a_request_body_limit_above_the_safe_ceiling()
    {
        var options = ValidOptions(1_048_577);

        var action = () => NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(options);

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*MaxRequestBodyBytes must be between 1024 and 1048576 bytes*");
    }

    [Fact]
    public void Configuration_rejects_an_invalid_development_write_scope()
    {
        var options = ValidOptions();
        options.DevelopmentWriteScope = "not a scope";

        var action = () => NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(options);

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*DevelopmentWriteScope must be a non-empty OAuth scope token*");
    }

    [Fact]
    public void Configuration_rejects_an_invalid_catalogued_observe_scope()
    {
        var options = ValidOptions();
        options.ObserveScope = "not a scope";

        var action = () => NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(options);

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*ObserveScope must be a non-empty OAuth scope token*");
    }

    [Fact]
    public void Configuration_binds_the_explicit_deployment_environment_variables_over_the_section_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Mcp:Http:Instance"] = "prod",
                ["NetRatel:Mcp:Http:ConfigurationPath"] = "/section/agent.json",
                ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://section-dev.example",
                ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://section-prod.example",
                ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.dev.example/mcp",
                ["NetRatel:Mcp:Http:Authority"] = "https://auth.dev.example",
                ["NetRatel:Mcp:Http:Audience"] = "https://mcp.dev.example/mcp",
                ["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read",
                ["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators",
                [NetRatelMcpHttpOptions.InstanceEnvironmentVariable] = "dev",
                [NetRatelMcpHttpOptions.ConfigurationPathEnvironmentVariable] = "/run/netratel-mcp/agent.json",
                [NetRatelMcpHttpOptions.DevApiBaseUrlEnvironmentVariable] = "https://netratel-dev-api.example",
                [NetRatelMcpHttpOptions.ProdApiBaseUrlEnvironmentVariable] = "https://netratel-prod-api.example"
            })
            .Build();

        var options = NetRatelMcpHttpOptions.FromConfiguration(configuration);

        options.Instance.Should().Be("dev");
        options.ConfigurationPath.Should().Be("/run/netratel-mcp/agent.json");
        options.DevApiBaseUrl.Should().Be("https://netratel-dev-api.example");
        options.ProdApiBaseUrl.Should().Be("https://netratel-prod-api.example");
        options.PublicResourceUri.Should().Be("https://mcp.dev.example/mcp");
        options.RequiredScopes.Should().ContainSingle().Which.Should().Be("mcp:read");
        options.RequiredGroups.Should().ContainSingle().Which.Should().Be("netratel-operators");
    }

    [Fact]
    public void Configuration_requires_only_the_selected_environment_allowlist_entry()
    {
        var options = ValidOptions();
        options.ProdApiBaseUrl = string.Empty;

        var action = () => NetRatelMcpHttpOptionsValidator.ThrowIfInvalid(options);

        action.Should().NotThrow();
    }

    [Fact]
    public void Production_host_rejects_disabled_delegation_before_it_registers_operator_routes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatel:Mcp:Http:Instance"] = "prod",
            ["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://api.prod.example",
            ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.prod.example/mcp",
            ["NetRatel:Mcp:Http:Authority"] = "https://auth.prod.example",
            ["NetRatel:Mcp:Http:Audience"] = "https://mcp.prod.example/mcp",
            ["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read",
            ["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators"
        });

        var action = () => NetRatelMcpHttpApplication.ConfigureServices(builder);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Production MCP host requires NetRatel:Mcp:Delegation:Enabled=true*");
    }

    [Fact]
    public void Development_host_rejects_operator_surface_without_signed_delegation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatel:Mcp:Http:Instance"] = "dev",
            ["NetRatel:Mcp:Http:OperatorSurfaceEnabled"] = "true",
            ["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://api.dev.example",
            ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.dev.example/mcp",
            ["NetRatel:Mcp:Http:Authority"] = "https://auth.dev.example",
            ["NetRatel:Mcp:Http:Audience"] = "https://mcp.dev.example/mcp",
            ["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read",
            ["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators"
        });

        var action = () => NetRatelMcpHttpApplication.ConfigureServices(builder);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*publishes V2 operator routes requires the same setting*");
    }

    [Fact]
    public async Task Host_starts_when_only_the_selected_environment_allowlist_entry_is_configured()
    {
        await using var host = await CreateApplicationAsync(includeProdApiBaseUrl: false);

        var health = await host.Application.GetTestClient().GetAsync("/health/live");

        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Host_starts_when_the_flat_deployment_variables_select_the_dev_target()
    {
        await using var host = await CreateApplicationAsync(includeProdApiBaseUrl: false, useDeploymentVariables: true);

        var health = await host.Application.GetTestClient().GetAsync("/health/live");

        health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Authorization_requires_every_configured_scope_and_group()
    {
        var requirement = new RequiredMcpClaimsRequirement(
            new HashSet<string>(["mcp:read", "mcp:operate"], StringComparer.Ordinal),
            new HashSet<string>(["netratel-operators"], StringComparer.Ordinal));
        var allowed = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope", "mcp:read mcp:operate"),
            new Claim("groups", "netratel-operators")
        ], "test"));
        var denied = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope", "mcp:read"),
            new Claim("groups", "netratel-operators")
        ], "test"));

        var allowedContext = new AuthorizationHandlerContext([requirement], allowed, null);
        var deniedContext = new AuthorizationHandlerContext([requirement], denied, null);
        var handler = new RequiredMcpClaimsHandler();

        await handler.HandleAsync(allowedContext);
        await handler.HandleAsync(deniedContext);

        allowedContext.HasSucceeded.Should().BeTrue();
        deniedContext.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public void Development_operation_scope_authorization_leaves_reads_unchanged_and_fails_closed_for_mutations()
    {
        var developmentAuthorization = new McpOperationScopeAuthorization(
            HostContext("dev"),
            ValidOptions());
        var readCaller = Principal("mcp:read");
        var operatorCaller = Principal($"mcp:read {NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope}");

        developmentAuthorization.Evaluate(readCaller, "netratel_health", Arguments("get")).Allowed.Should().BeTrue();
        developmentAuthorization.Evaluate(readCaller, "netratel_scripts", Arguments("create_marker"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_development_write_scope"));
        developmentAuthorization.Evaluate(operatorCaller, "netratel_scripts", Arguments("create_marker")).Allowed.Should().BeTrue();
        developmentAuthorization.Evaluate(operatorCaller, "netratel_scripts", Arguments("run")).Allowed.Should().BeTrue();
        developmentAuthorization.Evaluate(readCaller, "netratel_marker_jobs", Arguments("run"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_development_write_scope"));
        developmentAuthorization.Evaluate(operatorCaller, "netratel_marker_jobs", Arguments("run")).Allowed.Should().BeTrue();
        developmentAuthorization.Evaluate(readCaller, "netratel_onboarding", Arguments("collateral"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_development_onboarding_scope"));
        developmentAuthorization.Evaluate(operatorCaller, "netratel_onboarding", Arguments("create_enrollment"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_development_onboarding_scope"));
        developmentAuthorization.Evaluate(Principal($"mcp:read {NetRatelMcpHttpOptions.DefaultDevelopmentOnboardingScope}"), "netratel_onboarding", Arguments("create_enrollment")).Allowed.Should().BeTrue();
        developmentAuthorization.Evaluate(operatorCaller, "netratel_notifications", Arguments("mark_read"))
            .Should().Be(new McpOperationScopeDecision(false, "operation_not_available_over_http"));
        developmentAuthorization.Evaluate(readCaller, "netratel_policy", Arguments("preview_create"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        developmentAuthorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultAdminScope), "netratel_policy", Arguments("preview_create"))
            .Should().Be(McpOperationScopeDecision.Permit);
        developmentAuthorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultAdminScope), "netratel_policy", Arguments("confirm_disable"))
            .Should().Be(McpOperationScopeDecision.Permit);
    }

    [Fact]
    public void Development_operation_scope_authorization_denies_non_read_operations_outside_development()
    {
        var productionAuthorization = new McpOperationScopeAuthorization(
            HostContext("prod"),
            ValidOptions(instance: "prod"));
        var operatorCaller = Principal($"mcp:read {NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope}");

        productionAuthorization.Evaluate(operatorCaller, "netratel_scripts", Arguments("create_marker"))
            .Should().Be(new McpOperationScopeDecision(false, "non_read_operations_are_development_only"));
    }

    [Fact]
    public void Production_operation_scope_authorization_requires_the_catalogued_exact_scope()
    {
        var options = ValidOptions(instance: "prod");
        options.ObserveScope = "mcp:custom-observe";
        var authorization = new McpOperationScopeAuthorization(HostContext("prod"), options);

        authorization.Evaluate(Principal("mcp:read"), "netratel_clients", Arguments("presence"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_clients", Arguments("presence"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(Principal("mcp:custom-observe"), "netratel_clients", Arguments("presence"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal("mcp:custom-observe"), "netratel_terminal", Arguments("availability"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultAdminScope), "netratel_clients", Arguments("preview_disable"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultAdminScope), "netratel_clients", Arguments("enable"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultExecuteScope), "netratel_clients", Arguments("software_update"))
            .Should().Be(McpOperationScopeDecision.Permit);
    }

    [Fact]
    public void Development_operator_surface_exposes_v2_operations_and_requires_exact_catalogued_scopes()
    {
        var options = ValidOptions();
        options.OperatorSurfaceEnabled = true;
        var authorization = new McpOperationScopeAuthorization(HostContext("dev", operatorSurfaceEnabled: true), options);

        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_commands", Arguments("availability"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope), "netratel_commands", Arguments("availability"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultExecuteScope), "netratel_commands", Arguments("execute"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_events", Arguments("list"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultAdminScope), "netratel_events", Arguments("disable"))
            .Should().Be(McpOperationScopeDecision.Permit);

        new McpOperationScopeAuthorization(HostContext("dev"), ValidOptions())
            .Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_commands", Arguments("availability"))
            .Should().Be(new McpOperationScopeDecision(false, "operation_not_available_in_environment"));
    }

    [Theory]
    [InlineData("netratel_files", "collect")]
    [InlineData("netratel_files", "status")]
    [InlineData("netratel_marker_jobs", "create")]
    [InlineData("netratel_terminal", "fixture")]
    [InlineData("netratel_scripts", "create_marker")]
    [InlineData("netratel_client_logs", "resync")]
    public void Development_v2_http_rejects_compatibility_operations_even_when_called_directly(string tool, string operation)
    {
        var options = ValidOptions();
        options.OperatorSurfaceEnabled = true;
        var authorization = new McpOperationScopeAuthorization(HostContext("dev", operatorSurfaceEnabled: true), options);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultAdminScope), tool, Arguments(operation))
            .Should().Be(new McpOperationScopeDecision(false, "operation_not_available_over_http"));
    }

    [Fact]
    public void Production_operation_scope_authorization_admits_policy_file_reads_and_requires_write_scope_for_file_write_workflows()
    {
        var authorization = new McpOperationScopeAuthorization(HostContext("prod"), ValidOptions(instance: "prod"));

        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultFilesScope),
                "netratel_files",
                Arguments("read"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultFilesScope),
                "netratel_files",
                Arguments("status"))
            .Should().Be(new McpOperationScopeDecision(false, "operation_not_available_in_environment"));
        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultFilesScope),
                "netratel_files",
                Arguments("preview_write_text"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope),
                "netratel_files",
                Arguments("preview_write_text"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope),
                "netratel_files",
                Arguments("confirm_write_text"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultFilesScope),
                "netratel_files",
                Arguments("preview_upload"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(
                Principal(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope),
                "netratel_files",
                Arguments("confirm_upload"))
            .Should().Be(McpOperationScopeDecision.Permit);
    }

    [Fact]
    public void Production_operation_scope_authorization_requires_catalogued_scopes_for_observability_reads_and_confirmed_resync()
    {
        var authorization = new McpOperationScopeAuthorization(HostContext("prod"), ValidOptions(instance: "prod"));

        authorization.Evaluate(Principal("mcp:read"), "netratel_client_logs", Arguments("history"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_client_logs", Arguments("history"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_client_logs", Arguments("search"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_client_telemetry", Arguments("snapshot"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_client_logs", Arguments("preview_resync"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultObserveScope), "netratel_client_logs", Arguments("confirm_resync"))
            .Should().Be(new McpOperationScopeDecision(false, "missing_operation_scope"));
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope), "netratel_client_logs", Arguments("confirm_resync"))
            .Should().Be(McpOperationScopeDecision.Permit);
        authorization.Evaluate(Principal(NetRatelMcpHttpOptions.DefaultDevelopmentWriteScope), "netratel_client_logs", Arguments("resync"))
            .Should().Be(new McpOperationScopeDecision(false, "non_read_operations_are_development_only"));
    }

    [Fact]
    public void Every_http_mutation_available_in_development_follows_its_catalogued_scope()
    {
        var authorization = new McpOperationScopeAuthorization(HostContext("dev"), ValidOptions());
        var callerWithoutTheScope = Principal("mcp:read");
        var mutations = NetRatelMcpCatalog.Tools
            .Where(tool => tool.AvailableOverHttp)
            .SelectMany(tool => tool.Operations
                .Where(operation => operation.Safety != NetRatelMcpOperationSafety.Read &&
                    operation.IsAvailableOverHttpIn("dev"))
                .Select(operation => (ToolName: tool.Name, OperationName: operation.Name)))
            .ToArray();

        mutations.Should().NotBeEmpty();
        McpOperationAccessCatalog.ElevatedOperations
            .Select(entry => (entry.ToolName, entry.OperationName))
            .Should().OnlyHaveUniqueItems();
        foreach (var mutation in mutations)
        {
            var access = McpOperationAccessCatalog.Find(mutation.ToolName, mutation.OperationName)!;
            var expectedCode = access.RequiredScope switch
            {
                McpOperationAccessScope.Admin => "missing_operation_scope",
                McpOperationAccessScope.Onboarding => "missing_development_onboarding_scope",
                _ => "missing_development_write_scope"
            };
            authorization.Evaluate(callerWithoutTheScope, mutation.ToolName, Arguments(mutation.OperationName))
                .Should().Be(new McpOperationScopeDecision(false, expectedCode));
            access.Should().NotBeNull();
        }
    }

    [Fact]
    public void Every_catalogued_operation_has_exactly_one_scope_and_minimum_role_classification()
    {
        var cataloguedOperations = NetRatelMcpCatalog.Tools
            .SelectMany(tool => tool.Operations.Select(operation => (ToolName: tool.Name, OperationName: operation.Name)))
            .OrderBy(entry => entry.ToolName, StringComparer.Ordinal)
            .ThenBy(entry => entry.OperationName, StringComparer.Ordinal)
            .ToArray();

        McpOperationAccessCatalog.Operations
            .Select(entry => (entry.ToolName, entry.OperationName))
            .Should().OnlyHaveUniqueItems();
        foreach (var operation in cataloguedOperations)
        {
            var access = McpOperationAccessCatalog.Find(operation.ToolName, operation.OperationName);
            access.Should().NotBeNull($"{operation.ToolName}/{operation.OperationName} is published by the MCP catalog");
            access!.MinimumRole.Should().BeOneOf(McpOperationMinimumRole.Operator, McpOperationMinimumRole.Administrator);
        }

        McpOperationAccessCatalog.Find("netratel_clients", "presence")!.DevelopmentCompatibilityScope
            .Should().Be(McpDevelopmentCompatibilityScope.HostRequirement);
        McpOperationAccessCatalog.Find("netratel_scripts", "run")!.DevelopmentCompatibilityScope
            .Should().Be(McpDevelopmentCompatibilityScope.DevelopmentWrite);
        McpOperationAccessCatalog.Find("netratel_onboarding", "create_enrollment")!.DevelopmentCompatibilityScope
            .Should().Be(McpDevelopmentCompatibilityScope.DevelopmentOnboarding);
    }

    [Fact]
    public async Task Target_binding_fails_closed_when_the_isolated_file_does_not_match_the_selected_environment_allowlist()
    {
        var configurationPath = await WriteIsolatedConfigurationAsync("https://wrong-api.example");
        try
        {
            var action = () => NetRatelMcpHttpTargetBinding.Resolve(ValidOptions(configurationPath: configurationPath));

            action.Should().Throw<AgentClientValidationException>()
                .WithMessage("*must match the dev API allowlist entry*");
        }
        finally
        {
            File.Delete(configurationPath);
        }
    }

    [Theory]
    [InlineData("dev", "https://api.dev.example")]
    [InlineData("prod", "https://api.prod.example")]
    public async Task Target_binding_selects_only_the_allowlist_entry_for_its_instance(string instance, string apiBaseUrl)
    {
        var configurationPath = await WriteIsolatedConfigurationAsync(apiBaseUrl);
        try
        {
            var binding = NetRatelMcpHttpTargetBinding.Resolve(ValidOptions(configurationPath: configurationPath, instance: instance));

            binding.Target.Instance.Should().Be(instance);
            binding.Target.ApiBaseUri.Should().Be(new Uri($"{apiBaseUrl}/"));
        }
        finally
        {
            File.Delete(configurationPath);
        }
    }

    private static async Task<TestApplication> CreateApplicationAsync(bool includeProdApiBaseUrl = true, bool useDeploymentVariables = false, bool localCredentialMode = false)
    {
        var configurationPath = await WriteIsolatedConfigurationAsync("https://api.dev.example");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        var values = new Dictionary<string, string?>
        {
            ["NetRatel:Mcp:Http:PublicResourceUri"] = "https://mcp.dev.example/mcp",
            ["NetRatel:Mcp:Http:AllowedOrigins:0"] = "https://operator.example"
        };
        if (localCredentialMode)
        {
            values["NetRatel:Mcp:Http:LocalCredentialMode"] = "true";
            values["NetRatel:Mcp:Delegation:Enabled"] = "true";
            values["NetRatel:Mcp:Delegation:Issuer"] = "netratel-mcp-dev";
            values["NetRatel:Mcp:Delegation:Audience"] = "netratel-api-dev";
            values["NetRatel:Mcp:Delegation:ServicePrincipal"] = "netratel-mcp-http-dev";
            values["NetRatel:Mcp:Delegation:KeyId"] = "test-2026-09";
            values["NetRatel:Mcp:Delegation:SharedKeyBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"));
        }
        else
        {
            values["NetRatel:Mcp:Http:Authority"] = "https://auth.dev.example";
            values["NetRatel:Mcp:Http:Audience"] = "https://mcp.dev.example/mcp";
            values["NetRatel:Mcp:Http:RequiredScopes:0"] = "mcp:read";
            values["NetRatel:Mcp:Http:RequiredGroups:0"] = "netratel-operators";
        }
        if (useDeploymentVariables)
        {
            values[NetRatelMcpHttpOptions.InstanceEnvironmentVariable] = "dev";
            values[NetRatelMcpHttpOptions.ConfigurationPathEnvironmentVariable] = configurationPath;
            values[NetRatelMcpHttpOptions.DevApiBaseUrlEnvironmentVariable] = "https://api.dev.example";
            if (includeProdApiBaseUrl)
                values[NetRatelMcpHttpOptions.ProdApiBaseUrlEnvironmentVariable] = "https://api.prod.example";
        }
        else
        {
            values["NetRatel:Mcp:Http:Instance"] = "dev";
            values["NetRatel:Mcp:Http:ConfigurationPath"] = configurationPath;
            values["NetRatel:Mcp:Http:DevApiBaseUrl"] = "https://api.dev.example";
            if (includeProdApiBaseUrl)
                values["NetRatel:Mcp:Http:ProdApiBaseUrl"] = "https://api.prod.example";
        }

        builder.Configuration.AddInMemoryCollection(values);
        try
        {
            NetRatelMcpHttpApplication.ConfigureServices(builder);
            builder.Services.PostConfigure<JwtBearerOptions>(NetRatelMcpHttpApplication.JwtScheme, options =>
            {
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKey = TestSigningKey;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
            });
            var app = builder.Build();
            NetRatelMcpHttpApplication.ConfigurePipeline(app);
            await app.StartAsync();
            return new TestApplication(app, configurationPath);
        }
        catch
        {
            File.Delete(configurationPath);
            throw;
        }

    }

    private static NetRatelMcpHttpOptions ValidOptions(long maxRequestBodyBytes = 1_048_576, string configurationPath = "", string audience = "https://mcp.dev.example/mcp", string instance = "dev") => new()
    {
        Instance = instance,
        ConfigurationPath = configurationPath,
        DevApiBaseUrl = "https://api.dev.example",
        ProdApiBaseUrl = "https://api.prod.example",
        PublicResourceUri = "https://mcp.dev.example/mcp",
        Authority = "https://auth.dev.example",
        Audience = audience,
        RequiredScopes = ["mcp:read"],
        RequiredGroups = ["netratel-operators"],
        MaxRequestBodyBytes = maxRequestBodyBytes
    };

    private static NetRatelMcpHostContext HostContext(string instance, bool? operatorSurfaceEnabled = null)
        => new(
            new NetRatelMcpTarget(
                instance,
                new Uri("https://api.dev.example/"),
                new Uri("https://mcp.dev.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests",
            operatorSurfaceEnabled);

    private static ClaimsPrincipal Principal(string scopes)
        => new(new ClaimsIdentity([new Claim("scope", scopes)], "test"));

    private static IDictionary<string, JsonElement> Arguments(string operation)
        => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["operation"] = JsonSerializer.SerializeToElement(operation)
        };

    private static async Task<string> WriteIsolatedConfigurationAsync(string apiBaseUrl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netratel-mcp-http-{Guid.NewGuid():N}.json");
        var configuration = new AgentClientConfiguration(
            apiBaseUrl,
            "https://auth.dev.example/connect/token",
            "test-client",
            "test-agent",
            "test-password-not-for-output",
            "mcp:read");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions));
        return path;
    }

    private static HttpRequestMessage McpRequest(int id, string method, object parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateTestToken());
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static async Task<JsonDocument> ReadMcpPayloadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var data = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SingleOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(data is null ? payload : data[6..]);
    }

    private static string CreateTestToken()
    {
        var token = new JwtSecurityToken(
            issuer: "https://auth.dev.example",
            audience: "https://mcp.dev.example/mcp",
            claims:
            [
                new Claim("scope", "mcp:read"),
                new Claim("groups", "netratel-operators")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(TestSigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static McpOperatorDelegationOptions DelegationOptions() => new()
    {
        Enabled = true,
        Issuer = "netratel-mcp-dev",
        Audience = "netratel-api-dev",
        ServicePrincipal = "netratel-mcp-http-dev",
        KeyId = "dev-2026-08",
        SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
    };

    private sealed class LocalExchangeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("netratel_principal_id", "local-owner"),
                new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, "credential-id"),
                new Claim("integration_credential_resource", "https://mcp.dev.example/mcp"),
                new Claim("integration_credential_purpose", "http_mcp")
            ], Scheme.Name)), Scheme.Name)));
    }

    private sealed class AllowingEffectiveAccessService : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(
            principal.HasClaim("netratel_principal_id", "local-owner") &&
            ((permission == NetRatelPermissions.TelemetryRead && tenantId == 42) ||
             (permission == NetRatelPermissions.McpDiscoveryRead && tenantId is null)));

        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("local-owner", false, false,
                new HashSet<string>([NetRatelPermissions.TelemetryRead, NetRatelPermissions.McpDiscoveryRead], StringComparer.Ordinal)));

        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestApplication(WebApplication application, string configurationPath) : IAsyncDisposable
    {
        public WebApplication Application { get; } = application;

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            File.Delete(configurationPath);
        }
    }

}
