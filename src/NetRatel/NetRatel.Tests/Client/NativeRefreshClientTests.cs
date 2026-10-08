using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using NetRatel.Tests.API;
using Xunit;
using NetRatel.Shared.Security;

namespace NetRatel.Tests.Client;

public sealed class NativeRefreshClientTests
{
    [Fact]
    public async Task RepeatedResponseLossAcrossLongGapsAndRestartKeepsOneProtectedExchangeAndFreshProof()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            var key = await store.GetOrCreateAsync(CancellationToken.None);
            var agentId = Guid.NewGuid();
            await store.SaveAsync(agentId.ToString(), "original-parent-secret");
            var clock = new RenewalManualTimeProvider();
            var ids = new List<Guid>();
            var nonces = new List<string>();
            var timestamps = new List<DateTimeOffset>();
            var capabilities = 0;
            using var http = NewHttp(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    capabilities++;
                    return Ok(new { refreshExchangeVersion = 1, rotationEnabled = true });
                }
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = json.RootElement;
                root.GetProperty("refreshToken").GetString().Should().Be("original-parent-secret");
                var id = root.GetProperty("exchangeId").GetGuid();
                ids.Add(id);
                var timestamp = DateTimeOffset.Parse(request.Headers.GetValues("X-NetRatel-Timestamp").Single());
                var nonce = request.Headers.GetValues("X-NetRatel-Nonce").Single();
                timestamps.Add(timestamp);
                nonces.Add(nonce);
                var hash = PopSignatureService.ComputeTokenBodyHash(agentId, "original-parent-secret", ["netratel:connect"], 1, id);
                PopSignatureService.VerifySignature(key.Algorithm, key.PublicKey, request.Headers.GetValues("X-NetRatel-Signature").Single(),
                    PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, nonce, hash)).Should().BeTrue();
                if (ids.Count <= 2) throw new HttpRequestException("Response lost after server commit.");
                return Ok(new { accessToken = "recovered", expiresIn = 900, refreshToken = "same-successor-secret", exchangeId = id });
            });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var service = new ClientAgentTokenService(http, new AgentCredentialStore(path), new AgentCredentialStore(path), clock);
                await Assert.ThrowsAsync<AgentClientAuthException>(() => service.GetAccessTokenAsync(CancellationToken.None));
                (await store.LoadPendingExchangeAsync(CancellationToken.None))!.ExchangeId.Should().Be(ids[0]);
                var protectedBytes = await File.ReadAllBytesAsync(path + ".native-refresh");
                System.Text.Encoding.UTF8.GetString(protectedBytes).Should().NotContain("original-parent-secret");
                clock.Advance(TimeSpan.FromHours(36));
            }
            var restarted = new ClientAgentTokenService(http, store, store, clock);
            (await restarted.GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("recovered");
            ids.Should().HaveCount(3).And.OnlyContain(id => id == ids[0]);
            nonces.Distinct().Should().HaveCount(3);
            timestamps.Should().Equal(clock.GetUtcNow().AddHours(-72), clock.GetUtcNow().AddHours(-36), clock.GetUtcNow());
            capabilities.Should().Be(1, "pending supported exchanges bypass renegotiation after restart");
            (await store.LoadAsync())!.Value.RefreshToken.Should().Be("same-successor-secret");
            (await store.LoadPendingExchangeAsync(CancellationToken.None)).Should().BeNull();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OldApiIsNegotiatedBeforeMutationAndUsesUnextendedLegacyWire()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            await store.GetOrCreateAsync(CancellationToken.None);
            await store.SaveAsync(Guid.NewGuid().ToString(), "parent");
            var requests = new List<HttpMethod>();
            using var http = NewHttp(async (request, ct) =>
            {
                requests.Add(request.Method);
                File.Exists(path + ".native-refresh").Should().BeFalse();
                if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body.RootElement.TryGetProperty("exchangeId", out _).Should().BeFalse();
                return Ok(new { accessToken = "legacy", expiresIn = 900, refreshToken = "legacy-successor" });
            });
            (await new ClientAgentTokenService(http, store, store).GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("legacy");
            requests.Should().Equal(HttpMethod.Get, HttpMethod.Post);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DefaultNonRotatingApiDoesNotCreatePendingStateOrRequireSuccessor()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            await store.SaveAsync(Guid.NewGuid().ToString(), "nonrotating-parent");
            using var http = NewHttp(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get) return Ok(new { refreshExchangeVersion = 1, rotationEnabled = false });
                File.Exists(path + ".native-refresh").Should().BeFalse();
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                body.RootElement.TryGetProperty("exchangeId", out _).Should().BeFalse();
                return Ok(new { accessToken = "authorized", expiresIn = 900 });
            });
            (await new ClientAgentTokenService(http, store, store).GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("authorized");
            (await store.LoadAsync())!.Value.RefreshToken.Should().Be("nonrotating-parent");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RotationEnabledWhileClientRemainsRunningIsRenegotiatedBeforeAnyRotatingLegacySend()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            await store.SaveAsync(Guid.NewGuid().ToString(), "parent");
            var rotating = false;
            var capabilityRequests = 0;
            using var http = NewHttp(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    capabilityRequests++;
                    return Ok(new { refreshExchangeVersion = 1, rotationEnabled = rotating });
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (!rotating)
                {
                    body.RootElement.TryGetProperty("exchangeId", out _).Should().BeFalse();
                    return Ok(new { accessToken = "before", expiresIn = 900 });
                }
                File.Exists(path + ".native-refresh").Should().BeTrue();
                var exchangeId = body.RootElement.GetProperty("exchangeId").GetGuid();
                return Ok(new { accessToken = "after", expiresIn = 900, refreshToken = "successor", exchangeId });
            });
            var service = new ClientAgentTokenService(http, store, store);
            (await service.GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("before");
            rotating = true;
            service.InvalidateAccessToken("before").Should().BeTrue();
            (await service.GetAccessTokenAsync(CancellationToken.None)).AccessToken.Should().Be("after");
            capabilityRequests.Should().Be(2);
            (await store.LoadAsync())!.Value.RefreshToken.Should().Be("successor");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CapabilityOutageDoesNotStartExchangeOrDowngradeToLegacy()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            await store.SaveAsync(Guid.NewGuid().ToString(), "parent");
            using var http = NewHttp((request, _) =>
            {
                request.Method.Should().Be(HttpMethod.Get);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            });
            var error = await Assert.ThrowsAsync<AgentClientAuthException>(() => new ClientAgentTokenService(http, store, store).GetAccessTokenAsync(CancellationToken.None));
            error.EndpointRole.Should().Be(AgentAuthEndpointRole.Capabilities);
            error.IsRecoverable.Should().BeTrue();
            File.Exists(path + ".native-refresh").Should().BeFalse();
            (await store.LoadAsync())!.Value.RefreshToken.Should().Be("parent");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProtectedJournalReconcilesDurableSaveBoundaryAndLaterLegacySaveWithoutRestoringStaleParent()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            var agent = Guid.NewGuid().ToString();
            var key = await store.GetOrCreateAsync(CancellationToken.None);
            await store.SaveAsync(agent, "parent");
            var parentBytes = await File.ReadAllBytesAsync(path);
            var exchange = new PendingAgentRefreshExchange(1, Guid.NewGuid(), agent, "parent", DeviceHash(key), ["netratel:connect"]);
            await store.BeginRefreshExchangeAsync(exchange, CancellationToken.None);
            // A released writer destructively rewrites only agent.dat; pending survives.
            await File.WriteAllBytesAsync(path, parentBytes);
            (await new AgentCredentialStore(path).LoadPendingExchangeAsync(CancellationToken.None))!.ExchangeId.Should().Be(exchange.ExchangeId);
            await store.CompleteRefreshExchangeAsync(exchange, "successor", CancellationToken.None);
            // Emulate a crash before the main-file rename / old parent snapshot.
            await File.WriteAllBytesAsync(path, parentBytes);
            var restarted = new AgentCredentialStore(path);
            (await restarted.LoadAsync())!.Value.RefreshToken.Should().Be("successor");
            (await restarted.LoadAsync(CancellationToken.None)).Should().Be(key);
            // A legitimate beta.1 save after using that successor takes precedence.
            await restarted.SaveAsync(agent, "later-legacy-successor");
            (await new AgentCredentialStore(path).LoadAsync())!.Value.RefreshToken.Should().Be("later-legacy-successor");
            File.Exists(path + ".native-refresh").Should().BeFalse();
            var obsolete = await Assert.ThrowsAsync<AgentClientAuthException>(() => store.CompleteRefreshExchangeAsync(exchange, "successor", CancellationToken.None));
            obsolete.Code.Should().Be("refresh_exchange_obsolete");
            obsolete.IsRecoverable.Should().BeTrue();
            (await store.LoadAsync())!.Value.RefreshToken.Should().Be("later-legacy-successor");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TwoStoreInstancesPersistOnePendingWinnerAndStaleParentSaveCannotReplaceCommittedSuccessor()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var first = new AgentCredentialStore(path);
            var second = new AgentCredentialStore(path);
            var agent = Guid.NewGuid().ToString();
            var key = await first.GetOrCreateAsync(CancellationToken.None);
            await first.SaveAsync(agent, "parent");
            var one = new PendingAgentRefreshExchange(1, Guid.NewGuid(), agent, "parent", DeviceHash(key), ["netratel:connect"]);
            var two = one with { ExchangeId = Guid.NewGuid() };
            var winners = await Task.WhenAll(Task.Run(() => first.BeginRefreshExchangeAsync(one, CancellationToken.None)),
                Task.Run(() => second.BeginRefreshExchangeAsync(two, CancellationToken.None)));
            winners[0].ExchangeId.Should().Be(winners[1].ExchangeId);
            (await new AgentCredentialStore(path).LoadPendingExchangeAsync(CancellationToken.None))!.ExchangeId.Should().Be(winners[0].ExchangeId);
            await Task.WhenAll(first.CompleteRefreshExchangeAsync(winners[0], "successor", CancellationToken.None),
                second.CompleteRefreshExchangeAsync(winners[1], "successor", CancellationToken.None));
            var committedBytes = await File.ReadAllBytesAsync(path);
            await second.SaveAsync(agent, "parent");
            (await File.ReadAllBytesAsync(path)).Should().Equal(committedBytes, "a known stale parent cannot be temporarily written over its successor");
            (await first.LoadAsync())!.Value.RefreshToken.Should().Be("successor");
            var conflict = await Assert.ThrowsAsync<AgentClientAuthException>(() => first.CompleteRefreshExchangeAsync(winners[0], "conflicting-successor", CancellationToken.None));
            conflict.FailureKind.Should().Be(AgentAuthFailureKind.Protocol);
            conflict.IsRecoverable.Should().BeFalse();
            (await first.LoadAsync())!.Value.RefreshToken.Should().Be("successor");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CredentialLeaseWaitIsCancelledWithoutChangingProtectedIdentity()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            await store.SaveAsync(Guid.NewGuid().ToString(), "parent");
            var originalBytes = await File.ReadAllBytesAsync(path);
            using var heldLease = new FileStream(path + ".native-refresh.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var stopping = new CancellationTokenSource();
            var read = store.LoadCredentialsAsync(stopping.Token);
            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            (await File.ReadAllBytesAsync(path)).Should().Equal(originalBytes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExchangeMutationRejectsAChangedDeviceBindingWithoutReplacingCredentials()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "agent.dat");
            var store = new AgentCredentialStore(path);
            var agent = Guid.NewGuid().ToString();
            var key = await store.GetOrCreateAsync(CancellationToken.None);
            await store.SaveAsync(agent, "parent");
            var exchange = new PendingAgentRefreshExchange(1, Guid.NewGuid(), agent, "parent", "different-key-hash", ["netratel:connect"]);
            await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.BeginRefreshExchangeAsync(exchange, CancellationToken.None));
            File.Exists(path + ".native-refresh").Should().BeFalse();
            var current = exchange with { DeviceKeyHash = DeviceHash(key) };
            await store.BeginRefreshExchangeAsync(current, CancellationToken.None);
            var replacementPath = Path.Combine(directory, "replacement.dat");
            var replacement = new AgentCredentialStore(replacementPath);
            await replacement.GetOrCreateAsync(CancellationToken.None);
            await replacement.SaveAsync(agent, "parent");
            var changedIdentityBytes = await File.ReadAllBytesAsync(replacementPath);
            await File.WriteAllBytesAsync(path, changedIdentityBytes);
            await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.CompleteRefreshExchangeAsync(current, "successor", CancellationToken.None));
            (await File.ReadAllBytesAsync(path)).Should().Equal(changedIdentityBytes);
            File.Exists(path + ".native-refresh").Should().BeTrue();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string DeviceHash(AgentDeviceKeyMaterial key) =>
        PopSignatureService.ComputeBodyHash($"{key.Algorithm.ToLowerInvariant()}:{key.PublicKey}");

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-native-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private static HttpClient NewHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("https://api.example") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
