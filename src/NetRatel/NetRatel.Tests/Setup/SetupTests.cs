using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.AgentClient;
using NetRatel.Setup;
using R3;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace NetRatel.Tests.Setup;

public sealed class SetupTests
{
    [Theory]
    [InlineData("http://instance.example")]
    [InlineData("https://instance.example/mcp")]
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
        await model.NextAsync(); Assert.Equal(ConfigurationSurface.HttpMcpClient, session.Surface); Assert.Equal(SetupStep.Instance, model.Step);
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
        await model.NextAsync(); model.Origin = "https://instance.example";
        var pending = model.NextAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await model.BackAsync(); response.SetResult(Capabilities()); await pending;
        Assert.Equal(SetupStep.Profile, model.Step); Assert.Null(session.Capabilities); Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task Virtual_input_navigates_and_resizes_without_rendering_manual_secret()
    {
        using var directory = new TempDirectory();
        await using var session = new SetupSession(ConfigurationSurface.Cli, System.IO.Path.Combine(directory.Path, "test.json"), "test", new StubHandler(_ => Task.FromResult(Capabilities())));
        var model = new SetupViewModel(session); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input);
        builder.Services.AddTermina("/setup", routes => routes.RegisterRoute<SetupPage, SetupViewModel>("/setup", _ => new SetupPage(), _ => model));
        builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        using var host = builder.Build(); await host.StartAsync();
        var instance = WaitFor(model, () => model.Step == SetupStep.Instance && !model.Busy);
        input.EnqueueKey(ConsoleKey.F2); await instance;
        var authentication = WaitFor(model, () => model.Step == SetupStep.Authentication && !model.Busy);
        input.EnqueueString("https://instance.example"); input.EnqueueKey(ConsoleKey.F2); await authentication;
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
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var method = body.RootElement.GetProperty("method").GetString()!; calls.Add(method);
            if (!authorized) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return method switch
            {
                "initialize" => Json(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-03-26", capabilities = new { } } }),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Json(new { jsonrpc = "2.0", id = 2, result = new { tools = new[] { new { name = "netratel_auth" } } } }),
                "tools/call" => Json(new { jsonrpc = "2.0", id = 3, result = new { isError = false, structuredContent = new { success = true } } }),
                _ => throw new InvalidOperationException("Unexpected MCP operation")
            };
        });
        await using var session = new SetupSession(ConfigurationSurface.HttpMcpClient, path, "test", handler);
        var checks = await session.DoctorAsync(default);
        Assert.Contains(checks, check => check.Name == "authentication" && check.Status == (authorized ? "completed" : "unauthorized"));
        if (authorized) { Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" }, calls); Assert.Contains(checks, check => check.Name == "permitted read-only tool" && check.Status == "completed"); }
        else { Assert.Single(calls); Assert.DoesNotContain(checks, check => check.Name == "MCP initialize" && check.Status == "completed"); }
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
    public async Task Virtual_hosting_input_scrolls_advanced_controls_and_review_at_80_by_24()
    {
        using var directory = new TempDirectory(); var input = new VirtualInputSource(); var terminal = new VirtualTerminal(80, 24);
        var model = new HostingSetupViewModel { Advanced = true };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true }); builder.Logging.ClearProviders();
        builder.Services.AddTerminaVirtualInput(input);
        builder.Services.AddTermina("/hosting", routes => routes.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", services => new HostingSetupPage(services.GetRequiredService<Termina.Clipboard.IClipboardService>()), _ => model));
        builder.Services.AddSingleton<IAnsiTerminal>(terminal);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var errors = model.Error.Subscribe(error => { if (error.Length > 0) handled.TrySetResult(); });
        for (var index = 0; index < 16; index++) input.EnqueueKey(ConsoleKey.Tab);
        input.EnqueueKey(ConsoleKey.End); input.EnqueueString("focused"); input.EnqueueKey(ConsoleKey.F2);
        using var host = builder.Build(); await host.StartAsync(); await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.EndsWith("focused", model.McpBackend);
        var visible = false; await model.InvokeAsync(() => visible = terminal.Contains("Web MCP backend URL")); Assert.True(visible);
        await model.InvokeAsync(() => { model.OutputDirectory = directory.Path; model.PublicOrigin = "https://instance.example"; model.ApiBaseUrl = "https://api.example"; model.McpBackend = "http://mcp-http:9224/"; model.Advanced = false; model.Revision.Value++; });
        var reviewed = WaitFor(model.Revision, () => model.Preview is not null); input.EnqueueKey(ConsoleKey.F2); await reviewed;
        var revision = model.Revision.Value + 1; var scrolled = WaitFor(model.Revision, () => model.Revision.Value >= revision);
        input.EnqueueKey(ConsoleKey.PageDown); input.EnqueueKey(ConsoleKey.End, control: true); input.EnqueueKey(ConsoleKey.F6); await scrolled;
        await model.InvokeAsync(() => visible = terminal.Contains("F2 writes persistent")); Assert.True(visible);
        input.EnqueueKey(ConsoleKey.Escape); input.Complete(); await host.StopAsync(); Assert.Null(model.Result);
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
            var entered = WaitFor(model, () => model.Step == SetupStep.Instance && !model.Busy); input.EnqueueKey(ConsoleKey.F2); await entered;
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
        await model.NextAsync(); model.Origin = "https://instance.example"; await model.NextAsync(); model.Authentication = "Existing credential"; model.ManualSecret = "nrt_ic_test-secret"; await model.NextAsync();
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
        await model.NextAsync(); model.Origin = "https://instance.example"; await model.NextAsync(); await model.NextAsync();
        Assert.Equal(SetupStep.Grants, model.Step); session.SelectTenant(7); var delivery = session.Delivery;
        await model.NextAsync(); Assert.Equal(SetupStep.Review, model.Step);
        await model.BackAsync(); Assert.Equal(SetupStep.Grants, model.Step); Assert.Same(delivery, session.Delivery); Assert.NotNull(session.Pairing); Assert.Equal("7", session.Configuration.DefaultTenantId);
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
    private static HttpResponseMessage Capabilities() => Json(new { protocolVersion = 1, publicOrigin = "https://instance.example", pairingSupported = true, verificationUri = "https://instance.example/account/integration-credentials", httpMcpResource = "https://instance.example/mcp" });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request); }
    private sealed class TempDirectory : IDisposable
    { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netratel-setup-" + Guid.NewGuid().ToString("N")); public TempDirectory() => Directory.CreateDirectory(Path); public void Dispose() => Directory.Delete(Path, true); }
}
