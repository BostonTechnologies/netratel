using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class LocalTwoFactorEndpointTests
{
    [Fact]
    public async Task Setup_returns_a_new_authenticator_secret_only_with_no_store()
    {
        using var app = await BuildAppAsync();
        await SeedUserAsync(app.Services, enrolled: false);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-User", "local-user");

        var response = await client.PostAsJsonAsync("/api/v2/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.CurrentPasswordRequest("A-strong-local-password-1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Setup_rejects_in_place_replacement_of_an_enrolled_authenticator_without_changing_its_key()
    {
        using var app = await BuildAppAsync();
        var keyBefore = await SeedUserAsync(app.Services, enrolled: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-User", "local-user");

        var response = await client.PostAsJsonAsync("/api/v2/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.CurrentPasswordRequest("A-strong-local-password-1"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = await users.FindByIdAsync("local-user");
        (await users.GetAuthenticatorKeyAsync(user!)).Should().Be(keyBefore);
    }

    private static async Task<IHost> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var root = new InMemoryDatabaseRoot();
        var databaseName = $"local-two-factor-{Guid.NewGuid():N}";
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(LocalAuthenticationOptions.LocalUserPolicy, policy =>
        {
            policy.AddAuthenticationSchemes(TestAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
        }));
        builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("local-login", limiter =>
        {
            limiter.PermitLimit = 10;
            limiter.Window = TimeSpan.FromMinutes(1);
        }));
        builder.Services.AddSingleton(new LocalAuthenticationOptions("Local", true, "NetRatel.Local"));
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase(databaseName, root));
        builder.Services.AddIdentityCore<LocalUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<NetRatelIdentityDbContext>()
            .AddDefaultTokenProviders();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapLocalAuthenticationEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task<string?> SeedUserAsync(IServiceProvider services, bool enrolled)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = new LocalUser
        {
            Id = "local-user",
            UserName = "operator@example.test",
            Email = "operator@example.test",
            PrincipalId = "local-principal",
            SecurityStamp = "initial-stamp"
        };
        (await users.CreateAsync(user, "A-strong-local-password-1")).Succeeded.Should().BeTrue();
        if (!enrolled)
            return await users.GetAuthenticatorKeyAsync(user);

        (await users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();
        return await users.GetAuthenticatorKeyAsync(user);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "LocalTwoFactorTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers["X-NetRatel-User"].ToString();
            return string.IsNullOrWhiteSpace(userId)
                ? Task.FromResult(AuthenticateResult.Fail("Missing test user."))
                : Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], SchemeName)),
                    SchemeName)));
        }
    }
}
