using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Security_status_reports_the_persisted_factor_state_without_exposing_the_key(bool enrolled)
    {
        using var app = await BuildAppAsync();
        await SeedUserAsync(app.Services, enrolled);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-User", "local-user");

        var response = await client.GetAsync("/api/v2/local-auth/security/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var status = await response.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalSecurityStatusResponse>();
        status!.TwoFactorEnabled.Should().Be(enrolled);
        status.MinimumPassphraseLength.Should().BeGreaterThan(0);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("initial-stamp");
    }

    [Fact]
    public async Task Setup_returns_a_new_authenticator_secret_without_invalidating_the_current_session()
    {
        using var app = await BuildAppAsync();
        await SeedUserAsync(app.Services, enrolled: false);
        string securityStampBefore;
        await using (var beforeScope = app.Services.CreateAsyncScope())
        {
            var beforeUsers = beforeScope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
            securityStampBefore = (await beforeUsers.FindByIdAsync("local-user"))!.SecurityStamp!;
        }

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-User", "local-user");

        var response = await client.PostAsJsonAsync("/api/v2/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.CurrentPasswordRequest("A-strong-local-password-1"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = await users.FindByIdAsync("local-user");
        user!.SecurityStamp.Should().Be(securityStampBefore);
        (await users.GetAuthenticatorKeyAsync(user)).Should().NotBeNullOrWhiteSpace();
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

    [Fact]
    public async Task Recovery_codes_are_accepted_verbatim_once_by_the_two_factor_login_endpoint()
    {
        using var app = await BuildAppAsync();
        await SeedUserAsync(app.Services, enrolled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = (await users.FindByIdAsync("local-user"))!;
        var recoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.Single();
        var client = app.GetTestClient();

        await BeginTwoFactorLoginAsync(client);
        var accepted = await client.PostAsJsonAsync("/api/v2/local-auth/login/two-factor",
            new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest(recoveryCode));

        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await BeginTwoFactorLoginAsync(client);
        var reused = await client.PostAsJsonAsync("/api/v2/local-auth/login/two-factor",
            new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest(recoveryCode));

        reused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_factor_mutations_are_throttled_per_account_without_changing_factor_state()
    {
        using var app = await BuildAppAsync();
        await SeedUserAsync(app.Services, enrolled: false);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-NetRatel-User", "local-user");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var invalid = await client.PostAsJsonAsync("/api/v2/local-auth/two-factor/setup",
                new LocalAuthenticationEndpoints.CurrentPasswordRequest("incorrect passphrase"));
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var limited = await client.PostAsJsonAsync("/api/v2/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.CurrentPasswordRequest("A-strong-local-password-1"));
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await client.GetAsync("/api/v2/local-auth/security/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await SeedUserAsync(app.Services, enrolled: false, userId: "other-local-user");
        var otherClient = app.GetTestClient();
        otherClient.DefaultRequestHeaders.Add("X-NetRatel-User", "other-local-user");
        var otherAccount = await otherClient.PostAsJsonAsync("/api/v2/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.CurrentPasswordRequest("incorrect passphrase"));
        otherAccount.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = await users.FindByIdAsync("local-user");
        (await users.GetAuthenticatorKeyAsync(user!)).Should().BeNull();
    }

    private static async Task BeginTwoFactorLoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/v2/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("operator@example.test", "A-strong-local-password-1", false));

        login.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var challengeCookie = login.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("NetRatel.Local.LoginChallenge=", StringComparison.Ordinal))
            .Split(';', 2)[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", challengeCookie).Should().BeTrue();
    }

    private static async Task<IHost> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        var root = new InMemoryDatabaseRoot();
        var databaseName = $"local-two-factor-{Guid.NewGuid():N}";
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { })
            .AddCookie(LocalAuthenticationOptions.Scheme);
        builder.Services.AddAuthorization(options => options.AddPolicy(LocalAuthenticationOptions.LocalUserPolicy, policy =>
        {
            policy.AddAuthenticationSchemes(TestAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
        }));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter("local-login", limiter =>
            {
                limiter.PermitLimit = 10;
                limiter.Window = TimeSpan.FromMinutes(1);
            });
            options.AddPolicy("local-security", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
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

    private static async Task<string?> SeedUserAsync(IServiceProvider services, bool enrolled, string userId = "local-user")
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalUser>>();
        var user = new LocalUser
        {
            Id = userId,
            UserName = userId == "local-user" ? "operator@example.test" : $"{userId}@example.test",
            Email = userId == "local-user" ? "operator@example.test" : $"{userId}@example.test",
            PrincipalId = $"{userId}-principal",
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
