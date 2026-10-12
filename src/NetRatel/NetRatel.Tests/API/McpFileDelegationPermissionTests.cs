using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Integration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpFileDelegationPermissionTests
{
    [Theory]
    [InlineData("browse", NetRatelPermissions.FileRead)]
    [InlineData("preview_create_directory", NetRatelPermissions.FileWrite)]
    [InlineData("confirm_create_directory", NetRatelPermissions.FileWrite)]
    [InlineData("preview_delete", NetRatelPermissions.FileDelete)]
    [InlineData("confirm_delete", NetRatelPermissions.FileDelete)]
    public async Task Signed_external_OAuth_roles_require_current_canonical_permission_in_the_same_tenant(string operation, string permission)
    {
        await using var scenario = new Scenario();
        (await scenario.InvokeAsync(operation)).Status.Should().Be(StatusCodes.Status403Forbidden);
        var grant = await scenario.GrantAsync(permission, 7);
        (await scenario.InvokeAsync(operation)).Entered.Should().BeTrue();
        (await scenario.InvokeAsync(operation, tenant: 8)).Status.Should().Be(StatusCodes.Status403Forbidden);
        scenario.Db.PrincipalRoleAssignments.Remove(grant);
        await scenario.Db.SaveChangesAsync();
        (await scenario.InvokeAsync(operation)).Status.Should().Be(StatusCodes.Status403Forbidden,
            "revocation is checked even while the signed delegation is still valid");
    }

    [Fact]
    public async Task Missing_or_different_issuer_cannot_borrow_the_external_subjects_grant()
    {
        await using var scenario = new Scenario();
        await scenario.GrantAsync(NetRatelPermissions.FileWrite, 7);
        (await scenario.InvokeAsync("confirm_create_directory", issuer: null)).Status.Should().Be(StatusCodes.Status403Forbidden);
        (await scenario.InvokeAsync("confirm_create_directory", issuer: "https://other-issuer.example")).Status.Should().Be(StatusCodes.Status403Forbidden);
        (await scenario.InvokeAsync("confirm_create_directory")).Entered.Should().BeTrue();
    }

    [Fact]
    public async Task Move_requires_write_and_delete_for_one_tenant_in_exchange_and_execution()
    {
        await using var scenario = new Scenario();
        await scenario.GrantAsync(NetRatelPermissions.FileWrite, 7);
        await scenario.GrantAsync(NetRatelPermissions.FileDelete, 8);
        (await scenario.InvokeAsync("confirm_move")).Status.Should().Be(StatusCodes.Status403Forbidden);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", Scenario.Owner)], "Oidc"));
        var access = scenario.Services.GetRequiredService<IEffectiveAccessService>();
        (await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, "netratel_files", "preview_move", 7, default)).Should().BeFalse();
        await scenario.GrantAsync(NetRatelPermissions.FileDelete, 7);
        (await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, "netratel_files", "preview_move", 7, default)).Should().BeTrue();
        (await scenario.InvokeAsync("confirm_move")).Entered.Should().BeTrue();
    }

    [Fact]
    public async Task Local_file_assertions_require_exact_permission_and_both_owner_and_credential_grants()
    {
        await using var scenario = new Scenario();
        await scenario.GrantAsync(NetRatelPermissions.FileWrite, 7);
        await scenario.GrantAsync(NetRatelPermissions.FileDelete, 7);
        var credential = new IntegrationCredential
        {
            OwnerPrincipalId = Scenario.Owner, Purpose = IntegrationCredentialPurpose.HttpMcp,
            Resource = Scenario.Resource, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Grants = [new() { TenantId = 7, Permission = NetRatelPermissions.FileWrite }]
        };
        scenario.Db.IntegrationCredentials.Add(credential);
        await scenario.Db.SaveChangesAsync();
        (await scenario.InvokeAsync("confirm_delete", credential: credential.Id, ingress: NetRatelPermissions.FileWrite))
            .Status.Should().Be(StatusCodes.Status401Unauthorized, "an old broad write assertion cannot authorize deletion");
        (await scenario.InvokeAsync("confirm_move", credential: credential.Id, ingress: NetRatelPermissions.FileWrite))
            .Status.Should().Be(StatusCodes.Status401Unauthorized, "an owner's delete grant cannot enlarge a write-only credential");
        credential.Grants.Add(new() { CredentialId = credential.Id, TenantId = 7, Permission = NetRatelPermissions.FileDelete });
        await scenario.Db.SaveChangesAsync();
        (await scenario.InvokeAsync("confirm_move", credential: credential.Id, ingress: NetRatelPermissions.FileWrite)).Entered.Should().BeTrue();
        (await scenario.InvokeAsync("confirm_delete", credential: credential.Id, ingress: NetRatelPermissions.FileDelete)).Entered.Should().BeTrue();
        credential.RevokedAtUtc = DateTimeOffset.UtcNow;
        await scenario.Db.SaveChangesAsync();
        (await scenario.InvokeAsync("confirm_delete", credential: credential.Id, ingress: NetRatelPermissions.FileDelete))
            .Status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public const string Owner = "canonical-file-operator";
        public const string Resource = "https://mcp.example/mcp";
        private const string Issuer = "https://issuer.example";
        private static readonly Guid AgentId = Guid.Parse("9afec4c0-b709-42dc-bd05-2ca1c6ddc139");
        private readonly McpOperatorDelegationOptions _options = new()
        {
            Enabled = true, Issuer = "delegation-test", Audience = "api-test", KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("file-delegation-test-key-at-least-32-bytes"))
        };
        public ServiceProvider Services { get; }
        public NetRatelIdentityDbContext Db { get; }

        public Scenario()
        {
            var configuration = new ConfigurationBuilder().Build();
            Services = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")))
                .AddScoped<IEffectiveAccessService, EffectiveAccessService>()
                .AddScoped<IApplicationPrincipalResolver, ApplicationPrincipalResolver>()
                .AddScoped<IIntegrationCredentialCurrentVerifier, IntegrationCredentialService>()
                .BuildServiceProvider();
            Db = Services.GetRequiredService<NetRatelIdentityDbContext>();
            Db.ApplicationPrincipals.Add(new ApplicationPrincipal { Id = Owner, ExternalIssuer = Issuer, ExternalSubject = "external-operator" });
            Db.SaveChanges();
        }

        public async Task<PrincipalRoleAssignment> GrantAsync(string permission, int tenant)
        {
            var role = new AccessRole { Name = $"test-{Guid.NewGuid():N}" };
            role.Permissions.Add(new AccessRolePermission { Permission = permission });
            var assignment = new PrincipalRoleAssignment { PrincipalId = Owner, RoleId = role.Id, Role = role, TenantId = tenant };
            Db.PrincipalRoleAssignments.Add(assignment);
            await Db.SaveChangesAsync();
            return assignment;
        }

        public async Task<(bool Entered, int Status)> InvokeAsync(string operation, int tenant = 7, string? issuer = Issuer,
            string? credential = null, string? ingress = null)
        {
            var tokens = new McpOperatorDelegationTokenService(_options);
            var assertion = tokens.Create(new McpOperatorDelegationIdentity(credential is null ? "external-operator" : Owner,
                "oauth-client", null, [], ["Administrator", "AutomationOperator"], ["netratel.mcp.files", "netratel.mcp.write"])
                { ExternalIssuer = issuer },
                new McpOperatorDelegationRequest("netratel_files", operation, Guid.NewGuid().ToString("N"), Resource, "prod", tenant, AgentId, "test")
                { IngressCredentialId = credential, IngressPermission = ingress });
            var entered = false;
            var middleware = new McpOperatorDelegationMiddleware(context =>
            {
                entered = true;
                context.TryGetMcpOperatorDelegation(out _).Should().BeTrue();
                return Task.CompletedTask;
            }, _options, tokens);
            var http = new DefaultHttpContext { RequestServices = Services };
            using var body = new MemoryStream();
            http.Response.Body = body;
            http.Request.Headers[McpOperatorDelegationOptions.HeaderName] = assertion;
            await middleware.InvokeAsync(http);
            return (entered, http.Response.StatusCode);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }
}
