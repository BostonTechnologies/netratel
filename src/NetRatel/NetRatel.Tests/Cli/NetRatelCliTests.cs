using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Cli;

public sealed class NetRatelCliTests
{
    [Fact]
    public void CliConfig_Uses_Public_Environment_Names_And_Preserves_Legacy_Aliases()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CliConfig.ApiBaseUrlEnvironmentVariable] = "https://public.example.test",
            [CliConfig.OidcTokenUrlEnvironmentVariable] = "https://issuer.example.test/token",
            [CliConfig.OidcClientIdEnvironmentVariable] = "public-cli",
            [CliConfig.OidcUsernameEnvironmentVariable] = "operator@example.test",
            [CliConfig.OidcAppPasswordEnvironmentVariable] = "synthetic-password",
            [CliConfig.OidcScopeEnvironmentVariable] = "netratel.api",
            ["BT_NetRatel_API_BASE_URL"] = "https://legacy.example.test",
            ["BT_OIDC_CLIENT_ID"] = "legacy-cli"
        };

        var config = CliConfig.FromEnvironment(name => variables.GetValueOrDefault(name));

        config.Resolve().Should().BeEquivalentTo(new ResolvedCliConfig(
            new Uri("https://public.example.test"),
            "https://issuer.example.test/token",
            "public-cli",
            "operator@example.test",
            "synthetic-password",
            "netratel.api"));
    }

    [Fact]
    public void CliConfig_Uses_Legacy_Environment_Aliases_When_Public_Names_Are_Absent()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["BT_NetRatel_API_BASE_URL"] = "https://legacy.example.test",
            ["BT_OIDC_TOKEN_URL"] = "https://legacy-issuer.example.test/token",
            ["BT_OIDC_CLIENT_ID"] = "legacy-cli",
            ["BT_OIDC_USERNAME"] = "operator@example.test",
            ["BT_OIDC_APP_PASSWORD"] = "synthetic-password",
            ["BT_OIDC_SCOPE"] = "netratel.api"
        };

        var config = CliConfig.FromEnvironment(name => variables.GetValueOrDefault(name));

        config.Resolve().OidcClientId.Should().Be("legacy-cli");
        config.Resolve().ApiBaseUrl.Should().Be(new Uri("https://legacy.example.test"));
    }

    [Fact]
    public void RootCommand_Exposes_OperatorSurface()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var commandNames = root.Children.OfType<System.CommandLine.Command>().Select(c => c.Name).ToArray();

        commandNames.Should().Contain([
            "auth",
            "config",
            "health",
            "logs",
            "tenants",
            "scripts",
            "jobs",
            "job-runs",
            "tasks",
            "clients",
            "client-files",
            "files-v2",
            "commands",
            "scripts-v2",
            "tasks-v2",
            "requests-v2",
            "tenants-v2",
            "onboarding-v2",
            "clients-v2",
            "notifications-v2",
            "events-v2",
            "connectivity-v2",
            "terminal-v2",
            "terminal",
            "requests",
            "secrets",
            "search",
            "telemetry",
            "connectivity",
            "notifications",
            "events",
            "system",
            "raw"
        ]);
    }

    [Fact]
    public void TaskCommand_Exposes_Only_SourceBacked_V2_Reads()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var tasks = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "tasks");

        tasks.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "result", "recent", "get", "logs", "logs-by-request"]);
    }

    [Fact]
    public void ScriptsCommand_Exposes_Only_SourceBacked_Reads()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var scripts = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "scripts");

        scripts.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "get", "params"]);
    }

    [Fact]
    public void ScriptsV2Command_Exposes_The_Owned_Preview_And_Confirmation_Lifecycle()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var scripts = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "scripts-v2");

        scripts.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "get", "params", "validate", "preview-create", "create", "preview-update", "update", "preview-parse-manifest", "parse-manifest", "preview-delete", "delete", "preview-run", "run"]);
    }

    [Fact]
    public void TasksV2Command_Exposes_Only_The_Owned_Preview_Confirmation_And_Result_Lifecycle()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var tasks = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "tasks-v2");

        tasks.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "recent", "get", "logs", "logs-by-request", "preview-create-command", "create-command", "preview-run-library-script", "run-library-script", "preview-cancel", "cancel"]);
    }

    [Fact]
    public void ClientsV2Command_Exposes_Only_The_PolicyAdmitted_Read_And_Lifecycle_Surface()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var clients = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "clients-v2");

        clients.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["get", "presence", "capabilities", "binding", "telemetry", "update-attempts", "update-metadata", "preview-ping", "confirm-ping", "preview-software-update", "confirm-software-update", "preview-disable", "confirm-disable", "preview-enable", "confirm-enable", "preview-delete", "confirm-delete"]);
    }

    [Fact]
    public void FilesV2Command_Exposes_Only_The_Bounded_Owned_Read_Artifact_And_PreviewConfirmation_Surface()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var files = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "files-v2");

        files.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            [
                "browse", "stat", "read",
                "preview-collect-artifact", "confirm-collect-artifact", "artifact-status", "download-artifact",
                "preview-cleanup-artifact", "confirm-cleanup-artifact",
                "preview-write-text", "confirm-write-text", "preview-upload", "confirm-upload",
                "preview-create-directory", "confirm-create-directory", "preview-delete", "confirm-delete",
                "preview-copy", "confirm-copy", "preview-move", "confirm-move"
            ]);
    }

    [Fact]
    public async Task FilesV2PreviewWriteText_Uses_The_Exact_Targeted_Route_Without_Printing_Content()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string content = "replace secret file value";
        string? posted = null;
        var output = new StringWriter();

        var code = await RunCliAsync([
            "files-v2", "preview-write-text",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--path", "C:\\netratel\\review.txt",
            "--text", content
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/mcp/operator/agents/7/{agentId:D}/files/write-text/preview");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"planToken":"opaque-plan","idempotencyKey":"opaque-key","payloadSha256":"abc"}""");
        }, output);

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("path").GetString().Should().Be("C:\\netratel\\review.txt");
        body.RootElement.GetProperty("text").GetString().Should().Be(content);
        output.ToString().Should().NotContain(content);
    }

    [Fact]
    public async Task FilesV2ConfirmCopy_Requires_Confirmation_Before_Acquiring_A_Token_Or_Calling_The_Api()
    {
        var calls = 0;
        var output = new StringWriter();

        var code = await NetRatelCli.RunAsync([
            "files-v2", "confirm-copy",
            "--tenant-id", "7",
            "--agent-id", "11111111-2222-3333-4444-555555555555",
            "--source-path", "C:\\netratel\\review.txt",
            "--destination-path", "C:\\netratel\\published.txt",
            "--plan-token", "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            "--idempotency-key", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        ], new CliRuntime(() =>
        {
            calls++;
            throw new InvalidOperationException("The unconfirmed file mutation must not call the API.");
        })
        { Out = output, Error = new StringWriter() });

        code.Should().Be(CliExitCodes.Success);
        calls.Should().Be(0);
        output.ToString().Should().Contain("confirmationRequired");
    }

    [Fact]
    public async Task FilesV2ConfirmCopy_Uses_The_Exact_Route_And_Confirmation_Body()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? posted = null;
        const string plan = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string key = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        var code = await RunCliAsync([
            "files-v2", "confirm-copy",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--source-path", "C:\\netratel\\review.txt",
            "--destination-path", "C:\\netratel\\published.txt",
            "--plan-token", plan,
            "--idempotency-key", key,
            "--confirm"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/mcp/operator/agents/7/{agentId:D}/files/copy/confirm");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"replayed":false}""");
        });

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("sourcePath").GetString().Should().Be("C:\\netratel\\review.txt");
        body.RootElement.GetProperty("destinationPath").GetString().Should().Be("C:\\netratel\\published.txt");
        body.RootElement.GetProperty("planToken").GetString().Should().Be(plan);
        body.RootElement.GetProperty("idempotencyKey").GetString().Should().Be(key);
    }

    [Fact]
    public async Task TasksV2PreviewCommand_Uses_Exact_Owned_Endpoint_Without_Leaking_Command_Output()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? posted = null;
        var output = new StringWriter();

        var code = await RunCliAsync([
            "tasks-v2", "preview-create-command",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--shell", "bash",
            "--command", "printf sensitive-task-value",
            "--working-directory", "/var/tmp/netratel",
            "--timeout-seconds", "30",
            "--maximum-output-bytes", "4096"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/mcp/operator/agents/7/{agentId:D}/tasks/preview/create_command");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"planToken":"opaque-plan","idempotencyKey":"opaque-key","commandSummary":"sha256:abc"}""");
        }, output);

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("command").GetProperty("command").GetString().Should().Be("printf sensitive-task-value");
        output.ToString().Should().NotContain("sensitive-task-value");
    }

    [Fact]
    public void RequestsV2Command_Exposes_Only_The_Owned_Preview_Confirmation_And_Result_Lifecycle()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var requests = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "requests-v2");

        requests.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "get", "preview-create", "create", "preview-update", "update", "preview-claim", "claim", "preview-complete", "complete", "preview-fail", "fail", "preview-cancel", "cancel"]);
    }

    [Fact]
    public async Task RequestsV2PreviewCreate_Uses_Exact_Owned_Endpoint()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? posted = null;
        var code = await RunCliAsync([
            "requests-v2", "preview-create",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--job-id", "42",
            "--summary", "Apply reviewed configuration"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/mcp/operator/agents/7/{agentId:D}/requests/preview/create");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"planToken":"opaque-plan","idempotencyKey":"opaque-key"}""");
        });

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("jobId").GetInt64().Should().Be(42);
        body.RootElement.GetProperty("summary").GetString().Should().Be("Apply reviewed configuration");
    }

    [Fact]
    public void TenantsCommand_Exposes_Only_SourceBacked_Reads()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var tenants = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "tenants");

        tenants.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "get"]);
    }

    [Fact]
    public void TenantsV2Command_Exposes_The_ControlPlane_Preview_And_Confirmation_Lifecycle()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var tenants = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "tenants-v2");

        tenants.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["list", "get", "preview-create", "create", "preview-update", "update", "preview-delete", "delete"]);
    }

    [Fact]
    public async Task TenantsV2PreviewUpdate_Uses_Exact_ControlPlane_Endpoint()
    {
        string? posted = null;
        var code = await RunCliAsync([
            "tenants-v2", "preview-update",
            "--tenant-id", "7",
            "--expected-version", "3",
            "--name", "Camelot",
            "--domain", "camelot.example",
            "--auto-update", "true"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be("https://api.example/api/v2/mcp/operator/tenants/preview/update");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"planToken":"opaque-plan","idempotencyKey":"opaque-key"}""");
        });

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("tenantId").GetInt32().Should().Be(7);
        body.RootElement.GetProperty("expectedVersion").GetInt64().Should().Be(3);
        body.RootElement.GetProperty("autoUpdate").GetBoolean().Should().BeTrue();
        body.RootElement.TryGetProperty("agentId", out _).Should().BeFalse();
    }

    [Fact]
    public void OnboardingV2Command_Exposes_TenantScoped_Metadata_And_PreviewConfirmation_Lifecycle()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var onboarding = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "onboarding-v2");

        onboarding.Children.OfType<System.CommandLine.Command>().Select(command => command.Name).Should().BeEquivalentTo(
            ["collateral", "download", "list", "get", "preview-create", "create", "preview-revoke", "revoke"]);
    }

    [Fact]
    public async Task OnboardingV2ConfirmCreate_Uses_TenantOnly_Production_Endpoint()
    {
        string? posted = null;
        var code = await RunCliAsync([
            "onboarding-v2", "create",
            "--tenant-id", "7",
            "--runtime", "linux-x64",
            "--valid-for-minutes", "5",
            "--max-uses", "1",
            "--plan-token", "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            "--idempotency-key", "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0",
            "--confirm"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be("https://api.example/api/v2/mcp/operator/tenants/7/onboarding/confirm/create-enrollment");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"enrollmentCode":"immediate-only-code"}""");
        });

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("runtime").GetString().Should().Be("linux-x64");
        body.RootElement.GetProperty("maxUses").GetInt32().Should().Be(1);
        body.RootElement.TryGetProperty("agentId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AuthConfigure_WritesConfig_And_RedactsSecret()
    {
        var temp = Directory.CreateTempSubdirectory("netratel-cli-test-");
        var path = Path.Combine(temp.FullName, "cli.json");
        var output = new StringWriter();
        var errors = new StringWriter();

        var code = await NetRatelCli.RunAsync([
            "--config", path,
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "auth", "configure"
        ], new CliRuntime { Out = output, Error = errors });

        code.Should().Be(CliExitCodes.Success);
        File.ReadAllText(path).Should().Contain("secret");

        output.GetStringBuilder().Clear();
        code = await NetRatelCli.RunAsync(["--config", path, "config", "show"], new CliRuntime { Out = output, Error = errors });

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"oidcAppPassword\":\"***\"");
        output.ToString().Should().NotContain("secret");
    }

    [Fact]
    public async Task AuthToken_MintsClientCredentialsToken_WithoutPersistingToken()
    {
        async Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            request.RequestUri!.ToString().Should().Be("https://auth.example/token");
            var body = await request.Content!.ReadAsStringAsync();
            body.Should().Contain("grant_type=client_credentials");
            body.Should().Contain("client_id=client");
            body.Should().Contain("client_secret=secret");
            body.Should().Contain("username=agent");
            body.Should().Contain("password=secret");
            return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
        }
        var output = new StringWriter();

        var code = await NetRatelCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "auth", "token"
        ], new CliRuntime(() => new RecordingHandler(Responder)) { Out = output, Error = new StringWriter() });

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Trim().Should().Be("minted-token");
    }

    [Fact]
    public async Task TenantsList_UsesBearerToken_And_PrintsJson()
    {
        var calls = 0;
        Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.ToString().Should().Be("https://api.example/api/v1/tenants/");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("minted-token");
            return Task.FromResult(Json(HttpStatusCode.OK, """[{"tenantId":1,"name":"Example Organization"}]"""));
        }
        var output = new StringWriter();

        var code = await NetRatelCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "tenants", "list"
        ], new CliRuntime(() => new RecordingHandler(Responder)) { Out = output, Error = new StringWriter() });

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Trim().Should().Be("""[{"tenantId":1,"name":"Example Organization"}]""");
    }

    [Fact]
    public async Task Health_UsesOneBearerTokenForEveryProtectedProbe()
    {
        var protectedPaths = new List<string>();

        var code = await RunCliAsync(["health"], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("minted-token");
            protectedPaths.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(Json(HttpStatusCode.OK, """{"status":"ready"}"""));
        });

        code.Should().Be(CliExitCodes.Success);
        protectedPaths.Should().Equal("/health/live", "/health/ready", "/api/v1/auth/ai-agent/status");
    }

    [Fact]
    public async Task Raw_Rejects_NonOperatorPaths()
    {
        var errors = new StringWriter();

        var code = await NetRatelCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            "raw", "get", "--path", "/internal/health"
        ], new CliRuntime { Out = new StringWriter(), Error = errors });

        code.Should().Be(CliExitCodes.ValidationError);
        errors.ToString().Should().Contain("operator allow-list");
    }

    [Fact]
    public void TerminalCommand_Exposes_SimpleAgentCommands()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var terminal = root.Children.OfType<System.CommandLine.Command>().Single(c => c.Name == "terminal");
        var commandNames = terminal.Children.OfType<System.CommandLine.Command>().Select(c => c.Name).ToArray();

        commandNames.Should().Contain(["listhosts", "command"]);
    }

    [Fact]
    public void CommandsCommand_Exposes_Preview_Confirmation_And_Owned_Lifecycle_Operations()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var commands = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "commands");

        commands.Children.OfType<System.CommandLine.Command>().Select(command => command.Name)
            .Should().BeEquivalentTo(["availability", "preview", "execute", "get", "cancel"]);
    }

    [Fact]
    public async Task CommandsPreview_Uses_Exact_V2_Targeted_Route_Without_Leaking_The_Command_To_Output()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? posted = null;
        var output = new StringWriter();

        var code = await RunCliAsync([
            "commands", "preview",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--shell", "bash",
            "--command", "printf secret-value",
            "--working-directory", "/var/tmp/netratel"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/mcp/operator/agents/7/{agentId:D}/commands/preview");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"planToken":"opaque-plan","idempotencyKey":"opaque-key","commandSummary":"sha256:abc; utf8_bytes:18"}""");
        }, output);

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("command").GetString().Should().Be("printf secret-value");
        output.ToString().Should().NotContain("secret-value");
        output.ToString().Should().Contain("commandSummary");
    }

    [Fact]
    public async Task CommandsExecute_WithoutConfirmation_Does_Not_Mint_A_Token_Or_Call_The_API()
    {
        var calls = 0;
        var output = new StringWriter();

        var code = await NetRatelCli.RunAsync([
            "commands", "execute",
            "--tenant-id", "7",
            "--agent-id", "11111111-2222-3333-4444-555555555555",
            "--shell", "bash",
            "--command", "hostname",
            "--working-directory", "/var/tmp/netratel",
            "--plan-token", "opaque-plan",
            "--idempotency-key", "opaque-key"
        ], new CliRuntime(() =>
        {
            calls++;
            throw new InvalidOperationException("The unconfirmed command must not call the API.");
        })
        { Out = output, Error = new StringWriter() });

        code.Should().Be(CliExitCodes.Success);
        calls.Should().Be(0);
        output.ToString().Should().Contain("confirmationRequired");
    }

    [Fact]
    public void TerminalV2Command_Exposes_Only_The_PolicyBounded_Owned_Session_Contract()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var terminal = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "terminal-v2");

        terminal.Children.OfType<System.CommandLine.Command>().Select(command => command.Name)
            .Should().BeEquivalentTo(["availability", "preview-open", "open", "get", "diagnostics", "input", "stream-window", "resize", "close"]);
    }

    [Fact]
    public async Task TerminalV2Preview_Uses_Exact_Production_Endpoint()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        string? posted = null;

        var code = await RunCliAsync([
            "terminal-v2", "preview-open",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--shell", "bash",
            "--working-directory", "/var/tmp/netratel",
            "--columns", "120",
            "--rows", "40"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/mcp/operator/agents/7/{agentId:D}/terminal/sessions/preview");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"planToken":"opaque-plan","idempotencyKey":"opaque-key"}""");
        });

        code.Should().Be(CliExitCodes.Success);
        using var body = JsonDocument.Parse(posted!);
        body.RootElement.GetProperty("shell").GetString().Should().Be("bash");
        body.RootElement.GetProperty("columns").GetInt32().Should().Be(120);
        body.RootElement.GetProperty("rows").GetInt32().Should().Be(40);
    }

    [Fact]
    public void ClientsCommand_Exposes_Bounded_Development_Observability_Commands()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var clients = root.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "clients");

        clients.Children.OfType<System.CommandLine.Command>().Select(command => command.Name)
            .Should().Contain(["telemetry-target", "telemetry-window", "client-logs"]);

        var clientLogs = clients.Children.OfType<System.CommandLine.Command>().Single(command => command.Name == "client-logs");
        clientLogs.Children.OfType<System.CommandLine.Command>().Select(command => command.Name)
            .Should().BeEquivalentTo(["sources", "history", "tail", "resync"]);
    }

    [Fact]
    public async Task ClientLogsHistory_Uses_The_TargetGated_Bounded_Route()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var code = await RunCliAsync([
            "clients", "client-logs", "history",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--source-id", "client-log",
            "--cursor", "before:2",
            "--page-size", "2",
            "--severity", "Information",
            "--prefix", "Gateway",
            "--category", "Gateway",
            "--text", "MCP-QA"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/development/mcp/agents/7/{agentId:D}/logs/history?sourceId=client-log&cursor=before%3A2&pageSize=2&severity=Information&prefix=Gateway&category=Gateway&text=MCP-QA");
            return Task.FromResult(Json(HttpStatusCode.OK, """{"records":[],"hasMore":false}"""));
        });

        code.Should().Be(CliExitCodes.Success);
    }

    [Fact]
    public async Task ClientLogsTail_And_TelemetryWindow_Use_Bounded_TargetGated_Routes()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var requestUris = new List<string>();
        var tokenRequests = 0;

        Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                tokenRequests++;
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            requestUris.Add(request.RequestUri.ToString());
            return Task.FromResult(Json(HttpStatusCode.OK, """{"records":[],"samples":[]}"""));
        }

        var tailCode = await RunCliAsync([
            "clients", "client-logs", "tail",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--source-id", "client-log",
            "--window-seconds", "10",
            "--max-records", "10"
        ], Responder);
        var telemetryCode = await RunCliAsync([
            "clients", "telemetry-window",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--window-seconds", "10",
            "--max-samples", "10"
        ], Responder);

        tailCode.Should().Be(CliExitCodes.Success);
        telemetryCode.Should().Be(CliExitCodes.Success);
        tokenRequests.Should().Be(2);
        requestUris.Should().Contain($"https://api.example/api/v2/development/mcp/agents/7/{agentId:D}/logs/tail?sourceId=client-log&windowSeconds=10&maxRecords=10");
        requestUris.Should().Contain($"https://api.example/api/v2/development/mcp/agents/7/{agentId:D}/telemetry/stream-window?windowSeconds=10&maxSamples=10");
    }

    [Fact]
    public async Task ClientLogsResync_RequiresConfirmation_AndUsesTheTargetGatedPostRoute()
    {
        var agentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var calls = 0;

        var unconfirmed = await RunCliAsync([
            "clients", "client-logs", "resync",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--source-id", "client-log"
        ], _ =>
        {
            calls++;
            throw new InvalidOperationException("The unconfirmed resync must not call the API.");
        });

        unconfirmed.Should().Be(CliExitCodes.ValidationError);
        calls.Should().Be(0);

        var confirmed = await RunCliAsync([
            "clients", "client-logs", "resync",
            "--tenant-id", "7",
            "--agent-id", agentId.ToString("D"),
            "--source-id", "client-log",
            "--confirm"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
            }

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/development/mcp/agents/7/{agentId:D}/logs/resync");
            (await request.Content!.ReadAsStringAsync()).Should().Be("""{"sourceId":"client-log"}""");
            return Json(HttpStatusCode.OK, """{"resyncCompleted":true,"resyncRequired":false}""");
        });

        confirmed.Should().Be(CliExitCodes.Success);
    }

    [Fact]
    public void SystemCommand_Exposes_OnlySupportedVersionEndpoint()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var system = root.Children.OfType<System.CommandLine.Command>().Single(c => c.Name == "system");

        system.Children.OfType<System.CommandLine.Command>().Select(c => c.Name).Should().Equal("version");
    }

    [Fact]
    public void Job_run_command_omits_the_legacy_cleanup_alias_without_an_api_route()
    {
        var root = NetRatelCli.BuildRoot(new CliRuntime());
        var jobRuns = root.Children.OfType<System.CommandLine.Command>().Single(c => c.Name == "job-runs");

        jobRuns.Children.OfType<System.CommandLine.Command>().Select(c => c.Name)
            .Should().NotContain("cleanup-cancelled-tasks");
    }

    [Fact]
    public async Task TerminalStdin_Posts_Data_Field()
    {
        string? posted = null;
        var calls = 0;
        async Task<HttpResponseMessage> Responder(HttpRequestMessage request)
        {
            calls++;
            if (calls == 1)
            {
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
            }

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.ToString().Should().Be("https://api.example/api/v1/terminal/session-1/stdin");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.Accepted, """{"message":"Input queued."}""");
        }

        var code = await RunCliAsync([
            "terminal", "stdin", "session-1", "--input", "uptime\n"
        ], Responder);

        code.Should().Be(CliExitCodes.Success);
        posted.Should().Be("""{"data":"uptime\n"}""");
    }

    [Fact]
    public async Task TerminalListHosts_Filters_And_Prints_HostSummary()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "listhosts", "--online-only", "--search", "example"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri!.ToString().Should().Be("https://api.example/api/v1/clients/");
            return Task.FromResult(Json(HttpStatusCode.OK, ClientsJson()));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("example-host-01");
        output.ToString().Should().Contain("powershell");
        output.ToString().Should().NotContain("OFFLINE01");
    }

    [Fact]
    public async Task TerminalCommand_Resolves_Host_And_Submits_ExecShellCommand()
    {
        string? posted = null;
        var output = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "command", "dir C:\\", "example-host-01", "--no-wait"
        ], async request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"minted-token"}""");
            }

            if (request.Method == HttpMethod.Get)
            {
                request.RequestUri.ToString().Should().Be("https://api.example/api/v1/clients/");
                return Json(HttpStatusCode.OK, ClientsJson());
            }

            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.ToString().Should().Be("https://api.example/api/v1/client-tasks/");
            posted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.Created, """{"id":42,"requestId":"req-1","clientIdentity":"abcdef1234567890","status":"Pending","taskType":"exec-shell-cmd"}""");
        }, output);

        code.Should().Be(CliExitCodes.Success);
        using var doc = JsonDocument.Parse(posted!);
        doc.RootElement.GetProperty("clientIdentity").GetString().Should().Be("abcdef1234567890");
        doc.RootElement.GetProperty("taskType").GetString().Should().Be("exec-shell-cmd");
        doc.RootElement.GetProperty("shellCommand").GetProperty("command").GetString().Should().Be("dir C:\\");
        doc.RootElement.GetProperty("shellCommand").GetProperty("preferred").GetInt32().Should().Be(2);
        output.ToString().Should().Contain("\"requestId\":\"req-1\"");
    }

    [Fact]
    public async Task TerminalCommand_Ambiguous_Host_Does_Not_Submit()
    {
        var postCount = 0;
        var errors = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "command", "hostname", "example"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            if (request.Method == HttpMethod.Post)
            {
                postCount++;
            }

            return Task.FromResult(Json(HttpStatusCode.OK, ClientsJson(includeSecondExample: true)));
        }, errors: errors);

        code.Should().Be(CliExitCodes.ValidationError);
        postCount.Should().Be(0);
        errors.ToString().Should().Contain("ambiguous");
    }

    [Fact]
    public async Task TerminalCommand_Offline_Host_Does_Not_Submit()
    {
        var postCount = 0;
        var errors = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "command", "hostname", "OFFLINE01"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            if (request.Method == HttpMethod.Post)
            {
                postCount++;
            }

            return Task.FromResult(Json(HttpStatusCode.OK, ClientsJson()));
        }, errors: errors);

        code.Should().Be(CliExitCodes.ValidationError);
        postCount.Should().Be(0);
        errors.ToString().Should().Contain("offline");
    }

    [Fact]
    public async Task TerminalCommand_Waits_And_Prints_Task_Output()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "command", "hostname", "example-host-01", "--poll-seconds", "1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            var uri = request.RequestUri.ToString();
            if (request.Method == HttpMethod.Get && uri == "https://api.example/api/v1/clients/")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, ClientsJson()));
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(HttpStatusCode.Created, """{"id":42,"requestId":"req-1","clientIdentity":"abcdef1234567890","status":"Pending"}"""));
            }

            if (uri == "https://api.example/api/v2/tasks?requestId=req-1")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """[{"id":42,"requestId":"req-1","clientIdentity":"abcdef1234567890","status":"Completed","exitCode":0}]"""));
            }

            if (uri == "https://api.example/api/v2/tasks/logs?requestId=req-1&sinceId=0&stream=all")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """[{"id":1,"requestId":"req-1","clientIdentity":"abcdef1234567890","stream":"stdout","message":"example-host-01\n","seq":1}]"""));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"status\":\"Completed\"");
        output.ToString().Should().Contain("example-host-01");
        output.ToString().Should().Contain("\"exitCode\":0");
    }

    [Fact]
    public async Task TasksList_WithoutRequestId_UsesRecentEndpoint_AndPrintsSummary()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "tasks", "list", "--limit", "10", "--task-type", "exec-shell-cmd"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be("https://api.example/api/v2/tasks/recent?limit=10&taskType=exec-shell-cmd");
            return Task.FromResult(Json(HttpStatusCode.OK, TasksJson()));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"target\":\"example-host-01\"");
        output.ToString().Should().Contain("\"preview\"");
    }

    [Fact]
    public async Task TasksList_WithRequestId_UsesRequestEndpoint()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "tasks", "list", "--request-id", "req-1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be("https://api.example/api/v2/tasks?requestId=req-1");
            return Task.FromResult(Json(HttpStatusCode.OK, TasksJson()));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"requestId\":\"req-1\"");
    }

    [Fact]
    public async Task TerminalCommand_ReturnsPartialSuccess_WhenResultLookupFails()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "command", "hostname", "example-host-01", "--poll-seconds", "1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            var uri = request.RequestUri.ToString();
            if (request.Method == HttpMethod.Get && uri == "https://api.example/api/v1/clients/")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, ClientsJson()));
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(HttpStatusCode.Created, """{"id":42,"requestId":"req-1","clientIdentity":"abcdef1234567890","status":"Pending"}"""));
            }

            if (uri == "https://api.example/api/v2/tasks?requestId=req-1" ||
                uri == "https://api.example/api/v2/tasks/logs?requestId=req-1&sinceId=0&stream=all")
            {
                return Task.FromResult(Json(HttpStatusCode.BadRequest, "lookup failed"));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"submitted\":true");
        output.ToString().Should().Contain("\"requestId\":\"req-1\"");
        output.ToString().Should().Contain("\"resultLookupError\"");
    }

    [Fact]
    public async Task TerminalCommand_Uses_V2_Task_Reads_After_Submission()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "terminal", "command", "hostname", "example-host-01", "--poll-seconds", "1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            var uri = request.RequestUri.ToString();
            if (request.Method == HttpMethod.Get && uri == "https://api.example/api/v1/clients/")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, ClientsJson()));
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(HttpStatusCode.Created, """{"id":42,"requestId":"req-1","clientIdentity":"abcdef1234567890","status":"Pending"}"""));
            }

            if (uri == "https://api.example/api/v2/tasks?requestId=req-1")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, TasksJson()));
            }

            if (uri == "https://api.example/api/v2/tasks/logs?requestId=req-1&sinceId=0&stream=all")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """[{"id":1,"requestId":"req-1","clientIdentity":"abcdef1234567890","stream":"stdout","message":"example-host-01\n","seq":1}]"""));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"status\":\"Completed\"");
        output.ToString().Should().Contain("example-host-01");
    }

    [Fact]
    public async Task TasksResult_Combines_Status_And_Output()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "tasks", "result", "req-1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            var uri = request.RequestUri.ToString();
            if (uri == "https://api.example/api/v2/tasks?requestId=req-1")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, TasksJson()));
            }

            if (uri == "https://api.example/api/v2/tasks/logs?requestId=req-1&sinceId=0&stream=all")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """[{"id":1,"requestId":"req-1","clientIdentity":"abcdef1234567890","stream":"stdout","message":"example-host-01\n","seq":1}]"""));
            }

            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"requestId\":\"req-1\"");
        output.ToString().Should().Contain("\"stdout\":\"example-host-01\\n\"");
    }

    [Fact]
    public async Task ClientsTelemetry_UsesTheSourceBackedRoute()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "clients", "telemetry", "client-1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be("https://api.example/api/v1/clients/client-1/telemetry");
            return Task.FromResult(Json(HttpStatusCode.OK, """{"clientIdentity":"client-1","cpuPercent":7}"""));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("client-1").And.Contain("cpuPercent");
    }

    [Fact]
    public async Task ClientsTelemetryTarget_UsesTheDevelopmentTargetRoute()
    {
        var output = new StringWriter();
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var code = await RunCliAsync([
            "clients", "telemetry-target", "--tenant-id", "7", "--agent-id", agentId.ToString("D")
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be($"https://api.example/api/v2/development/mcp/agents/7/{agentId:D}/telemetry/snapshot");
            return Task.FromResult(Json(HttpStatusCode.OK, """{"cpuPercent":7}"""));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("cpuPercent");
    }

    [Fact]
    public async Task ClientsUpdateAttempts_UsesTheSourceBackedBoundedRoute()
    {
        var output = new StringWriter();
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var code = await RunCliAsync([
            "clients", "update-attempts", "--client-identity", agentId.ToString("D"), "--release-id", "3", "--status", "Accepted"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be($"https://api.example/api/v1/client-updates/attempts?clientIdentity={agentId:D}&releaseId=3&status=Accepted");
            return Task.FromResult(Json(HttpStatusCode.OK, """[{"state":"Accepted"}]"""));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("Accepted");
    }

    [Fact]
    public async Task JobsList_PrintsAgentSummary()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "jobs", "list", "--search", "Inventory"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be("https://api.example/api/v1/jobs/?search=Inventory");
            return Task.FromResult(Json(HttpStatusCode.OK, """
                [{"id":7,"name":"Inventory","folderPath":"/ops","tenantId":4098,"clientIdentity":"abcdef1234567890","content":"very large script body","description":"Collect workstation inventory"}]
                """));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"name\":\"Inventory\"");
        output.ToString().Should().Contain("\"folder\":\"/ops\"");
        output.ToString().Should().NotContain("very large script body");
    }

    [Fact]
    public async Task LogsSearch_PrintsAgentSummary_FromEntriesEnvelope()
    {
        var output = new StringWriter();

        var code = await RunCliAsync([
            "logs", "search", "--limit", "1"
        ], request =>
        {
            if (request.RequestUri!.Host == "auth.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"minted-token"}"""));
            }

            request.RequestUri.ToString().Should().Be("https://api.example/api/v1/ops/ai-agent/logs?limit=1");
            return Task.FromResult(Json(HttpStatusCode.OK, """
                {"nextSince":123,"entries":[{"sequence":122,"level":"Info","timestamp":"2026-06-22T05:21:00Z","correlationId":"corr-1","message":"A long but useful message for the agent"}]}
                """));
        }, output);

        code.Should().Be(CliExitCodes.Success);
        output.ToString().Should().Contain("\"nextSince\":\"123\"");
        output.ToString().Should().Contain("\"message\":\"A long but useful message for the agent\"");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<int> RunCliAsync(
        string[] args,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
        StringWriter? output = null,
        StringWriter? errors = null)
        => NetRatelCli.RunAsync([
            "--api-base-url", "https://api.example",
            "--token-url", "https://auth.example/token",
            "--client-id", "client",
            "--username", "agent",
            "--app-password", "secret",
            .. args
        ], new CliRuntime(() => new RecordingHandler(responder)) { Out = output ?? new StringWriter(), Error = errors ?? new StringWriter() });

    private static string ClientsJson(bool includeSecondExample = false)
        => includeSecondExample
            ? """
              [
                {"clientIdentity":"abcdef1234567890","shortId":"abcdef12","displayName":"example-host-01","clientName":"Example Agent","hostName":"example-host-01","online":true,"enabled":true,"availableShells":["powershell","cmd"],"detectedOs":"Windows","effectiveTerminalTransport":"ApiWebSocket","terminalTunnelConnected":true,"lastHeartbeat":"2026-06-22T10:00:00Z"},
                {"clientIdentity":"1111111122222222","shortId":"11111111","displayName":"example-app-01","clientName":"Example App","hostName":"example-app-01","online":true,"enabled":true,"availableShells":["powershell"],"detectedOs":"Windows"}
              ]
              """
            : """
              [
                {"clientIdentity":"abcdef1234567890","shortId":"abcdef12","displayName":"example-host-01","clientName":"Example Agent","hostName":"example-host-01","online":true,"enabled":true,"availableShells":["powershell","cmd"],"detectedOs":"Windows","effectiveTerminalTransport":"ApiWebSocket","terminalTunnelConnected":true,"lastHeartbeat":"2026-06-22T10:00:00Z"},
                {"clientIdentity":"9999999900000000","shortId":"99999999","displayName":"OFFLINE01","clientName":"Offline Agent","hostName":"OFFLINE01","online":false,"enabled":true,"availableShells":["bash"],"detectedOs":"Linux"}
              ]
              """;

    private static string NoisyClientsJson()
        => """
           [
             {"clientIdentity":"abcdef1234567890","shortId":"abcdef12","displayName":"example-host-01","clientName":"Example Agent","hostName":"example-host-01","online":true,"enabled":true,"availableShells":["powershell","cmd"],"detectedOs":"Windows","environment":"Development","tenantName":"Example Organization","agentVersion":"1.2.3","lastHeartbeat":"2026-06-22T10:00:00Z","logs":["large-noisy-log-entry"]}
           ]
           """;

    private static string TasksJson()
        => """
           [
             {"id":42,"requestId":"req-1","clientIdentity":"abcdef1234567890","clientHostName":"example-host-01","clientDisplayName":"example-host-01","taskType":"exec-shell-cmd","status":"Completed","returnData":"{\"stdout\":[\"example-host-01\\n\"],\"stderr\":[]}","exitCode":0,"created":"2026-06-22T05:21:00Z","completedAt":"2026-06-22T05:21:01Z","environment":"Development"}
           ]
           """;

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
