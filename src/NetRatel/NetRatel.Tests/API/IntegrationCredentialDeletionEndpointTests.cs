using System.Net;
using System.Net.Http.Json;
using NetRatel.Tests.Infrastructure;
using NetRatel.Shared.Connectivity;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class IntegrationCredentialDeletionEndpointTests
{
    [Theory]
    [InlineData("owned", true)]
    [InlineData("missing-or-other-owner", false)]
    public async Task Delete_uses_current_owner_and_returns_non_enumerating_no_store_success(string id, bool found)
    {
        var credentials = new RecordingCredentials { Found = found };
        using var app = await BuildAppAsync(credentials);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "local");
        client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");

        using var response = await client.DeleteAsync($"/api/v2/account/integration-credentials/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        credentials.Deletions.Should().ContainSingle().Which.Should().Be(("owner", id, "owner"));
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("integration_credential", "1", HttpStatusCode.Forbidden)]
    [InlineData("local", null, HttpStatusCode.Forbidden)]
    [InlineData("local", "invalid", HttpStatusCode.Forbidden)]
    public async Task Delete_rejects_noninteractive_or_unvalidated_cookie_mutations(string? identity, string? mutation, HttpStatusCode expected)
    {
        var credentials = new RecordingCredentials();
        using var app = await BuildAppAsync(credentials);
        using var client = app.GetTestClient();
        if (identity is not null) client.DefaultRequestHeaders.Add("X-Test-Identity", identity);
        if (mutation is not null) client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", mutation);

        using var response = await client.DeleteAsync("/api/v2/account/integration-credentials/owned");
        response.StatusCode.Should().Be(expected);
        credentials.Deletions.Should().BeEmpty();
    }

    [Fact]
    public async Task Current_admission_is_owner_scoped_secret_free_and_read_only_for_configured_Production_grants()
    {
        await using var fixture = await ReviewedMcpAccessTests.Fixture.CreateAsync();
        using var app = await BuildAppAsync(fixture.Credentials, fixture.Pairing);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Identity", "local");
        client.DefaultRequestHeaders.Add("X-Test-Owner", "requester");
        var path = $"/api/v2/account/integration-credentials/{fixture.Credential.CredentialId}/admission";
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var admission = await response.Content.ReadFromJsonAsync<CredentialMcpAdmission>();
        admission!.ConfiguredGrants.Should().HaveCount(2);
        admission.EffectiveAdmission.Should().OnlyContain(x => x.ApplicationGrantConfigured && !x.AuthorizationAdmitted && x.FailureCode == "tenant_not_authorized");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(fixture.Credential.Secret).And.NotContain("DeviceCapability");
        (await fixture.App.McpOperatorPolicies.CountAsync()).Should().Be(0);
        using var invalid = await client.PostAsJsonAsync(path, new { targets = new object?[] { null } });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        client.DefaultRequestHeaders.Remove("X-Test-Owner");
        using var unowned = await client.GetAsync(path);
        using var missing = await client.GetAsync($"/api/v2/account/integration-credentials/{Guid.NewGuid():N}/admission");
        unowned.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(unowned.StatusCode);
    }

    private static async Task<IHost> BuildAppAsync(IIntegrationCredentialService credentials, IntegrationPairingService? pairing = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseEnvironment(Environments.Production);
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("DeleteTest").AddScheme<AuthenticationSchemeOptions, AccountAuthentication>("DeleteTest", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("InteractiveAccount", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => context.User.Identities.Any(identity => identity.IsAuthenticated &&
                (identity.AuthenticationType == "Oidc" || identity.AuthenticationType == LocalAuthenticationOptions.Scheme)));
        }));
        builder.Services.AddSingleton<IIntegrationCredentialService>(credentials);
        if (pairing is not null) builder.Services.AddSingleton(pairing);
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase("credential-delete-identity-" + Guid.NewGuid().ToString("N")));
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase("credential-delete-app-" + Guid.NewGuid().ToString("N")));
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        builder.Services.AddScoped<IIntegrationCredentialGrantValidator, IntegrationCredentialGrantValidator>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIntegrationCredentialEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class AccountAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var mode = Request.Headers["X-Test-Identity"].ToString();
            if (mode.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("netratel_principal_id", Request.Headers["X-Test-Owner"].FirstOrDefault() ?? "owner"), new Claim("auth_mode", mode)],
                mode == "local" ? LocalAuthenticationOptions.Scheme : mode);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "DeleteTest")));
        }
    }

    private sealed class RecordingCredentials : IIntegrationCredentialService
    {
        public bool Found { get; init; } = true;
        public List<(string Owner, string Id, string Actor)> Deletions { get; } = [];
        public Task<bool> DeleteAsync(string ownerPrincipalId, string credentialId, string deletedByPrincipalId, CancellationToken cancellationToken = default)
        {
            Deletions.Add((ownerPrincipalId, credentialId, deletedByPrincipalId));
            return Task.FromResult(Found);
        }
        public Task<IntegrationCredentialSecret> CreateAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IntegrationCredentialSummary>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RevokeAsync(string ownerPrincipalId, string credentialId, string revokedByPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VerifiedIntegrationCredential?> VerifyAsync(string secret, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
