using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
    [InlineData("collect", NetRatelPermissions.FileRead, null)]
    [InlineData("preview_collect", NetRatelPermissions.FileRead, null)]
    [InlineData("confirm_collect", NetRatelPermissions.FileRead, null)]
    [InlineData("copy", NetRatelPermissions.FileWrite, NetRatelPermissions.FileRead)]
    [InlineData("preview_copy", NetRatelPermissions.FileWrite, NetRatelPermissions.FileRead)]
    [InlineData("confirm_copy", NetRatelPermissions.FileWrite, NetRatelPermissions.FileRead)]
    [InlineData("move", NetRatelPermissions.FileWrite, NetRatelPermissions.FileDelete)]
    [InlineData("preview_move", NetRatelPermissions.FileWrite, NetRatelPermissions.FileDelete)]
    [InlineData("confirm_move", NetRatelPermissions.FileWrite, NetRatelPermissions.FileDelete)]
    [InlineData("cleanup", NetRatelPermissions.FileDelete, null)]
    [InlineData("artifact_cleanup", NetRatelPermissions.FileDelete, null)]
    [InlineData("preview_artifact_cleanup", NetRatelPermissions.FileDelete, null)]
    [InlineData("confirm_artifact_cleanup", NetRatelPermissions.FileDelete, null)]
    public void File_mutation_protocol_scope_retains_the_exact_client_data_permissions(
        string operation, string permission, string? additionalPermission)
    {
        McpOperationAccessCatalog.Find("netratel_files", operation)!.RequiredScope.Should().Be(McpOperationAccessScope.Write,
            "collect and relocation retain the existing MCP write protocol and confirmation boundary");
        McpLocalDelegationPermissionMapper.RequiredPermission("netratel_files", operation).Should().Be(permission);
        McpLocalDelegationPermissionMapper.AdditionalPermissions("netratel_files", operation)
            .Should().Equal(additionalPermission is null ? [] : [additionalPermission]);
    }

    [Theory]
    [InlineData("browse", NetRatelPermissions.FileRead)]
    [InlineData("collect", NetRatelPermissions.FileRead)]
    [InlineData("preview_collect", NetRatelPermissions.FileRead)]
    [InlineData("confirm_collect", NetRatelPermissions.FileRead)]
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

    [Theory]
    [InlineData("copy", NetRatelPermissions.FileRead)]
    [InlineData("preview_copy", NetRatelPermissions.FileRead)]
    [InlineData("confirm_copy", NetRatelPermissions.FileRead)]
    [InlineData("move", NetRatelPermissions.FileDelete)]
    [InlineData("preview_move", NetRatelPermissions.FileDelete)]
    [InlineData("confirm_move", NetRatelPermissions.FileDelete)]
    public async Task Relocation_requires_both_current_permissions_in_one_tenant_for_exchange_and_execution(
        string operation, string sourcePermission)
    {
        await using var scenario = new Scenario();
        await scenario.GrantAsync(NetRatelPermissions.FileWrite, 7);
        await scenario.GrantAsync(sourcePermission, 8);
        (await scenario.InvokeAsync(operation)).Status.Should().Be(StatusCodes.Status403Forbidden);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", Scenario.Owner)], "Oidc"));
        var access = scenario.Services.GetRequiredService<IEffectiveAccessService>();
        (await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, "netratel_files", operation, 7, default)).Should().BeFalse();
        var grant = await scenario.GrantAsync(sourcePermission, 7);
        (await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, "netratel_files", operation, 7, default)).Should().BeTrue();
        (await scenario.InvokeAsync(operation)).Entered.Should().BeTrue();
        scenario.Db.PrincipalRoleAssignments.Remove(grant);
        await scenario.Db.SaveChangesAsync();
        (await McpLocalDelegationPermissionMapper.AuthorizeAsync(access, user, "netratel_files", operation, 7, default)).Should().BeFalse();
        (await scenario.InvokeAsync(operation)).Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("collect")]
    [InlineData("preview_collect")]
    [InlineData("confirm_collect")]
    public async Task Local_collect_requires_current_read_grants_without_requiring_client_write(string operation)
    {
        await using var scenario = new Scenario();
        var ownerGrant = await scenario.GrantAsync(NetRatelPermissions.FileRead, 7);
        var credential = new IntegrationCredential
        {
            OwnerPrincipalId = Scenario.Owner, Purpose = IntegrationCredentialPurpose.HttpMcp,
            Resource = Scenario.Resource, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Grants = [new() { TenantId = 7, Permission = NetRatelPermissions.FileRead }]
        };
        scenario.Db.IntegrationCredentials.Add(credential);
        await scenario.Db.SaveChangesAsync();

        (await scenario.InvokeAsync(operation, credential: credential.Id, ingress: NetRatelPermissions.FileRead))
            .Entered.Should().BeTrue("collect only reads client data into an internal artifact");
        AssertPermissionDenied(await scenario.InvokeAsync(operation, credential: credential.Id, ingress: NetRatelPermissions.FileWrite),
            "the signed ingress permission must match the operation exactly");
        AssertPermissionDenied(await scenario.InvokeAsync(operation, tenant: 8, credential: credential.Id, ingress: NetRatelPermissions.FileRead),
            "a tenant grant cannot authorize a different tenant");
        scenario.Db.PrincipalRoleAssignments.Remove(ownerGrant);
        await scenario.Db.SaveChangesAsync();
        AssertPermissionDenied(await scenario.InvokeAsync(operation, credential: credential.Id, ingress: NetRatelPermissions.FileRead),
            "the credential cannot retain a removed owner read grant");
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("preview_copy")]
    [InlineData("confirm_copy")]
    public async Task Local_copy_requires_source_read_in_both_current_owner_and_attenuated_credential(string operation)
    {
        await using var scenario = new Scenario();
        await scenario.GrantAsync(NetRatelPermissions.FileWrite, 7);
        var ownerRead = await scenario.GrantAsync(NetRatelPermissions.FileRead, 7);
        var credential = new IntegrationCredential
        {
            OwnerPrincipalId = Scenario.Owner, Purpose = IntegrationCredentialPurpose.HttpMcp,
            Resource = Scenario.Resource, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Grants = [new() { TenantId = 7, Permission = NetRatelPermissions.FileWrite },
                new() { TenantId = 8, Permission = NetRatelPermissions.FileRead }]
        };
        scenario.Db.IntegrationCredentials.Add(credential);
        await scenario.Db.SaveChangesAsync();

        AssertPermissionDenied(await scenario.InvokeAsync(operation, credential: credential.Id, ingress: NetRatelPermissions.FileWrite),
            "a write credential cannot borrow the owner's source read grant");
        credential.Grants.Add(new() { CredentialId = credential.Id, TenantId = 7, Permission = NetRatelPermissions.FileRead });
        await scenario.Db.SaveChangesAsync();
        (await scenario.InvokeAsync(operation, credential: credential.Id, ingress: NetRatelPermissions.FileWrite)).Entered.Should().BeTrue();
        scenario.Db.PrincipalRoleAssignments.Remove(ownerRead);
        await scenario.Db.SaveChangesAsync();
        AssertPermissionDenied(await scenario.InvokeAsync(operation, credential: credential.Id, ingress: NetRatelPermissions.FileWrite),
            "both current owner permissions still constrain an issued credential");
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
        AssertPermissionDenied(await scenario.InvokeAsync("confirm_delete", credential: credential.Id, ingress: NetRatelPermissions.FileWrite),
            "an old broad write assertion cannot authorize deletion");
        AssertPermissionDenied(await scenario.InvokeAsync("confirm_move", credential: credential.Id, ingress: NetRatelPermissions.FileWrite),
            "an owner's delete grant cannot enlarge a write-only credential");
        credential.Grants.Add(new() { CredentialId = credential.Id, TenantId = 7, Permission = NetRatelPermissions.FileDelete });
        await scenario.Db.SaveChangesAsync();
        (await scenario.InvokeAsync("confirm_move", credential: credential.Id, ingress: NetRatelPermissions.FileWrite)).Entered.Should().BeTrue();
        (await scenario.InvokeAsync("confirm_delete", credential: credential.Id, ingress: NetRatelPermissions.FileDelete)).Entered.Should().BeTrue();
        credential.RevokedAtUtc = DateTimeOffset.UtcNow;
        await scenario.Db.SaveChangesAsync();
        var revoked = await scenario.InvokeAsync("confirm_delete", credential: credential.Id, ingress: NetRatelPermissions.FileDelete);
        revoked.Entered.Should().BeFalse();
        revoked.Status.Should().Be(StatusCodes.Status401Unauthorized);
        revoked.Code.Should().Be("delegated_identity_revoked");
    }

    private static void AssertPermissionDenied((bool Entered, int Status, string? Code) response, string because)
    {
        response.Entered.Should().BeFalse(because);
        response.Status.Should().Be(StatusCodes.Status403Forbidden, because);
        response.Code.Should().Be("local_credential_permission_denied", because);
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

        public async Task<(bool Entered, int Status, string? Code)> InvokeAsync(string operation, int tenant = 7, string? issuer = Issuer,
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
            string? code = null;
            if (body.Length > 0)
            {
                using var response = JsonDocument.Parse(body.ToArray());
                code = response.RootElement.GetProperty("code").GetString();
            }
            return (entered, http.Response.StatusCode, code);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }
}
