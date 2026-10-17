using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.AgentClient;
using NetRatel.Setup;
using NetRatel.Tests.Mcp;
using R3;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace NetRatel.Tests.Setup;

public sealed class SetupTests
{
    [Fact]
    public async Task Reviewed_state_cannot_alias_destination_and_preserves_the_existing_profile()
    {
        using var directory = new TempDirectory();
        var destination = System.IO.Path.Combine(directory.Path, "auxiliary.json");
        var existing = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", CredentialPurpose = "HttpMcp",
            IntegrationCredential = "nrt_ic_existing-profile", Instance = "prod", ApprovedOrigin = "https://instance.example", ApprovedTarget = "https://instance.example/mcp" };
        new NetRatelConfigurationService().SaveProfile(destination, existing, ConfigurationSurface.HttpMcpClient);
        var before = ConfigurationFileStorage.Hash(destination);
        using var handler = new StubHandler(_ => Task.FromResult(Capabilities()));
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, destination, "auxiliary", handler);
        await session.DiscoverAsync("https://instance.example", default);
        var alias = System.IO.Path.Combine(directory.Path, ".", "auxiliary.json");
        var proposal = new NetRatel.Shared.Connectivity.ReviewedMcpAccessProposal(Guid.NewGuid().ToString(), "Auxiliary", "create", DateTimeOffset.UtcNow.AddDays(1), [], ["mcp.discovery.read"], []);
        var prepare = await Assert.ThrowsAsync<AgentClientValidationException>(() => session.PrepareReviewedAccessAsync(proposal, existing, alias, default));
        Assert.Contains("different from the destination", prepare.Message);
        var restore = await Assert.ThrowsAsync<AgentClientValidationException>(() => session.RestoreReviewedAccessAsync(alias, default));
        Assert.Contains("different from the destination", restore.Message);
        Assert.Equal(before, ConfigurationFileStorage.Hash(destination));
        Assert.Equal(existing.IntegrationCredential, new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpClient, destination, "auxiliary")).Configuration.IntegrationCredential);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_save_reconciles_lost_ack_after_restart_and_rejects_a_changed_profile(bool changeProfile)
    {
        using var directory = new TempDirectory();
        var destination = System.IO.Path.Combine(directory.Path, "auxiliary.json");
        var pending = System.IO.Path.Combine(directory.Path, "pending.json");
        var requestId = Guid.NewGuid().ToString("N"); var polls = 0; var acknowledgements = 0;
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        var proposal = new NetRatel.Shared.Connectivity.ReviewedMcpAccessProposal(requestId, "Acknowledgement recovery", "create", expiry, [], ["mcp.discovery.read"], []);
        const string credentialId = "b4444444444444448444444444444444";
        const string device = "nrt_pair_lost-ack-capability";
        const string secret = "nrt_ic_lost-ack-private-secret";
        using var handler = new StubHandler(request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities", StringComparison.Ordinal)) return Task.FromResult(Capabilities());
            if (route == "/mcp/setup/access/start") return Task.FromResult(Json(new NetRatel.Shared.Connectivity.IntegrationPairingStartResponse(requestId, "ABCD-EFGH", device,
                "https://instance.example/account/integration-credentials", "https://instance.example", DateTimeOffset.UtcNow.AddMinutes(10), 5)));
            if (route.EndsWith("/poll", StringComparison.Ordinal))
            {
                polls++; return Task.FromResult(Json(new NetRatel.Shared.Connectivity.IntegrationPairingPollResponse("issued", Delivery: new(credentialId, secret, proposal.Name,
                    NetRatel.Shared.Connectivity.PairingCredentialPurpose.HttpMcp, "https://instance.example/mcp", expiry, [], proposal.InstancePermissions))));
            }
            if (route.EndsWith("/acknowledge", StringComparison.Ordinal))
            {
                acknowledgements++;
                if (acknowledgements == 1) throw new HttpRequestException("Synthetic lost acknowledgement after server consumed delivery.");
                return Task.FromResult(Json(new { status = "consumed" }));
            }
            throw new InvalidOperationException("Unexpected recovery request.");
        });
        var requesting = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", CredentialPurpose = "HttpMcp",
            IntegrationCredential = "nrt_ic_requesting-client", Instance = "prod", ApprovedOrigin = "https://instance.example", ApprovedTarget = "https://instance.example/mcp" };
        await using (var first = new SetupSession(ConfigurationSurface.HttpMcpClient, destination, "auxiliary", handler))
        {
            await first.DiscoverAsync("https://instance.example", default);
            await first.PrepareReviewedAccessAsync(proposal, requesting, pending, default);
            await first.PollAsync(default); first.Review();
            await Assert.ThrowsAsync<HttpRequestException>(() => first.SaveAsync(false, default));
            Assert.True(first.Persisted); Assert.False(first.Saved);
            Assert.DoesNotContain(secret, File.ReadAllText(pending));
            Assert.Contains(credentialId, File.ReadAllText(pending));
        }
        if (changeProfile) File.AppendAllText(destination, " ");
        var retained = ConfigurationFileStorage.Hash(destination);
        await using (var restarted = new SetupSession(ConfigurationSurface.HttpMcpClient, destination, "auxiliary", handler))
        {
            if (changeProfile)
            {
                var failure = await Assert.ThrowsAsync<AgentClientValidationException>(() => restarted.RestoreReviewedAccessAsync(pending, default));
                Assert.Contains("changed after", failure.Message); Assert.False(restarted.Saved);
                Assert.Equal(1, acknowledgements);
            }
            else
            {
                await restarted.RestoreReviewedAccessAsync(pending, default);
                Assert.True(await restarted.ReconcileReviewedSavedAsync(default));
                Assert.True(restarted.Saved); Assert.Equal(credentialId, restarted.ReviewedSavedCredentialId);
                Assert.Equal(2, acknowledgements); Assert.Equal(1, polls);
            }
        }
        Assert.Equal(retained, ConfigurationFileStorage.Hash(destination));
        Assert.True(File.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_prepare_updates_private_pending_state_and_resume_preserves_exact_delivery_before_protected_save(bool mismatchedExpiry)
    {
        using var directory = new TempDirectory();
        var destination = System.IO.Path.Combine(directory.Path, "auxiliary.json");
        var pending = System.IO.Path.Combine(directory.Path, "private", "pending.json");
        var starts = 0; var acknowledgements = 0; var cancellations = 0;
        var expiry = DateTimeOffset.UtcNow.AddDays(30).AddTicks(1234);
        var requestId = Guid.NewGuid().ToString("D");
        var proposal = new NetRatel.Shared.Connectivity.ReviewedMcpAccessProposal(requestId, "Restricted auxiliary", "create", expiry,
            [new(17, "flow.read")], ["mcp.discovery.read"], [new(17, "netratel_flows", "list")]);
        const string device = "nrt_pair_private-reviewed-capability";
        const string deliveredSecret = "nrt_ic_private-reviewed-delivery";
        const string credentialId = "b3333333333343338333333333333333";
        using var handler = new StubHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities", StringComparison.Ordinal)) return Capabilities();
            if (route == "/mcp/setup/access/start")
            {
                starts++;
                Assert.Equal("Bearer nrt_ic_requesting-client", request.Headers.Authorization!.ToString());
                Assert.True(File.Exists(pending));
                Assert.DoesNotContain(device, File.ReadAllText(pending));
                return Json(new NetRatel.Shared.Connectivity.IntegrationPairingStartResponse(Guid.Parse(requestId).ToString("N"), "ABCD-EFGH", device,
                    "https://instance.example/account/integration-credentials", "https://instance.example", DateTimeOffset.UtcNow.AddMinutes(10), 5));
            }
            if (route.EndsWith("/poll", StringComparison.Ordinal))
            {
                Assert.Contains(device, await request.Content!.ReadAsStringAsync());
                return Json(new NetRatel.Shared.Connectivity.IntegrationPairingPollResponse("issued", Delivery:
                    new(credentialId, deliveredSecret, proposal.Name, NetRatel.Shared.Connectivity.PairingCredentialPurpose.HttpMcp,
                        "https://instance.example/mcp", mismatchedExpiry ? expiry.AddSeconds(1) : expiry, proposal.Grants, proposal.InstancePermissions)));
            }
            if (route.EndsWith("/acknowledge", StringComparison.Ordinal)) { acknowledgements++; return Json(new { status = "consumed" }); }
            if (route.EndsWith("/cancel", StringComparison.Ordinal)) { cancellations++; return Json(new { status = "cancelled" }); }
            throw new InvalidOperationException("Unexpected reviewed setup route.");
        });
        var requester = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", CredentialPurpose = "HttpMcp",
            IntegrationCredential = "nrt_ic_requesting-client", Instance = "prod", ApprovedOrigin = "https://instance.example", ApprovedTarget = "https://instance.example/mcp"
        };
        await using (var preparing = new SetupSession(ConfigurationSurface.HttpMcpClient, destination, "auxiliary", handler))
        {
            await preparing.DiscoverAsync("https://instance.example", default);
            await preparing.PrepareReviewedAccessAsync(proposal, requester, pending, default);
            Assert.Equal(device, preparing.Pairing!.DeviceCapability);
            Assert.Contains(device, ConfigurationFileStorage.ReadText(pending, secret: true));
            Assert.DoesNotContain(deliveredSecret, File.ReadAllText(pending));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pending));
            await preparing.PrepareReviewedAccessAsync(proposal, requester, pending, default);
            Assert.Equal(1, starts);
        }
        Assert.Equal(0, cancellations);
        await using (var resuming = new SetupSession(ConfigurationSurface.HttpMcpClient, destination, "auxiliary", handler))
        {
            await resuming.RestoreReviewedAccessAsync(pending, default);
            if (mismatchedExpiry)
            {
                await Assert.ThrowsAsync<AgentClientValidationException>(() => resuming.PollAsync(default));
                Assert.Null(resuming.Delivery); Assert.False(File.Exists(destination)); Assert.Equal(0, acknowledgements);
            }
            else
            {
                var issued = await resuming.PollAsync(default);
                Assert.Equal(expiry, issued.Delivery!.ExpiresAtUtc);
                resuming.Review(); await resuming.SaveAsync(false, default);
                Assert.True(resuming.Saved); Assert.Equal(1, acknowledgements);
                Assert.DoesNotContain(deliveredSecret, File.ReadAllText(destination));
                Assert.Equal(deliveredSecret, new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpClient, destination, "auxiliary")).Configuration.IntegrationCredential);
            }
        }
        Assert.Equal(0, cancellations);
    }

    [Fact]
    public async Task Unknown_host_authentication_has_a_visible_choice_and_Enter_selects_it_without_Advanced()
    {
        using var directory = new TempDirectory();
        string? fingerprint = null;
        var handler = new StubHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities")) return Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", resource = "https://instance.example/mcp", verificationUri = "https://instance.example/account/integration-credentials", localCredentialMode = false, authenticationModeKnown = false, requiredGroups = Array.Empty<string>(), requiredScopes = Array.Empty<string>() });
            if (route.EndsWith("start"))
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                fingerprint = body.RootElement.GetProperty("registrationFingerprint").GetString();
                return Json(new { requestId = "host", userCode = "HOST-CODE", deviceCapability = "host-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
            }
            return Json(route.EndsWith("poll") ? new { status = "approved", approvalId = (string?)"approved", registrationFingerprint = fingerprint } : new { status = "cancelled", approvalId = (string?)null, registrationFingerprint = (string?)null });
        });
        var effective = HttpHostingDeploymentFixture.WithSource(new(directory.Path, "https://instance.example", "https://instance.example") { ExistingM2MCallerClientIds = [] });
        var model = new HostingSetupViewModel(new Dictionary<string, string> { ["--deployment-manager"] = "release-compose", ["--output-directory"] = directory.Path }, handler)
            { EffectiveComposeSourcePath = effective.EffectiveComposeSourcePath };
        var input = new VirtualInputSource(); var terminal = new VirtualTerminal(96, 30);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input);
        builder.Services.AddTermina("/hosting", routes => routes.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", services => new HostingSetupPage(services.GetRequiredService<Termina.Clipboard.IClipboardService>()), _ => model));
        builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        var choice = WaitFor(model.Revision, () => model.Step == HostingSetupStep.Authentication && !model.Busy);
        input.EnqueueString("https://instance.example/mcp/"); input.EnqueueKey(ConsoleKey.Enter); await choice;
        Assert.False(model.Advanced); Assert.False(model.AuthenticationModeSelected); Assert.Null(fingerprint);
        var visible = false; await model.InvokeAsync(() => visible = terminal.Contains("No authentication mode selected")); Assert.True(visible);
        var correction = WaitFor(model.Revision, () => !model.Busy && model.Error.Value.StartsWith("Select the authentication"));
        input.EnqueueKey(ConsoleKey.F2); await correction;
        await model.InvokeAsync(() => visible = terminal.Contains("Next action") && terminal.Contains("Select the authentication")); Assert.True(visible);
        var review = WaitFor(model.Revision, () => model.Step == HostingSetupStep.Review && !model.Busy);
        input.EnqueueKey(ConsoleKey.Enter); await review;
        Assert.Equal("Local credential", model.AuthMode); Assert.True(model.AuthenticationModeSelected); Assert.NotNull(fingerprint);
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync(); await model.CleanupAsync();
    }

    [Fact]
    public async Task Verify_requires_saved_server_setup_and_never_starts_another_administrator_registration()
    {
        using var directory = new TempDirectory(); var starts = 0;
        using var model = new HostingSetupViewModel(new Dictionary<string, string> { ["--deployment-manager"] = "release-compose", ["--output-directory"] = directory.Path, ["--instance"] = "https://instance.example", ["--verify"] = "true" },
            new StubHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("start")) starts++;
                return Task.FromResult(Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", resource = "https://instance.example/mcp", verificationUri = "https://instance.example/account/integration-credentials", localCredentialMode = true, requiredGroups = Array.Empty<string>(), requiredScopes = Array.Empty<string>() }));
            }));
        await model.NextAsync();
        Assert.Equal(HostingSetupStep.Instance, model.Step); Assert.Contains("No saved server setup", model.Error.Value); Assert.Equal(0, starts); Assert.False(model.Verified);
        await model.CleanupAsync();
    }

    [Fact]
    public async Task Native_host_verification_requires_observed_runtime_before_temporary_client_consent()
    {
        using var directory = new TempDirectory();
        var request = HttpHostingDeploymentFixture.WithSource(new(directory.Path, "https://instance.example", "https://instance.example") { ExistingM2MCallerClientIds = [] });
        new NetRatelHttpHostingSetupService().Apply(request);
        var temporaryApprovals = 0;
        using var model = new HostingSetupViewModel(new Dictionary<string, string>
        {
            ["--output-directory"] = directory.Path, ["--instance"] = "https://instance.example", ["--verify"] = "true"
        }, new StubHandler(message =>
        {
            if (message.RequestUri!.AbsolutePath.Contains("integration-pairing", StringComparison.Ordinal)) temporaryApprovals++;
            return Task.FromResult(Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", resource = "https://instance.example/mcp",
                verificationUri = "https://instance.example/account/integration-credentials", localCredentialMode = true, requiredGroups = Array.Empty<string>(), requiredScopes = Array.Empty<string>() }));
        })) { RuntimeEnvironmentSourcePath = null };
        try
        {
            await model.NextAsync();
            Assert.Contains("effective_runtime_missing", model.Error.Value);
            Assert.False(model.Verified);
            Assert.Equal(0, temporaryApprovals);
        }
        finally { await model.CleanupAsync(); }
    }

    [Theory]
    [InlineData("http://instance.example")]
    [InlineData("https://instance.example/prefix%2fmcp")]
    [InlineData("https://instance.example/mcp?token=x")]
    [InlineData("https://instance.example/path/../mcp")]
    [InlineData("https://user:secret@instance.example")]
    [InlineData("https://instance.example?token=x")]
    public void Instance_url_rejects_unsafe_or_non_origin_input(string input)
        => Assert.Throws<AgentClientValidationException>(() => SetupSession.ValidateOrigin(input));

    [Fact]
    public async Task Http_client_initial_state_keeps_the_selected_transport_coherent()
    {
        using var directory = new TempDirectory();
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, System.IO.Path.Combine(directory.Path, "client.json"), "test");
        using var model = new SetupViewModel(session);
        Assert.Equal(ConfigurationSurface.HttpMcpClient, model.Surface);
        Assert.Equal(ConfigurationSurface.HttpMcpClient, session.Surface); Assert.Equal(SetupStep.Instance, model.Step);
    }
    [Theory]
    [InlineData("https://instance.example", "https://instance.example/")]
    [InlineData("https://instance.example/", "https://instance.example/")]
    [InlineData("https://instance.example/mcp", "https://instance.example/")]
    [InlineData("https://instance.example:8443/mcp/", "https://instance.example:8443/")]
    public void Common_instance_addresses_share_one_normalized_origin(string input, string origin)
        => Assert.Equal(origin, SetupSession.ValidateOrigin(input).AbsoluteUri);

    [Fact]
    public async Task Interactive_options_seed_the_url_and_existing_provider_authentication_is_selected()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "external.json");
        var config = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example", AuthenticationMode = "Oidc", OidcTokenUrl = "https://identity.example/token", OidcClientId = "personal-app", OidcUsername = "operator", OidcAppPassword = "protected-test-password", ProfileName = "test", Instance = "prod" };
        new NetRatelConfigurationService().SaveProfile(path, config, ConfigurationSurface.Cli);
        await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", new StubHandler(_ => Task.FromResult(Capabilities())));
        using var model = SetupCommand.CreateViewModel(session, new Dictionary<string, string> { ["--instance"] = "https://instance.example/mcp" });
        Assert.Equal(SetupStep.Instance, model.Step); Assert.Equal("https://instance.example/mcp", model.Origin); Assert.Equal("Use existing authentication", model.Authentication);
        await model.NextAsync(); await model.NextAsync();
        Assert.Equal(SetupStep.Review, model.Step); Assert.Equal("Oidc", session.Configuration.AuthenticationMode); Assert.Equal("https://identity.example/token", session.Configuration.OidcTokenUrl);
    }

    [Fact]
    public async Task Http_client_oidc_discovery_uses_the_advertised_cross_authority_token_endpoint_and_preserves_its_query()
    {
        using var directory = new TempDirectory(); var paired = false; var tokenRequests = 0;
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, System.IO.Path.Combine(directory.Path, "client.json"), "test", new StubHandler(async request =>
        {
            if (request.RequestUri!.Host == "identity.example") return Json(new { issuer = "https://identity.example/realms/netratel", token_endpoint = "https://tokens.identity.example/oauth/token?tenant=ops" });
            if (request.RequestUri.Host == "tokens.identity.example")
            {
                tokenRequests++; Assert.Equal("/oauth/token", request.RequestUri.AbsolutePath); Assert.Equal("?tenant=ops", request.RequestUri.Query);
                Assert.Contains("client_id=personal-app", await request.Content!.ReadAsStringAsync());
                return Json(new { access_token = "cross-authority-personal-token", expires_in = 60 });
            }
            if (request.RequestUri.AbsolutePath == "/mcp") { Assert.Equal("cross-authority-personal-token", request.Headers.Authorization?.Parameter); return await McpReply(request); }
            if (request.RequestUri.AbsolutePath.EndsWith("start")) paired = true;
            return Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", pairingSupported = true, verificationUri = "https://instance.example/account/integration-credentials", httpMcpResource = "https://instance.example/mcp", httpMcpAuthenticationMode = "oidc", httpMcpAuthenticationModeKnown = true, httpMcpOidcAuthority = "https://identity.example/realms/netratel", httpMcpRequiredScopes = new[] { "mcp.read" } });
        }));
        var model = new SetupViewModel(session) { Origin = "https://instance.example/mcp" }; var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input); builder.Services.AddTermina("/setup", routes => routes.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(), _ => model)); builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        var authentication = WaitFor(model, () => model.Step == SetupStep.Authentication && !model.Busy); input.EnqueueKey(ConsoleKey.Enter); await authentication;
        Assert.Equal("External authentication", model.Authentication); Assert.True(model.ExternalProviderDiscovered);
        Assert.Equal("https://tokens.identity.example/oauth/token?tenant=ops", model.ExternalTokenUrl); Assert.Equal("mcp.read", model.ExternalScope); Assert.False(paired);
        var visible = false; await model.InvokeAsync(() => visible = terminal.Contains("tokens.identity.example/oauth/token?tenant=ops")); Assert.True(visible);
        model.ExternalClientId = "personal-app"; model.ExternalUsername = "personal-operator"; model.ExternalPassword = "protected-provider-password";
        await model.NextAsync(); Assert.Equal(SetupStep.Review, model.Step);
        var checks = await session.VerifyTransientHttpMcpAsync(default); Assert.All(checks, check => Assert.Equal("completed", check.Status)); Assert.Equal(1, tokenRequests); Assert.False(File.Exists(session.Path));
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync();
    }

    [Theory]
    [InlineData("http://tokens.identity.example/token", "https://identity.example/realm")]
    [InlineData("https://user:password@tokens.identity.example/token", "https://identity.example/realm")]
    [InlineData("https://tokens.identity.example/token#fragment", "https://identity.example/realm")]
    [InlineData("https://tokens.identity.example/token", "https://unapproved-issuer.example/realm")]
    public async Task Oidc_discovery_rejects_unsafe_token_endpoints_and_an_unapproved_issuer(string endpoint, string issuer)
    {
        using var directory = new TempDirectory(); var requests = 0;
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, System.IO.Path.Combine(directory.Path, "client.json"), "test", new StubHandler(request =>
        {
            requests++; Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.Host == "identity.example") return Task.FromResult(Json(new { issuer, token_endpoint = endpoint }));
            return Task.FromResult(Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", pairingSupported = true, verificationUri = "https://instance.example/account/integration-credentials", httpMcpResource = "https://instance.example/mcp", httpMcpAuthenticationMode = "oidc", httpMcpOidcAuthority = "https://identity.example/realm" }));
        }));
        await session.DiscoverAsync("https://instance.example", default);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => session.DiscoverExternalProviderAsync(default));
        Assert.Equal(2, requests); Assert.False(File.Exists(session.Path));
    }

    [Fact]
    public async Task Existing_read_only_external_profile_reuses_its_source_without_copying_or_replacing_secrets()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "external.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "Oidc", OidcTokenUrl = "https://identity.example/token", OidcClientId = "personal-app", OidcUsername = "operator", OidcAppPassword = "protected-password", Instance = "prod" }, ConfigurationSurface.HttpMcpClient);
        var before = ConfigurationFileStorage.Hash(path); var files = Directory.EnumerateFiles(directory.Path).Order().ToArray();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", new StubHandler(_ => Task.FromResult(Capabilities())));
            using var model = new SetupViewModel(session);
            await model.NextAsync(); await model.NextAsync();
            Assert.Equal(SetupStep.Review, model.Step); Assert.True(session.ReusesExistingConfiguration);
            session.Review(); await session.SaveAsync(false, default);
            Assert.True(session.Saved); Assert.Equal(before, ConfigurationFileStorage.Hash(path)); Assert.Equal(files, Directory.EnumerateFiles(directory.Path).Order().ToArray());
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public async Task Non_tty_setup_has_deterministic_exit_and_no_stdout()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await SetupCommand.RunAsync(["setup"], ConfigurationSurface.StdioMcp, output, error, interactive: false);
        Assert.Equal(2, exit); Assert.Equal("", output.ToString()); Assert.Contains("requires a TTY", error.ToString());
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(0, await SetupCommand.RunAsync(["setup", "--help"], ConfigurationSurface.HttpMcpOutbound, output, error, interactive: false));
        Assert.Contains("--non-interactive", output.ToString()); Assert.Equal("", error.ToString());
    }
    [Theory]
    [InlineData(false, new[] { "setup" })]
    [InlineData(false, new[] { "setup", "--target", "http-client" })]
    [InlineData(false, new[] { "setup", "--mode", "client" })]
    [InlineData(true, new[] { "setup", "--mode", "host" })]
    [InlineData(true, new[] { "setup", "--target", "http-host" })]
    public async Task Http_distribution_preserves_personal_client_default_and_requires_explicit_host_selection(bool hosting, string[] arguments)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await SetupCommand.RunAsync(arguments, ConfigurationSurface.HttpMcpOutbound, output, error, interactive: false));
        Assert.Contains(hosting ? "Interactive hosting setup" : "Interactive setup requires a TTY", error.ToString());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Pairing_grants_save_bound_profile_once_and_ack_after_atomic_save()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "client.json");
        var requests = new List<string>(); var savedAtAck = false;
        var handler = new StubHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath; requests.Add(route);
            if (route.EndsWith("capabilities")) return Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", pairingSupported = true, verificationUri = "https://instance.example/account/integration-credentials?tab=credentials", httpMcpResource = "https://mcp.example/mcp" });
            if (route.EndsWith("start")) return Json(new { requestId = "request", userCode = "ABCD-EFGH", deviceCapability = "device-capability", verificationUri = "https://instance.example/account/integration-credentials?tab=credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5), intervalSeconds = 5 });
            if (route.EndsWith("poll")) return Json(new { status = "issued", intervalSeconds = 5, delivery = new { credentialId = "42", secret = "nrt_ic_test-secret-never-rendered", name = "test", purpose = 1, resource = "https://mcp.example/mcp", expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1), grants = new[] { new { tenantId = 7, permission = "telemetry.read" } }, instancePermissions = Array.Empty<string>() } });
            if (route.EndsWith("acknowledge")) { savedAtAck = File.Exists(path); return Json(new { status = "consumed" }); }
            return Json(new { status = "cancelled" });
        });
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", handler);
        await session.DiscoverAsync("https://instance.example", default); await session.BeginPairingAsync(default); await session.BeginPairingAsync(default);
        Assert.Equal("https://mcp.example/mcp", session.CanonicalTarget);
        Assert.Equal("issued", (await session.PollAsync(default)).Status);
        Assert.Throws<AgentClientValidationException>(() => session.SelectTenant(99)); session.SelectTenant(7);
        session.Review(); await session.SaveAsync(false, default); await session.SaveAsync(false, default);
        Assert.True(savedAtAck); Assert.Single(requests.Where(p => p.EndsWith("start"))); Assert.Single(requests.Where(p => p.EndsWith("acknowledge")));
        var json = File.ReadAllText(path); Assert.DoesNotContain("nrt_ic_test-secret", json);
        var loaded = new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpClient, path, "test", GetEnvironmentVariable: _ => null));
        Assert.True(loaded.Validate().IsValid); Assert.Equal("https://mcp.example/mcp", loaded.Configuration.ApprovedTarget);
        Assert.DoesNotContain("nrt_ic_test-secret", session.LaunchSnippet()); Assert.Contains("NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN", session.LaunchSnippet());
    }
    [Theory]
    [InlineData("pending")]
    [InlineData("denied")]
    [InlineData("expired")]
    public async Task Browser_status_is_preserved_and_cancel_only_cleans_its_request(string status)
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "test.json"); var cancellations = 0;
        var handler = new StubHandler(request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities")) return Task.FromResult(Capabilities());
            if (route.EndsWith("start")) return Task.FromResult(Json(new { requestId = "one", userCode = "CODE-ONLY", deviceCapability = "private-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 }));
            if (route.EndsWith("poll")) return Task.FromResult(Json(new { status, intervalSeconds = 5 }));
            if (route.EndsWith("cancel")) cancellations++;
            return Task.FromResult(Json(new { status = "cancelled" }));
        });
        await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", handler);
        await session.DiscoverAsync("https://instance.example", default); await session.BeginPairingAsync(default);
        Assert.Equal(status, (await session.PollAsync(default)).Status); await session.CancelPairingAsync();
        Assert.Equal(1, cancellations); Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task Back_fences_late_discovery_results_without_modifying_a_profile()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "test.json");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", new StubHandler(request => { started.SetResult(); return response.Task; }));
        using var model = new SetupViewModel(session);
        model.Origin = "https://instance.example";
        var pending = model.NextAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.BackAsync(); response.SetResult(Capabilities()); await pending;
        Assert.Equal(SetupStep.Instance, model.Step); Assert.Null(session.Capabilities); Assert.False(File.Exists(path));
    }
    [Theory]
    [InlineData(ConfigurationSurface.Cli)]
    [InlineData(ConfigurationSurface.StdioMcp)]
    [InlineData(ConfigurationSurface.HttpMcpClient)]
    public async Task Virtual_input_navigates_and_resizes_without_rendering_manual_secret(ConfigurationSurface surface)
    {
        using var directory = new TempDirectory();
        await using var session = new SetupSession(surface, System.IO.Path.Combine(directory.Path, "test.json"), "test", new StubHandler(_ => Task.FromResult(Capabilities())));
        var model = new SetupViewModel(session); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input);
        builder.Services.AddTermina("/setup", routes => routes.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(), _ => model));
        builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        Assert.Equal(SetupStep.Instance, model.Step);
        var authentication = WaitFor(model, () => model.Step == SetupStep.Authentication && !model.Busy);
        input.EnqueueString("https://instance.example/mcp/"); input.EnqueueKey(ConsoleKey.Enter); await authentication;
        await model.InvokeAsync(() => { model.Authentication = "Existing credential"; model.ManualSecret = "never-render-this-secret"; model.Refresh(); });
        var resized = WaitFor(model, () => model.Width == 120); input.EnqueueResize(120, 30); await resized;
        Assert.False(terminal.Contains("never-render-this-secret")); Assert.Equal(120, model.Width); Assert.Equal(SetupStep.Authentication, model.Step);
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync(); Assert.False(File.Exists(session.Path));
    }
    [Theory]
    [InlineData(true, "https://instance.example/mcp")]
    [InlineData(true, "https://instance.example/prefix/mcp")]
    [InlineData(false, "https://instance.example/mcp")]
    public async Task Http_mcp_doctor_uses_initialize_and_offered_read_only_tool_without_accepting_unauthorized_response(bool authorized, string resource)
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "mcp.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = resource, IntegrationCredential = "nrt_ic_test-secret", AuthenticationMode = "IntegrationCredential", ProfileName = "test", Instance = "prod" }, ConfigurationSurface.HttpMcpClient);
        var calls = new List<string>();
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("capabilities")) return Capabilities();
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var method = body.RootElement.GetProperty("method").GetString()!; calls.Add(method);
            if (!authorized) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return method switch
            {
                "initialize" => Json(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-03-26", capabilities = new { } } }),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Json(new { jsonrpc = "2.0", id = 2, result = new { tools = new[] { new { name = "netratel_auth" } } } }),
                "tools/call" => Json(new { jsonrpc = "2.0", id = 3, result = new { isError = false, structuredContent = new { success = true, data = new { authenticated = true, authMode = "IntegrationCredential", groups = Array.Empty<string>(), roles = new[] { "operator" } } } } }),
                _ => throw new InvalidOperationException("Unexpected MCP operation")
            };
        });
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", handler);
        var checks = await session.DoctorAsync(default);
        Assert.Contains(checks, check => check.Name == "authentication" && check.Status == (authorized ? "completed" : "unauthorized"));
        if (authorized) { Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" }, calls); Assert.Contains(checks, check => check.Name == "permitted read-only tool" && check.Status == "completed"); }
        else
        {
            Assert.Single(calls); Assert.DoesNotContain(checks, check => check.Name == "MCP initialize" && check.Status == "completed");
            using var model = new SetupViewModel(session);
            await model.NextAsync(); await model.NextAsync(); await model.NextAsync();
            Assert.Equal(SetupStep.Finish, model.Step); Assert.Contains(model.Checks, check => check.Status == "unauthorized");
            var hash = ConfigurationFileStorage.Hash(path); var before = calls.Count;
            await model.NextAsync();
            Assert.Equal(before + 1, calls.Count); Assert.Equal(hash, ConfigurationFileStorage.Hash(path)); Assert.Contains(model.Checks, check => check.Status == "unauthorized");
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_http_verification_uses_the_selected_provider_token_and_rejects_failure_without_fallback(bool denied)
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "oidc.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with
        { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "Oidc", OidcTokenUrl = "https://identity.example/token", OidcClientId = "personal-app", OidcUsername = "operator", OidcAppPassword = "protected-password", OidcScope = "mcp.read", Instance = "prod" }, ConfigurationSurface.HttpMcpClient);
        var tokens = 0; var mcpCalls = 0;
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", new StubHandler(async request =>
        {
            if (request.RequestUri!.Host == "identity.example")
            {
                tokens++; Assert.Equal("/token", request.RequestUri.AbsolutePath);
                var form = await request.Content!.ReadAsStringAsync(); Assert.Contains("grant_type=client_credentials", form); Assert.Contains("client_id=personal-app", form); Assert.Contains("scope=mcp.read", form);
                return denied ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(new { access_token = "external-verification-token", expires_in = 60, token_type = "Bearer" });
            }
            mcpCalls++; Assert.Equal("external-verification-token", request.Headers.Authorization?.Parameter);
            return await McpReply(request);
        }));
        var checks = await session.DoctorAsync(default);
        Assert.Equal(1, tokens);
        var launch = session.LaunchSnippet(); Assert.Contains("OAuth sign-in", launch); Assert.Contains("/.well-known/oauth-protected-resource/mcp", launch); Assert.DoesNotContain("INTEGRATION_TOKEN", launch); Assert.DoesNotContain("protected-password", launch); Assert.DoesNotContain("external-verification-token", launch);
        if (denied) { Assert.Equal(0, mcpCalls); Assert.DoesNotContain(checks, check => check.Name == "permitted read-only tool" && check.Status == "completed"); }
        else { Assert.Equal(4, mcpCalls); Assert.All(checks, check => Assert.Equal("completed", check.Status)); }
    }

    [Fact]
    public async Task Http_verification_rejects_generic_success_and_wrong_rpc_response()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "generic.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", IntegrationCredential = "nrt_ic_test-secret", Instance = "prod" }, ConfigurationSurface.HttpMcpClient);
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", new StubHandler(_ => Task.FromResult(Json(new { success = true, result = new { protocolVersion = "2025-03-26" } }))));
        var checks = await session.DoctorAsync(default);
        Assert.DoesNotContain(checks, check => check.Name == "authentication" && check.Status == "completed");
        Assert.Contains(checks, check => check.Name == "MCP initialize" && check.Status == "unavailable");
    }

    [Fact]
    public async Task Virtual_host_journey_approves_dedicated_generation_applies_then_verifies_with_a_distinct_transient_personal_credential()
    {
        using var directory = new TempDirectory(); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        string? fingerprint = null; var personalRevocations = 0;
        var handler = new StubHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/api/http-host-setup/v1/capabilities") return Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", resource = "https://instance.example/mcp", verificationUri = "https://instance.example/account/integration-credentials", localCredentialMode = true, oidcAuthority = (string?)null, requiredGroups = Array.Empty<string>(), requiredScopes = Array.Empty<string>(), apiServiceAudience = "custom.api", existingM2MCallerClientIds = Array.Empty<string>() });
            if (route == "/api/http-host-setup/v1/start")
            {
                using var document = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); fingerprint = document.RootElement.GetProperty("registrationFingerprint").GetString();
                return Json(new { requestId = "host", userCode = "HOST-CODE", deviceCapability = "host-only-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
            }
            if (route == "/api/http-host-setup/v1/poll") return Json(new { status = "approved", intervalSeconds = 5, approvalId = "admin-approved", registrationFingerprint = fingerprint });
            if (route == "/api/http-host-setup/v1/cancel") return Json(new { status = "cancelled" });
            if (route.EndsWith("capabilities")) return Capabilities();
            if (route.EndsWith("start")) return Json(new { requestId = "personal", userCode = "READ-CODE", deviceCapability = "personal-only-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
            if (route.EndsWith("poll")) return Json(new { status = "issued", intervalSeconds = 5, delivery = new { credentialId = "personal-42", secret = "nrt_ic_transient-verifier", name = "read-only check", purpose = 1, resource = "https://instance.example/mcp", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5), grants = Array.Empty<object>(), instancePermissions = new[] { "mcp.discovery.read" } } });
            if (route.EndsWith("cancel")) { personalRevocations++; return Json(new { status = "cancelled" }); }
            Assert.Equal("nrt_ic_transient-verifier", request.Headers.Authorization?.Parameter); return await McpReply(request);
        });
        var effective = HttpHostingDeploymentFixture.WithSource(new(directory.Path, "https://instance.example", "https://instance.example") { ExistingM2MCallerClientIds = [] });
        var model = new HostingSetupViewModel(new Dictionary<string, string> { ["--deployment-manager"] = "release-compose", ["--output-directory"] = directory.Path }, handler)
            { EffectiveComposeSourcePath = effective.EffectiveComposeSourcePath };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input); builder.Services.AddTermina("/hosting", routes => routes.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", services => new HostingSetupPage(services.GetRequiredService<Termina.Clipboard.IClipboardService>()), _ => model)); builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        var reviewed = WaitFor(model.Revision, () => model.Step == HostingSetupStep.Review && !model.Busy);
        input.EnqueueString("https://instance.example/mcp"); input.EnqueueKey(ConsoleKey.Enter); await reviewed;
        Assert.NotNull(fingerprint); Assert.Equal(64, fingerprint.Length); Assert.Null(model.Result);
        var applied = WaitFor(model.Revision, () => model.Step == HostingSetupStep.Apply && !model.Busy); input.EnqueueKey(ConsoleKey.Enter); await applied;
        Assert.False(model.Verified); Assert.NotNull(model.Result); Assert.Contains("docker compose", model.ApplyCommand);
        var apiSettings = File.ReadAllText(System.IO.Path.Combine(directory.Path, "api.env"));
        Assert.Contains("custom.api", apiSettings); Assert.DoesNotContain("netratel-cli", apiSettings); Assert.DoesNotContain("netratel-client-artifacts", apiSettings); Assert.DoesNotContain("\nM2M__Audience=", apiSettings);
        var visible = false; await model.InvokeAsync(() => { visible = terminal.Contains("deployment apply pending"); Console.WriteLine("Rendered host apply screen:\n" + terminal); }); Assert.True(visible);
        model.RuntimeEnvironmentSourcePath = AppliedRuntimeFixture(directory.Path);
        var finished = WaitFor(model.Revision, () => !model.Busy && (model.Verified || model.Step == HostingSetupStep.Verify && model.Error.Value.Length > 0));
        input.EnqueueKey(ConsoleKey.Tab); input.EnqueueKey(ConsoleKey.Enter); await finished;
        Assert.True(model.Verified, $"Verification stopped at {model.Step}: {model.Error.Value}");
        Assert.Equal(1, personalRevocations); Assert.All(model.Checks, check => Assert.Equal("completed", check.Status));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories), path => File.ReadAllText(path).Contains("nrt_ic_transient-verifier", StringComparison.Ordinal));
        await model.InvokeAsync(() => Console.WriteLine("Rendered host verified screen:\n" + terminal));
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync(); await model.CleanupAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Virtual_host_rerun_accepts_mixed_saved_components_and_approves_the_effective_service_principal(bool adoptOutbound)
    {
        using var directory = new TempDirectory();
        var output = System.IO.Path.Combine(directory.Path, "setup");
        var request = new HttpHostingSetupRequest(output, "https://instance.example", "https://instance.example")
        { ExistingM2MCallerClientIds = [] };
        if (adoptOutbound)
        {
            var existing = System.IO.Path.Combine(directory.Path, "existing-outbound.json");
            ConfigurationFileStorage.WriteAtomic(existing, System.Text.Json.JsonSerializer.Serialize(AgentClientConfiguration.Empty with
            {
                ApiBaseUrl = "https://instance.example", ApiM2MTokenUrl = "https://instance.example/connect/token",
                ApiM2MClientId = "existing-service", ApiM2MClientSecret = "existing-protected-service-secret", ApiM2MScope = "netratel.api", Instance = "prod"
            }, AgentClientConfiguration.JsonOptions));
            request = request with { ExistingOutboundConfigurationPath = existing };
        }
        else
        {
            var key = System.IO.Path.Combine(directory.Path, "existing-delegation.key");
            ConfigurationFileStorage.WriteAtomic(key, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "\n");
            request = request with { ExistingDelegationKeyFile = key, DelegationKeyId = "existing-v1", DelegationIssuer = "existing-issuer", DelegationAudience = "existing-api" };
        }
        request = HttpHostingDeploymentFixture.WithSource(request);
        var setup = new NetRatelHttpHostingSetupService(); setup.Apply(request);
        var savedFingerprint = setup.PrepareRegistration(request);
        var originalHashes = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, ConfigurationFileStorage.Hash);
        string? submittedPrincipal = null; string? submittedFingerprint = null;
        var handler = new StubHandler(async message =>
        {
            var route = message.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities")) return Json(new
            {
                protocolVersion = 1, publicOrigin = "https://instance.example", resource = "https://instance.example/mcp",
                verificationUri = "https://instance.example/account/integration-credentials", localCredentialMode = true,
                requiredGroups = Array.Empty<string>(), requiredScopes = Array.Empty<string>(), existingHostConfigured = true,
                existingM2MCallerClientIds = adoptOutbound ? Array.Empty<string>() : new[] { "netratel-mcp-http" }
            });
            if (route.EndsWith("start"))
            {
                using var body = System.Text.Json.JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
                submittedPrincipal = body.RootElement.GetProperty("servicePrincipal").GetString();
                submittedFingerprint = body.RootElement.GetProperty("registrationFingerprint").GetString();
                return Json(new { requestId = "host", userCode = "HOST-CODE", deviceCapability = "host-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
            }
            if (route.EndsWith("poll")) return Json(new { status = "approved", approvalId = "approved", registrationFingerprint = submittedFingerprint });
            if (route.EndsWith("cancel")) return Json(new { status = "cancelled" });
            throw new InvalidOperationException("Unexpected hosting request");
        });
        var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var model = new HostingSetupViewModel(new Dictionary<string, string> { ["--output-directory"] = output, ["--instance"] = "https://instance.example/mcp" }, handler);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input); builder.Services.AddTermina("/hosting", routes => routes.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", services => new HostingSetupPage(services.GetRequiredService<Termina.Clipboard.IClipboardService>()), _ => model)); builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        var reviewed = WaitFor(model.Revision, () => model.Step == HostingSetupStep.Review && !model.Busy);
        input.EnqueueKey(ConsoleKey.Enter); await reviewed;
        Assert.Equal(adoptOutbound ? "existing-service" : "netratel-mcp-http", submittedPrincipal);
        Assert.Equal(savedFingerprint, submittedFingerprint);
        Assert.DoesNotContain(model.Preview!.Changes, change => change.StartsWith("Replace ", StringComparison.Ordinal));
        Assert.All(originalHashes, original => Assert.Equal(original.Value, ConfigurationFileStorage.Hash(original.Key)));
        Assert.Null(model.Result); Assert.False(model.Verified);
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync(); await model.CleanupAsync();
    }

    [Fact]
    public async Task Lost_acknowledgement_can_retry_saved_delivery_without_overwriting_the_profile()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "test.json"); var acknowledgements = 0;
        var handler = new StubHandler(request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities")) return Task.FromResult(Capabilities());
            if (route.EndsWith("start")) return Task.FromResult(Json(new { requestId = "one", userCode = "CODE", deviceCapability = "capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 }));
            if (route.EndsWith("poll")) return Task.FromResult(Json(new { status = "issued", intervalSeconds = 5, delivery = new { credentialId = "42", secret = "nrt_ic_test-secret", name = "test", purpose = 0, resource = (string?)null, expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1), grants = new[] { new { tenantId = 7, permission = "telemetry.read" } }, instancePermissions = Array.Empty<string>() } }));
            if (++acknowledgements == 1) throw new HttpRequestException("Lost acknowledgement response");
            return Task.FromResult(Json(new { status = "consumed" }));
        });
        await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", handler);
        await session.DiscoverAsync("https://instance.example", default); await session.BeginPairingAsync(default); await session.PollAsync(default); session.Review();
        await Assert.ThrowsAsync<HttpRequestException>(() => session.SaveAsync(false, default));
        Assert.True(session.Persisted); Assert.False(session.Saved); var hash = ConfigurationFileStorage.Hash(path);
        await session.SaveAsync(false, default); Assert.True(session.Saved); Assert.Equal(hash, ConfigurationFileStorage.Hash(path)); Assert.Equal(2, acknowledgements);
    }
    [Fact]
    public async Task Virtual_hosting_input_starts_with_only_focused_url_and_enters_real_discovery()
    {
        using var directory = new TempDirectory(); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        string? requested = null;
        var model = new HostingSetupViewModel(new Dictionary<string, string> { ["--deployment-manager"] = "release-compose", ["--output-directory"] = directory.Path }, new StubHandler(request =>
        { requested = request.RequestUri!.AbsoluteUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input);
        builder.Services.AddTermina("/hosting", routes => routes.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", services => new HostingSetupPage(services.GetRequiredService<Termina.Clipboard.IClipboardService>()), _ => model));
        builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        var handled = WaitFor(model.Revision, () => !model.Busy && model.Error.Value.Contains("compatible"));
        input.EnqueueString("https://instance.example/mcp/"); input.EnqueueKey(ConsoleKey.Enter); await handled;
        Assert.Equal("https://instance.example/api/http-host-setup/v1/capabilities", requested);
        Assert.Equal("https://instance.example/mcp/", model.PublicOrigin); Assert.Null(model.Result);
        var visible = false; await model.InvokeAsync(() => visible = terminal.Contains("Instance Web URL") && !terminal.Contains("Persistent output directory") && !terminal.Contains("Selected API")); Assert.True(visible);
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync(); await model.CleanupAsync();
    }
    [Fact]
    public async Task Virtual_input_keeps_read_only_instance_fields_at_their_managed_source()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "readonly.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example", IntegrationCredential = "nrt_ic_test-secret", ProfileName = "test", Instance = "prod" }, ConfigurationSurface.Cli);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", new StubHandler(_ => Task.FromResult(Capabilities())));
            var model = new SetupViewModel(session); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
            builder.Services.AddTerminaVirtualInput(input); builder.Services.AddTermina("/setup", routes => routes.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(), _ => model)); builder.Services.AddSingleton<IAnsiTerminal>(terminal);
            using var host = builder.Build(); await host.StartAsync();
            Assert.Equal(SetupStep.Instance, model.Step);
            Assert.True(session.IsExternallyManaged("apiBaseUrl"));
            var revision = model.Revision.Value + 1; var handled = WaitFor(model, () => model.Revision.Value >= revision);
            input.EnqueueString("https://attacker.example"); input.EnqueueKey(ConsoleKey.UpArrow); input.EnqueueKey(ConsoleKey.F6); await handled;
            Assert.Equal("https://instance.example", model.Origin);
            var visible = false; await model.InvokeAsync(() => visible = terminal.Contains("read-only")); Assert.True(visible);
            input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync();
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
    [Fact]
    public async Task Cancellation_after_durable_save_does_not_mark_unexecuted_doctor_as_complete()
    {
        using var directory = new TempDirectory(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, System.IO.Path.Combine(directory.Path, "test.json"), "test", new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("capabilities")) return Task.FromResult(Capabilities());
            started.TrySetResult(); return response.Task;
        }));
        using var model = new SetupViewModel(session);
        model.Origin = "https://instance.example"; await model.NextAsync(); model.Authentication = "Existing credential"; model.ManualSecret = "nrt_ic_test-secret"; await model.NextAsync();
        var save = model.NextAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(session.Saved);
        model.RequestShutdown(); response.SetResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); await save;
        Assert.True(model.Cancelled); Assert.Equal(SetupStep.Review, model.Step); Assert.Empty(model.Checks); Assert.True(File.Exists(session.Path));
    }
    [Fact]
    public void Terminal_text_removes_control_sequences_and_direction_overrides()
    {
        var sanitized = TerminalText.Safe("profile\u001b[31m\n\u202eevil");
        Assert.DoesNotContain('\u001b', sanitized); Assert.DoesNotContain('\n', sanitized); Assert.DoesNotContain('\u202e', sanitized);
    }
    [Fact]
    public async Task Back_from_review_preserves_approved_delivery_until_atomic_save_and_acknowledgement()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "test.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example", IntegrationCredential = "nrt_ic_previous-consumer", ProfileName = "test", Instance = "prod" }, ConfigurationSurface.Cli);
        var cancellations = 0; var acknowledgements = 0;
        await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", BrowserHandler(() => cancellations++, () => acknowledgements++));
        using var model = new SetupViewModel(session);
        model.Origin = "https://instance.example"; model.Authentication = "Browser approval"; await model.NextAsync(); await model.NextAsync();
        Assert.Equal(SetupStep.Review, model.Step); var delivery = session.Delivery;
        Assert.Equal("7", session.Configuration.DefaultTenantId);
        await model.BackAsync(); Assert.Equal(SetupStep.Authentication, model.Step); Assert.Same(delivery, session.Delivery); Assert.NotNull(session.Pairing); Assert.Equal("7", session.Configuration.DefaultTenantId);
        await model.NextAsync(); Assert.Equal(SetupStep.Review, model.Step);
        await session.SaveAsync(replace: true, default);
        Assert.True(session.Saved); Assert.Equal(0, cancellations); Assert.Equal(1, acknowledgements);
        var saved = new NetRatelConfigurationService().Load(new(ConfigurationSurface.Cli, path, "test", GetEnvironmentVariable: _ => null));
        Assert.Equal("nrt_ic_new-delivery", saved.Configuration.IntegrationCredential);
    }
    [Fact]
    public async Task Cancelling_issued_pairing_discards_its_secret_and_preserves_the_previous_profile()
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "test.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example", IntegrationCredential = "nrt_ic_previous-consumer", ProfileName = "test", Instance = "prod" }, ConfigurationSurface.Cli);
        var original = ConfigurationFileStorage.Hash(path); var cancellations = 0; var acknowledgements = 0;
        await using var session = new SetupSession(ConfigurationSurface.Cli, path, "test", BrowserHandler(() => cancellations++, () => acknowledgements++));
        await session.DiscoverAsync("https://instance.example", default); await session.BeginPairingAsync(default); await session.PollAsync(default);
        Assert.Equal("nrt_ic_new-delivery", session.Configuration.IntegrationCredential);
        await session.CancelPairingAsync();
        Assert.Null(session.Pairing); Assert.Null(session.Delivery); Assert.False(session.Persisted);
        Assert.Equal("nrt_ic_previous-consumer", session.Configuration.IntegrationCredential);
        Assert.Equal(original, ConfigurationFileStorage.Hash(path)); Assert.Equal(1, cancellations); Assert.Equal(0, acknowledgements);
    }
    [Fact]
    public async Task Host_rerun_uses_current_discovered_service_audience_after_review_without_rotating_identity()
    {
        using var directory = new TempDirectory();
        var output = System.IO.Path.Combine(directory.Path, "setup");
        var original = new HttpHostingSetupRequest(output, "https://instance.example", "https://instance.example")
        { ApiServiceAudience = "old.api", ApiServiceAuthority = "https://old-authority.example", ExistingM2MCallerClientIds = [] };
        original = HttpHostingDeploymentFixture.WithSource(original);
        new NetRatelHttpHostingSetupService().Apply(original);
        var protectedHashes = Directory.EnumerateFiles(System.IO.Path.Combine(output, "secrets"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(System.IO.Path.Combine(output, "runtime"), "*", SearchOption.AllDirectories))
            .ToDictionary(path => path, ConfigurationFileStorage.Hash);
        string? fingerprint = null;
        var handler = new StubHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("capabilities")) return Json(new
            {
                protocolVersion = 1, publicOrigin = "https://instance.example", resource = "https://instance.example/mcp", verificationUri = "https://instance.example/account/integration-credentials", localCredentialMode = true,
                requiredGroups = Array.Empty<string>(), requiredScopes = Array.Empty<string>(), existingHostConfigured = true,
                apiServiceAudience = "current.api", apiServiceAuthority = "https://current-authority.example", existingM2MCallerClientIds = new[] { "netratel-mcp-http" }
            });
            if (route.EndsWith("start"))
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); fingerprint = body.RootElement.GetProperty("registrationFingerprint").GetString();
                return Json(new { requestId = "host", userCode = "HOST-CODE", deviceCapability = "host-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
            }
            if (route.EndsWith("poll")) return Json(new { status = "approved", approvalId = "approved", registrationFingerprint = fingerprint });
            return Json(new { status = "cancelled" });
        });
        using var model = new HostingSetupViewModel(new Dictionary<string, string> { ["--output-directory"] = output, ["--instance"] = "https://instance.example" }, handler);
        try
        {
            await model.NextAsync(); Assert.Equal(HostingSetupStep.Review, model.Step);
            Assert.Contains(model.Preview!.Changes, change => change.StartsWith("Replace ", StringComparison.Ordinal));
            await model.NextAsync(); Assert.True(model.ConfirmReplace); Assert.Null(model.Result);
            await model.NextAsync(replace: true); Assert.Equal(HostingSetupStep.Apply, model.Step); Assert.NotNull(model.Result); Assert.False(model.Verified);
            var api = File.ReadAllText(System.IO.Path.Combine(output, "api.env")); var mcp = File.ReadAllText(System.IO.Path.Combine(output, "mcp-http.env"));
            Assert.Contains("M2MClients__netratel_mcp_http__AllowedAudiences__0='current.api'", api);
            Assert.Contains("M2MClients__netratel_mcp_http__AllowedScopes__0='current.api'", api);
            Assert.Contains("ApiM2MScope='current.api'", mcp); Assert.DoesNotContain("M2M__Authority=", api); Assert.DoesNotContain("M2M__Audience=", api);
            Assert.Equal("current.api", NetRatelHttpHostingSetupService.LoadRequest(output)!.ApiServiceAudience);
            Assert.Equal("https://current-authority.example", NetRatelHttpHostingSetupService.LoadRequest(output)!.ApiServiceAuthority);
            Assert.All(protectedHashes, entry => Assert.Equal(entry.Value, ConfigurationFileStorage.Hash(entry.Key)));
        }
        finally { await model.CleanupAsync(); }
    }

    [Fact]
    public async Task Virtual_http_client_approval_saves_and_reloads_the_default_profile_in_personal_persistent_storage()
    {
        using var directory = new TempDirectory();
        var clientRoot = System.IO.Path.Combine(directory.Path, "personal-client-state");
        var hostRoot = System.IO.Path.Combine(directory.Path, "separate-host-state");
        string? EnvironmentValue(string key) => key switch
        {
            "NETRATEL_CLIENT_DIRECTORY" => clientRoot,
            "NETRATEL_SETUP_DIRECTORY" => hostRoot,
            _ => null
        };
        var acknowledged = 0;
        var handler = new StubHandler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            Assert.DoesNotContain("http-host-setup", route);
            if (route.EndsWith("capabilities")) return Capabilities();
            if (route.EndsWith("start"))
            {
                using var document = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(1, document.RootElement.GetProperty("purpose").GetInt32());
                return Json(new { requestId = "personal-client", userCode = "CLIENT-CODE", deviceCapability = "personal-client-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
            }
            if (route.EndsWith("poll")) return Json(new { status = "issued", intervalSeconds = 5, delivery = new { credentialId = "personal-credential", secret = "nrt_ic_personal-client-only", name = "default", purpose = 1, resource = "https://instance.example/mcp", expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1), grants = Array.Empty<object>(), instancePermissions = new[] { "mcp.discovery.read" } } });
            if (route.EndsWith("acknowledge")) { acknowledged++; return Json(new { status = "consumed" }); }
            Assert.Equal("nrt_ic_personal-client-only", request.Headers.Authorization?.Parameter);
            return await McpReply(request);
        });
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, handler: handler, getEnvironmentVariable: EnvironmentValue);
        var expected = System.IO.Path.Combine(clientRoot, "profiles", "httpmcpclient", "default.json");
        Assert.Equal(expected, session.Path); Assert.Equal("default", session.ProfileName);
        var model = new SetupViewModel(session); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input); builder.Services.AddTermina("/setup", routes => routes.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(), _ => model)); builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using (var host = builder.Build())
        {
            await host.StartAsync();
            var authentication = WaitFor(model, () => model.Step == SetupStep.Authentication && !model.Busy);
            input.EnqueueString("https://instance.example/mcp/"); input.EnqueueKey(ConsoleKey.Enter); await authentication;
            var review = WaitFor(model, () => model.Step == SetupStep.Review && !model.Busy);
            input.EnqueueKey(ConsoleKey.Enter); await review;
            var finished = WaitFor(model, () => model.Step == SetupStep.Finish && !model.Busy);
            input.EnqueueKey(ConsoleKey.Enter); await finished;
            Assert.True(session.Saved); Assert.Equal(1, acknowledged); Assert.All(model.Checks, check => Assert.Equal("completed", check.Status));
            Assert.False(terminal.Contains("nrt_ic_personal-client-only"));
            await model.InvokeAsync(() => Console.WriteLine("Rendered personal HTTP client saved and verified screen:\n" + terminal));
            input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync();
        }
        var persisted = NetRatelConfigurationService.ReadConfiguration(expected);
        Assert.Equal("HttpMcp", persisted.CredentialPurpose); Assert.Null(persisted.IntegrationCredential); Assert.NotNull(persisted.IntegrationCredentialFile);
        Assert.StartsWith(clientRoot, persisted.IntegrationCredentialFile); Assert.Equal("nrt_ic_personal-client-only", ConfigurationFileStorage.ReadText(persisted.IntegrationCredentialFile, secret: true).Trim());
        Assert.False(Directory.Exists(hostRoot));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(expected));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(persisted.IntegrationCredentialFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(System.IO.Path.GetDirectoryName(expected)!));
        }
        var before = ConfigurationFileStorage.Hash(expected); var secrets = Directory.EnumerateFiles(clientRoot, "*.secret", SearchOption.AllDirectories).ToArray();
        await using var reloaded = new SetupSession(ConfigurationSurface.HttpMcpClient, handler: handler, getEnvironmentVariable: EnvironmentValue);
        Assert.Equal(expected, reloaded.Path); Assert.True(reloaded.HasConfiguredAuthentication); Assert.Equal("nrt_ic_personal-client-only", reloaded.Configuration.IntegrationCredential);
        await reloaded.DiscoverAsync("https://instance.example", default); reloaded.UseExternalConfiguration(); reloaded.Review(); await reloaded.SaveAsync(false, default);
        Assert.Equal(before, ConfigurationFileStorage.Hash(expected)); Assert.Equal(secrets, Directory.EnumerateFiles(clientRoot, "*.secret", SearchOption.AllDirectories).ToArray()); Assert.Equal(1, acknowledged);
        Console.WriteLine("Personal HTTP client default profile reloaded and reused without replacing its protected credential: " + expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Http_doctor_accepts_the_actual_production_tool_catalog_and_bounds_oversized_responses(bool eventStream, bool oversized)
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "personal.json");
        new NetRatelConfigurationService().SaveProfile(path, AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", IntegrationCredential = "nrt_ic_production-catalog", Instance = "prod" }, ConfigurationSurface.HttpMcpClient);
        var tools = NetRatel.Mcp.Core.NetRatelMcpToolDefinitions.CreateForHttp("prod").Select(tool => tool.ProtocolTool).ToArray();
        var catalog = System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, result = new { tools } }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Console.WriteLine($"Production HTTP catalog: {tools.Length} tools, {catalog.Length} characters, {System.Text.Encoding.UTF8.GetByteCount(catalog)} UTF-8 bytes.");
        var calls = new List<string>(); ResponseProbeStream? responseStream = null;
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", new StubHandler(async request =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var method = body.RootElement.GetProperty("method").GetString()!; calls.Add(method);
            if (method != "tools/list") return await McpReply(request);
            var payload = oversized ? catalog + new string(' ', 1024 * 1024) : catalog;
            responseStream = new ResponseProbeStream(System.Text.Encoding.UTF8.GetBytes(eventStream ? "event: message\ndata: " + payload + "\n\n" : payload));
            var content = new StreamContent(responseStream); content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(eventStream ? "text/event-stream" : "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var checks = await session.DoctorAsync(default);
        if (oversized)
        {
            Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list" }, calls);
            Assert.Contains(checks, check => check.Name == "transport" && check.Status == "unavailable");
            Assert.NotNull(responseStream); Assert.True(responseStream.Consumed < responseStream.TotalBytes, "Reject oversized responses before buffering their complete JSON or SSE data line.");
            Assert.DoesNotContain(checks, check => check.Name == "permitted read-only tool" && check.Status == "completed");
        }
        else
        {
            Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" }, calls);
            Assert.All(checks, check => Assert.Equal("completed", check.Status));
            Assert.Contains(checks, check => check.Name == "permitted read-only tool");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_external_authentication_edits_guided_values_and_preserves_managed_sources(bool managed)
    {
        using var directory = new TempDirectory(); var path = System.IO.Path.Combine(directory.Path, "existing.json");
        var original = managed
            ? AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "Oidc", OidcTokenUrl = "https://managed-provider.example/token", OidcClientId = "managed-app", OidcUsername = "operator", OidcAppPassword = "protected-old-provider-password", Instance = "prod" }
            : AgentClientConfiguration.Empty with { ApiBaseUrl = "https://instance.example/mcp", AuthenticationMode = "IntegrationCredential", IntegrationCredential = "nrt_ic_existing-personal", Instance = "prod" };
        new NetRatelConfigurationService().SaveProfile(path, original, ConfigurationSurface.HttpMcpClient);
        string? EnvironmentValue(string key) => managed && key == "NETRATEL_MCP_CLIENT_OIDC_TOKEN_URL" ? "https://managed-provider.example/token" : null;
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", new StubHandler(_ => Task.FromResult(Capabilities())), EnvironmentValue);
        var model = new SetupViewModel(session); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input); builder.Services.AddTermina("/setup", routes => routes.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(), _ => model)); builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        await model.NextAsync(); await model.NextAsync(); Assert.Equal(SetupStep.Review, model.Step); Assert.True(session.ReusesExistingConfiguration);
        await model.BackAsync(); await model.SetAuthenticationAsync("External authentication");
        var visible = false; await model.InvokeAsync(() => visible = terminal.Contains("Provider HTTPS token endpoint")); Assert.True(visible);
        var hash = ConfigurationFileStorage.Hash(path);
        model.ExternalTokenUrl = "https://new-provider.example/oauth/token?tenant=ops"; model.ExternalClientId = "new-personal-app"; model.ExternalUsername = "new-operator"; model.ExternalPassword = "protected-new-provider-password";
        await model.NextAsync();
        if (managed)
        {
            Assert.Equal(SetupStep.Authentication, model.Step); Assert.Contains("externally managed", model.Status.Value);
            Assert.Equal("https://managed-provider.example/token", session.Configuration.OidcTokenUrl);
        }
        else
        {
            Assert.Equal(SetupStep.Review, model.Step); Assert.False(session.ReusesExistingConfiguration); Assert.Null(session.Configuration.IntegrationCredential);
            Assert.Equal("https://new-provider.example/oauth/token?tenant=ops", session.Configuration.OidcTokenUrl); Assert.Equal("new-personal-app", session.Configuration.OidcClientId);
            await model.NextAsync(); Assert.True(model.ConfirmReplace); Assert.False(session.Saved);
        }
        Assert.Equal(hash, ConfigurationFileStorage.Hash(path)); Assert.False(terminal.Contains("protected-new-provider-password"));
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync();
    }

    private static string AppliedRuntimeFixture(string output)
    {
        // Simulate a deployment owner applying the exported env files. This fixture
        // reads generated deployment inputs, never the verifier's expected manifest.
        var services = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var service in new[] { "web", "api", "mcp-http" })
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(System.IO.Path.Combine(output, service + ".env")))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var separator = line.IndexOf('='); Assert.True(separator > 0);
                var value = line[(separator + 1)..];
                Assert.StartsWith("'", value); Assert.EndsWith("'", value);
                value = value[1..^1]; Assert.DoesNotContain("'", value);
                values.Add(line[..separator], value);
            }
            services.Add(service, values);
        }
        services["api"]["DOTNET_ENVIRONMENT"] = "Production"; // Explicit original fixture provider.
        Assert.Equal("LocalCredential", services["api"]["Integration__HttpMcp__AuthenticationMode"]);
        Assert.Equal("netratel-mcp-http", services["mcp-http"]["NetRatel__Mcp__Delegation__ServicePrincipal"]);
        Assert.Equal("https://instance.example/mcp", services["mcp-http"]["NetRatel__Mcp__Http__PublicResourceUri"]);
        var runtime = services.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
        runtime["x-netratel-protected-configuration"] = services.Keys.ToDictionary(service => service, _ => new Dictionary<string, string?>());
        runtime["x-netratel-runtime-images"] = new Dictionary<string, string>();
        var path = System.IO.Path.Combine(output, ".applied-runtime-fixture.json");
        ConfigurationFileStorage.WriteAtomic(path, System.Text.Json.JsonSerializer.Serialize(runtime));
        return path;
    }

    private static async Task<HttpResponseMessage> McpReply(HttpRequestMessage request)
    {
        using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        return body.RootElement.GetProperty("method").GetString() switch
        {
            "initialize" => Json(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-03-26", capabilities = new { } } }),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            "tools/list" => Json(new { jsonrpc = "2.0", id = 2, result = new { tools = new[] { new { name = "netratel_auth" } } } }),
            "tools/call" => Json(new { jsonrpc = "2.0", id = 3, result = new { isError = false, structuredContent = new { success = true, data = new { authenticated = true, authMode = "IntegrationCredential", groups = Array.Empty<string>(), roles = new[] { "operator" } } } } }),
            _ => throw new InvalidOperationException("Unexpected MCP operation")
        };
    }
    private static StubHandler BrowserHandler(Action cancel, Action acknowledge) => new(async request =>
    {
        var route = request.RequestUri!.AbsolutePath;
        if (route.EndsWith("capabilities")) return Capabilities();
        if (route.EndsWith("start")) return Json(new { requestId = "request", userCode = "CODE", deviceCapability = "request-private-capability", verificationUri = "https://instance.example/account/integration-credentials", publicOrigin = "https://instance.example", expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), intervalSeconds = 5 });
        if (route.EndsWith("poll")) return Json(new { status = "issued", intervalSeconds = 5, delivery = new { credentialId = "42", secret = "nrt_ic_new-delivery", name = "test", purpose = 0, resource = (string?)null, expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1), grants = new[] { new { tenantId = 7, permission = "telemetry.read" } }, instancePermissions = Array.Empty<string>() } });
        using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("request-private-capability", body.RootElement.GetProperty("deviceCapability").GetString());
        Assert.Null(request.Headers.Authorization);
        if (route.EndsWith("cancel")) { cancel(); return Json(new { status = "cancelled" }); }
        if (route.EndsWith("acknowledge")) { acknowledge(); return Json(new { status = "consumed" }); }
        throw new InvalidOperationException("Unexpected pairing request.");
    });
    private static Task WaitFor(SetupViewModel model, Func<bool> predicate) => WaitFor(model.Revision, predicate);
    private static Task WaitFor(Observable<int> revision, Func<bool> predicate)
    {
        if (predicate()) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        subscription = revision.Subscribe(_ => { if (predicate()) completion.TrySetResult(); });
        return Finish();
        async Task Finish() { try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); } finally { subscription.Dispose(); } }
    }
    private static HttpResponseMessage Capabilities() => Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", pairingSupported = true, verificationUri = "https://instance.example/account/integration-credentials", httpMcpResource = "https://instance.example/mcp", configurationCapabilities = new[] { NetRatel.Shared.Connectivity.IntegrationPairingProtocol.TemporaryVerifierCapability } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class ResponseProbeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int TotalBytes { get; } = bytes.Length;
        public long Consumed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing && CanRead) Consumed = Position; base.Dispose(disposing); }
    }
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request); }
    private sealed class TempDirectory : IDisposable
    { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netratel-setup-" + Guid.NewGuid().ToString("N")); public TempDirectory() { if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Path); else Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } public void Dispose() => Directory.Delete(Path, true); }
}
