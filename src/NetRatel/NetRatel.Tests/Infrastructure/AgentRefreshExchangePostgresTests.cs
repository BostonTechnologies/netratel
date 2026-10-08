using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;
using NetRatel.Shared.Security;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class AgentRefreshExchangePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Theory]
    [InlineData(24, true)]
    [InlineData(72, true)]
    [InlineData(72, false)]
    public async Task Lost_response_resumes_the_exact_successor_after_extended_outage_and_legacy_rollback(int hours, bool enablePoP)
    {
        await using var fixture = await ExchangeFixture.CreateAsync(postgres, enablePoP);
        var exchangeId = Guid.NewGuid();
        var issued = await fixture.ExchangeAsync(fixture.ParentToken, exchangeId);
        Assert.Equal(exchangeId, issued.ExchangeId);
        Assert.False(string.IsNullOrWhiteSpace(issued.RefreshToken));
        await using (var db = fixture.OpenDb())
        {
            var parent = await db.AgentRefreshTokens.SingleAsync(x => x.TokenHash == EnrollmentService.HashRefreshToken(fixture.ParentToken));
            var successorRow = await db.AgentRefreshTokens.SingleAsync(x => x.Id == parent.ReplacedByTokenId);
            Assert.False(string.IsNullOrWhiteSpace(parent.ProtectedSuccessorToken));
            Assert.NotEqual(issued.RefreshToken, parent.ProtectedSuccessorToken);
            Assert.Equal(parent.ExpiresAtUtc, successorRow.ExpiresAtUtc);
        }

        // Discard the response, restart the request service and advance virtual
        // time beyond both the access-token lifetime and legacy recovery window.
        fixture.Clock.Advance(TimeSpan.FromHours(hours));
        fixture.RestartProtectionProvider();
        var resumed = await fixture.ExchangeAsync(fixture.ParentToken, exchangeId);
        Assert.Equal(issued.RefreshToken, resumed.RefreshToken);
        Assert.Equal(exchangeId, resumed.ExchangeId);
        Assert.NotEqual(issued.AccessToken, resumed.AccessToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(resumed.AccessToken);
        Assert.Equal(fixture.Clock.GetUtcNow().UtcDateTime, jwt.ValidFrom);
        Assert.True(jwt.ValidTo > fixture.Clock.GetUtcNow().UtcDateTime);

        // Lose the recovered response as well. Repeated long outages must keep
        // resolving the original result rather than minting a recovery chain.
        fixture.Clock.Advance(TimeSpan.FromHours(hours));
        fixture.RestartProtectionProvider();
        Assert.Equal(issued.RefreshToken, (await fixture.ExchangeAsync(fixture.ParentToken, exchangeId)).RefreshToken);

        // A rollback client has no exchange fields and signs the legacy body.
        // It must resolve the recorded successor before applying two-minute reuse.
        if (!enablePoP)
        {
            // Recorded exchanges require a fresh device proof even when the
            // deployment's ordinary legacy-token PoP switch is disabled.
            var unsignedLegacy = fixture.SignRequest(fixture.ParentToken, null) with { ProofSignature = null };
            var missingProof = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeRequestAsync(unsignedLegacy));
            Assert.Equal("pop_headers_required", missingProof.Code);
        }
        var legacy = await fixture.ExchangeAsync(fixture.ParentToken, null);
        Assert.Equal(issued.RefreshToken, legacy.RefreshToken);
        Assert.Equal(2, await fixture.CountRefreshTokensAsync());

        // Once the successor is legitimately used, its predecessor can no longer
        // recover any secret, with either the extended or legacy request shape.
        var successor = await fixture.ExchangeAsync(issued.RefreshToken!, Guid.NewGuid());
        Assert.NotEqual(issued.RefreshToken, successor.RefreshToken);
        await using (var db = fixture.OpenDb())
        {
            var parent = await db.AgentRefreshTokens.SingleAsync(x => x.TokenHash == EnrollmentService.HashRefreshToken(fixture.ParentToken));
            Assert.Equal(fixture.Clock.GetUtcNow(), parent.ExchangeAcknowledgedAtUtc);
            Assert.Null(parent.ProtectedSuccessorToken);
        }
        var unavailable = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId));
        Assert.Equal("refresh_exchange_unavailable", unavailable.Code);
        var legacyUnavailable = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, null));
        Assert.Equal(401, legacyUnavailable.StatusCode);
        Assert.Equal(3, await fixture.CountRefreshTokensAsync());
    }

    [Fact]
    public async Task Different_exchange_ids_racing_on_one_parent_have_one_winner_and_never_fork()
    {
        await using var fixture = await ExchangeFixture.CreateAsync(postgres);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var outcomes = await Task.WhenAll(
            fixture.TryExchangeAsync(fixture.ParentToken, firstId),
            fixture.TryExchangeAsync(fixture.ParentToken, secondId));

        var winner = Assert.Single(outcomes, x => x.Response is not null).Response!;
        var loser = Assert.Single(outcomes, x => x.Error is not null).Error!;
        Assert.Equal("refresh_exchange_conflict", loser.Code);
        Assert.Equal(2, await fixture.CountRefreshTokensAsync());
        var replay = await fixture.ExchangeAsync(fixture.ParentToken, winner.ExchangeId);
        Assert.Equal(winner.RefreshToken, replay.RefreshToken);
        var losingId = winner.ExchangeId == firstId ? secondId : firstId;
        var conflict = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, losingId));
        Assert.Equal("refresh_exchange_conflict", conflict.Code);

        await using var db = fixture.OpenDb();
        var rows = await db.AgentRefreshTokens.Where(x => x.AgentId == fixture.AgentId).ToListAsync();
        var parent = Assert.Single(rows, x => x.TokenHash == EnrollmentService.HashRefreshToken(fixture.ParentToken));
        var live = Assert.Single(rows, x => x.RevokedAtUtc is null);
        Assert.Equal(live.Id, parent.ReplacedByTokenId);
        Assert.Equal(EnrollmentService.HashRefreshToken(winner.RefreshToken!), live.TokenHash);
    }

    [Fact]
    public async Task Recorded_exchange_rechecks_current_agent_key_scopes_and_natural_expiry()
    {
        await using var fixture = await ExchangeFixture.CreateAsync(postgres);
        var exchangeId = Guid.NewGuid();
        var issued = await fixture.ExchangeAsync(fixture.ParentToken, exchangeId);

        await fixture.UpdateAgentAsync(agent => { agent.IsEnabled = false; agent.Status = AgentStatus.Disabled; });
        var disabled = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId));
        Assert.Equal("agent_disabled", disabled.Code);
        await fixture.UpdateAgentAsync(agent => { agent.IsEnabled = true; agent.Status = AgentStatus.Active; });

        using var changedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var changedPublic = Convert.ToBase64String(changedKey.ExportSubjectPublicKeyInfo());
        await fixture.UpdateAgentAsync(agent => { agent.PublicKey = changedPublic; agent.PublicKeyFingerprint = Fingerprint(changedPublic); });
        var changed = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId,
            signingPrivateKey: Convert.ToBase64String(changedKey.ExportPkcs8PrivateKey())));
        Assert.Equal("refresh_exchange_binding_mismatch", changed.Code);
        await fixture.UpdateAgentAsync(agent => { agent.PublicKey = fixture.PublicKey; agent.PublicKeyFingerprint = Fingerprint(fixture.PublicKey); });

        await fixture.UpdateAgentAsync(agent => agent.TenantId = 92);
        var changedTenant = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId));
        Assert.Equal("refresh_exchange_binding_mismatch", changedTenant.Code);
        await fixture.UpdateAgentAsync(agent => agent.TenantId = 91);

        var requireCertificate = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeRequestAsync(
            fixture.SignRequest(fixture.ParentToken, exchangeId), requireMtls: true));
        Assert.Equal("mtls_required", requireCertificate.Code);

        await fixture.UpdateAgentAsync(agent => agent.AllowedScopesJson = JsonSerializer.Serialize(new[] { "netratel:connect" }));
        var narrowed = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId));
        Assert.Equal("refresh_exchange_authorization_changed", narrowed.Code);
        await fixture.UpdateAgentAsync(agent => agent.AllowedScopesJson = JsonSerializer.Serialize(ExchangeFixture.Scopes));

        var differentScopes = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId,
            requestedScopes: ["netratel:connect"]));
        Assert.Equal("refresh_exchange_binding_mismatch", differentScopes.Code);
        var changedPolicy = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeRequestAsync(
            fixture.SignRequest(fixture.ParentToken, exchangeId), enableRefreshRotation: false));
        Assert.Equal(403, changedPolicy.StatusCode);
        Assert.Equal("refresh_exchange_policy_changed", changedPolicy.Code);
        Assert.Equal(issued.RefreshToken, (await fixture.ExchangeAsync(fixture.ParentToken, exchangeId)).RefreshToken);

        fixture.Clock.Advance(TimeSpan.FromDays(90));
        var expired = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeAsync(fixture.ParentToken, exchangeId));
        Assert.Equal("refresh_token_expired", expired.Code);
        Assert.Equal(2, await fixture.CountRefreshTokensAsync());
        await using var expiredDb = fixture.OpenDb();
        var expiredParent = await expiredDb.AgentRefreshTokens.SingleAsync(x => x.TokenHash == EnrollmentService.HashRefreshToken(fixture.ParentToken));
        Assert.Null(expiredParent.ProtectedSuccessorToken);
    }

    [Fact]
    public async Task Exchange_id_tampering_invalidates_proof_before_any_rotation()
    {
        await using var fixture = await ExchangeFixture.CreateAsync(postgres, enablePoP: false);
        var unsigned = fixture.SignRequest(fixture.ParentToken, Guid.NewGuid()) with { ProofSignature = null };
        var missingProof = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeRequestAsync(unsigned));
        Assert.Equal("pop_headers_required", missingProof.Code);
        var signed = fixture.SignRequest(fixture.ParentToken, Guid.NewGuid());
        var tampered = signed with { ExchangeId = Guid.NewGuid() };
        var error = await Assert.ThrowsAsync<AgentAuthException>(() => fixture.ExchangeRequestAsync(tampered));
        Assert.Equal("pop_signature_invalid", error.Code);
        Assert.Equal(1, await fixture.CountRefreshTokensAsync());
    }

    private static string Fingerprint(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)));

    private sealed class ExchangeFixture : IAsyncDisposable
    {
        public static readonly string[] Scopes = ["netratel:connect", "netratel:telemetry"];
        private readonly string connection;
        private readonly string keyDirectory;
        private readonly string privateKey;
        private IDataProtectionProvider protection;
        private readonly AgentAuthOptions options;
        private readonly bool enablePoP;

        private ExchangeFixture(string connection, string keyDirectory, string publicKey, string privateKey, ExchangeClock clock, bool enablePoP)
        {
            this.connection = connection;
            this.keyDirectory = keyDirectory;
            this.privateKey = privateKey;
            this.enablePoP = enablePoP;
            PublicKey = publicKey;
            Clock = clock;
            AgentId = Guid.NewGuid();
            ParentToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            protection = CreateProtectionProvider();
            options = new AgentAuthOptions
            {
                PrivateKeyPath = Path.Combine(keyDirectory, "signing.pem"),
                AccessTokenLifetimeMinutes = 15,
                RefreshTokenLifetimeDays = 90
            };
        }

        public Guid AgentId { get; }
        public string ParentToken { get; }
        public string PublicKey { get; }
        public ExchangeClock Clock { get; }

        public static async Task<ExchangeFixture> CreateAsync(PostgreSqlPersistenceFixture postgres, bool enablePoP = true)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var directory = Path.Combine(Path.GetTempPath(), "netratel-refresh-exchange-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(Path.Combine(directory, "signing.pem"), signingKey.ExportPkcs8PrivateKeyPem());
            var fixture = new ExchangeFixture(connection, directory, Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(deviceKey.ExportPkcs8PrivateKey()), new ExchangeClock(), enablePoP);
            try
            {
                await using var db = fixture.OpenDb();
                await db.Database.MigrateAsync();
                var now = fixture.Clock.GetUtcNow();
                db.Tenants.Add(new() { Id = 91, Name = "Refresh exchange regression", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Tenants.Add(new() { Id = 92, Name = "Different refresh exchange tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Agents.Add(new()
                {
                    Id = fixture.AgentId, TenantId = 91, Status = AgentStatus.Active, IsEnabled = true,
                    CreatedAtUtc = now, PublicKey = fixture.PublicKey, PublicKeyFingerprint = Fingerprint(fixture.PublicKey),
                    KeyAlgorithm = "ecdsa-p256", AllowedScopesJson = JsonSerializer.Serialize(Scopes)
                });
                db.AgentRefreshTokens.Add(new()
                {
                    Id = Guid.NewGuid(), AgentId = fixture.AgentId,
                    TokenHash = EnrollmentService.HashRefreshToken(fixture.ParentToken),
                    CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(90)
                });
                await db.SaveChangesAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public OrchestratorDbContext OpenDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);

        private IDataProtectionProvider CreateProtectionProvider() => DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(keyDirectory, "protection")), builder => builder.SetApplicationName("NetRatel.AgentRefreshExchange.Tests"));

        public void RestartProtectionProvider() => protection = CreateProtectionProvider();

        public AgentTokenRequest SignRequest(string token, Guid? exchangeId, IReadOnlyList<string>? requestedScopes = null,
            string? signingPrivateKey = null)
        {
            var scopes = requestedScopes ?? Scopes;
            var timestamp = Clock.GetUtcNow();
            var nonce = Guid.NewGuid().ToString("N");
            var version = exchangeId.HasValue ? 1 : (int?)null;
            var bodyHash = PopSignatureService.ComputeTokenBodyHash(AgentId, token, scopes, version, exchangeId);
            var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, nonce, bodyHash);
            return new AgentTokenRequest(AgentId, token,
                ProofSignature: PopSignatureService.Sign("ecdsa-p256", signingPrivateKey ?? privateKey, message),
                ProofNonce: nonce, ProofTimestampUtc: timestamp, RequestedScopes: scopes, ExchangeVersion: version, ExchangeId: exchangeId);
        }

        public Task<AgentTokenResponse> ExchangeAsync(string token, Guid? exchangeId, IReadOnlyList<string>? requestedScopes = null,
            string? signingPrivateKey = null) => ExchangeRequestAsync(SignRequest(token, exchangeId, requestedScopes, signingPrivateKey));

        public async Task<AgentTokenResponse> ExchangeRequestAsync(AgentTokenRequest request, bool enableRefreshRotation = true, bool requireMtls = false)
        {
            // Each call models a fresh backend request, including after restart,
            // and concurrent calls use independent physical PostgreSQL connections.
            await using var db = OpenDb();
            using var signing = new OidcSigningService(Options.Create(options));
            var service = new AgentTokenService(db, signing, Options.Create(options), Options.Create(new SecurityHardeningOptions
            {
                EnablePoP = enablePoP, EnableRefreshRotation = enableRefreshRotation, RequireScopes = true, RefreshRotationRecoveryMinutes = 2,
                EnableMTls = requireMtls, RequireMTls = requireMtls
            }), new AgentNonceReplayService(db, Clock), NullLogger<AgentTokenService>.Instance, protection, Clock);
            return await service.ExchangeRefreshTokenAsync(request, CancellationToken.None);
        }

        public async Task<(AgentTokenResponse? Response, AgentAuthException? Error)> TryExchangeAsync(string token, Guid exchangeId)
        {
            try { return (await ExchangeAsync(token, exchangeId), null); }
            catch (AgentAuthException error) { return (null, error); }
        }

        public async Task<int> CountRefreshTokensAsync()
        {
            await using var db = OpenDb();
            return await db.AgentRefreshTokens.CountAsync(x => x.AgentId == AgentId);
        }

        public async Task UpdateAgentAsync(Action<Agent> update)
        {
            await using var db = OpenDb();
            var agent = await db.Agents.SingleAsync(x => x.Id == AgentId);
            update(agent);
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(keyDirectory, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExchangeClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}

public sealed class AgentRefreshExchangeCanonicalTests
{
    [Fact]
    public void Extended_body_has_fixed_versioned_hash_and_preserves_legacy_hash()
    {
        var agentId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var exchangeId = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
        string[] scopes = ["netratel:connect", "scope:two"];
        Assert.Equal("cYy72Ed4MFvLLK1Tg3JwFYHhyFAHZ9LJw8B1IgXROGg=",
            PopSignatureService.ComputeTokenBodyHash(agentId, " a:refresh/token ", scopes, 1, exchangeId));
        Assert.Equal("e6qPcEh+zUD8ATK7mRYxlZmtqL2fGnfxcCMzVJp8vFI=",
            PopSignatureService.ComputeTokenBodyHash(agentId, " a:refresh/token ", scopes));
        Assert.NotEqual(PopSignatureService.ComputeTokenBodyHash(agentId, "token", null, 1, exchangeId),
            PopSignatureService.ComputeTokenBodyHash(agentId, "token", [], 1, exchangeId));
    }

    [Fact]
    public void Signed_extended_proof_rejects_changed_exchange_id_and_scopes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var privateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        var agentId = Guid.NewGuid();
        var exchangeId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var hash = PopSignatureService.ComputeTokenBodyHash(agentId, "token", ["netratel:connect"], 1, exchangeId);
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, "nonce", hash);
        var signature = PopSignatureService.Sign("ecdsa-p256", privateKey, message);
        Assert.True(PopSignatureService.VerifySignature("ecdsa-p256", publicKey, signature, message));
        foreach (var changedHash in new[]
        {
            PopSignatureService.ComputeTokenBodyHash(agentId, "token", ["netratel:connect"], 1, Guid.NewGuid()),
            PopSignatureService.ComputeTokenBodyHash(agentId, "token", ["netratel:telemetry"], 1, exchangeId)
        })
        {
            var changedMessage = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, "nonce", changedHash);
            Assert.False(PopSignatureService.VerifySignature("ecdsa-p256", publicKey, signature, changedMessage));
        }
    }
}
