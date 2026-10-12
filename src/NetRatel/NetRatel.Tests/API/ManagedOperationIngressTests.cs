using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Integration;
using NetRatel.API.Security.M2M;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class ManagedOperationIngressTests
{
    private const string Path = "/api/v2/mcp/operator/application/netratel_clients/metadata";

    [Fact]
    public async Task Real_api_credential_reaches_managed_handler_without_opening_default_authenticated_routes()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var response = await scenario.ReadAsync(scenario.ApiCredential.Secret);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var metadata = await response.Content.ReadFromJsonAsync<ClientMetadataDto>();
        metadata!.AgentId.Should().Be(scenario.AgentId);
        metadata.TenantId.Should().Be(7);
        metadata.Tags.Should().Equal("production", "linux");
        (await scenario.AcceptedCountAsync()).Should().Be(1);

        using var legacy = new HttpRequestMessage(HttpMethod.Get, "/default-authentication-probe");
        legacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scenario.ApiCredential.Secret);
        using var denied = await scenario.Client.SendAsync(legacy);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("credential-grant")]
    [InlineData("owner-grant")]
    [InlineData("operation-policy")]
    public async Task Removing_current_authority_denies_the_same_authenticated_api_credential(string removed)
    {
        await using var scenario = await Scenario.CreateAsync();
        using var before = await scenario.ReadAsync(scenario.ApiCredential.Secret);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var scope = scenario.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            var application = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (removed == "credential-grant")
                identity.IntegrationCredentialGrants.RemoveRange(await identity.IntegrationCredentialGrants
                    .Where(grant => grant.CredentialId == scenario.ApiCredential.CredentialId).ToArrayAsync());
            else if (removed == "owner-grant")
                identity.PrincipalRoleAssignments.RemoveRange(await identity.PrincipalRoleAssignments.ToArrayAsync());
            else
                application.McpOperatorPolicies.RemoveRange(await application.McpOperatorPolicies.ToArrayAsync());
            await identity.SaveChangesAsync();
            await application.SaveChangesAsync();
        }

        using var denied = await scenario.ReadAsync(scenario.ApiCredential.Secret);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scenario.AcceptedCountAsync()).Should().Be(1, "the denied request must not reach the managed read handler");
    }

    [Fact]
    public async Task Authenticated_mcp_relay_reaches_handler_only_as_its_verified_current_delegated_account()
    {
        await using var scenario = await Scenario.CreateAsync();
        using var response = await scenario.ReadAsync(scenario.M2mToken(), scenario.Assertion());

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var metadata = await response.Content.ReadFromJsonAsync<ClientMetadataDto>();
        metadata!.AgentId.Should().Be(scenario.AgentId);
        metadata.Tags.Should().Equal("production", "linux");
        (await scenario.AcceptedCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Forbidden)]
    [InlineData("operation", HttpStatusCode.Forbidden)]
    [InlineData("target", HttpStatusCode.Forbidden)]
    [InlineData("signature", HttpStatusCode.Unauthorized)]
    [InlineData("relay-client", HttpStatusCode.Forbidden)]
    public async Task Relay_authentication_cannot_replace_a_verified_exact_delegation(string mismatch, HttpStatusCode expected)
    {
        await using var scenario = await Scenario.CreateAsync();
        var assertion = mismatch == "missing" ? null : scenario.Assertion(
            operation: mismatch == "operation" ? "set_metadata" : "metadata",
            agentId: mismatch == "target" ? Guid.NewGuid() : scenario.AgentId);
        if (mismatch == "signature") assertion += "invalid";

        using var response = await scenario.ReadAsync(scenario.M2mToken(mismatch == "relay-client" ? "unapproved-relay" : "mcp-service"), assertion);

        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Direct_api_credential_cannot_attach_another_accounts_delegation_to_impersonate_a_relay()
    {
        await using var scenario = await Scenario.CreateAsync();

        using var response = await scenario.ReadAsync(scenario.ApiCredential.Secret, scenario.Assertion());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("ManagedService")]
    [InlineData("System")]
    public async Task Other_authenticated_identity_kinds_cannot_borrow_an_account_grant(string kind)
    {
        await using var scenario = await Scenario.CreateAsync();
        using var request = scenario.Request();
        request.Headers.Authorization = new AuthenticationHeaderValue("Boundary", kind);

        using var response = await scenario.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    private sealed partial class Scenario : IAsyncDisposable
    {
        private const string Authority = "https://api.example.test";
        private const string Audience = "netratel-api";
        private const string Resource = "https://mcp.example.test/mcp";
        private readonly SymmetricSecurityKey _relayKey = new(RandomNumberGenerator.GetBytes(32));
        private readonly McpOperatorDelegationOptions _delegation = new()
        {
            Enabled = true, Issuer = "test-mcp", Audience = "test-api", ServicePrincipal = "netratel-mcp-http",
            KeyId = "test", SharedKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        public string Owner { get; } = Guid.NewGuid().ToString("D");
        public Guid AgentId { get; } = Guid.NewGuid();
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public IntegrationCredentialSecret ApiCredential { get; private set; } = null!;
        private IntegrationCredentialSecret _httpCredential = null!;

        public static async Task<Scenario> CreateAsync(bool includeFiles = false)
        {
            var scenario = new Scenario();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var identityName = Guid.NewGuid().ToString("N");
            var applicationName = Guid.NewGuid().ToString("N");
            builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase(identityName));
            builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(applicationName));
            builder.Services.AddScoped<IIntegrationCredentialService, IntegrationCredentialService>();
            builder.Services.AddScoped<IIntegrationCredentialCurrentVerifier, IntegrationCredentialService>();
            builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
            builder.Services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
            builder.Services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
            builder.Services.AddScoped<ManagedOperationAdmission>();
            builder.Services.AddSingleton<IApplicationPrincipalResolver, NoExternalIdentity>();
            builder.Services.AddSingleton<IClientPresenceReadModel, OfflinePresence>();
            builder.Services.AddSingleton(scenario._delegation);
            builder.Services.AddSingleton<McpOperatorDelegationTokenService>();
            builder.Services.AddSingleton(scenario);
            builder.Services.AddSingleton(Options.Create(new M2MOptions
            {
                Authority = Authority, Audience = Audience, AllowedCallerClientIds = ["mcp-service"]
            }));
            var authentication = builder.Services.AddAuthentication("Bearer")
                .AddPolicyScheme("Bearer", "Test credential selector", options => options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString().StartsWith("Boundary ", StringComparison.Ordinal)
                        ? "Boundary" : context.Request.Headers.Authorization.ToString().StartsWith("Bearer nrt_ic_", StringComparison.Ordinal)
                            ? IntegrationCredentialAuthenticationHandler.SchemeName : "M2M")
                .AddScheme<AuthenticationSchemeOptions, IntegrationCredentialAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName, _ => { })
                .AddScheme<AuthenticationSchemeOptions, BoundaryAuthenticationHandler>("Boundary", _ => { })
                .AddJwtBearer("M2M", options => options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Authority, ValidAudience = Audience, IssuerSigningKey = scenario._relayKey,
                    ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                    ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.Zero, NameClaimType = "client_id"
                });
            ManagedOperationAuthorization.AddAuthentication(authentication);
            builder.Services.AddAuthorization(options =>
            {
                // Match Program's existing default boundary, which must stay closed to API credentials.
                options.DefaultPolicy = new AuthorizationPolicyBuilder("Bearer").RequireAuthenticatedUser()
                    .RequireAssertion(context => IntegrationCredentialAuthorizationBoundary.AllowsDefaultAuthenticatedRoute(context.User)).Build();
                ManagedOperationAuthorization.AddPolicies(options);
            });
            builder.Services.AddSingleton<IAuthorizationHandler, AllowedClientHandler>();
            ManagedOperationAuthorization.AddHandlers(builder.Services);
            if (includeFiles) scenario.AddFileServices(builder.Services);
            scenario.App = builder.Build();
            scenario.App.UseAuthentication();
            scenario.App.UseMiddleware<McpOperatorDelegationMiddleware>();
            scenario.App.UseAuthorization();
            scenario.App.MapManagedOperationEndpoints();
            if (includeFiles) scenario.App.MapMcpOperatorClientFileEndpoints();
            scenario.App.MapGet("/default-authentication-probe", () => "default route entered").RequireAuthorization();
            await scenario.SeedAsync(includeFiles);
            await scenario.App.StartAsync();
            scenario.Client = scenario.App.GetTestClient();
            return scenario;
        }

        private async Task SeedAsync(bool includeFiles)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            identity.ApplicationPrincipals.Add(new ApplicationPrincipal { Id = Owner });
            identity.Users.Add(new LocalUser { Id = Guid.NewGuid().ToString("D"), PrincipalId = Owner, UserName = "operator", IsEnabled = true });
            var role = new AccessRole { Name = "Scoped reader" };
            role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
            if (includeFiles)
                foreach (var permission in FilePermissions)
                    role.Permissions.Add(new AccessRolePermission { Permission = permission });
            identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = Owner, Role = role, RoleId = role.Id, TenantId = 7 });
            await identity.SaveChangesAsync();
            var credentials = scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>();
            var grants = role.Permissions.Select(permission => new IntegrationCredentialGrantRequest(7, permission.Permission)).ToArray();
            ApiCredential = await credentials.CreateAsync(Owner, new("CLI", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddHours(1), grants));
            _httpCredential = await credentials.CreateAsync(Owner, new("HTTP MCP", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddHours(1), grants, Resource));

            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Tenants.Add(new Tenant { Id = 7, Name = "Selected tenant" });
            db.Agents.Add(new Agent { Id = AgentId, TenantId = 7, Name = "enrolled-client", DeviceInfoJson = "{}", Tags = ["production", "linux"] });
            db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
            {
                TenantId = 7, AgentId = AgentId, Classification = McpOperatorTargetClassification.ManagedStandard,
                TagsJson = "[]", UpdatedBy = "policy-admin", Version = 1
            });
            db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
            {
                Id = Guid.NewGuid(), Name = "Read this client", Environment = McpOperatorEnvironment.Production, Effect = McpOperatorPolicyEffect.Allow,
                PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = Owner,
                TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = 7, AgentId = AgentId,
                OperationFamily = McpOperatorOperationFamily.ClientAdministration, Operation = "netratel_clients/metadata",
                ConstraintsJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "policy-admin", Version = 1
            });
            if (includeFiles) SeedFilePolicies(db);
            await db.SaveChangesAsync();
        }

        public HttpRequestMessage Request() => new(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new { request = new { tenantId = 7, agentId = AgentId }, confirm = false })
        };

        public async Task<HttpResponseMessage> ReadAsync(string token, string? delegation = null)
        {
            using var request = Request();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (delegation is not null) request.Headers.Add(McpOperatorDelegationOptions.HeaderName, delegation);
            return await Client.SendAsync(request);
        }

        public string M2mToken(string clientId = "mcp-service") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Authority, Audience, [new Claim("client_id", clientId)], expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(_relayKey, SecurityAlgorithms.HmacSha256)));

        public string Assertion(string operation = "metadata", Guid? agentId = null) => new McpOperatorDelegationTokenService(_delegation).Create(
            new McpOperatorDelegationIdentity(Owner, "operator-client", null, [], ["Operator"], ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest("netratel_clients", operation, Guid.NewGuid().ToString("N"), Resource, "prod", 7, agentId ?? AgentId)
            { IngressCredentialId = _httpCredential.CredentialId, IngressPermission = NetRatelPermissions.TelemetryRead });

        public async Task<int> AcceptedCountAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().McpOperatorAcceptedAudits.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class BoundaryAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, Scenario scenario) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var kind = Request.Headers.Authorization.ToString()["Boundary ".Length..];
            Claim[] claims = [new("netratel_principal_id", scenario.Owner), new("client_id", "mcp-service")];
            if (kind == "ManagedService") claims = [.. claims, new("auth_mode", "service"), new("token_use", "netratel_service")];
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, kind)), Scheme.Name)));
        }
    }

    private sealed class NoExternalIdentity : IApplicationPrincipalResolver
    {
        public Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class OfflinePresence : IClientPresenceReadModel
    {
        public Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceReadModelSnapshot(0, []));
        public Task<ClientPresenceSnapshot?> GetClientSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult<ClientPresenceSnapshot?>(null);
    }
}
