using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Security;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class MachineTokenMiddlewareTests
{
    private const string Issuer = "https://issuer.example.test";

    [Theory]
    [InlineData(true, "machine", "/machine", "valid", HttpStatusCode.OK)]
    [InlineData(false, "machine", "/machine", "valid", HttpStatusCode.Unauthorized)]
    [InlineData(false, "machine", "/", "valid", HttpStatusCode.Unauthorized)]
    [InlineData(true, "human", "/", "valid", HttpStatusCode.OK)]
    [InlineData(false, "human", "/", "valid", HttpStatusCode.OK)]
    [InlineData(true, "human", "/machine", "valid", HttpStatusCode.Unauthorized)]
    [InlineData(true, "machine", "/machine", "algorithm", HttpStatusCode.Unauthorized)]
    [InlineData(true, "machine", "/machine", "signature", HttpStatusCode.Unauthorized)]
    [InlineData(true, "machine", "/machine", "issuer", HttpStatusCode.Unauthorized)]
    [InlineData(true, "other", "/machine", "valid", HttpStatusCode.Unauthorized)]
    [InlineData(true, "machine", "/machine", "expired", HttpStatusCode.Unauthorized)]
    [InlineData(true, "machine", "/machine", "group", HttpStatusCode.Unauthorized)]
    [InlineData(true, "machine", "/", "multi-audience", HttpStatusCode.Unauthorized)]
    public async Task Signed_tokens_pass_discovery_routing_and_real_bearer_policies(
        bool enabled, string audience, string path, string variant, HttpStatusCode expected)
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "disposable-issuer" };
        var parameters = rsa.ExportParameters(false);
        using var issuer = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(context =>
            context.Request.Path == "/.well-known/openid-configuration"
                ? context.Response.WriteAsJsonAsync(new { issuer = Issuer, jwks_uri = Issuer + "/keys" })
                : context.Response.WriteAsJsonAsync(new { keys = new[] { new {
                    kty = "RSA", kid = key.KeyId, use = "sig",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!)
                } } })))).StartAsync();
        using var backchannel = issuer.GetTestClient();
        var settings = new MachineTokenAuthenticationOptions
        {
            Enabled = enabled, Authority = Issuer, Audience = "machine",
            RequiredGroups = ["operators"], SessionRoles = ["Operator"], AllowedSigningAlgorithms = ["RS256"]
        };
        using var api = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddAuthentication("Bearer")
                .AddPolicyScheme("Bearer", "Bearer", options => options.ForwardDefaultSelector = context =>
                {
                    var token = context.Request.Headers.Authorization.ToString()["Bearer ".Length..];
                    return MachineTokenAuthentication.IsCandidate(new JwtSecurityTokenHandler().ReadJwtToken(token), settings)
                        ? "MachineToken" : "Oidc";
                })
                .AddJwtBearer("MachineToken", options =>
                {
                    MachineTokenAuthentication.Configure(options, settings, development: false);
                    options.Backchannel = backchannel;
                })
                .AddJwtBearer("Oidc", options =>
                {
                    options.Authority = Issuer;
                    options.Audience = "human";
                    options.Backchannel = backchannel;
                    options.MapInboundClaims = false;
                });
            services.AddAuthorization(options => options.AddPolicy("MachineOnly", policy =>
                policy.AddAuthenticationSchemes("MachineToken").RequireAuthenticatedUser().RequireClaim("auth_mode", "machine_token")));
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                RequestDelegate identity = context => context.Response.WriteAsync(
                    context.User.IsInRole("Operator") ? "machine-role" : "human-without-machine-role");
                endpoints.MapGet("/", identity).RequireAuthorization();
                endpoints.MapGet("/machine", identity).RequireAuthorization("MachineOnly");
            });
        })).StartAsync();
        var claims = variant == "group" ? new List<Claim>() : [new Claim("groups", "operators")];
        if (variant == "multi-audience")
            claims.Add(new Claim("aud", "human"));
        var token = new JwtSecurityToken(
            variant == "issuer" ? "https://wrong.example.test" : Issuer,
            audience, claims, DateTime.UtcNow.AddHours(-2),
            variant == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(variant == "signature" ? new RsaSecurityKey(otherRsa) { KeyId = key.KeyId } : key,
                variant == "algorithm" ? "RS512" : "RS256"));
        using var client = api.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
            (await response.Content.ReadAsStringAsync()).Should().Be(audience == "human" ? "human-without-machine-role" : "machine-role");
    }
}
