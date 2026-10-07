using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using NetRatel.API.Gateway;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Infrastructure.Auth;
using NetRatel.Client.Service.Auth;
using NetRatel.Tests.API;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientAgentTokenServiceTests
{
    [Fact]
    public async Task WholeSecondJwtExpiry_RefreshesAtItsMarginBeforeTheMillisecondResponseHint()
    {
        var clock = new RenewalManualTimeProvider();
        clock.Advance(TimeSpan.FromMilliseconds(750));
        var identity = new AuthenticatedAgentIdentity(42, Guid.NewGuid());
        var credentials = new FakeCredentialStore(identity.ClientId, "valid-refresh-token");
        using var signing = new AgentGatewayRenewalTestCredentials();
        var issuedAt = clock.GetUtcNow();
        var firstToken = signing.CreateToken(identity, issuedAt.AddMinutes(2), issuedAt.AddSeconds(-1));
        var nextToken = signing.CreateToken(identity, issuedAt.AddMinutes(4), issuedAt.AddSeconds(-1));
        var handler = new TokenResponseHandler((firstToken, 120), (nextToken, 180));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials, clock);
        var firstExpiry = DateTimeOffset.FromUnixTimeSeconds(issuedAt.AddMinutes(2).ToUnixTimeSeconds());

        var first = await service.GetAccessTokenAsync(CancellationToken.None);
        first.Should().Be((firstToken, firstExpiry));

        clock.Advance(TimeSpan.FromSeconds(59));
        (await service.GetAccessTokenAsync(CancellationToken.None)).Should().Be(first);
        handler.RequestCount.Should().Be(1);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        clock.GetUtcNow().Should().Be(firstExpiry.AddMinutes(-1));
        var renewed = await service.GetAccessTokenAsync(CancellationToken.None);

        renewed.AccessToken.Should().Be(nextToken).And.NotBe(firstToken);
        renewed.ExpiresAtUtc.Should().BeAfter(first.ExpiresAtUtc);
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task LongerJwtExpiry_DoesNotExtendTheResponseHintCacheLifetime()
    {
        var clock = new RenewalManualTimeProvider();
        clock.Advance(TimeSpan.FromMilliseconds(750));
        var identity = new AuthenticatedAgentIdentity(42, Guid.NewGuid());
        var credentials = new FakeCredentialStore(identity.ClientId, "valid-refresh-token");
        using var signing = new AgentGatewayRenewalTestCredentials();
        var issuedAt = clock.GetUtcNow();
        var firstToken = signing.CreateToken(identity, issuedAt.AddMinutes(3), issuedAt.AddSeconds(-1));
        var nextToken = signing.CreateToken(identity, issuedAt.AddMinutes(5), issuedAt.AddSeconds(-1));
        var handler = new TokenResponseHandler((firstToken, 120), (nextToken, 180));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials, clock);

        var first = await service.GetAccessTokenAsync(CancellationToken.None);
        first.ExpiresAtUtc.Should().Be(issuedAt.AddMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(1));

        var renewed = await service.GetAccessTokenAsync(CancellationToken.None);
        renewed.AccessToken.Should().Be(nextToken);
        handler.RequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData("opaque-access-token")]
    [InlineData("eyJhbGciOiJFUzI1NiJ9.bm90LWpzb24.c2lnbmF0dXJl")]
    public async Task UnreadableToken_PreservesTheResponseHintAndRefreshMargin(string token)
    {
        var clock = new RenewalManualTimeProvider();
        clock.Advance(TimeSpan.FromMilliseconds(750));
        var credentials = new FakeCredentialStore(Guid.NewGuid().ToString(), "valid-refresh-token");
        var handler = new TokenResponseHandler((token, 120), ("next-opaque-token", 120));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials, clock);
        var issuedAt = clock.GetUtcNow();

        var first = await service.GetAccessTokenAsync(CancellationToken.None);
        first.Should().Be((token, issuedAt.AddMinutes(2)));
        clock.Advance(TimeSpan.FromSeconds(59));
        (await service.GetAccessTokenAsync(CancellationToken.None)).Should().Be(first);
        handler.RequestCount.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(1));

        var renewed = await service.GetAccessTokenAsync(CancellationToken.None);
        renewed.AccessToken.Should().Be("next-opaque-token");
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenServerReportsDeletedAgent_PreservesCredentialUntilAuthorizedRecovery()
    {
        var credentials = new FakeCredentialStore("4b750cbb-0d54-4e74-a7de-863d273b4d76", "stale-refresh-token");
        using var http = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.Forbidden,
            """{"title":"Agent not found","detail":"Agent not found.","code":"agent_not_found"}"""))
        {
            BaseAddress = new Uri("https://netratel-dev-api.example")
        };
        var service = new ClientAgentTokenService(http, credentials, credentials);

        var action = async () => await service.GetAccessTokenAsync(CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentClientAuthException>();
        exception.Which.Code.Should().Be("agent_not_found");
        exception.Which.ShouldClearCredentials.Should().BeFalse();
        credentials.Cleared.Should().BeFalse();
        (await credentials.LoadAsync()).Should().Be(("4b750cbb-0d54-4e74-a7de-863d273b4d76", "stale-refresh-token"));
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenServerReportsDisabledAgent_PreservesCredential()
    {
        var credentials = new FakeCredentialStore("4b750cbb-0d54-4e74-a7de-863d273b4d76", "valid-refresh-token");
        using var http = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.Forbidden,
            """{"title":"Agent disabled","detail":"Agent disabled.","code":"agent_disabled"}"""))
        {
            BaseAddress = new Uri("https://netratel-dev-api.example")
        };
        var service = new ClientAgentTokenService(http, credentials, credentials);

        var action = async () => await service.GetAccessTokenAsync(CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentClientAuthException>();
        exception.Which.Code.Should().Be("agent_disabled");
        exception.Which.ShouldClearCredentials.Should().BeFalse();
        credentials.Cleared.Should().BeFalse();
        (await credentials.LoadAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task RevokedRefreshAcrossRestarts_DoesNotConsumePendingInstallerEnrollmentGrant()
    {
        var credentials = new FakeCredentialStore("4b750cbb-0d54-4e74-a7de-863d273b4d76", "revoked-refresh-token");
        var originalCredentials = await credentials.LoadAsync();
        var originalDeviceKey = await credentials.GetOrCreateAsync(CancellationToken.None);
        var fileSystem = new FakeInjectedEnrollmentFileSystem();
        var installDirectory = Path.Combine(Path.GetTempPath(), $"netratel-revoked-grant-{Guid.NewGuid():N}");
        var enrollmentPath = Path.Combine(installDirectory, "netratel.enroll.json");
        fileSystem.Files[enrollmentPath] = """
            {
              "schema":"netratel.enroll.v1",
              "tenantId":42,
              "enrollmentCode":"ENR-PENDING-REPAIR",
              "issuer":"https://netratel-dev-api.example",
              "validToUtc":"2099-02-27T00:00:00Z"
            }
            """;
        var bootstrap = new InjectedEnrollmentBootstrap(fileSystem, () => installDirectory);
        var enrollment = new CountingEnrollmentService();
        var handler = new CountingUnauthorizedHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var options = new ClientOptions { ApiBaseUrl = "https://netratel-dev-api.example" };

        for (var start = 0; start < 2; start++)
        {
            if (await credentials.LoadAsync() is null)
            {
                await bootstrap.TryEnrollAsync(options, enrollment, credentials, CancellationToken.None);
            }

            var tokenService = new ClientAgentTokenService(http, credentials, credentials);
            var action = () => StartupTokenAcquisition.GetAccessTokenAsync(
                options, tokenService, enrollment, credentials, bootstrap, _ => { }, CancellationToken.None);
            var exception = await action.Should().ThrowAsync<AgentClientAuthException>();
            exception.Which.Code.Should().Be("refresh_token_rejected");
            exception.Which.ShouldClearCredentials.Should().BeFalse();
            (await credentials.LoadAsync()).Should().Be(originalCredentials);
            fileSystem.Files.Should().ContainKey(enrollmentPath);
        }

        handler.RequestCount.Should().Be(2);
        enrollment.Codes.Should().BeEmpty();
        fileSystem.Deleted.Should().BeEmpty();
        (await credentials.GetOrCreateAsync(CancellationToken.None)).Should().Be(originalDeviceKey);
    }

    [Fact]
    public async Task DisabledThenEnabled_RefreshesWithSameStoredCredential()
    {
        var credentials = new FakeCredentialStore("4b750cbb-0d54-4e74-a7de-863d273b4d76", "valid-refresh-token");
        var handler = new EnableAfterDisabledHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials);
        var result = await DisabledAgentTokenRetry.GetAccessTokenAsync(service, _ => { }, CancellationToken.None,
            (_, _) => Task.CompletedTask);

        result.AccessToken.Should().Be("enabled-token");
        credentials.Cleared.Should().BeFalse();
        (await credentials.LoadAsync())!.Value.RefreshToken.Should().Be("valid-refresh-token");
        handler.RefreshTokens.Should().Equal("valid-refresh-token", "valid-refresh-token");
    }

    [Theory]
    [InlineData(401, "proof_clock_skew")]
    [InlineData(401, "proof_nonce_reused")]
    [InlineData(403, "agent_disabled")]
    public async Task StructuredErrorsRetainStatusReasonAndEndpointRole(int status, string code)
    {
        var credentials = new FakeCredentialStore(Guid.NewGuid().ToString(), "refresh");
        using var http = new HttpClient(new StaticResponseHandler((HttpStatusCode)status,
            $"{{\"extensions\":{{\"code\":\"{code}\"}}}}")) { BaseAddress = new Uri("https://api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials);
        var error = await Assert.ThrowsAsync<AgentClientAuthException>(() => service.GetAccessTokenAsync(CancellationToken.None));
        error.StatusCode.Should().Be(status);
        error.Code.Should().Be(code);
        error.EndpointRole.Should().Be(AgentAuthEndpointRole.Token);
        error.ShouldClearCredentials.Should().BeFalse();
    }

    [Fact]
    public async Task ProactiveTransientFailureRetainsValidTokenUntilItsOriginalExpiryAndDoesNotNestRetries()
    {
        var clock = new RenewalManualTimeProvider();
        var credentials = new FakeCredentialStore(Guid.NewGuid().ToString(), "refresh");
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            if (requests == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { accessToken = "original", expiresIn = 120 }) });
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = JsonContent.Create(new { code = "backend_unavailable" }) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1200));
            return Task.FromResult(response);
        })) { BaseAddress = new Uri("https://api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials, clock);
        var original = await service.GetAccessTokenAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(61));
        (await service.GetAccessTokenAsync(CancellationToken.None)).Should().Be(original);
        requests.Should().Be(2);
        clock.Advance(TimeSpan.FromSeconds(59));
        var error = await Assert.ThrowsAsync<AgentClientAuthException>(() => service.GetAccessTokenAsync(CancellationToken.None));
        error.IsRecoverable.Should().BeTrue();
        error.RetryAfter.Should().Be(TimeSpan.FromSeconds(600));
        error.RetryAfterWasCapped.Should().BeTrue();
        requests.Should().Be(3);
    }

    [Fact]
    public async Task AcquisitionOmitsOptionalJwtAndUsesInjectedClockEvenAfterCachedTokenExpiry()
    {
        var clock = new RenewalManualTimeProvider();
        var identity = new AuthenticatedAgentIdentity(42, Guid.NewGuid());
        var credentials = new FakeCredentialStore(identity.ClientId, "refresh");
        using var signing = new AgentGatewayRenewalTestCredentials();
        var initialJwt = signing.CreateToken(identity, clock.GetUtcNow().AddMinutes(2), clock.GetUtcNow().AddSeconds(-1));
        var timestamps = new List<string>();
        using var http = new HttpClient(new DelegateHandler((request, _) =>
        {
            request.Headers.Authorization.Should().BeNull();
            timestamps.Add(request.Headers.GetValues("X-NetRatel-Timestamp").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { accessToken = timestamps.Count == 1 ? initialJwt : "replacement", expiresIn = 120 }) });
        })) { BaseAddress = new Uri("https://api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials, clock);
        await service.GetAccessTokenAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        await service.GetAccessTokenAsync(CancellationToken.None);
        timestamps.Should().Equal(clock.GetUtcNow().AddHours(-2).UtcDateTime.ToString("O"), clock.GetUtcNow().UtcDateTime.ToString("O"));
    }

    [Fact]
    public async Task ConcurrentAcquisitionIsSingleFlightAndOldRejectionCannotInvalidateNewCache()
    {
        var credentials = new FakeCredentialStore(Guid.NewGuid().ToString(), "refresh");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            requests++;
            if (requests == 1) { started.SetResult(); await release.Task.WaitAsync(ct); }
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { accessToken = "token-" + requests, expiresIn = 3600 }) };
        })) { BaseAddress = new Uri("https://api.example") };
        var service = new ClientAgentTokenService(http, credentials, credentials);
        var callers = Enumerable.Range(0, 8).Select(_ => service.GetAccessTokenAsync(CancellationToken.None)).ToArray();
        await started.Task;
        release.SetResult();
        var first = await Task.WhenAll(callers);
        first.Select(value => value.AccessToken).Should().OnlyContain(value => value == "token-1");
        requests.Should().Be(1);
        service.InvalidateAccessToken("token-1").Should().BeTrue();
        (await service.GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("token-2");
        service.InvalidateAccessToken("token-1").Should().BeFalse();
        (await service.GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("token-2");
        requests.Should().Be(2);
    }

    [Theory]
    [InlineData("9999999999999999999999999999999999999999", 600, true)]
    [InlineData("120", 120, false)]
    [InlineData("invalid", null, false)]
    [InlineData("0", null, false)]
    [InlineData("-1", null, false)]
    [InlineData("future-date", 120, false)]
    [InlineData("past-date", null, false)]
    public async Task RetryAfterRetainsBoundedRawDeltaOrDateAndIgnoresInvalidHints(string value, int? expectedSeconds, bool capped)
    {
        var clock = new RenewalManualTimeProvider();
        var raw = value switch
        {
            "future-date" => clock.GetUtcNow().AddSeconds(120).UtcDateTime.ToString("R"),
            "past-date" => clock.GetUtcNow().AddSeconds(-1).UtcDateTime.ToString("R"),
            _ => value
        };
        var credentials = new FakeCredentialStore(Guid.NewGuid().ToString(), "refresh");
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { code = "unavailable" }) };
            response.Headers.TryAddWithoutValidation("Retry-After", raw).Should().BeTrue();
            return Task.FromResult(response);
        })) { BaseAddress = new Uri("https://api.example") };
        var error = await Assert.ThrowsAsync<AgentClientAuthException>(() => new ClientAgentTokenService(http, credentials, credentials, clock).GetAccessTokenAsync(CancellationToken.None));
        error.RetryAfter.Should().Be(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        error.RetryAfterWasCapped.Should().Be(capped);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class EnableAfterDisabledHandler : HttpMessageHandler
    {
        public List<string?> RefreshTokens { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RefreshTokens.Add(body.RootElement.GetProperty("refreshToken").GetString());
            return RefreshTokens.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"detail":"Agent disabled.","code":"agent_disabled"}""", Encoding.UTF8, "application/problem+json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"accessToken":"enabled-token","expiresIn":3600}""", Encoding.UTF8, "application/json")
                };
        }
    }

    private sealed class TokenResponseHandler(params (string AccessToken, int ExpiresIn)[] responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[RequestCount++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { accessToken = response.AccessToken, expiresIn = response.ExpiresIn })
            });
        }
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            });
    }

    private sealed class CountingUnauthorizedHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/problem+json")
            });
        }
    }

    private sealed class CountingEnrollmentService : IAgentEnrollmentService
    {
        public List<string> Codes { get; } = [];

        public Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct)
        {
            Codes.Add(enrollmentCode);
            return Task.FromResult(("new-agent", "new-refresh"));
        }
    }

    private sealed class FakeInjectedEnrollmentFileSystem : IInjectedEnrollmentFileSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Deleted { get; } = [];
        public bool Exists(string path) => Files.ContainsKey(path);
        public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => Task.FromResult(Files[path]);
        public void Delete(string path)
        {
            Deleted.Add(path);
            Files.Remove(path);
        }
    }

    private sealed class FakeCredentialStore(string agentId, string refreshToken) : IAgentCredentialStore, IAgentDeviceKeyStore
    {
        private (string AgentId, string RefreshToken)? _credentials = (agentId, refreshToken);
        private readonly AgentDeviceKeyMaterial _key = CreateKey();

        public bool Cleared { get; private set; }

        public Task SaveAsync(string agentId, string refreshToken)
        {
            _credentials = (agentId, refreshToken);
            return Task.CompletedTask;
        }

        public Task<(string AgentId, string RefreshToken)?> LoadAsync() => Task.FromResult(_credentials);

        public Task ClearRefreshCredentialsAsync()
        {
            Cleared = true;
            _credentials = null;
            return Task.CompletedTask;
        }

        public Task ResetInstallationIdentityAsync() => Task.CompletedTask;

        public Task ClearAsync() => ClearRefreshCredentialsAsync();

        public Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct) => Task.FromResult(_key);

        public Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct) => Task.FromResult<AgentDeviceKeyMaterial?>(_key);

        private static AgentDeviceKeyMaterial CreateKey()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new AgentDeviceKeyMaterial(
                Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                "ecdsa-p256");
        }
    }
}
