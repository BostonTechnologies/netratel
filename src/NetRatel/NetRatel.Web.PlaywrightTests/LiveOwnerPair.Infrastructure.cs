using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.Web.PlaywrightTests;

/// <summary>Disposable real API/Web/Pg pair; setup credentials remain private and never enter an artifact.</summary>
internal sealed partial class LiveOwnerPair : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string privateRoot = Path.Combine(Path.GetTempPath(), "netratel-owner-pair", Guid.NewGuid().ToString("N"));
    private readonly string project = "netratel-owner-pair-" + Guid.NewGuid().ToString("N");
    private readonly string humanPassword = "aA1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string humanEmail = "owner@example.test";
    private readonly Guid sourceInstanceId = Guid.NewGuid();
    private readonly Guid ratelDeskInstanceId = Guid.NewGuid();
    private readonly string scenario;
    private readonly bool nrInitiates;
    private readonly OwnerImagePins pins;
    private readonly List<ImageIdentity> images = [];
    private readonly List<VisualReceipt> visuals = [];
    private readonly Dictionary<string, int> databaseVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> commandCounts = new(StringComparer.Ordinal);
    private readonly HttpClient anonymous = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string evidenceRoot;
    private string composeFile => Path.Combine(privateRoot, "compose.json");
    private string host = "";
    public string NetRatelWeb { get; private set; } = "";
    public string NetRatelApi { get; private set; } = "";
    public string RatelDeskWeb { get; private set; } = "";
    public string RatelDeskApi { get; private set; } = "";
    private string NetRatelIssuer => NetRatelApi + "/services";
    private string RatelDeskIssuer => RatelDeskApi + "/services";
    private string netRatelInstanceId = "";
    private string tenantId = "", agentId = "", definitionId = "", organizationId = "", customerId = "", attemptId = "";
    private IAPIRequestContext? browserApi;
    private bool responderStopped, disposed;
    private int pageErrorCount;
    private int approvalHopCount, callbackHopCount, protectedHopFailures;
    private int? lastHttpStatus;
    private ServiceLinkAdminStatus? finalNrStatus, finalRdStatus;
    private ServiceLinkMetadata? nrMetadata, rdMetadata;
    private OriginalObservation? original;
    private bool readOnlyProbePassed;
    private string InitiatorWeb => nrInitiates ? NetRatelWeb : RatelDeskWeb;
    private string ResponderWeb => nrInitiates ? RatelDeskWeb : NetRatelWeb;
    private string IntegrationUrl(bool netRatel, bool openSetup = false) => (netRatel ? NetRatelWeb : RatelDeskWeb) + "/account/integration-credentials"
        + (openSetup ? "?purpose=" + (netRatel ? "helpdesk-m2m" : "netratel-m2m") : "");
    private string CaseId => scenario + "-" + (nrInitiates ? "netratel-initiates" : "rateldesk-initiates");

    private LiveOwnerPair(string scenario, bool nrInitiates, OwnerImagePins pins)
    {
        this.scenario = scenario; this.nrInitiates = nrInitiates; this.pins = pins;
        evidenceRoot = Path.GetFullPath(Require("NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY"));
    }

    public static async Task<LiveOwnerPair> StartAsync(string scenario, bool nrInitiates)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The owner Compose acceptance requires its existing hosted Linux lane.");
        if (scenario is not ("connected" or "signed-out" or "peer-loss")) throw new InvalidOperationException("Unknown owner acceptance case.");
        var pins = JsonSerializer.Deserialize<OwnerImagePins>(await File.ReadAllTextAsync(Require("NETRATEL_OWNER_PAIR_PINS_FILE")), Json)
            ?? throw new InvalidOperationException("The actual-image handoff is missing.");
        pins.Validate();
        var pair = new LiveOwnerPair(scenario, nrInitiates, pins);
        try
        {
            Directory.CreateDirectory(pair.privateRoot);
            File.SetUnixFileMode(pair.privateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(pair.evidenceRoot);
            using var bridge = JsonDocument.Parse(await DockerAsync(["network", "inspect", "bridge", "--format", "{{json .IPAM.Config}}"]));
            pair.host = bridge.RootElement[0].GetProperty("Gateway").GetString() ?? throw new InvalidOperationException("The real Compose host gateway is unavailable.");
            if (!IPAddress.TryParse(pair.host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("The disposable pair requires the existing IPv4 Docker host-gateway topology.");
            pair.NetRatelWeb = $"http://{pair.host}:{AllocatePort(address)}";
            pair.NetRatelApi = $"http://{pair.host}:{AllocatePort(address)}";
            pair.RatelDeskWeb = $"http://{pair.host}:{AllocatePort(address)}";
            pair.RatelDeskApi = $"http://{pair.host}:{AllocatePort(address)}";
            if (new[] { pair.NetRatelWeb, pair.NetRatelApi, pair.RatelDeskWeb, pair.RatelDeskApi }.Distinct().Count() != 4)
                throw new InvalidOperationException("The allocated product Web/API addresses must remain distinct.");
            await pair.VerifySelectedImagesAsync();
            pair.WritePrivateCompose();
            await pair.ComposeAsync(["up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "nr-postgres", "rd-postgres"]);
            await pair.VerifyDatabaseVersionsAsync();
            await pair.ComposeAsync(["run", "--rm", "--no-deps", "nr-volume-init"]);
            await pair.ComposeAsync(["run", "--rm", "--no-deps", "nr-migrations"]);
            var initialized = await pair.ComposeAsync(["run", "--rm", "--no-deps", "rd-api", "--initialize-unattended"]);
            if (!initialized.Contains("RatelDesk initialization completed.", StringComparison.Ordinal))
                throw new InvalidOperationException("The published companion did not complete real unattended initialization.");
            // No build/pull/fallback occurs here; every image was selected and verified first.
            await pair.ComposeAsync(["up", "-d", "--no-build", "--pull", "never", "nr-api", "nr-web", "rd-api", "rd-web"]);
            await pair.WaitReadyAsync();
            await pair.VerifyRunningImagesAsync();
            return pair;
        }
        catch
        {
            try { await pair.WriteReceiptAsync("failed", "start-isolated-products", null); }
            finally { await pair.DisposeAsync(); }
            throw;
        }
    }

    private async Task VerifySelectedImagesAsync()
    {
        foreach (var (service, reference, source, version, expectedId) in new[]
        {
            ("nr-api", pins.NetRatel.Api, pins.NetRatel.Source, pins.NetRatel.Version, pins.NetRatel.ApiId),
            ("nr-web", pins.NetRatel.Web, pins.NetRatel.Source, pins.NetRatel.Version, pins.NetRatel.WebId),
            ("nr-migrations", pins.NetRatel.Migrations, pins.NetRatel.Source, pins.NetRatel.Version, pins.NetRatel.MigrationsId),
            ("rd-api", pins.RatelDesk.Api, pins.RatelDesk.Source, pins.RatelDesk.Version, ""),
            ("rd-web", pins.RatelDesk.Web, pins.RatelDesk.Source, pins.RatelDesk.Version, ""),
            ("nr-postgres", pins.Fixture.NetRatelPostgres, "", "", ""),
            ("rd-postgres", pins.RatelDesk.Postgres, "", "", ""),
            ("nr-volume-init", pins.Fixture.VolumeInit, "", "", "")
        })
        {
            using var selected = JsonDocument.Parse(await DockerAsync(["image", "inspect", reference]));
            var image = selected.RootElement[0];
            var id = image.GetProperty("Id").GetString()!;
            Check(Regex.IsMatch(id, "^sha256:[0-9a-f]{64}$"), "A selected image has no immutable local identity.");
            if (expectedId.Length > 0) Check(id == expectedId, "The candidate image differs from the hosted source-build handoff.");
            if (source.Length > 0)
            {
                var labels = image.GetProperty("Config").GetProperty("Labels");
                Check(labels.GetProperty("org.opencontainers.image.revision").GetString() == source && labels.GetProperty("org.opencontainers.image.version").GetString() == version,
                    "The selected product image source/version labels differ from the immutable handoff.");
            }
            if (reference.Contains('@'))
                Check(image.GetProperty("RepoDigests").EnumerateArray().Any(x => x.GetString() == reference), "The selected registry image does not match its pinned repository digest.");
            images.Add(new(service, reference, id, source, version));
        }
    }

    private async Task VerifyRunningImagesAsync()
    {
        foreach (var selected in images.Where(x => x.Service is not ("nr-volume-init" or "nr-migrations")))
        {
            var container = (await ComposeAsync(["ps", "-q", selected.Service])).Trim();
            Check(Regex.IsMatch(container, "^[0-9a-f]{12,64}$"), "A required actual product/database container is absent.");
            var actual = (await DockerAsync(["inspect", container, "--format", "{{.Image}}"])).Trim();
            Check(actual == selected.Id, "A running product/database uses a different image from its selected identity.");
        }
    }

    private async Task VerifyDatabaseVersionsAsync()
    {
        foreach (var (service, database, expectedMajor) in new[] { ("nr-postgres", "netratel", 17), ("rd-postgres", "rateldesk", 16) })
        {
            var actual = int.Parse((await ComposeAsync(["exec", "-T", service, "psql", "-U", database, "-d", database, "-At", "-c", "SELECT current_setting('server_version_num')"])).Trim(), CultureInfo.InvariantCulture);
            Check(actual / 10000 == expectedMajor, "The independently pinned actual fixture database differs from its required source-profile major version.");
            databaseVersions[service] = actual;
        }
    }

    private void WritePrivateCompose()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The owner Compose acceptance requires its existing hosted Linux lane.");
        using var agentSigning = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agentKey = Path.Combine(privateRoot, "agent-signing.pem");
        File.WriteAllText(agentKey, agentSigning.ExportECPrivateKeyPem());
        File.SetUnixFileMode(agentKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // The synthetic foundation producer exists once in this private fixture seed.
        // Its explicit UI adoption does not impersonate or prove a future Flow adapter.
        var sourceFile = Path.Combine(privateRoot, "fixture-producer.json");
        File.WriteAllText(sourceFile, JsonSerializer.Serialize(new { sourceInstanceId, owner = project }));
        File.SetUnixFileMode(sourceFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var nrDbPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var rdDbPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var nrConnection = $"Host=nr-postgres;Database=netratel;Username=netratel;Password={nrDbPassword}";
        var rdConnection = $"Host=rd-postgres;Database=rateldesk;Username=rateldesk;Password={rdDbPassword}";
        var bypass = LocalNoProxy(host);
        var nrApi = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["ASPNETCORE_URLS"] = "http://+:9222",
            ["ConnectionStrings__NetRatelDb"] = nrConnection, ["DataProtection__KeysDirectory"] = "/var/netratel/keys",
            ["Authentication__Mode"] = "Local", ["Authentication__MachineToken__Enabled"] = "false",
            ["Authentication__Local__AllowInsecureLocalhost"] = "true",
            ["Bootstrap__AllowedOrigins__0"] = NetRatelWeb, ["AgentAuth__PrivateKeyPath"] = "/run/netratel-secrets/agent-signing.pem",
            // Only explicit disposable transport/policy options; public identity and enabled state are entered through the owner UI.
            ["ServiceIdentity__AllowPrivateHttp"] = "true", ["ServiceLinks__AllowPrivateHttp"] = "true",
            ["ServiceLinks__BootstrapLifetimeSeconds"] = "120", ["ServiceLinks__WorkerIntervalSeconds"] = "2", ["ServiceLinks__AutomaticRotationEnabled"] = "false",
            ["NO_PROXY"] = bypass, ["no_proxy"] = bypass
        };
        var nrWeb = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["ASPNETCORE_URLS"] = "http://+:9111", ["ApiBaseUrl"] = "http://nr-api:9222",
            ["ConnectionStrings__NetRatelDb"] = nrConnection, ["NetRatel_KEYS_DIR"] = "/var/netratel/keys",
            ["Authentication__Mode"] = "Local", ["Authentication__MachineToken__Enabled"] = "false", ["Authentication__Local__AllowInsecureLocalhost"] = "true",
            ["NO_PROXY"] = bypass, ["no_proxy"] = bypass
        };
        var rdApi = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["Authentication__Mode"] = "Local", ["Authentication__AllowInsecureLocalhost"] = "true",
            ["DataProtection__KeyRingPath"] = "/var/lib/rateldesk/keys", ["Bootstrap__StateDirectory"] = "/var/lib/rateldesk/bootstrap",
            ["Bootstrap__DataDirectory"] = "/var/lib/rateldesk/data", ["StorageOptions__RootPath"] = "/app/storage",
            ["Bootstrap__Unattended__Provider"] = "PostgreSql", ["Bootstrap__Unattended__PostgreSqlConnectionString"] = rdConnection,
            ["Bootstrap__Unattended__Email"] = humanEmail, ["Bootstrap__Unattended__DisplayName"] = "Disposable owner",
            ["Bootstrap__Unattended__Password"] = humanPassword, ["Bootstrap__Unattended__OrganizationName"] = "Disposable owner organization",
            ["Bootstrap__Unattended__ApplicationUrl"] = RatelDeskWeb,
            ["ServiceIdentity__Enabled"] = "true", ["ServiceIdentity__Issuer"] = RatelDeskIssuer, ["ServiceIdentity__Audience"] = "rateldesk.owner-browser.services",
            ["ServiceIdentity__ApiBaseUrl"] = RatelDeskApi, ["ServiceIdentity__WebBaseUrl"] = RatelDeskWeb,
            ["ServiceIdentity__InstanceId"] = ratelDeskInstanceId.ToString("D"), ["ServiceIdentity__AllowPrivateHttp"] = "true",
            ["ServiceLinks__Enabled"] = "true", ["ServiceLinks__ApiBaseUrl"] = RatelDeskApi, ["ServiceLinks__WebBaseUrl"] = RatelDeskWeb,
            ["ServiceLinks__AllowPrivateHttp"] = "true", ["ServiceLinks__BootstrapLifetimeSeconds"] = "120",
            ["ServiceLinks__WorkerIntervalSeconds"] = "2", ["ServiceLinks__AutomaticRotationEnabled"] = "false", ["NO_PROXY"] = bypass, ["no_proxy"] = bypass
        };
        var rdWeb = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production", ["ApiBaseUrl"] = "http://rd-api:8222/",
            ["ReverseProxy__Clusters__apiCluster__Destinations__api1__Address"] = "http://rd-api:8222/",
            ["DataProtection__KeyRingPath"] = "/var/lib/rateldesk/keys", ["Authentication__Mode"] = "Local",
            ["Authentication__AllowInsecureLocalhost"] = "true", ["NO_PROXY"] = bypass, ["no_proxy"] = bypass
        };
        object Database(string image, string database, string password, string volume) => new
        {
            image, pull_policy = "never", environment = new Dictionary<string, string>
            {
                ["POSTGRES_DB"] = database, ["POSTGRES_USER"] = database, ["POSTGRES_PASSWORD"] = password
            },
            volumes = new[] { volume + ":/var/lib/postgresql/data" },
            healthcheck = new { test = new[] { "CMD-SHELL", $"pg_isready -U {database} -d {database}" }, interval = "2s", timeout = "2s", retries = 45 }
        };
        object App(string image, Dictionary<string, string> environment, string url, int port, string[] volumes) => new
        {
            image, pull_policy = "never", environment, ports = new[] { $"{host}:{new Uri(url).Port}:{port}" }, volumes
        };
        var compose = new
        {
            services = new Dictionary<string, object>
            {
                ["nr-postgres"] = Database(pins.Fixture.NetRatelPostgres, "netratel", nrDbPassword, "nr-database"),
                ["rd-postgres"] = Database(pins.RatelDesk.Postgres, "rateldesk", rdDbPassword, "rd-database"),
                ["nr-volume-init"] = new { image = pins.Fixture.VolumeInit, pull_policy = "never", user = "0:0",
                    command = new[] { "sh", "-ceu", "chown -R 1654:1654 /var/netratel; chmod 700 /var/netratel/keys; chown 1654:1654 /agent-signing.pem; chmod 600 /agent-signing.pem" },
                    volumes = new[] { "nr-data:/var/netratel", "nr-keys:/var/netratel/keys", agentKey + ":/agent-signing.pem" } },
                ["nr-migrations"] = new { image = pins.NetRatel.MigrationsId, pull_policy = "never", environment = new Dictionary<string, string> { ["ConnectionStrings__NetRatelDb"] = nrConnection, ["NO_PROXY"] = bypass, ["no_proxy"] = bypass } },
                ["nr-api"] = App(pins.NetRatel.ApiId, nrApi, NetRatelApi, 9222, ["nr-data:/var/netratel", "nr-keys:/var/netratel/keys", agentKey + ":/run/netratel-secrets/agent-signing.pem:ro"]),
                ["nr-web"] = App(pins.NetRatel.WebId, nrWeb, NetRatelWeb, 9111, ["nr-keys:/var/netratel/keys"]),
                ["rd-api"] = App(pins.RatelDesk.Api, rdApi, RatelDeskApi, 8222, ["rd-keys:/var/lib/rateldesk/keys", "rd-bootstrap:/var/lib/rateldesk/bootstrap", "rd-data:/var/lib/rateldesk/data", "rd-storage:/app/storage"]),
                ["rd-web"] = App(pins.RatelDesk.Web, rdWeb, RatelDeskWeb, 8111, ["rd-keys:/var/lib/rateldesk/keys"])
            },
            volumes = new Dictionary<string, object> { ["nr-database"] = new { }, ["nr-data"] = new { }, ["nr-keys"] = new { }, ["rd-database"] = new { }, ["rd-keys"] = new { }, ["rd-bootstrap"] = new { }, ["rd-data"] = new { }, ["rd-storage"] = new { } }
        };
        File.WriteAllText(composeFile, JsonSerializer.Serialize(compose, Json));
        File.SetUnixFileMode(composeFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private async Task WaitReadyAsync()
    {
        await WaitHttpAsync(NetRatelApi + "/health/live", [200]);
        await WaitHttpAsync(NetRatelWeb + "/api/v2/setup/status", [200]);
        await WaitHttpAsync(RatelDeskApi + "/health/live", [200]);
        await WaitHttpAsync(RatelDeskWeb + "/login", [200]);
    }
    private async Task WaitHttpAsync(string address, int[] statuses)
    {
        for (var step = 0; step < 90; step++)
        {
            try { using var response = await anonymous.GetAsync(address); if (statuses.Contains((int)response.StatusCode)) return; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("An actual fixture product did not reach the required readiness boundary.");
    }
    public async Task StopResponderAsync()
    {
        await ComposeAsync(["stop", nrInitiates ? "rd-api" : "nr-api", nrInitiates ? "rd-web" : "nr-web"]);
        responderStopped = true;
        using var unavailable = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var reached = false;
        try { using var result = await unavailable.GetAsync(ResponderWeb + "/login"); reached = result.IsSuccessStatusCode; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        Check(!reached, "The actual peer interruption did not stop the selected owned Web.");
    }
    public async Task RestartResponderAsync()
    {
        await ComposeAsync(["start", nrInitiates ? "rd-api" : "nr-api", nrInitiates ? "rd-web" : "nr-web"]);
        await WaitReadyAsync();
        responderStopped = false;
        await VerifyRunningImagesAsync();
    }
    private Task<string> ComposeAsync(string[] arguments) => DockerAsync(["compose", "-p", project, "-f", composeFile, .. arguments]);
    private static async Task<string> DockerAsync(string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start the owned fixture operation.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw new InvalidOperationException("The owned fixture Docker operation timed out."); }
        var output = await stdout;
        _ = await stderr; // Raw logs/Compose diagnostics can contain passwords or proof; never publish them.
        Check(process.ExitCode == 0, "The owned fixture Docker operation failed; raw diagnostics are deliberately excluded from public artifacts.");
        return output;
    }
    private static int AllocatePort(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private static string LocalNoProxy(string host)
    {
        var existing = new List<string?> { Environment.GetEnvironmentVariable("NO_PROXY"), Environment.GetEnvironmentVariable("no_proxy") };
        var config = Path.Combine(Environment.GetEnvironmentVariable("DOCKER_CONFIG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker"), "config.json");
        if (File.Exists(config))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(config));
            if (document.RootElement.TryGetProperty("proxies", out var proxies) && proxies.TryGetProperty("default", out var defaults) && defaults.TryGetProperty("noProxy", out var bypass)) existing.Add(bypass.GetString());
        }
        return string.Join(',', existing.Where(x => !string.IsNullOrWhiteSpace(x)).SelectMany(x => x!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Concat(["localhost", "127.0.0.1", "nr-api", "nr-web", "nr-postgres", "rd-api", "rd-web", "rd-postgres", host]).Distinct(StringComparer.OrdinalIgnoreCase));
    }
    private static string Require(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("A required owner-pair handoff input is absent.");
    private static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        anonymous.Dispose();
        try
        {
            if (File.Exists(composeFile)) await ComposeAsync(["down", "--volumes", "--remove-orphans"]);
        }
        finally
        {
            // A teardown failure remains a failure, but cannot retain the generated
            // human password, private key or Compose environment file on disk.
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
        // No global prune, image deletion, external-volume removal, or source edit.
    }

    private sealed record ImageIdentity(string Service, string Reference, string Id, string Source, string Version);
    private sealed record VisualReceipt(string File, string Sha256, string Product, string Phase, string Theme, int Width, int Height, int ZoomPercent);
    private sealed record OwnerImagePins(CandidateImages NetRatel, CompanionImages RatelDesk, FixtureImages Fixture)
    {
        public void Validate()
        {
            Check(Regex.IsMatch(NetRatel.Source, "^[0-9a-f]{40}$") && Regex.IsMatch(RatelDesk.Source, "^[0-9a-f]{40}$"), "Both actual product source pins are required.");
            Check(!string.IsNullOrWhiteSpace(NetRatel.Version) && !string.IsNullOrWhiteSpace(RatelDesk.Version) && !NetRatel.Version.Contains("__") && !RatelDesk.Version.Contains("__"), "Both actual product version pins are required.");
            Check(new[] { NetRatel.ApiId, NetRatel.WebId, NetRatel.MigrationsId }.All(x => Regex.IsMatch(x, "^sha256:[0-9a-f]{64}$")), "All candidate source-image IDs must be pinned by the hosted build handoff.");
            Check(new[] { RatelDesk.Api, RatelDesk.Web, RatelDesk.Postgres, Fixture.NetRatelPostgres, Fixture.VolumeInit }.All(x => Regex.IsMatch(x, @"^[a-z0-9./:_-]+@sha256:[0-9a-f]{64}$")), "The published companion and auxiliary fixture images require exact repository digest pins.");
        }
    }
    private sealed record CandidateImages(string Version, string Source, string Api, string Web, string Migrations, string ApiId, string WebId, string MigrationsId);
    private sealed record CompanionImages(string Version, string Source, string Api, string Web, string Postgres);
    private sealed record FixtureImages(string NetRatelPostgres, string VolumeInit);
}
