using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service.Auth;
using NetRatel.Infrastructure.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class StartupTokenAcquisitionTests
{
    [Fact]
    public async Task AgentNotFoundWithExplicitCodePerformsOneEnrollmentAndOneTokenRetry()
    {
        var credentials = new FakeCredentialStore(("old-agent", "old-refresh"));
        var enrollment = new FakeEnrollmentService(("new-agent", "new-refresh"));
        var tokens = new SequenceTokenService(credentials,
            new AgentClientAuthException("Agent identity was not found.", 403, code: "agent_not_found"),
            ("access-token", DateTimeOffset.UtcNow.AddHours(1)));
        var bootstrap = new FakeInjectedEnrollmentBootstrap();

        var result = await StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions { EnrollmentCode = "  ENR-VALID  " },
            tokens,
            enrollment,
            credentials,
            bootstrap,
            _ => { },
            CancellationToken.None);

        result.AccessToken.Should().Be("access-token");
        tokens.RequestCount.Should().Be(2);
        enrollment.Codes.Should().Equal("ENR-VALID");
        bootstrap.CallCount.Should().Be(0);
        (await credentials.LoadAsync()).Should().Be(("new-agent", "new-refresh"));
        credentials.DeviceIdentity.Should().Be("stable-device-key");
    }

    [Fact]
    public async Task AgentNotFoundWithoutExplicitEnrollmentInputFailsWithoutRetryingTokenOrEnrollment()
    {
        var credentials = new FakeCredentialStore(("old-agent", "old-refresh"));
        var enrollment = new FakeEnrollmentService(("new-agent", "new-refresh"));
        var tokens = new SequenceTokenService(credentials,
            new AgentClientAuthException("Agent identity was not found.", 403, code: "agent_not_found"));
        var bootstrap = new FakeInjectedEnrollmentBootstrap();

        var action = () => StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions(), tokens, enrollment, credentials, bootstrap, _ => { }, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentClientAuthException>();
        exception.Which.Message.Should().Contain("Supply a valid enrollment code");
        exception.Which.Code.Should().Be("agent_not_found");
        tokens.RequestCount.Should().Be(1);
        enrollment.Codes.Should().BeEmpty();
        bootstrap.CallCount.Should().Be(1);
        (await credentials.LoadAsync()).Should().Be(("old-agent", "old-refresh"));
        credentials.DeviceIdentity.Should().Be("stable-device-key");
    }

    [Fact]
    public async Task AgentNotFoundConsumesOneValidatedInjectedGrantDuringSameStartup()
    {
        var credentials = new FakeCredentialStore(("old-agent", "old-refresh"));
        var enrollment = new FakeEnrollmentService(("new-agent", "new-refresh"));
        var tokens = new SequenceTokenService(credentials,
            new AgentClientAuthException("Agent identity was not found.", 403, code: "agent_not_found"),
            ("access-token", DateTimeOffset.UtcNow.AddHours(1)));
        var bootstrap = new FakeInjectedEnrollmentBootstrap("ENR-INJECTED");

        var result = await StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions(), tokens, enrollment, credentials, bootstrap, _ => { }, CancellationToken.None);

        result.AccessToken.Should().Be("access-token");
        bootstrap.CallCount.Should().Be(1);
        enrollment.Codes.Should().Equal("ENR-INJECTED");
        tokens.RequestCount.Should().Be(2);
        (await credentials.LoadAsync()).Should().Be(("new-agent", "new-refresh"));
    }

    [Fact]
    public async Task AgentNotFoundWithRealBootstrapConsumesOneGrantAndKeepsDeviceKeyDuringSameStartup()
    {
        var credentials = new FakeCredentialStore(("4b750cbb-0d54-4e74-a7de-863d273b4d76", "old-refresh"));
        var originalKey = await credentials.GetOrCreateAsync(CancellationToken.None);
        var installDirectory = Path.Combine(Path.GetTempPath(), $"netratel-recovery-{Guid.NewGuid():N}");
        var enrollmentPath = Path.Combine(installDirectory, "netratel.enroll.json");
        var fileSystem = new FakeInjectedEnrollmentFileSystem();
        fileSystem.Files[enrollmentPath] = JsonSerializer.Serialize(new InjectedEnrollmentBootstrap.InjectedEnrollmentPayload(
            "netratel.enroll.v1", 42, "ENR-VALID-ONCE", "https://netratel-dev-api.example",
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30)));
        var bootstrap = new InjectedEnrollmentBootstrap(fileSystem, () => installDirectory, _ => { });
        var handler = new RecoveryHttpHandler(rejectEnrollment: false);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var enrollment = new AgentEnrollmentService(http, credentials);
        var tokens = new ClientAgentTokenService(http, credentials, credentials);

        var result = await StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel-dev-api.example" },
            tokens, enrollment, credentials, bootstrap, _ => { }, CancellationToken.None);

        result.AccessToken.Should().Be("recovered-access-token");
        handler.TokenRequests.Should().Be(2);
        handler.EnrollmentRequests.Should().Be(1);
        handler.EnrollmentCodes.Should().Equal("ENR-VALID-ONCE");
        handler.EnrollmentPublicKeys.Should().Equal(originalKey.PublicKey);
        handler.TokenAgentIds.Should().Equal("4b750cbb-0d54-4e74-a7de-863d273b4d76", "f839b63d-5d1f-4b5c-a064-0f3b9e87e216");
        handler.TokenRefreshTokens.Should().Equal("old-refresh", "fresh-refresh");
        fileSystem.Deleted.Should().Equal(enrollmentPath);
        fileSystem.Files.Should().NotContainKey(enrollmentPath);
        (await credentials.LoadAsync()).Should().Be(("f839b63d-5d1f-4b5c-a064-0f3b9e87e216", "rotated-refresh"));
        (await credentials.GetOrCreateAsync(CancellationToken.None)).PublicKey.Should().Be(originalKey.PublicKey);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("rejected-by-server")]
    public async Task AgentNotFoundWithInvalidOrRejectedInjectedGrantAcrossRestartsPreservesIdentity(string grantState)
    {
        var originalCredentials = ("4b750cbb-0d54-4e74-a7de-863d273b4d76", "old-refresh");
        var credentials = new FakeCredentialStore(originalCredentials);
        var originalKey = await credentials.GetOrCreateAsync(CancellationToken.None);
        var installDirectory = Path.Combine(Path.GetTempPath(), $"netratel-invalid-recovery-{Guid.NewGuid():N}");
        var enrollmentPath = Path.Combine(installDirectory, "netratel.enroll.json");
        var fileSystem = new FakeInjectedEnrollmentFileSystem();
        if (grantState != "absent")
        {
            var validTo = grantState == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(30);
            var issuer = grantState == "wrong-issuer"
                ? "https://another-api.example"
                : "https://netratel-dev-api.example";
            fileSystem.Files[enrollmentPath] = JsonSerializer.Serialize(new InjectedEnrollmentBootstrap.InjectedEnrollmentPayload(
                "netratel.enroll.v1", 42, "ENR-NOT-AUTHORIZED", issuer, DateTime.UtcNow.AddMinutes(-5), validTo));
        }

        var bootstrap = new InjectedEnrollmentBootstrap(fileSystem, () => installDirectory, _ => { });
        var handler = new RecoveryHttpHandler(
            rejectEnrollment: grantState == "rejected-by-server",
            alwaysForbidden: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var enrollment = new AgentEnrollmentService(http, credentials);
        var options = new ClientOptions { ApiBaseUrl = "https://netratel-dev-api.example" };

        for (var start = 0; start < 2; start++)
        {
            var tokens = new ClientAgentTokenService(http, credentials, credentials);
            var action = () => StartupTokenAcquisition.GetAccessTokenAsync(
                options, tokens, enrollment, credentials, bootstrap, _ => { }, CancellationToken.None);

            await action.Should().ThrowAsync<AgentClientAuthException>();
            (await credentials.LoadAsync()).Should().Be(originalCredentials);
            (await credentials.GetOrCreateAsync(CancellationToken.None)).PublicKey.Should().Be(originalKey.PublicKey);
        }

        handler.TokenRequests.Should().Be(2);
        handler.EnrollmentRequests.Should().Be(grantState == "rejected-by-server" ? 2 : 0);
        fileSystem.Deleted.Should().BeEmpty();
        if (grantState == "absent")
            fileSystem.Files.Should().NotContainKey(enrollmentPath);
        else
            fileSystem.Files.Should().ContainKey(enrollmentPath);
    }

    [Theory]
    [InlineData("invalid_token")]
    [InlineData(null)]
    public async Task OtherForbiddenErrorsNeverTriggerEnrollmentRecovery(string? code)
    {
        var credentials = new FakeCredentialStore(("agent", "refresh"));
        var enrollment = new FakeEnrollmentService(("new-agent", "new-refresh"));
        var tokens = new SequenceTokenService(credentials,
            new AgentClientAuthException("Forbidden.", 403, code: code));
        var bootstrap = new FakeInjectedEnrollmentBootstrap();

        var action = () => StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions { EnrollmentCode = "ENR-VALID" }, tokens, enrollment, credentials,
            bootstrap, _ => { }, CancellationToken.None);

        await action.Should().ThrowAsync<AgentClientAuthException>();
        tokens.RequestCount.Should().Be(1);
        enrollment.Codes.Should().BeEmpty();
        bootstrap.CallCount.Should().Be(0);
        (await credentials.LoadAsync()).Should().Be(("agent", "refresh"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"code\":42}")]
    [InlineData("{\"extensions\":{\"code\":false,\"correlationId\":{}}}")]
    public async Task MalformedForbiddenProblemShapesNeverTriggerRecoveryOrChangeStoredIdentity(string problemBody)
    {
        var originalCredentials = ("4b750cbb-0d54-4e74-a7de-863d273b4d76", "old-refresh");
        var credentials = new FakeCredentialStore(originalCredentials);
        var originalKey = await credentials.GetOrCreateAsync(CancellationToken.None);
        var installDirectory = Path.Combine(Path.GetTempPath(), $"netratel-malformed-problem-{Guid.NewGuid():N}");
        var enrollmentPath = Path.Combine(installDirectory, "netratel.enroll.json");
        var fileSystem = new FakeInjectedEnrollmentFileSystem();
        fileSystem.Files[enrollmentPath] = JsonSerializer.Serialize(new InjectedEnrollmentBootstrap.InjectedEnrollmentPayload(
            "netratel.enroll.v1", 42, "ENR-VALID-ONCE", "https://netratel-dev-api.example",
            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30)));
        var bootstrap = new InjectedEnrollmentBootstrap(fileSystem, () => installDirectory, _ => { });
        var handler = new RecoveryHttpHandler(
            rejectEnrollment: false,
            tokenFailureBody: problemBody,
            alwaysForbidden: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://netratel-dev-api.example") };
        var enrollment = new AgentEnrollmentService(http, credentials);
        var options = new ClientOptions { ApiBaseUrl = "https://netratel-dev-api.example" };

        for (var start = 0; start < 2; start++)
        {
            var tokens = new ClientAgentTokenService(http, credentials, credentials);
            var action = () => StartupTokenAcquisition.GetAccessTokenAsync(
                options, tokens, enrollment, credentials, bootstrap, _ => { }, CancellationToken.None);

            var exception = await action.Should().ThrowAsync<AgentClientAuthException>();
            exception.Which.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
            exception.Which.Code.Should().BeNull();
            exception.Which.Message.Should().NotContain(problemBody);
            (await credentials.LoadAsync()).Should().Be(originalCredentials);
            (await credentials.GetOrCreateAsync(CancellationToken.None)).PublicKey.Should().Be(originalKey.PublicKey);
        }

        handler.TokenRequests.Should().Be(2);
        handler.EnrollmentRequests.Should().Be(0);
        fileSystem.Deleted.Should().BeEmpty();
        fileSystem.Files.Should().ContainKey(enrollmentPath);
    }

    [Fact]
    public async Task DisabledAgentNeverTriggersEnrollmentRecovery()
    {
        var credentials = new FakeCredentialStore(("agent", "refresh"));
        var enrollment = new FakeEnrollmentService(("new-agent", "new-refresh"));
        var tokens = new SequenceTokenService(credentials,
            new AgentClientAuthException("Agent is disabled.", 403, code: "agent_disabled"));
        using var stopping = new CancellationTokenSource();

        var action = StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions { EnrollmentCode = "ENR-VALID" }, tokens, enrollment, credentials,
            new FakeInjectedEnrollmentBootstrap(), _ => { }, stopping.Token);
        await tokens.FirstRequestStarted.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        tokens.RequestCount.Should().Be(1);
        enrollment.Codes.Should().BeEmpty();
        (await credentials.LoadAsync()).Should().Be(("agent", "refresh"));
    }

    [Fact]
    public async Task FailedRecoveryTokenRequestDoesNotEnrollMoreThanOnce()
    {
        var credentials = new FakeCredentialStore(("old-agent", "old-refresh"));
        var enrollment = new FakeEnrollmentService(("new-agent", "new-refresh"));
        var tokens = new SequenceTokenService(credentials,
            new AgentClientAuthException("Agent identity was not found.", 403, code: "agent_not_found"),
            new AgentClientAuthException("Invalid token.", 401, code: "invalid_token"));

        var action = () => StartupTokenAcquisition.GetAccessTokenAsync(
            new ClientOptions { EnrollmentCode = "ENR-VALID" }, tokens, enrollment, credentials,
            new FakeInjectedEnrollmentBootstrap(), _ => { }, CancellationToken.None);

        await action.Should().ThrowAsync<AgentClientAuthException>().Where(e => e.Code == "invalid_token");
        tokens.RequestCount.Should().Be(2);
        enrollment.Codes.Should().Equal("ENR-VALID");
    }

    private sealed class FakeCredentialStore((string AgentId, string RefreshToken)? initial) : IAgentCredentialStore, IAgentDeviceKeyStore
    {
        private (string AgentId, string RefreshToken)? _credentials = initial;
        private readonly AgentDeviceKeyMaterial _deviceKey = CreateDeviceKey();
        public string DeviceIdentity { get; } = "stable-device-key";
        public Task SaveAsync(string agentId, string refreshToken)
        {
            _credentials = (agentId, refreshToken);
            return Task.CompletedTask;
        }

        public Task<(string AgentId, string RefreshToken)?> LoadAsync() => Task.FromResult(_credentials);
        public Task ClearRefreshCredentialsAsync()
        {
            _credentials = null;
            return Task.CompletedTask;
        }
        public Task ResetInstallationIdentityAsync() => Task.CompletedTask;
        public Task ClearAsync() => ClearRefreshCredentialsAsync();
        public Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct) => Task.FromResult(_deviceKey);
        public Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct) => Task.FromResult<AgentDeviceKeyMaterial?>(_deviceKey);

        private static AgentDeviceKeyMaterial CreateDeviceKey()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new AgentDeviceKeyMaterial(
                Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                "ecdsa-p256");
        }
    }

    private sealed class FakeEnrollmentService((string AgentId, string RefreshToken) result) : IAgentEnrollmentService
    {
        public List<string> Codes { get; } = [];
        public Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Codes.Add(enrollmentCode);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeInjectedEnrollmentBootstrap : IInjectedEnrollmentBootstrap
    {
        private readonly string? _enrollmentCode;
        public FakeInjectedEnrollmentBootstrap(string? enrollmentCode = null) => _enrollmentCode = enrollmentCode;
        public int CallCount { get; private set; }
        public async Task<(string AgentId, string RefreshToken)?> TryEnrollAsync(
            ClientOptions options, IAgentEnrollmentService enrollmentService, IAgentCredentialStore credentialStore,
            CancellationToken ct)
        {
            CallCount++;
            if (_enrollmentCode is null) return null;
            var enrolled = await enrollmentService.EnrollAsync(_enrollmentCode, ct);
            await credentialStore.SaveAsync(enrolled.AgentId, enrolled.RefreshToken);
            return enrolled;
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

    private sealed class RecoveryHttpHandler : HttpMessageHandler
    {
        private readonly bool _rejectEnrollment;
        private readonly string _tokenFailureBody;
        private readonly bool _alwaysForbidden;
        private readonly string _newAgentId = "f839b63d-5d1f-4b5c-a064-0f3b9e87e216";

        public RecoveryHttpHandler(
            bool rejectEnrollment,
            string tokenFailureBody = "{\"code\":\"agent_not_found\"}",
            bool alwaysForbidden = false)
        {
            _rejectEnrollment = rejectEnrollment;
            _tokenFailureBody = tokenFailureBody;
            _alwaysForbidden = alwaysForbidden;
        }

        public int TokenRequests { get; private set; }
        public int EnrollmentRequests { get; private set; }
        public List<string> EnrollmentCodes { get; } = [];
        public List<string> EnrollmentPublicKeys { get; } = [];
        public List<string> TokenAgentIds { get; } = [];
        public List<string> TokenRefreshTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (path == "/api/v1/agents/token")
            {
                TokenRequests++;
                var agentId = body.RootElement.GetProperty("agentId").GetString()!;
                var refreshToken = body.RootElement.GetProperty("refreshToken").GetString()!;
                TokenAgentIds.Add(agentId);
                TokenRefreshTokens.Add(refreshToken);
                var wasEnrolledForThisIdentity = EnrollmentRequests > 0 &&
                    string.Equals(agentId, _newAgentId, StringComparison.Ordinal) &&
                    string.Equals(refreshToken, "fresh-refresh", StringComparison.Ordinal);
                if (TokenRequests == 1 || _alwaysForbidden || !wasEnrolledForThisIdentity)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent(_tokenFailureBody, Encoding.UTF8, "application/problem+json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"accessToken":"recovered-access-token","expiresIn":3600,"refreshToken":"rotated-refresh"}""", Encoding.UTF8, "application/json")
                };
            }

            if (path == "/api/v1/agents/enroll")
            {
                EnrollmentRequests++;
                EnrollmentCodes.Add(body.RootElement.GetProperty("enrollmentCode").GetString()!);
                EnrollmentPublicKeys.Add(body.RootElement.GetProperty("publicKey").GetString()!);
                if (_rejectEnrollment)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/problem+json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"agentId\":\"{_newAgentId}\",\"refreshToken\":\"fresh-refresh\",\"expiresInDays\":7}}", Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected auth request path '{path}'.");
        }
    }

    private sealed class SequenceTokenService(
        IAgentCredentialStore credentials,
        params object[] outcomes) : IAgentTokenService
    {
        private int _requests;
        private readonly TaskCompletionSource _firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount => Volatile.Read(ref _requests);
        public Task FirstRequestStarted => _firstRequestStarted.Task;

        public async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _requests) - 1;
            if (index == 0) _firstRequestStarted.TrySetResult();
            if (outcomes[index] is AgentClientAuthException exception)
            {
                if (exception.ShouldClearCredentials) await credentials.ClearRefreshCredentialsAsync();
                throw exception;
            }

            return ((string AccessToken, DateTimeOffset ExpiresAtUtc))outcomes[index];
        }
    }
}
