using System.Net;
using System.Text;
using FluentAssertions;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class AgentClientPolicyTests
{
    [Fact]
    public void Agent_client_configuration_prefers_public_environment_names_over_legacy_aliases()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AgentClientConfiguration.ApiBaseUrlEnvironmentVariable] = "https://public-api.example.test",
            [AgentClientConfiguration.OidcTokenUrlEnvironmentVariable] = "https://public-issuer.example.test/token",
            [AgentClientConfiguration.OidcClientIdEnvironmentVariable] = "public-client",
            [AgentClientConfiguration.OidcUsernameEnvironmentVariable] = "operator@example.test",
            [AgentClientConfiguration.OidcAppPasswordEnvironmentVariable] = "synthetic-password",
            [AgentClientConfiguration.OidcScopeEnvironmentVariable] = "netratel.api",
            ["BT_NetRatel_API_BASE_URL"] = "https://legacy-api.example.test",
            ["BT_OIDC_CLIENT_ID"] = "legacy-client"
        };

        var configuration = AgentClientConfiguration.FromEnvironment(name => variables.GetValueOrDefault(name));

        configuration.Resolve().Should().BeEquivalentTo(new ResolvedAgentClientConfiguration(
            new Uri("https://public-api.example.test"),
            "https://public-issuer.example.test/token",
            "public-client",
            "operator@example.test",
            "synthetic-password",
            "netratel.api"));
    }

    [Fact]
    public void Mcp_configuration_rejects_missing_explicit_configuration_path()
    {
        var load = () => AgentClientConfigurationResolver.LoadMcpIsolatedConfiguration(_ => null);

        load.Should().Throw<AgentClientValidationException>()
            .WithMessage("*NETRATEL_MCP_CONFIG is required*");
    }

    [Fact]
    public async Task Mcp_configuration_requires_an_explicit_canonical_file_and_ignores_the_legacy_alias_when_present()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netratel-mcp-canonical-{Guid.NewGuid():N}.json");
        try
        {
            var expected = new AgentClientConfiguration("https://isolated-api.example", "https://isolated-auth.example/token", "client", "agent", "secret", "scope");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(expected, AgentClientConfiguration.JsonOptions));

            var actual = AgentClientConfigurationResolver.LoadMcpIsolatedConfiguration(name => name switch
            {
                "NETRATEL_MCP_CONFIG" => path,
                "NetRatel_MCP_CONFIG" => "/not-used/legacy-config.json",
                _ => null
            });

            actual.Should().Be(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redaction_never_returns_the_oidc_app_password()
    {
        var config = new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "super-secret", "scope");

        var redacted = System.Text.Json.JsonSerializer.Serialize(AgentClientConfigurationResolver.Redact(config));

        redacted.Should().NotContain("super-secret");
        redacted.Should().Contain("***REDACTED***");
    }

    [Fact]
    public void Isolated_stdio_configuration_accepts_only_its_explicit_integration_credential_contract()
    {
        var configuration = new AgentClientConfiguration("https://isolated-api.example", null, null, null, null, null)
        {
            IntegrationCredential = "nrt_ic_12345678opaque"
        };

        var resolved = configuration.Resolve();

        resolved.AuthenticationMode.Should().Be(AgentClientAuthenticationMode.IntegrationCredential);
        resolved.IntegrationCredential.Should().Be("nrt_ic_12345678opaque");
    }

    [Fact]
    public async Task Agent_client_sends_an_integration_credential_directly_without_oidc_fallback()
    {
        var calls = 0;
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", null, null, null, null, null)
            {
                IntegrationCredential = "nrt_ic_12345678opaque"
            },
            () => new RecordingHandler(request =>
            {
                calls++;
                request.RequestUri!.Host.Should().Be("api.example");
                request.Headers.Authorization!.Parameter.Should().Be("nrt_ic_12345678opaque");
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{\"code\":\"invalid_integration_credential\"}"));
            }));

        var action = () => client.GetAsync("/api/v2/agents/7/telemetry");

        await action.Should().ThrowAsync<AgentClientRemoteException>();
        calls.Should().Be(1, "a rejected integration credential must not fall back to OIDC");
    }

    [Fact]
    public async Task Isolated_stdio_api_m2m_mode_remains_explicit_and_uses_the_configured_client_secret_flow()
    {
        var calls = 0;
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", null, null, null, null, null)
            {
                ApiM2MTokenUrl = "https://api.example/connect/token",
                ApiM2MClientId = "stdio-client",
                ApiM2MClientSecret = "synthetic-secret",
                ApiM2MScope = "netratel.api"
            },
            () => new RecordingHandler(async request =>
            {
                calls++;
                if (request.RequestUri!.AbsolutePath == "/connect/token")
                {
                    var body = await request.Content!.ReadAsStringAsync();
                    body.Should().Contain("client_id=stdio-client").And.NotContain("username=");
                    return Json(HttpStatusCode.OK, "{\"access_token\":\"m2m-token\",\"expires_in\":300}");
                }

                request.Headers.Authorization!.Parameter.Should().Be("m2m-token");
                return Json(HttpStatusCode.OK, "[]");
            }));

        await client.GetAsync("/api/v2/agents/7/telemetry");

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Isolated_configuration_requires_an_explicit_file()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"netratel-mcp-missing-{Guid.NewGuid():N}.json");
        var missing = () => AgentClientConfigurationResolver.LoadIsolated(missingPath);

        missing.Should().Throw<AgentClientValidationException>()
            .WithMessage("*does not exist*");

        var path = Path.Combine(Path.GetTempPath(), $"netratel-mcp-isolated-{Guid.NewGuid():N}.json");
        try
        {
            var expected = new AgentClientConfiguration("https://isolated-api.example", "https://isolated-auth.example/token", "client", "agent", "secret", "scope");
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(expected, AgentClientConfiguration.JsonOptions));

            var actual = AgentClientConfigurationResolver.LoadIsolated(path);

            actual.Should().Be(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Isolated_configuration_can_opt_into_the_target_pinned_api_m2m_contract_without_legacy_oidc_fields()
    {
        var target = new NetRatelMcpTarget(
            "dev",
            new Uri("https://api.example/"),
            new Uri("netratel://dev/status"),
            NetRatelMcpCatalog.Revision);
        var configuration = new AgentClientConfiguration("https://api.example/", null, null, null, null, null)
        {
            ApiM2MTokenUrl = "https://api.example/connect/token",
            ApiM2MClientId = "netratel-mcp-http-dev",
            ApiM2MClientSecret = "test-only-api-secret",
            ApiM2MScope = "orchestrator.api"
        };

        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<AgentClientConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(configuration, AgentClientConfiguration.JsonOptions),
            AgentClientConfiguration.JsonOptions);
        var options = NetRatelMcpOutboundOptions.FromIsolatedConfiguration(target, roundTripped!);

        options.TokenKind.Should().Be(NetRatelMcpOutboundTokenKind.ApiM2MClientSecret);
        options.ClientId.Should().Be("netratel-mcp-http-dev");
        options.TokenEndpoint.Should().Be(new Uri("https://api.example/connect/token"));
        options.Scope.Should().Be("orchestrator.api");
        options.Username.Should().BeEmpty();
        options.AppPassword.Should().BeEmpty();
    }

    [Fact]
    public void Isolated_api_m2m_configuration_rejects_a_token_endpoint_outside_the_selected_target()
    {
        var target = new NetRatelMcpTarget(
            "dev",
            new Uri("https://api.example/"),
            new Uri("netratel://dev/status"),
            NetRatelMcpCatalog.Revision);
        var configuration = new AgentClientConfiguration("https://api.example/", null, null, null, null, null)
        {
            ApiM2MTokenUrl = "https://other-api.example/connect/token",
            ApiM2MClientId = "netratel-mcp-http-dev",
            ApiM2MClientSecret = "test-only-api-secret",
            ApiM2MScope = "orchestrator.api"
        };

        var act = () => NetRatelMcpOutboundOptions.FromIsolatedConfiguration(target, configuration);

        act.Should().Throw<AgentClientValidationException>()
            .WithMessage("*selected API target's /connect/token endpoint*");
    }

    [Theory]
    [InlineData("netratel_config", "set")]
    [InlineData("netratel_notifications", "mark_read")]
    public void Known_mutations_require_confirmation(string tool, string operation)
        => MutationPolicy.RequiresConfirmation(tool, operation).Should().BeTrue();

    [Fact]
    public void Read_only_operations_do_not_require_confirmation()
        => MutationPolicy.RequiresConfirmation("netratel_jobs", "get").Should().BeFalse();

    [Fact]
    public void Unverifiable_job_definition_mutations_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_jobs", "create").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_jobs", "update").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_jobs", "delete").Should().BeFalse();
    }

    [Fact]
    public void Unverifiable_script_mutations_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_scripts", "create").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_scripts", "update").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_scripts", "delete").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_scripts", "parse_manifest").Should().BeFalse();
    }

    [Fact]
    public void Unverifiable_tenant_mutations_and_enrollment_credential_issuance_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_tenants", "create").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_tenants", "update").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_tenants", "delete").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_tenants", "enrollment_code_create").Should().BeFalse();
    }

    [Fact]
    public void Retired_client_mutations_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_clients", "update").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_clients", "ping").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_clients", "delete").Should().BeFalse();
    }

    [Fact]
    public void Retired_v1_client_file_and_terminal_mutations_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_client_files", "write").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_terminal", "stdin").Should().BeFalse();
    }

    [Fact]
    public void Unscoped_legacy_operational_mutations_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_job_runs", "start").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_events", "retry").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_connectivity", "test").Should().BeFalse();
    }

    [Fact]
    public void Unverifiable_v2_task_mutations_are_not_available_through_the_legacy_policy()
    {
        MutationPolicy.RequiresConfirmation("netratel_tasks", "create").Should().BeFalse();
        MutationPolicy.RequiresConfirmation("netratel_tasks", "run_library_script").Should().BeFalse();
    }

    [Fact]
    public void Confirmation_response_includes_the_operation_and_affected_ids()
    {
        var result = MutationPolicy.Require("retry_job", "This operation would retry job abc123.", "abc123");

        result.Status.Should().Be("confirmation_required");
        result.Confirmation.ConfirmField.Should().Be("confirm");
        result.Confirmation.AffectedIds.Should().ContainSingle().Which.Should().Be("abc123");
    }

    [Fact]
    public async Task Agent_client_mints_a_token_and_sends_it_as_a_bearer_header()
    {
        var calls = 0;
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope"),
            () => new RecordingHandler(async request =>
            {
                calls++;
                if (request.RequestUri!.Host == "auth.example")
                {
                    (await request.Content!.ReadAsStringAsync()).Should().Contain("grant_type=client_credentials");
                    return Json(HttpStatusCode.OK, """{"access_token":"minted-token","expires_in":300}""");
                }

                request.Headers.Authorization!.Scheme.Should().Be("Bearer");
                request.Headers.Authorization.Parameter.Should().Be("minted-token");
                return Json(HttpStatusCode.OK, "[]");
            }));

        await client.GetAsync("/api/v1/jobruns/");

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Agent_client_coalesces_concurrent_token_refreshes()
    {
        var tokenCalls = 0;
        var apiCalls = 0;
        var tokenRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTokenResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new NetRatelAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope"),
            () => new RecordingHandler(async request =>
            {
                if (request.RequestUri!.Host == "auth.example")
                {
                    Interlocked.Increment(ref tokenCalls);
                    tokenRequestStarted.TrySetResult();
                    await releaseTokenResponse.Task;
                    return Json(HttpStatusCode.OK, """{"access_token":"minted-token","expires_in":300}""");
                }

                request.Headers.Authorization!.Parameter.Should().Be("minted-token");
                Interlocked.Increment(ref apiCalls);
                return Json(HttpStatusCode.OK, "[]");
            }));

        var requests = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync("/api/v1/jobruns/")));
        await tokenRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseTokenResponse.TrySetResult();
        await requests;

        tokenCalls.Should().Be(1);
        apiCalls.Should().Be(8);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string content)
        => new(statusCode) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }
}
