using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Runs the published companion API/Web, without building or impersonating RatelDesk.</summary>
internal sealed partial class ServiceLinkPublishedRatelDeskPeer : IAsyncDisposable
{
    public const string PublishedSource = "a428d84b228213a3b1036b896610a19e51e19fa6";
    public const string PublishedVersion = "0.1.1-beta.16";
    internal const string ApiImage = "ghcr.io/bostontechnologies/rateldesk-api@sha256:7d8c84b756981f4c94c04d2fdeffefe08f472739c06eb60b05e57b74f4110410";
    internal const string WebImage = "ghcr.io/bostontechnologies/rateldesk-web@sha256:6a79b193ed20a7d14383821aab64dfb8b72fb4a68ec42ef5a16a061710c4d64c";
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
        // The published peer owns this PostgreSQL through its isolated Compose
        // project, not the NetRatel Testcontainers database. Read real server
        // ERROR lines before normal disposal; never retain DETAIL/STATEMENT,
        // arbitrary error text, SQL, parameters or connection credentials.
        var postgresErrors = new List<object>();
        string? postgresCaptureFailureType = null;
        try
        {
            var logs = await DockerAsync(["compose", "-p", project, "-f", composeFile,
                "logs", "--no-color", "--timestamps", "--tail", "200", "postgres"],
                TimeSpan.FromSeconds(5), includeStandardError: true);
            foreach (var line in logs.Split('\n'))
            {
                var error = System.Text.RegularExpressions.Regex.Match(line,
                    @"^(?:[A-Za-z0-9_.-]+\s*\|\s*)?(?<stamp>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?Z)\s+[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)? [A-Z]{1,8} \[[0-9]{1,10}\]\s+(?<severity>ERROR|FATAL|PANIC):\s+(?:(?<sqlState>[0-9A-Z]{5}):\s+)?(?<message>.*)$");
                if (!error.Success) continue;
                var message = error.Groups["message"].Value;
                var kind = message switch
                {
                    _ when message.StartsWith("could not serialize access", StringComparison.Ordinal) => "serialization-failure",
                    _ when message.StartsWith("deadlock detected", StringComparison.Ordinal) => "deadlock-detected",
                    _ when message.StartsWith("duplicate key value violates unique constraint", StringComparison.Ordinal) => "unique-violation",
                    _ when message.Contains("violates check constraint", StringComparison.Ordinal) => "check-violation",
                    _ when message.Contains("violates foreign key constraint", StringComparison.Ordinal) => "foreign-key-violation",
                    _ when message.Contains("violates not-null constraint", StringComparison.Ordinal) => "not-null-violation",
                    _ when message.StartsWith("canceling statement due to statement timeout", StringComparison.Ordinal) => "statement-timeout",
                    _ when message.StartsWith("canceling statement due to lock timeout", StringComparison.Ordinal) => "lock-timeout",
                    _ => "unclassified-server-error"
                };
                DateTimeOffset? timestamp = DateTimeOffset.TryParse(error.Groups["stamp"].Value,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed) ? parsed : null;
                var constraint = System.Text.RegularExpressions.Regex.Match(message,
                    @"\bconstraint ""(?<name>(?:PK|FK|IX|CK|UQ|UX)_[A-Za-z][A-Za-z0-9_]{0,119})""");
                postgresErrors.Add(new
                {
                    timestampUtc = timestamp, severity = error.Groups["severity"].Value,
                    // Default PostgreSQL logging may omit SQLSTATE. Do not derive
                    // or invent a code from the classified English server message.
                    sqlState = error.Groups["sqlState"].Success ? error.Groups["sqlState"].Value : null,
                    kind, constraintName = constraint.Success ? constraint.Groups["name"].Value : null
                });
            }
        }
        catch (Exception error) { postgresCaptureFailureType = error.GetType().FullName; }
        try
        {
            var postgresFile = Path.Combine(directory, project + ".postgres-errors.json");
            File.WriteAllText(postgresFile, JsonSerializer.Serialize(new
            {
                capturedAtUtc = DateTimeOffset.UtcNow, product = "rateldesk", composeProject = project,
                databaseService = "postgres", databaseRole = "published-companion-server",
                PublishedSource, PublishedVersion, ApiImage,
                rotationPolicy, captureSucceeded = postgresCaptureFailureType is null,
                captureFailureType = postgresCaptureFailureType, retainedServerErrors = postgresErrors,
                logTailLines = 200, logReadBudgetSeconds = 5,
                errorOnly = true, rawMessagesStatementsDetailsAndParametersRetained = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(postgresFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* Optional diagnostic receipt failures preserve the original failure and owned cleanup. */ }
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
            ["StorageOptions__PublicApiBaseUrl"] = ApiBaseUrl,
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

    public async Task WaitForPostActivationSensitiveWindowAsync(CancellationToken ct = default)
    {
        // This is called only after both real peers reported active. Their setup
        // traffic shares each issuer's unchanged 20-per-minute IP partition.
        // A full real window from that observation covers RatelDesk's later
        // setup window without depending on NetRatel's earlier identity read,
        // the remote limiter's first-request timestamp or idle-bucket eviction.
        // Keep this wait concurrent with the existing NetRatel setup wait and
        // within the original case cancellation budget; no token is retried.
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        // The existing protected list is read-only: it neither progresses nor
        // expires an attempt. Observe actual admission after normal refill.
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readiness.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            for (var admission = 0; admission < 10; admission++)
            {
                using var response = await Administrator.GetAsync("/api/v1/admin/service-links/", readiness.Token);
                if (response.StatusCode == HttpStatusCode.OK) return;
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new InvalidOperationException($"The post-activation RatelDesk sensitive list admission returned HTTP {(int)response.StatusCode}.");
                if (admission < 9) await Task.Delay(TimeSpan.FromMilliseconds(100), readiness.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && readiness.IsCancellationRequested)
        {
            throw new InvalidOperationException("The post-activation RatelDesk sensitive list admission did not return HTTP 200 within one second after the real window.");
        }
        throw new InvalidOperationException("The post-activation RatelDesk sensitive list admission remained HTTP 429 after the real window.");
    }

    public async Task RestartAsync()
    {
        await ComposeAsync(["restart", "api", "web"]);
        await StartAfterRestartAsync();
    }

    internal Task StopForRestartAsync() => ComposeAsync(["stop", "api", "web"]);

    internal async Task StartAfterRestartAsync()
    {
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

    private Task<string> ComposeAsync(string[] arguments, CancellationToken ct = default) => DockerAsync(["compose", "-p", project, "-f", composeFile, .. arguments], ct: ct);

    private static async Task<string> DockerAsync(string[] arguments, TimeSpan? operationBudget = null, bool includeStandardError = false, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(operationBudget ?? TimeSpan.FromMinutes(3));
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
            // The server-log read is sanitized by its caller on success; never
            // persist partial raw PostgreSQL stderr if that observation fails.
            if (!includeStandardError)
            {
                // Private diagnostics are deliberately outside ordinary test artifacts and never included in exceptions.
                var directory = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_PRIVATE_DIAGNOSTIC_DIRECTORY")
                    ?? Path.Combine(Path.GetTempPath(), "netratel-service-link-private-diagnostics");
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".stderr");
                File.WriteAllText(file, diagnostic.Length > 16384 ? diagnostic[^16384..] : diagnostic);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var operation = arguments.FirstOrDefault(value => value is "up" or "run" or "restart" or "down" or "exec" or "inspect") ?? "command";
            var diagnosticDisposition = includeStandardError ? "raw server-log output was discarded" : "bounded private diagnostics were retained separately";
            throw new InvalidOperationException($"The isolated published-peer Docker {operation} operation failed with exit code {process.ExitCode}; {diagnosticDisposition}.");
        }
        return includeStandardError ? output + "\n" + diagnostic : output;
    }

    public async ValueTask DisposeAsync()
    {
        if (OwnedCleanupComplete) return;
        var failures = new List<Exception>();
        try { Administrator?.Dispose(); } catch (Exception e) { failures.Add(e); }
        try { Anonymous?.Dispose(); } catch (Exception e) { failures.Add(e); }
        if (File.Exists(composeFile))
        {
            // An explicit private diagnostic run also retains failures after startup.
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_PRIVATE_DIAGNOSTIC_DIRECTORY")))
            {
                try { await CapturePrivateFailureDiagnosticsAsync(); }
                catch { /* Optional diagnostic capture must not prevent owned cleanup. */ }
            }
            try
            {
                await ComposeAsync(["down", "--volumes", "--remove-orphans"]);
                // Keep exact compose ownership/private input after failed down.
                // A later bounded dispose must still address the same stack.
                File.Delete(composeFile);
            }
            catch (Exception e) { failures.Add(e); }
        }
        if (!cleanupProxyDisposed)
            try { await Proxy.DisposeAsync(); cleanupProxyDisposed = true; } catch (Exception e) { failures.Add(e); }
        if (!File.Exists(composeFile))
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (Exception e) { failures.Add(e); }
        OwnedCleanupComplete = failures.Count == 0 && cleanupProxyDisposed && !File.Exists(composeFile) && !Directory.Exists(root);
        if (!OwnedCleanupComplete) throw new AggregateException("Owned RatelDesk cleanup was incomplete.", failures);
    }
    private bool cleanupProxyDisposed;
    public bool OwnedCleanupComplete { get; private set; }
}
