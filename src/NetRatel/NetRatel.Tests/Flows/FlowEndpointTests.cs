using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Middleware;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowEndpointTests
{
    private const string Path = "/api/v1/tenants/17/flows";

    [Fact]
    public async Task Tenant_permissions_are_checked_before_disclosure_and_readers_cannot_edit_or_publish()
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var flow = (await (await http.PostAsJsonAsync(Path, new FlowCreateRequest("scoped draft"))).Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        http.DefaultRequestHeaders.Authorization = new("Test", "read");
        (await http.GetAsync(Path.Replace("/17/", "/18/", StringComparison.Ordinal))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.GetAsync(Path.Replace("/17/", "/18/", StringComparison.Ordinal) + $"/{flow.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.PutAsJsonAsync($"{Path}/{flow.Id}/draft", new FlowSaveDraftRequest(flow.Revision, "unauthorized", FlowTestData.Graph()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.PostAsJsonAsync($"{Path}/{flow.Id}/publish", new FlowRevisionRequest(flow.Revision))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var tenants = await http.GetFromJsonAsync<FlowTenantAccessDto[]>("/api/v1/flows/tenants");
        tenants.Should().ContainSingle(); tenants![0].TenantId.Should().Be(17); tenants[0].CanEdit.Should().BeFalse(); tenants[0].CanPublish.Should().BeFalse();
        (await http.GetFromJsonAsync<FlowDefinitionDto>($"{Path}/{flow.Id}"))!.Name.Should().Be("scoped draft");
    }

    [Fact]
    public async Task Page_reads_save_validate_and_dry_run_are_inert_and_unavailable_receiver_cannot_publish()
    {
        using var host = await HostAsync(); var http = host.GetTestClient(); http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var response = await http.PostAsJsonAsync(Path, new FlowCreateRequest("inert editing")); response.StatusCode.Should().Be(HttpStatusCode.OK);
        var flow = (await response.Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        response = await http.PutAsJsonAsync($"{Path}/{flow.Id}/draft", new FlowSaveDraftRequest(flow.Revision, flow.Name, FlowTestData.Graph()));
        flow = (await response.Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        for (var index = 0; index < 2; index++)
        {
            (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await http.GetAsync($"{Path}/{flow.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await http.GetAsync($"{Path}/{flow.Id}/versions")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await http.GetAsync($"{Path}/{flow.Id}/runs")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await http.PostAsJsonAsync(Path + "/validate", flow.Draft)).StatusCode.Should().Be(HttpStatusCode.OK);
            var preview = await http.PostAsJsonAsync(Path + "/dry-run", new FlowDryRunRequest(flow.Draft, FlowTestData.EventData("inert preview", 90)));
            (await preview.Content.ReadFromJsonAsync<FlowDryRunResultDto>())!.Code.Should().Be("inert-preview");
        }
        var publish = await http.PostAsJsonAsync($"{Path}/{flow.Id}/publish", new FlowRevisionRequest(flow.Revision));
        publish.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await publish.Content.ReadAsStringAsync()).Should().Be("{\"code\":\"receiver-idempotency-unverified\"}");
        host.Services.GetRequiredService<ExecutionSentinel>().Calls.Should().Be(0);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.FlowRuns.CountAsync()).Should().Be(0); (await db.FlowActions.CountAsync()).Should().Be(0); (await db.FlowVersions.CountAsync()).Should().Be(0);
        (await http.PostAsync($"{Path}/{flow.Id}/execute", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declared_and_chunked_oversized_bodies_are_bounded_before_JSON_binding(bool chunked)
    {
        using var host = await HostAsync(); var http = host.GetTestClient(); http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"" + new string('x', (int)FlowRequestBodyLimitMiddleware.MaximumBytes) + "\"}");
        using HttpContent content = chunked ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        (await http.PostAsync(Path, content)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        host.Services.GetRequiredService<ExecutionSentinel>().Calls.Should().Be(0);
        await using var scope = host.Services.CreateAsyncScope(); (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().FlowDefinitions.CountAsync()).Should().Be(0);
    }

    private static async Task<IHost> HostAsync()
    {
        var database = Guid.NewGuid().ToString("N");
        var builder = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer(); web.ConfigureServices(services =>
            {
                services.AddRouting(); services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { }); services.AddAuthorization();
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
                services.AddSingleton<IEffectiveAccessService, Access>(); services.AddSingleton<ExecutionSentinel>(); services.AddNetRatelFlows();
                services.AddSingleton<IFlowConnectorCatalog, Catalog>();
                services.AddSingleton<IFlowRuntimeAdapter>(provider => provider.GetRequiredService<ExecutionSentinel>());
                services.AddSingleton<IFlowIncidentActionDispatcher>(provider => provider.GetRequiredService<ExecutionSentinel>());
            });
            web.Configure(app => { app.UseMiddleware<FlowRequestBodyLimitMiddleware>(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapFlowEndpoints()); });
        });
        var host = await builder.StartAsync();
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Tenants.AddRange(new Tenant { Id = 17, Name = "Authorized" }, new Tenant { Id = 18, Name = "Protected" }); await db.SaveChangesAsync(); return host;
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            return Task.FromResult(header.StartsWith("Test ", StringComparison.Ordinal)
                ? AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new("mode", header[5..]), new("netratel_principal_id", FlowTestData.Authority.PrincipalId)], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
        }
    }
    private sealed class Access : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(tenantId == 17 &&
            (principal.FindFirstValue("mode") == "all" || permission == NetRatelPermissions.FlowRead));
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) => Task.FromResult<int[]?>([17]);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Catalog : IFlowConnectorCatalog
    {
        public Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => Task.FromResult<FlowConnectorReferenceDto?>(new(connectorId, tenantId, "Unverified receiver", true, false, "receiver-idempotency-unverified", 1));
        public Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FlowConnectorReferenceDto>>([]);
    }
    private sealed class ExecutionSentinel : IFlowRuntimeAdapter, IFlowIncidentActionDispatcher
    {
        public int Calls { get; private set; }
        public Task<FlowRuntimeResult> ExecuteAsync(FlowRunLease lease, Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> action, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("read-only endpoint executed a runtime"); }
        public Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("read-only endpoint prepared an action"); }
        public Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("read-only endpoint dispatched an action"); }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
