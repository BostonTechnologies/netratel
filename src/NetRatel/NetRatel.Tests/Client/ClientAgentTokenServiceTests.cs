using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Infrastructure.Auth;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientAgentTokenServiceTests
{
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
