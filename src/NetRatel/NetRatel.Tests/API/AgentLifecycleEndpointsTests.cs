using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentLifecycleEndpointsTests
{
    [Fact]
    public async Task Enroll_Disable_Enable_ControlsTokenIssuance()
    {
        using var app = await BuildAppAsync();
        var anonClient = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var enrollResp = await SendEnrollmentAsync(anonClient, deviceKey);
        enrollResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var enroll = await enrollResp.Content.ReadFromJsonAsync<EnrollResult>();
        enroll.Should().NotBeNull();

        var tokenOk = await anonClient.PostAsJsonAsync("/api/v1/agents/token", new { agentId = enroll!.AgentId, refreshToken = enroll.RefreshToken });
        tokenOk.StatusCode.Should().Be(HttpStatusCode.OK);

        var adminClient = app.GetTestClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var disable = await adminClient.PostAsJsonAsync($"/api/v1/tenants/42/agents/{enroll.AgentId}/disable", new { reason = "Lost/stolen device" });
        disable.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tokenDisabled = await anonClient.PostAsJsonAsync("/api/v1/agents/token", new { agentId = enroll.AgentId, refreshToken = enroll.RefreshToken });
        tokenDisabled.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var disabledBody = await tokenDisabled.Content.ReadAsStringAsync();
        disabledBody.Should().Contain("agent_disabled");

        var enable = await adminClient.PostAsync($"/api/v1/tenants/42/agents/{enroll.AgentId}/enable", null);
        enable.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tokenAgain = await anonClient.PostAsJsonAsync("/api/v1/agents/token", new { agentId = enroll.AgentId, refreshToken = enroll.RefreshToken });
        tokenAgain.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RefreshRotation_ReusedToken_IsRejected()
    {
        using var app = await BuildAppAsync(o => o.EnableRefreshRotation = true);
        var client = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var enrollResp = await SendEnrollmentAsync(client, deviceKey);
        enrollResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var enroll = await enrollResp.Content.ReadFromJsonAsync<EnrollResult>();
        enroll.Should().NotBeNull();

        var first = await client.PostAsJsonAsync("/api/v1/agents/token", new { agentId = enroll!.AgentId, refreshToken = enroll.RefreshToken });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<TokenResult>();
        firstBody.Should().NotBeNull();
        firstBody!.RefreshToken.Should().NotBeNullOrWhiteSpace();

        var reused = await client.PostAsJsonAsync("/api/v1/agents/token", new { agentId = enroll.AgentId, refreshToken = enroll.RefreshToken });
        reused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var rotated = await client.PostAsJsonAsync("/api/v1/agents/token", new { agentId = enroll.AgentId, refreshToken = firstBody.RefreshToken });
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LegacyStoConnectScope_IsIssuedAsNetRatelConnectScope()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var enrollResp = await SendEnrollmentAsync(client, deviceKey);
        var enroll = await enrollResp.Content.ReadFromJsonAsync<EnrollResult>();
        enrollResp.StatusCode.Should().Be(HttpStatusCode.OK);
        enroll.Should().NotBeNull();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = await db.Agents.SingleAsync(candidate => candidate.Id == enroll!.AgentId);
            agent.AllowedScopesJson = JsonSerializer.Serialize(new[] { "sto:connect" });
            await db.SaveChangesAsync();
        }

        var tokenResp = await client.PostAsJsonAsync(
            "/api/v1/agents/token",
            new { agentId = enroll!.AgentId, refreshToken = enroll.RefreshToken, requestedScopes = new[] { "netratel:connect" } });
        var token = await tokenResp.Content.ReadFromJsonAsync<TokenResult>();

        tokenResp.StatusCode.Should().Be(HttpStatusCode.OK);
        token.Should().NotBeNull();
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token!.AccessToken);
        jwt.Claims.Should().Contain(claim => claim.Type == "scope" && claim.Value == "netratel:connect");
        jwt.Claims.Should().NotContain(claim => claim.Type == "scope" && claim.Value == "sto:connect");
    }

    [Fact]
    public async Task PopNonceReplay_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        using var app = await BuildAppAsync(o =>
        {
            o.EnablePoP = true;
            o.NonceRetentionMinutes = 10;
        });
        var client = app.GetTestClient();

        var enrollResp = await client.PostAsJsonAsync("/api/v1/agents/enroll", new
        {
            enrollmentCode = "ENR-TEST42",
            publicKey,
            keyAlgorithm = "ecdsa-p256"
        });
        enrollResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var enroll = await enrollResp.Content.ReadFromJsonAsync<EnrollResult>();
        enroll.Should().NotBeNull();

        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var body = JsonSerializer.Serialize(new
        {
            agentId = enroll!.AgentId,
            refreshToken = enroll.RefreshToken,
            requestedScopes = new[] { "netratel:connect" }
        });
        var bodyHash = PopSignatureService.ComputeBodyHash($"{enroll.AgentId:N}:{enroll.RefreshToken}:netratel:connect");
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, nonce, bodyHash);
        var signature = Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256));

        var first = await SendPopTokenAsync(client, body, signature, nonce, timestamp);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await SendPopTokenAsync(client, body, signature, nonce, timestamp);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Enrollment_SameDeviceKey_RecoversCanonicalAgent_WhileSameHostDifferentKeyRemainsDistinct()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string deviceInfo = "{\"hostName\":\"shared-host\",\"os\":\"Linux\",\"architecture\":\"X64\"}";

        var firstResponse = await SendEnrollmentAsync(client, firstKey, deviceInfo: deviceInfo);
        var first = await firstResponse.Content.ReadFromJsonAsync<EnrollResult>();
        var recoveredResponse = await SendEnrollmentAsync(client, firstKey, deviceInfo: deviceInfo);
        var recovered = await recoveredResponse.Content.ReadFromJsonAsync<EnrollResult>();
        var distinctResponse = await SendEnrollmentAsync(client, secondKey, deviceInfo: deviceInfo);
        var distinct = await distinctResponse.Content.ReadFromJsonAsync<EnrollResult>();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        recoveredResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        distinctResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        recovered!.AgentId.Should().Be(first!.AgentId);
        distinct!.AgentId.Should().NotBe(first.AgentId);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Agents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Enrollment_SameDeviceKey_IsTenantScoped()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var firstResponse = await SendEnrollmentAsync(client, deviceKey);
        var first = await firstResponse.Content.ReadFromJsonAsync<EnrollResult>();
        var secondResponse = await SendEnrollmentAsync(client, deviceKey, enrollmentCode: "ENR-TEST43");
        var second = await secondResponse.Content.ReadFromJsonAsync<EnrollResult>();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        second!.AgentId.Should().NotBe(first!.AgentId);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Agents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Enrollment_ConcurrentRequestsForSameDevice_ReturnOneCanonicalAgent()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var responses = await Task.WhenAll(
            SendEnrollmentAsync(client, deviceKey),
            SendEnrollmentAsync(client, deviceKey));
        var enrollments = await Task.WhenAll(
            responses[0].Content.ReadFromJsonAsync<EnrollResult>(),
            responses[1].Content.ReadFromJsonAsync<EnrollResult>());

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        enrollments.Should().OnlyContain(enrollment => enrollment != null);
        enrollments[1]!.AgentId.Should().Be(enrollments[0]!.AgentId);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Agents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Enrollment_DisabledCanonicalAgent_StaysDisabledDuringRecovery()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var firstResponse = await SendEnrollmentAsync(client, deviceKey);
        var first = await firstResponse.Content.ReadFromJsonAsync<EnrollResult>();
        var adminClient = app.GetTestClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        await adminClient.PostAsJsonAsync($"/api/v1/tenants/42/agents/{first!.AgentId}/disable", new { reason = "policy" });

        var recoveryResponse = await SendEnrollmentAsync(client, deviceKey);
        var recovered = await recoveryResponse.Content.ReadFromJsonAsync<EnrollResult>();
        var tokenResponse = await client.PostAsJsonAsync(
            "/api/v1/agents/token",
            new { agentId = recovered!.AgentId, refreshToken = recovered.RefreshToken });

        recoveryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        recovered.AgentId.Should().Be(first.AgentId);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Agents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RefreshRotation_LostResponse_RecoversOnceWithProof_WithoutChangingAgentIdentity()
    {
        using var app = await BuildAppAsync(options =>
        {
            options.EnableRefreshRotation = true;
            options.RefreshRotationRecoveryMinutes = 2;
        });
        var client = app.GetTestClient();
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrollmentResponse = await SendEnrollmentAsync(client, deviceKey);
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<EnrollResult>();

        var rotationResponse = await client.PostAsJsonAsync(
            "/api/v1/agents/token",
            new { agentId = enrollment!.AgentId, refreshToken = enrollment.RefreshToken });
        var rotation = await rotationResponse.Content.ReadFromJsonAsync<TokenResult>();
        var recoveryResponse = await SendPopTokenAsync(client, deviceKey, enrollment.AgentId, enrollment.RefreshToken);
        var recovery = await recoveryResponse.Content.ReadFromJsonAsync<TokenResult>();
        var replayResponse = await SendPopTokenAsync(client, deviceKey, enrollment.AgentId, enrollment.RefreshToken);

        rotationResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        rotation!.RefreshToken.Should().NotBeNullOrWhiteSpace();
        recoveryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        recovery!.RefreshToken.Should().NotBeNullOrWhiteSpace().And.NotBe(rotation.RefreshToken);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Agents.CountAsync()).Should().Be(1);
        (await db.Agents.SingleAsync()).Id.Should().Be(enrollment.AgentId);
    }

    [Fact]
    public async Task AdminAgentEndpoints_RequireAuth()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        var response = await client.GetAsync("/api/v1/tenants/42/agents");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<IHost> BuildAppAsync(Action<SecurityHardeningOptions>? securityOverride = null)
    {
        var dbName = Guid.NewGuid().ToString("N");
        var keyPath = Path.Combine(Path.GetTempPath(), $"netratel-key-{Guid.NewGuid():N}.pem");
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await File.WriteAllTextAsync(keyPath, ecdsa.ExportECPrivateKeyPem());
        }

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("Operator", p => p.RequireAuthenticatedUser());
                });

                services.AddDbContext<OrchestratorDbContext>(opts =>
                {
                    opts.UseInMemoryDatabase(dbName);
                    opts.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
                });

                services.AddHttpContextAccessor();
                services.Configure<AgentAuthOptions>(o =>
                {
                    o.Issuer = "https://netratel.example.invalid";
                    o.Audience = "spacetimedb";
                    o.PrivateKeyPath = keyPath;
                    o.SigningKeyId = "test-key";
                    o.AccessTokenLifetimeMinutes = 10;
                    o.RefreshTokenLifetimeDays = 7;
                });
                services.Configure<SecurityHardeningOptions>(o =>
                {
                    o.EnablePoP = false;
                    o.EnableRefreshRotation = false;
                    o.EnableMTls = false;
                    o.RequireMTls = false;
                    o.RequireScopes = false;
                    o.NonceRetentionMinutes = 10;
                    o.PopTimestampToleranceMinutes = 5;
                    securityOverride?.Invoke(o);
                });

                services.AddScoped<IEnrollmentService, EnrollmentService>();
                services.AddScoped<IAgentTokenService, AgentTokenService>();
                services.AddScoped<IAgentManagementService, AgentManagementService>();
                services.AddScoped<IPrimaryClientAgentBindingService, PrimaryClientAgentBindingService>();
                services.AddScoped<AgentNonceReplayService>();
                services.AddScoped<OidcSigningService>();
            });

            web.Configure(async app =>
            {
                using var scope = app.ApplicationServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                db.EnrollmentCodes.Add(new EnrollmentCode
                {
                    Id = Guid.NewGuid(),
                    TenantId = 42,
                    Code = "ENR-TEST42",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    ValidToUtc = DateTimeOffset.UtcNow.AddHours(1),
                    MaxUses = 10,
                    Uses = 0
                });
                db.EnrollmentCodes.Add(new EnrollmentCode
                {
                    Id = Guid.NewGuid(),
                    TenantId = 43,
                    Code = "ENR-TEST43",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    ValidToUtc = DateTimeOffset.UtcNow.AddHours(1),
                    MaxUses = 10,
                    Uses = 0
                });
                await db.SaveChangesAsync();

                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapAgentAuthEndpoints());
            });
        });

        return await builder.StartAsync();
    }

    private sealed record EnrollResult(Guid AgentId, string RefreshToken, int ExpiresInDays);
    private sealed record TokenResult(string AccessToken, int ExpiresIn, string? RefreshToken);

    private static Task<HttpResponseMessage> SendEnrollmentAsync(
        HttpClient client,
        ECDsa key,
        string enrollmentCode = "ENR-TEST42",
        string? deviceInfo = null)
    {
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var requestedScopes = new[] { "netratel:connect" };
        var body = JsonSerializer.Serialize(new
        {
            enrollmentCode,
            publicKey,
            keyAlgorithm = "ecdsa-p256",
            requestedScopes,
            deviceInfoJson = deviceInfo
        });
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var bodyHash = PopSignatureService.ComputeEnrollmentBodyHash(
            enrollmentCode,
            publicKey,
            "ecdsa-p256",
            deviceInfo,
            requestedScopes);
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/enroll", timestamp, nonce, bodyHash);
        return SendSignedRequestAsync(client, "/api/v1/agents/enroll", body, key, nonce, timestamp, message);
    }

    private static Task<HttpResponseMessage> SendPopTokenAsync(
        HttpClient client,
        ECDsa key,
        Guid agentId,
        string refreshToken)
    {
        var requestedScopes = new[] { "netratel:connect" };
        var body = JsonSerializer.Serialize(new { agentId, refreshToken, requestedScopes });
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var bodyHash = PopSignatureService.ComputeTokenBodyHash(agentId, refreshToken, requestedScopes);
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, nonce, bodyHash);
        return SendSignedRequestAsync(client, "/api/v1/agents/token", body, key, nonce, timestamp, message);
    }

    private static Task<HttpResponseMessage> SendSignedRequestAsync(
        HttpClient client,
        string path,
        string body,
        ECDsa key,
        string nonce,
        DateTimeOffset timestamp,
        byte[] message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-NetRatel-Signature", Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256)));
        request.Headers.Add("X-NetRatel-Nonce", nonce);
        request.Headers.Add("X-NetRatel-Timestamp", timestamp.UtcDateTime.ToString("O"));
        return client.SendAsync(request);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.Fail("Missing auth header"));
            }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test-admin") }, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private static Task<HttpResponseMessage> SendPopTokenAsync(
        HttpClient client,
        string body,
        string signature,
        string nonce,
        DateTimeOffset timestamp)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/token")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-NetRatel-Signature", signature);
        req.Headers.Add("X-NetRatel-Nonce", nonce);
        req.Headers.Add("X-NetRatel-Timestamp", timestamp.UtcDateTime.ToString("O"));
        return client.SendAsync(req);
    }
}
