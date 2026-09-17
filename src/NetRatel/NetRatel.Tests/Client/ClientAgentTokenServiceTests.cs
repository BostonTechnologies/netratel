using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Infrastructure.Auth;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientAgentTokenServiceTests
{
    [Fact]
    public async Task GetAccessTokenAsync_WhenServerReportsDeletedAgent_ClearsStaleCredential()
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
        exception.Which.ShouldClearCredentials.Should().BeTrue();
        credentials.Cleared.Should().BeTrue();
        (await credentials.LoadAsync()).Should().BeNull();
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
