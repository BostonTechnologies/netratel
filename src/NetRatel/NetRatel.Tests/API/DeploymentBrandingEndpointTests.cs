using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DeploymentBrandingEndpointTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGUlEQVR4nGMQq2n4TwlmGDVg1IBRA4aLAQBrbxEftZLlpAAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData("local-admin")]
    [InlineData("oidc-admin")]
    public async Task Account_administrator_can_upload_a_real_multipart_branding_asset(string identity)
    {
        await using var app = await BuildAppAsync();
        using var client = AccountClient(app, identity, marker: identity == "local-admin" ? "1" : null);
        using var form = Upload(Png);
        using var response = await client.PostAsync("/api/v2/branding/assets", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BrandingAssetUploadResult>();
        result.Should().NotBeNull();
        result!.Slot.Should().Be("logo-light");
        result.Width.Should().Be(16);
        result.Height.Should().Be(16);
        using var anonymous = app.GetTestClient();
        using var asset = await anonymous.GetAsync(result.Url);
        asset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asset.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
        asset.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        using var scope = app.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>().DeploymentBrandingOverrides.SingleAsync();
        stored.UpdatedByPrincipalId.Should().Be(identity == "local-admin" ? "principal-admin" : "principal-operator");
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("local-member", HttpStatusCode.Forbidden)]
    [InlineData("service-admin", HttpStatusCode.Forbidden)]
    [InlineData("integration-admin", HttpStatusCode.Forbidden)]
    public async Task Instance_policy_denies_anonymous_tenant_service_and_attenuated_callers(string? identity, HttpStatusCode expected)
    {
        await using var app = await BuildAppAsync();
        using var client = AccountClient(app, identity, marker: "1");
        using var form = Upload(Png);
        using var response = await client.PostAsync("/api/v2/branding/assets", form);
        response.StatusCode.Should().Be(expected);
        await AssertNoMutationAsync(app);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1,1")]
    public async Task Ambient_Local_cookie_upload_requires_the_exact_account_marker(string? marker)
    {
        await using var app = await BuildAppAsync();
        using var client = AccountClient(app, "local-admin", marker);
        client.DefaultRequestHeaders.Add("Origin", "https://forged.example");
        using var form = Upload(Png);
        using var response = await client.PostAsync("/api/v2/branding/assets", form);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNoMutationAsync(app);
    }

    [Fact]
    public async Task Local_JSON_branding_mutation_also_requires_the_account_marker()
    {
        await using var app = await BuildAppAsync();
        using var client = AccountClient(app, "local-admin", marker: null);
        using var response = await client.PutAsJsonAsync("/api/v2/branding", new UpdateDeploymentBrandingRequest([new("applicationName", "Example deployment", false)]));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNoMutationAsync(app);
    }

    [Fact]
    public async Task Credentialed_cross_origin_preflight_is_not_granted()
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v2/branding/assets");
        request.Headers.Add("Origin", "https://forged.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "X-NetRatel-Account-Request,Content-Type");
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal("*");
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        await AssertNoMutationAsync(app);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("svg")]
    [InlineData("small-dimensions")]
    [InlineData("wrong-media")]
    public async Task Invalid_assets_keep_the_existing_validation_boundary(string kind)
    {
        await using var app = await BuildAppAsync();
        using var client = AccountClient(app, "local-admin", marker: "1");
        var content = kind switch { "empty" => [], "oversize" => new byte[262145], "svg" => Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>") , _ => Png.ToArray() };
        if (kind == "small-dimensions") System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(16, 4), 15);
        using var form = Upload(content, kind == "svg" ? "image/svg+xml" : kind == "wrong-media" ? "image/jpeg" : "image/png");
        using var response = await client.PostAsync("/api/v2/branding/assets", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertNoMutationAsync(app);
    }

    [Fact]
    public async Task Deployment_managed_asset_remains_locked()
    {
        await using var app = await BuildAppAsync(lockedLogo: true);
        using var client = AccountClient(app, "local-admin", marker: "1");
        using var form = Upload(Png);
        using var response = await client.PostAsync("/api/v2/branding/assets", form);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertNoMutationAsync(app);
    }

    [Fact]
    public void Public_projection_and_asset_reads_are_anonymous_but_every_mutation_requires_instance_administration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IDeploymentBrandingService>(_ => throw new NotSupportedException());
        var app = builder.Build();
        app.MapDeploymentBrandingEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Should().Contain(["/api/v2/branding", "/api/v2/branding/assets/{assetId}", "/api/v2/branding/assets"]);
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/v2/branding" && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoints.Where(endpoint => endpoint.RoutePattern.RawText is "/api/v2/branding" or "/api/v2/branding/assets")
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null).SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(metadata => metadata.Policy).Should().Contain("InstanceAdministrator");
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/v2/branding/assets").Metadata.GetMetadata<IAntiforgeryMetadata>()!
            .RequiresValidation.Should().BeFalse();
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { new StringContent("logo-light"), "slot" }, { file, "file", "brand.png" } };
    }

    private static HttpClient AccountClient(WebApplication app, string? identity, string? marker)
    {
        var client = app.GetTestClient();
        if (identity is not null) client.DefaultRequestHeaders.Add("X-Test-Identity", identity);
        if (identity?.StartsWith("local", StringComparison.Ordinal) == true) client.DefaultRequestHeaders.Add("Cookie", "NetRatel.Local=fixture-protected-ticket");
        else if (identity is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-token");
        if (marker is not null) client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", marker);
        return client;
    }

    private static async Task AssertNoMutationAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        (await db.DeploymentBrandingAssets.CountAsync()).Should().Be(0);
        (await db.DeploymentBrandingOverrides.CountAsync()).Should().Be(0);
    }

    private static async Task<WebApplication> BuildAppAsync(bool lockedLogo = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        var databaseName = "branding-endpoint-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAccessHandler>();
        builder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        builder.Services.Configure<DeploymentBrandingOptions>(options => { });
        if (lockedLogo) builder.Services.Configure<DeploymentBrandingOptions>(builder.Configuration.GetSection("ManagedBranding"));
        if (lockedLogo) builder.Configuration["ManagedBranding:LogoLightUrl"] = "/brand/managed.png";
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddAuthentication("BrandingTest").AddScheme<AuthenticationSchemeOptions, AccountAuthentication>("BrandingTest", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("InstanceAdministrator", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.UserRoleAdministration, instanceScope: true));
        }));
        var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            if (context.Features.Get<IExceptionHandlerFeature>()?.Error is { } error)
                TestContext.Current.TestOutputHelper?.WriteLine(error.ToString());
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "endpoint_failure" });
        }));
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDeploymentBrandingEndpoints();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            db.Users.AddRange(new LocalUser { Id = "local-admin", PrincipalId = "principal-admin", IsInstanceAdministrator = true },
                new LocalUser { Id = "local-member", PrincipalId = "principal-member" });
            var role = new AccessRole { Id = "tenant-role", Name = "Tenant administrator" };
            role.Permissions.Add(new AccessRolePermission { RoleId = role.Id, Permission = NetRatelPermissions.UserRoleAdministration });
            db.AccessRoles.Add(role);
            db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "principal-member", RoleId = role.Id, TenantId = 1 });
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        return app;
    }

    private sealed class AccountAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var profile = Request.Headers["X-Test-Identity"].ToString();
            if (profile.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var local = profile.StartsWith("local", StringComparison.Ordinal);
            var mode = local ? "local" : profile == "service-admin" ? "service" : "oidc";
            List<Claim> claims = [new("auth_mode", mode), new("netratel_principal_id", profile == "local-member" ? "principal-member" : local ? "principal-admin" : "principal-operator")];
            if (!local) claims.Add(new Claim("roles", "Operator"));
            if (profile == "service-admin") claims.Add(new Claim("token_use", "netratel_service"));
            if (profile == "integration-admin") claims.Add(new Claim("netratel_integration_credential_id", "missing-attenuated-credential"));
            var identity = new ClaimsIdentity(claims, local ? LocalAuthenticationOptions.Scheme : "Oidc");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "BrandingTest")));
        }
    }
}
