using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>The complete NetRatel Production application, real PostgreSQL, and real administrator cookies.</summary>
internal sealed class ServiceLinkNetRatelPeer : IAsyncDisposable
{
    private readonly global::ApiFactory databaseOwner = new();
    private readonly Dictionary<string, string?> configuration;
    private readonly IInterceptor? interceptor;
    private readonly ServiceLinkNativeListener? nativeListener;
    private readonly ServiceLinkRotationTestPolicy? rotationPolicy;
    private readonly bool useSystemTime;
    private readonly int backendPort = ServiceLinkHttpProxy.AllocatePort();
    private readonly int gatewayPort = ServiceLinkHttpProxy.AllocatePort();
    private global::ApiFactory? sibling;
    private WebApplicationFactory<global::Program>? app;
    public HttpClient Administrator { get; private set; } = null!;
    public HttpClient Anonymous { get; private set; } = null!;
    public IServiceProvider Services => app!.Services;
    public ServiceLinkHttpProxy Proxy { get; }
    public ServiceLinkTestClock Clock { get; } = new();
    public string BaseUrl => Proxy.BaseUrl;
    public string WebBaseUrl => BaseUrl;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string TenantId { get; private set; } = "";
    public Guid SourceInstanceId { get; } = Guid.NewGuid();
    public Guid ResourceId { get; private set; } = Guid.NewGuid();
    public long RequestDefinitionId { get; private set; }

    private ServiceLinkNetRatelPeer(string reachableHost, IInterceptor? interceptor, ServiceLinkNativeListener? nativeListener,
        ServiceLinkRotationTestPolicy? rotationPolicy, bool useSystemTime)
    {
        this.interceptor = interceptor;
        this.nativeListener = nativeListener;
        this.rotationPolicy = rotationPolicy;
        this.useSystemTime = useSystemTime;
        if (nativeListener is not null && (nativeListener.Port == backendPort || nativeListener.Port == gatewayPort || backendPort == gatewayPort))
            throw new InvalidOperationException("The physical listener ports must be independent.");
        Proxy = new($"http://127.0.0.1:{backendPort}", reachableHost);
        configuration = new()
        {
            ["NetRatel_HTTP_PORT"] = backendPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["NetRatelAkka:GatewayGrpcPort"] = gatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ServiceIdentity:Enabled"] = "true",
            ["ServiceIdentity:Issuer"] = BaseUrl,
            ["ServiceIdentity:ApiBaseUrl"] = BaseUrl,
            ["ServiceIdentity:WebBaseUrl"] = BaseUrl,
            ["ServiceIdentity:Audience"] = "netratel.service-link-http-tests",
            ["ServiceIdentity:InstanceId"] = InstanceId.ToString("D"),
            ["ServiceIdentity:AllowPrivateHttp"] = "true",
            ["ServiceLinks:Enabled"] = "true",
            ["ServiceLinks:ApiBaseUrl"] = BaseUrl,
            ["ServiceLinks:WebBaseUrl"] = BaseUrl,
            ["ServiceLinks:AllowPrivateHttp"] = "true",
            ["ServiceLinks:BootstrapLifetimeSeconds"] = "120",
            ["ServiceLinks:AutomaticRotationEnabled"] = "false",
            ["M2M:Audience"] = "netratel.api",
            ["M2M:AllowedCallerClientIds:0"] = "synthetic-legacy-allowed"
        };
        if (nativeListener is not null) configuration["ServiceLinks:GatewayBaseUrl"] = nativeListener.Endpoint;
        if (rotationPolicy is not null)
        {
            configuration["ServiceLinks:AutomaticRotationEnabled"] = (rotationPolicy.Automatic && rotationPolicy.NetRatelIssuer).ToString();
            configuration["ServiceLinks:RotationAgeDays"] = "1";
            configuration["ServiceLinks:RotationOverlapSeconds"] = "60";
            configuration["ServiceLinks:RotationOfferLifetimeSeconds"] = "120";
            configuration["ServiceLinks:WorkerIntervalSeconds"] = "1";
            configuration["ServiceIdentity:AccessTokenLifetimeSeconds"] = "900";
            configuration["ServiceIdentity:ClockSkewSeconds"] = "0";
        }
    }

    public static async Task<ServiceLinkNetRatelPeer> CreateAsync(string reachableHost, IInterceptor? interceptor = null, ServiceLinkNativeListener? nativeListener = null,
        ServiceLinkRotationTestPolicy? rotationPolicy = null, bool useSystemTime = false)
    {
        var peer = new ServiceLinkNetRatelPeer(reachableHost, interceptor, nativeListener, rotationPolicy, useSystemTime);
        try
        {
            await peer.databaseOwner.InitializeAsync();
            await peer.Proxy.StartAsync();
            await peer.StartAsync();
            using var tenants = await peer.Administrator.GetFromJsonAsync<System.Text.Json.JsonDocument>("/api/v2/access/tenants");
            peer.TenantId = tenants!.RootElement.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "OpenAPI tenant")
                .GetProperty("tenantId").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var identity = await peer.Administrator.GetFromJsonAsync<ServiceLinkIdentityDto>("/api/v1/admin/service-links/identity");
            using var adoption = await peer.AdminAsync("/api/v1/admin/service-links/identity/source",
                new ServiceLinkAdoptSourceRequest(peer.SourceInstanceId.ToString("D"), identity!.Revision));
            adoption.EnsureSuccessStatusCode();
            await using (var scope = peer.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var tenant = int.Parse(peer.TenantId, System.Globalization.CultureInfo.InvariantCulture);
                db.Agents.Add(new() { Id = peer.ResourceId, TenantId = tenant, Name = "Synthetic reciprocal target", Status = AgentStatus.Active, IsEnabled = true, CreatedAtUtc = peer.Clock.GetUtcNow() });
                var definition = new JobDefinition { TenantId = tenant, AgentId = peer.ResourceId, Name = "Synthetic approved definition", CreatedAtUtc = peer.Clock.GetUtcNow(), UpdatedAtUtc = peer.Clock.GetUtcNow() };
                db.Jobs.Add(definition);
                await db.SaveChangesAsync();
                peer.RequestDefinitionId = definition.Id;
                // The same current tenant/agent has another real definition, deliberately outside the approved tuple.
                db.Jobs.Add(new JobDefinition { TenantId = tenant, AgentId = peer.ResourceId, Name = "Unapproved sibling definition", CreatedAtUtc = peer.Clock.GetUtcNow(), UpdatedAtUtc = peer.Clock.GetUtcNow() });
                await db.SaveChangesAsync();
            }
            return peer;
        }
        catch { await peer.DisposeAsync(); throw; }
    }

    private async Task StartAsync()
    {
        sibling = databaseOwner.CreateRuntimeSibling(configuration);
        app = sibling.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // This fixture keeps REST/h2c separate from the native TLS/HTTP2 socket.
            // ASP.NET's -1 sentinel prevents that socket becoming a REST redirect target.
            if (nativeListener is not null)
                services.Configure<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>(options => options.HttpsPort = -1);
            services.RemoveAll<TimeProvider>();
            // Physical, live rotation and explicitly selected payload acceptance
            // use real time. Deterministic recovery fixtures retain their clock.
            services.AddSingleton<TimeProvider>(useSystemTime || nativeListener is not null || rotationPolicy is not null ? TimeProvider.System : Clock);
            // Existing recovery fixtures drive steps explicitly. Live rotation
            // retains the actual production scheduler and every other service.
            if (rotationPolicy is null)
                foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ServiceLinkWorker)).ToArray())
                    services.Remove(worker);
            services.AddDbContext<OrchestratorDbContext>(o =>
            {
                o.LogTo(Console.Error.WriteLine, LogLevel.Error);
                if (interceptor is not null) o.AddInterceptors(interceptor);
            });
        }));
        if (nativeListener is null) app.UseKestrel(backendPort);
        else app.UseKestrel(nativeListener.ConfigureKestrel);
        app.StartServer();
        Anonymous = NewClient();
        Administrator = NewClient(cookies: true);
        using var login = await Administrator.PostAsJsonAsync("/api/v2/local-auth/login", new
        {
            Email = global::ApiFactory.LocalAdministratorEmail,
            Password = global::ApiFactory.LocalAdministratorPassword,
            RememberMe = false
        });
        if (login.StatusCode != HttpStatusCode.NoContent)
        {
            var detail = "no problem detail";
            if (login.Content.Headers.ContentType?.MediaType == "application/problem+json")
            {
                using var problem = await login.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
                if (problem?.RootElement.TryGetProperty("detail", out var value) == true && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    detail = (value.GetString() ?? detail).Replace(global::ApiFactory.LocalAdministratorPassword, "[redacted]", StringComparison.Ordinal);
            }
            throw new InvalidOperationException($"The real local administrator login failed with HTTP {(int)login.StatusCode}: {detail}.");
        }
    }

    private HttpClient NewClient(bool cookies = false) => new(new HttpClientHandler
    { AllowAutoRedirect = false, UseProxy = false, UseCookies = cookies }) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

    public async Task RestartAsync()
    {
        Administrator.Dispose(); Anonymous.Dispose();
        await app!.DisposeAsync();
        sibling!.Dispose();
        await StartAsync();
    }

    public async Task CreateNativeJobAndSelectTargetAsync(Guid enrolledAgentId, string name, CancellationToken ct)
    {
        if (nativeListener is null || enrolledAgentId == Guid.Empty)
            throw new InvalidOperationException("A complete physical listener and genuinely enrolled Agent-ID are required.");
        var tenant = int.Parse(TenantId, CultureInfo.InvariantCulture);
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (await db.Set<ServiceLinkAttempt>().AnyAsync(ct))
                throw new InvalidOperationException("The actual command target must be selected before human link approval.");
            var enrolled = await db.Agents.AsNoTracking().SingleAsync(agent => agent.Id == enrolledAgentId && agent.TenantId == tenant, ct);
            if (!enrolled.IsEnabled || enrolled.Status != AgentStatus.Active || enrolled.CreatedBy != "enrollment" ||
                string.IsNullOrWhiteSpace(enrolled.PublicKeyFingerprint))
                throw new InvalidOperationException("The enrolled physical Agent-ID has no current enabled active authority.");
        }
        // An authenticated human creates a new bounded definition. Existing seeded target rows
        // remain outside this new approved tuple and cannot stand in for native execution proof.
        using var response = await Administrator.PostAsJsonAsync("/api/v1/jobs/", new
        {
            Name = name, FolderPath = "/", Description = "disposable bounded real-agent service-link proof",
            TenantId = tenant, ClientIdentity = "", AgentId = enrolledAgentId,
            ExpectedRuntimeSeconds = 60, GraceSeconds = 60, HardTimeoutSeconds = 120
        }, ct);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"Actual human job creation returned HTTP {(int)response.StatusCode}.");
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var id = created.GetProperty("id").GetUInt64();
        if (id == 0 || created.GetProperty("agentId").GetGuid() != enrolledAgentId || created.GetProperty("tenantId").GetInt32() != tenant)
            throw new InvalidOperationException("The actual new job is not bound to the enrolled physical target.");
        ResourceId = enrolledAgentId;
        RequestDefinitionId = checked((long)id);
    }

    public async Task<HttpResponseMessage> AdminAsync(string path, object body) =>
        await Administrator.PostAsJsonAsync(path, ProjectLifecycle(path, body));

    public async Task<HttpResponseMessage> ServiceAsync(string path, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(ProjectLifecycle(path, body));
        return await Anonymous.SendAsync(request);
    }

    internal static object ProjectLifecycle(string path, object body) => body is ServiceLinkLifecycleRequest lifecycle
        ? ServiceLinkLifecycleProjection.Build(path[(path.LastIndexOf('/') + 1)..], lifecycle) : body;

    public async ValueTask DisposeAsync()
    {
        Administrator?.Dispose(); Anonymous?.Dispose();
        if (app is not null) await app.DisposeAsync();
        sibling?.Dispose();
        await Proxy.DisposeAsync();
        await ((IAsyncDisposable)databaseOwner).DisposeAsync();
    }
}

internal sealed class ServiceLinkTestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan value) => now += value;
}
