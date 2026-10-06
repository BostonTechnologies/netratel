using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Runs the published companion API/Web, without building or impersonating RatelDesk.</summary>
internal sealed class ServiceLinkPublishedRatelDeskPeer : IAsyncDisposable
{
    public const string PublishedSource = "3cd63a776df67bd98d7efb81a330b2d0c57ad1f1";
    public const string PublishedVersion = "0.1.1-beta.12";
    internal const string ApiImage = "ghcr.io/bostontechnologies/rateldesk-api@sha256:c32f3537a5c3834224a9c07c47423335c1448cfc9607f2fcfd66924b17525e0b";
    internal const string WebImage = "ghcr.io/bostontechnologies/rateldesk-web@sha256:baa433421994c6b739dba17aa887cc74a70828b0f9f91462d5ff9c50e22b2980";
    private readonly string root = Path.Combine(Path.GetTempPath(), "netratel-rateldesk-pair", Guid.NewGuid().ToString("N"));
    private readonly string project = "netratel-service-link-" + Guid.NewGuid().ToString("N");
    private readonly string password = "aA1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly int backendPort = ServiceLinkHttpProxy.AllocatePort();
    private readonly int webPort = ServiceLinkHttpProxy.AllocatePort();
    private readonly ServiceLinkRotationTestPolicy? rotationPolicy;
    private string composeFile => Path.Combine(root, "compose.json");
    public ServiceLinkHttpProxy Proxy { get; }
    public string ApiBaseUrl => Proxy.BaseUrl;
    public string WebBaseUrl { get; }
    public string ReachableHost { get; }
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string OrganizationId { get; private set; } = "";
    public string CustomerId { get; private set; } = "";
    public HttpClient Administrator { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;

    private ServiceLinkPublishedRatelDeskPeer(string reachableHost, ServiceLinkRotationTestPolicy? rotationPolicy)
    {
        ReachableHost = reachableHost;
        this.rotationPolicy = rotationPolicy;
        Proxy = new($"http://127.0.0.1:{backendPort}", reachableHost);
        WebBaseUrl = $"http://{reachableHost}:{webPort}";
    }

    public static async Task<ServiceLinkPublishedRatelDeskPeer> CreateAsync(ServiceLinkRotationTestPolicy? rotationPolicy = null)
    {
        var network = await DockerAsync(["network", "inspect", "bridge", "--format", "{{json .IPAM.Config}}"]);
        using var bridge = JsonDocument.Parse(network);
        var reachableHost = bridge.RootElement[0].GetProperty("Gateway").GetString()
            ?? throw new InvalidOperationException("The isolated Docker bridge has no reachable host gateway.");
        var peer = new ServiceLinkPublishedRatelDeskPeer(reachableHost, rotationPolicy);
        try
        {
            Directory.CreateDirectory(peer.root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(peer.root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            peer.WriteCompose();
            await peer.Proxy.StartAsync();
            await peer.ComposeAsync(["up", "-d", "--wait", "--wait-timeout", "60", "postgres"]);
            var initialized = await peer.ComposeAsync(["run", "--rm", "--no-deps", "api", "--initialize-unattended"]);
            if (!initialized.Contains("RatelDesk initialization completed.", StringComparison.Ordinal))
                throw new InvalidOperationException("The published RatelDesk image did not complete its actual unattended bootstrap.");
            await peer.ComposeAsync(["up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "api", "web"]);
            peer.Administrator = peer.NewClient(cookies: true);
            peer.Anonymous = peer.NewClient();
            await peer.LoginAsync();
            using var organizations = await peer.Administrator.GetFromJsonAsync<JsonDocument>("/api/v1/organizations/");
            peer.OrganizationId = organizations!.RootElement.EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Synthetic reciprocal peer")
                .GetProperty("id").GetString()!;
            using var customer = await peer.Administrator.PostAsJsonAsync("/api/v1/customers/", new
            {
                id = Guid.NewGuid().ToString("D"), name = "Synthetic incident requester", email = "customer@example.test",
                organizationId = peer.OrganizationId, state = 0
            });
            customer.EnsureSuccessStatusCode();
            using var created = await customer.Content.ReadFromJsonAsync<JsonDocument>();
            peer.CustomerId = created!.RootElement.GetProperty("id").GetString()!;
            var metadata = await peer.Anonymous.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
            if (metadata?.Product != "rateldesk" || metadata.ProductVersion != PublishedVersion || metadata.InstanceId != peer.InstanceId.ToString("D"))
                throw new InvalidOperationException("The running published RatelDesk metadata differs from the pinned peer build and configured identity.");
            await peer.VerifyImageAsync(ApiImage);
            await peer.VerifyImageAsync(WebImage);
            return peer;
        }
        catch
        {
            await peer.CapturePrivateFailureDiagnosticsAsync();
            await peer.DisposeAsync();
            throw;
        }
    }

    private async Task CapturePrivateFailureDiagnosticsAsync()
    {
        // Keep peer failure details out of public test logs and artifacts.
        // Inspect only disposable task containers before their normal cleanup.
        if (!File.Exists(composeFile)) return;
        var directory = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_PRIVATE_DIAGNOSTIC_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "netratel-service-link-private-diagnostics");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (suffix, arguments) in new[]
        {
            ("state", new[] { "ps", "--all", "--format", "json" }),
            ("api-log", new[] { "logs", "--no-color", "--tail", "200", "api" })
        })
        {
            try
            {
                var contents = await ComposeAsync(arguments);
                var file = Path.Combine(directory, project + "." + suffix);
                File.WriteAllText(file, contents.Length > 65536 ? contents[^65536..] : contents);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* The original failure and bounded Docker stderr remain authoritative. */ }
        }
        try
        {
            var id = (await ComposeAsync(["ps", "--all", "--quiet", "api"])).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{12,64}$")) return;
            // State includes exit/OOM/health evidence, never container environment or credentials.
            var state = await DockerAsync(["inspect", "--format", "{{json .State}}", id]);
            var file = Path.Combine(directory, project + ".api-state");
            File.WriteAllText(file, state.Length > 65536 ? state[^65536..] : state);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* Preserve the original startup failure even if state capture is unavailable. */ }
    }

    private async Task VerifyImageAsync(string image)
    {
        var identity = await DockerAsync(["image", "inspect", image, "--format", "{{index .Config.Labels \"org.opencontainers.image.revision\"}} {{index .Config.Labels \"org.opencontainers.image.version\"}}"]);
        if (identity.Trim() != PublishedSource + " " + PublishedVersion)
            throw new InvalidOperationException("The published companion image source/version labels do not match the immutable handoff.");
    }

    private void WriteCompose()
    {
        var databasePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var connection = $"Host=postgres;Database=rateldesk;Username=rateldesk;Password={databasePassword}";
        var localNoProxy = LocalNoProxy(ReachableHost);
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["NO_PROXY"] = localNoProxy, ["no_proxy"] = localNoProxy,
            ["Authentication__Mode"] = "Local", ["Authentication__AllowInsecureLocalhost"] = "true",
            ["DataProtection__KeyRingPath"] = "/var/lib/rateldesk/keys",
            ["Bootstrap__StateDirectory"] = "/var/lib/rateldesk/bootstrap",
            ["Bootstrap__DataDirectory"] = "/var/lib/rateldesk/data",
            ["StorageOptions__RootPath"] = "/app/storage",
            ["Bootstrap__Unattended__Provider"] = "PostgreSql",
            ["Bootstrap__Unattended__PostgreSqlConnectionString"] = connection,
            ["Bootstrap__Unattended__Email"] = "admin@example.test",
            ["Bootstrap__Unattended__DisplayName"] = "Synthetic Pair Administrator",
            ["Bootstrap__Unattended__Password"] = password,
            ["Bootstrap__Unattended__OrganizationName"] = "Synthetic reciprocal peer",
            ["Bootstrap__Unattended__ApplicationUrl"] = $"http://127.0.0.1:{webPort}",
            ["ServiceIdentity__Enabled"] = "true", ["ServiceIdentity__Issuer"] = ApiBaseUrl,
            ["ServiceIdentity__Audience"] = "rateldesk.service-link-http-tests",
            ["ServiceIdentity__ApiBaseUrl"] = ApiBaseUrl, ["ServiceIdentity__WebBaseUrl"] = WebBaseUrl,
            ["ServiceIdentity__InstanceId"] = InstanceId.ToString("D"), ["ServiceIdentity__AllowPrivateHttp"] = "true",
            ["ServiceLinks__Enabled"] = "true", ["ServiceLinks__ApiBaseUrl"] = ApiBaseUrl,
            ["ServiceLinks__WebBaseUrl"] = WebBaseUrl, ["ServiceLinks__AllowPrivateHttp"] = "true",
            ["ServiceLinks__BootstrapLifetimeSeconds"] = "120", ["ServiceLinks__WorkerIntervalSeconds"] = "60",
            ["ServiceLinks__AutomaticRotationEnabled"] = "false"
        };
        var mounts = new[] { "keys:/var/lib/rateldesk/keys", "bootstrap:/var/lib/rateldesk/bootstrap", "data:/var/lib/rateldesk/data", "storage:/app/storage" };
        if (rotationPolicy is not null)
        {
            environment["ServiceLinks__AutomaticRotationEnabled"] = (rotationPolicy.Automatic && !rotationPolicy.NetRatelIssuer).ToString();
            environment["ServiceLinks__RotationAgeDays"] = "1";
            environment["ServiceLinks__RotationOverlapSeconds"] = "60";
            environment["ServiceLinks__RotationOfferLifetimeSeconds"] = "120";
            environment["ServiceLinks__WorkerIntervalSeconds"] = "1";
            environment["ServiceIdentity__AccessTokenLifetimeSeconds"] = "900";
            environment["ServiceIdentity__ClockSkewSeconds"] = "0";
        }
        var api = new Dictionary<string, object>
        {
            ["image"] = ApiImage, ["environment"] = environment,
            ["ports"] = new[] { $"127.0.0.1:{backendPort}:8222" }, ["volumes"] = mounts,
            ["healthcheck"] = new { test = new[] { "CMD-SHELL", "wget -Y off -q -O /dev/null http://127.0.0.1:8222/health/live || exit 1" }, interval = "2s", timeout = "2s", retries = 40 }
        };
        var web = new Dictionary<string, object>
        {
            ["image"] = WebImage, ["ports"] = new[] { $"{ReachableHost}:{webPort}:8111" },
            ["environment"] = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production", ["ApiBaseUrl"] = "http://api:8222/",
                ["NO_PROXY"] = localNoProxy, ["no_proxy"] = localNoProxy,
                ["ReverseProxy__Clusters__apiCluster__Destinations__api1__Address"] = "http://api:8222/",
                ["Authentication__Mode"] = "Local", ["Authentication__AllowInsecureLocalhost"] = "true",
                ["DataProtection__KeyRingPath"] = "/var/lib/rateldesk/keys"
            },
            ["volumes"] = new[] { "keys:/var/lib/rateldesk/keys" },
            ["depends_on"] = new Dictionary<string, object> { ["api"] = new { condition = "service_healthy" } }
        };
        // Local protocol peers need no Internet route; Compose retains the managed Docker proxy settings.
        var ca = Environment.GetEnvironmentVariable("CODEX_PROXY_CERT");
        if (!string.IsNullOrWhiteSpace(ca) && File.Exists(ca))
        {
            api["volumes"] = mounts.Append($"{ca}:/run/proxy-ca.pem:ro").ToArray();
            environment["SSL_CERT_FILE"] = "/run/proxy-ca.pem";
        }
        var privateCrashRoot = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_PRIVATE_NATIVE_CRASH_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(privateCrashRoot))
        {
            // Explicit, private diagnostic instrumentation for a disposable peer only.
            // Ordinary acceptance keeps the published image's runtime settings unchanged.
            var crashDirectory = Path.Combine(Path.GetFullPath(privateCrashRoot), project);
            Directory.CreateDirectory(crashDirectory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(crashDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            api["volumes"] = ((string[])api["volumes"]).Append($"{crashDirectory}:/diagnostics").ToArray();
            environment["DOTNET_DbgEnableMiniDump"] = "1";
            environment["DOTNET_DbgMiniDumpType"] = "1";
            environment["DOTNET_DbgMiniDumpName"] = "/diagnostics/api.%p.dmp";
            environment["DOTNET_CreateDumpDiagnostics"] = "1";
            environment["DOTNET_EnableCrashReport"] = "1";
        }
        var compose = new
        {
            services = new Dictionary<string, object>
            {
                ["postgres"] = new { image = "postgres:16", environment = new { POSTGRES_DB = "rateldesk", POSTGRES_USER = "rateldesk", POSTGRES_PASSWORD = databasePassword },
                    volumes = new[] { "database:/var/lib/postgresql/data" },
                    healthcheck = new { test = new[] { "CMD-SHELL", "pg_isready -U rateldesk -d rateldesk" }, interval = "2s", timeout = "2s", retries = 30 } },
                ["api"] = api, ["web"] = web
            },
            volumes = new Dictionary<string, object> { ["database"] = new { }, ["keys"] = new { }, ["bootstrap"] = new { }, ["data"] = new { }, ["storage"] = new { } }
        };
        File.WriteAllText(composeFile, JsonSerializer.Serialize(compose));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(composeFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string LocalNoProxy(string reachableHost)
    {
        var existing = new List<string?> { Environment.GetEnvironmentVariable("NO_PROXY"), Environment.GetEnvironmentVariable("no_proxy") };
        // Docker supplies managed session proxy defaults to new containers. Preserve its selected public bypass list.
        var dockerDirectory = Environment.GetEnvironmentVariable("DOCKER_CONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        var config = Path.Combine(dockerDirectory, "config.json");
        if (File.Exists(config))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(config));
            if (document.RootElement.TryGetProperty("proxies", out var proxies) && proxies.TryGetProperty("default", out var defaults) &&
                defaults.TryGetProperty("noProxy", out var bypass) && bypass.ValueKind == JsonValueKind.String)
                existing.Add(bypass.GetString());
        }
        return string.Join(',', existing.Where(x => !string.IsNullOrWhiteSpace(x)).SelectMany(x => x!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Concat(["localhost", "127.0.0.1", "api", "postgres", reachableHost]).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private HttpClient NewClient(bool cookies = false)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = cookies })
            { BaseAddress = new Uri(ApiBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        // The published application's CSRF middleware explicitly supports this non-simple server-side Web client header.
        if (cookies) client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    private async Task LoginAsync()
    {
        using var login = await Administrator.PostAsJsonAsync("/api/v1/local-auth/login", new { email = "admin@example.test", password, rememberMe = false });
        if (login.StatusCode != HttpStatusCode.NoContent)
            throw new InvalidOperationException($"The published peer administrator login failed with HTTP {(int)login.StatusCode}.");
    }

    public async Task RestartAsync()
    {
        await ComposeAsync(["restart", "api", "web"]);
        await ComposeAsync(["up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "api", "web"]);
        await LoginAsync();
    }

    public async Task<HttpResponseMessage> AdminAsync(string path, object body) =>
        await Administrator.PostAsJsonAsync(path, ServiceLinkNetRatelPeer.ProjectLifecycle(path, body));

    public async Task<long> CountAsync(string table, string? predicate = null)
    {
        if (!new[] { "Incidents", "IncidentCreateReceipts", "IncidentReceiverSources", "ServicePrincipalRegistrations", "ServiceLinkAttempts", "ServiceLinkOperations" }.Contains(table))
            throw new ArgumentException("The proof query table is not permitted.", nameof(table));
        // The published peer uses TPH: Incident rows belong to Tickets, alongside other ticket kinds.
        var physicalTable = table == "Incidents" ? "Tickets" : table;
        var condition = table == "Incidents" ? "\"Discriminator\" = 'Incident'" : null;
        if (predicate is not null) condition = condition is null ? predicate : $"{condition} AND ({predicate})";
        var sql = $"SELECT count(*) FROM \"{physicalTable}\"" + (condition is null ? "" : " WHERE " + condition);
        var count = await ComposeAsync(["exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", "rateldesk", "-d", "rateldesk", "-At", "-c", sql]);
        return long.Parse(count.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<ServiceLinkHistoricalAgePrecondition> ApplyHistoricalPredecessorAgeAsync(string linkId, string directionId, long revision)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(linkId, "^[0-9a-f]{48}$") ||
            directionId is not ("initiator_to_responder" or "responder_to_initiator") || revision != 1 ||
            rotationPolicy is not { Automatic: true, NetRatelIssuer: false })
            throw new ArgumentException("Historical aging is restricted to this fixture's initial, genuinely active RatelDesk-issued predecessor.");
        // This one statement locks the actual protocol-created current principal/secret.
        // Its old-value CAS changes ONLY CreatedAtUtc; all other secret columns and
        // the principal are compared within the same transaction snapshot.
        var sql = $$"""
            WITH original AS MATERIALIZED (
              SELECT s."ServicePrincipalId", s."CredentialRevision", s."CreatedAtUtc", s."ExpiresAtUtc",
                     to_jsonb(s) - 'CreatedAtUtc' AS material, to_jsonb(p) AS principal
              FROM "ServicePrincipalSecrets" s
              JOIN "ServicePrincipalRegistrations" p ON p."Id" = s."ServicePrincipalId"
              JOIN "ServiceLinkAttempts" a ON a."InboundPrincipalId" = p."Id" AND a."LinkId" = p."LinkId"
              WHERE p."LinkId" = '{{linkId}}' AND p."DirectionId" = '{{directionId}}'
                AND p."Status" = 'active' AND p."CurrentCredentialRevision" = {{revision}}
                AND s."CredentialRevision" = {{revision}} AND s."Status" = 'active'
                AND s."RetireAtUtc" IS NULL AND s."ExpiresAtUtc" > CURRENT_TIMESTAMP
                AND a."Decision" = 'commit' AND a."LifecycleState" = 'active'
                AND a."LocalInboundActive" AND a."LocalBusinessSenderEnabled"
                AND NOT EXISTS (SELECT 1 FROM "ServiceLinkRotations" r WHERE r."LinkId" = p."LinkId")
              FOR UPDATE OF p, s
            ), changed AS (
              UPDATE "ServicePrincipalSecrets" s SET "CreatedAtUtc" = o."CreatedAtUtc" - INTERVAL '2 days'
              FROM original o
              WHERE s."ServicePrincipalId" = o."ServicePrincipalId" AND s."CredentialRevision" = o."CredentialRevision"
                AND s."CreatedAtUtc" = o."CreatedAtUtc"
              RETURNING s."ServicePrincipalId", s."CredentialRevision", s."CreatedAtUtc", s."ExpiresAtUtc",
                        to_jsonb(s) - 'CreatedAtUtc' AS material
            )
            SELECT jsonb_build_object('rowsChanged', (SELECT count(*) FROM changed),
              'principalId', c."ServicePrincipalId", 'credentialRevision', c."CredentialRevision",
              'originalCreatedAtUtc', o."CreatedAtUtc", 'historicalCreatedAtUtc', c."CreatedAtUtc",
              'originalHardExpiryUtc', o."ExpiresAtUtc",
              'allOtherSecretColumnsUnchanged', o.material = c.material,
              'principalUnchanged', o.principal = to_jsonb(p))
            FROM changed c JOIN original o USING ("ServicePrincipalId", "CredentialRevision")
            JOIN "ServicePrincipalRegistrations" p ON p."Id" = c."ServicePrincipalId";
            """;
        var result = await ComposeAsync(["exec", "-T", "postgres", "psql", "-v", "ON_ERROR_STOP=1", "-U", "rateldesk", "-d", "rateldesk", "-At", "-c", sql]);
        var evidence = JsonSerializer.Deserialize<ServiceLinkHistoricalAgePrecondition>(result.Trim(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (evidence is not { RowsChanged: 1, AllOtherSecretColumnsUnchanged: true, PrincipalUnchanged: true } ||
            evidence.CredentialRevision != revision || evidence.OriginalHardExpiryUtc <= DateTimeOffset.UtcNow ||
            evidence.HistoricalCreatedAtUtc != evidence.OriginalCreatedAtUtc.AddDays(-2))
            throw new InvalidOperationException("The exact historical-age precondition did not preserve all actual predecessor authority and hard-expiry fields.");
        return evidence;
    }

    private Task<string> ComposeAsync(string[] arguments) => DockerAsync(["compose", "-p", project, "-f", composeFile, .. arguments]);

    private static async Task<string> DockerAsync(string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var name in new[] { "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH" }) start.Environment.Remove(name);
        start.ArgumentList.Add("--host=unix:///var/run/docker.sock");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The managed Docker CLI could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        var diagnostic = await stderr;
        if (process.ExitCode != 0)
        {
            // Private diagnostics are deliberately outside ordinary test artifacts and never included in exceptions.
            var directory = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_PRIVATE_DIAGNOSTIC_DIRECTORY")
                ?? Path.Combine(Path.GetTempPath(), "netratel-service-link-private-diagnostics");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".stderr");
            File.WriteAllText(file, diagnostic.Length > 16384 ? diagnostic[^16384..] : diagnostic);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var operation = arguments.FirstOrDefault(value => value is "up" or "run" or "restart" or "down" or "exec" or "inspect") ?? "command";
            throw new InvalidOperationException($"The isolated published-peer Docker {operation} operation failed with exit code {process.ExitCode}; bounded private diagnostics were retained separately.");
        }
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        Administrator?.Dispose(); Anonymous?.Dispose();
        if (File.Exists(composeFile))
        {
            // An explicit private diagnostic run also retains failures after startup.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_PRIVATE_DIAGNOSTIC_DIRECTORY")))
            {
                try { await CapturePrivateFailureDiagnosticsAsync(); }
                catch { /* Optional diagnostic capture must not prevent owned cleanup. */ }
            }
            try { await ComposeAsync(["down", "--volumes", "--remove-orphans"]); }
            finally { File.Delete(composeFile); }
        }
        await Proxy.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
