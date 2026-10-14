using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TaskExecutionStreamEndpointTests
{
    [Fact]
    public async Task TaskStream_AuthorizesActivityTenant_AndClosesAfterPermissionRevocation()
    {
        var access = new FixtureAccess();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IEffectiveAccessService>(access);
        builder.Services.AddSingleton<ExecutionActivityBroker>();
        builder.Services.AddSingleton(TimeProvider.System);
        var databaseName = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<IJobRunService, JobRunService>();
        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.JobTaskActivities.AddRange(
                new JobTaskActivityRecord { Id = 1, RequestId = "allowed-task", TenantId = 7, Status = "Running", TaskType = "exec-library-script" },
                new JobTaskActivityRecord { Id = 2, RequestId = "other-tenant-task", TenantId = 8, Status = "Running", TaskType = "exec-library-script" });
            await db.SaveChangesAsync();
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTaskExecutionStreamEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var denied = await client.GetAsync("/api/v2/tasks/stream?requestId=other-tenant-task");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var broker = app.Services.GetRequiredService<ExecutionActivityBroker>();
        broker.SetConnected(true);
        using var response = await client.GetAsync("/api/v2/tasks/stream?requestId=allowed-task", HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await reader.ReadLineAsync(deadline.Token))!.Should().Contain("ready").And.Contain("allowed-task");
        (await reader.ReadLineAsync(deadline.Token)).Should().BeEmpty();
        access.Permitted = false;
        broker.Publish(new(Guid.NewGuid(), 7, null, "allowed-task"));
        (await reader.ReadLineAsync(deadline.Token)).Should().BeNull("permission revocation closes the task feed before exposing more evidence");
    }

    private sealed class FixtureAccess : IEffectiveAccessService
    {
        public bool Permitted { get; set; } = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Permitted && tenantId == 7 && permission == NetRatelPermissions.ScriptExecute);
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) => Task.FromResult<int[]?>([7]);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("fixture", false, false, new HashSet<string>()));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture")], "Fixture")), Scheme.Name)));
    }
}
