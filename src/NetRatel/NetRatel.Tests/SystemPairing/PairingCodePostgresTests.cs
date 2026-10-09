using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
using NetRatel.Tests.API;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.SystemPairing;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class PairingCodePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Codes_last_five_minutes_replace_previous_code_and_reject_wrong_or_expired_values()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var first = await service.GenerateAsync(Human, default);
        Assert.Equal(rig.Clock.GetUtcNow().AddMinutes(5), first.ExpiresAtUtc);
        Assert.Matches("^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$", first.Code);
        var second = await service.GenerateAsync(Human, default);
        var stored = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<PairingCodeRecord>().AsNoTracking().SingleAsync();
        Assert.Equal(PairingService.Hash(second.Code.Replace("-", "")), stored.CodeHash);
        Assert.DoesNotContain(second.Code, JsonSerializer.Serialize(stored));
        Assert.Equal("pairing-code-invalid", (await Assert.ThrowsAsync<PairingException>(() => service.ExchangeAsync(rig.Peer.Request(first.Code), default))).Code);
        Assert.Equal("pairing-code-invalid", (await Assert.ThrowsAsync<PairingException>(() => service.ExchangeAsync(rig.Peer.Request("ZZZZ-ZZZZ"), default))).Code);
        rig.Clock.Now = second.ExpiresAtUtc;
        Assert.Equal("pairing-code-invalid", (await Assert.ThrowsAsync<PairingException>(() => service.ExchangeAsync(rig.Peer.Request(second.Code), default))).Code);
    }

    [Fact]
    public async Task Concurrent_distinct_redemptions_across_contexts_have_one_winner_and_only_setup_authority()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        PairingCodeResponse code;
        await using (var scope = rig.Scope()) code = await scope.ServiceProvider.GetRequiredService<PairingService>().GenerateAsync(Human, default);
        async Task<(PairingExchangeRequest Request, PairingExchangeResponse Response)?> RedeemAsync()
        {
            await using var scope = rig.Scope();
            var request = rig.Peer.Request(code.Code);
            try { return (request, await scope.ServiceProvider.GetRequiredService<PairingService>().ExchangeAsync(request, default)); }
            catch (PairingException error) when (error.Code == "pairing-code-invalid") { return null; }
        }
        var attempts = await Task.WhenAll(RedeemAsync(), RedeemAsync());
        var winner = Assert.Single(attempts.Where(x => x is not null))!.Value;
        var response = winner.Response;
        await using var verify = rig.Scope(); var db = verify.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Single(await db.Set<SystemPairRecord>().ToArrayAsync()); Assert.Single(await db.Set<PairingRedemption>().ToArrayAsync());
        Assert.Empty(await db.Set<PairingConnectionRecord>().ToArrayAsync()); Assert.Empty(await db.Set<ServicePrincipalRegistration>().ToArrayAsync());
        var service = verify.ServiceProvider.GetRequiredService<PairingService>();
        Assert.Equal(response.PairId, (await service.AuthenticateSetupAsync(rig.Peer.Metadata.InstallationId, response.InboundSecret, default,
            PairingService.Hash(winner.Request.InboundSecret))).Id);
        await Assert.ThrowsAsync<PairingException>(() => service.AuthenticateSetupAsync(rig.Peer.Metadata.InstallationId, response.InboundSecret, default));
        Assert.Equal("Systems paired", Assert.Single(await service.ListAsync(Human, default)).Status);
    }

    [Fact]
    public async Task Lost_success_response_replays_exact_operation_after_restart_and_deletion_blocks_all_replay()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        PairingExchangeRequest request; PairingExchangeResponse original;
        await using (var scope = rig.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PairingService>();
            var code = await service.GenerateAsync(Human, default); request = rig.Peer.Request(code.Code);
            original = await service.ExchangeAsync(request, default);
        }
        // A fresh provider and key-ring instance represent restart, retaining the real PostgreSQL state.
        await rig.RestartAsync();
        await using var restarted = rig.Scope(); var serviceAfterRestart = restarted.ServiceProvider.GetRequiredService<PairingService>();
        Assert.Equal(original, await serviceAfterRestart.ExchangeAsync(request, default));
        var changed = rig.Peer.Sign(request with { InboundSecret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)), Signature = "" });
        Assert.Equal("pairing-retry-unavailable", (await Assert.ThrowsAsync<PairingException>(() => serviceAfterRestart.ExchangeAsync(changed, default))).Code);
        Assert.Equal("pairing-code-invalid", (await Assert.ThrowsAsync<PairingException>(() => serviceAfterRestart.ExchangeAsync(rig.Peer.Request(request.Code), default))).Code);
        var reads = rig.Peer.Reads;
        await serviceAfterRestart.DeleteAsync(original.PairId, null, Human, default);
        await serviceAfterRestart.DeleteAsync(original.PairId, null, Human, default);
        Assert.Equal(reads, rig.Peer.Reads); Assert.Empty(await serviceAfterRestart.ListAsync(Human, default));
        Assert.Equal("pairing-revoked", (await Assert.ThrowsAsync<PairingException>(() => serviceAfterRestart.AuthenticateSetupAsync(rig.Peer.Metadata.InstallationId, original.InboundSecret, default,
            PairingService.Hash(request.InboundSecret)))).Code);
        await Assert.ThrowsAsync<PairingException>(() => serviceAfterRestart.ExchangeAsync(request, default));
        await using var last = rig.Scope(); Assert.Empty(await last.ServiceProvider.GetRequiredService<PairingService>().ListAsync(Human, default));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("machine_token")]
    [InlineData("account-api")]
    public async Task Code_generation_requires_current_human_authority_even_with_matching_owner_claim(string mode)
    {
        await using var rig = await Rig.CreateAsync(postgres); await using var scope = rig.Scope();
        var claims = new List<Claim> { new("netratel_principal_id", PairingBusinessAuthorityFixture.Administrator) };
        if (mode == "account-api") claims.Add(new("netratel_integration_credential_id", "fixture-api-credential")); else claims.Add(new("auth_mode", mode));
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, mode));
        Assert.Equal("administrator-required", (await Assert.ThrowsAsync<PairingException>(() => scope.ServiceProvider.GetRequiredService<PairingService>().GenerateAsync(actor, default))).Code);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<PairingCodeRecord>().ToArrayAsync());
    }

    [Theory]
    [InlineData("disabled-owner")]
    [InlineData("removed-permission")]
    public async Task Consumed_code_retry_rechecks_the_original_current_administrator(string revocation)
    {
        await using var rig = await Rig.CreateAsync(postgres); await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var request = rig.Peer.Request((await service.GenerateAsync(Human, default)).Code);
        await service.ExchangeAsync(request, default);
        if (revocation == "disabled-owner")
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            identity.Users.Add(new LocalUser { Id = Guid.NewGuid().ToString("N"), PrincipalId = PairingBusinessAuthorityFixture.Administrator,
                UserName = "disabled-pairing-owner", NormalizedUserName = "DISABLED-PAIRING-OWNER", IsEnabled = false });
            await identity.SaveChangesAsync();
        }
        else ((PairingBusinessAuthorityFixture.Access)scope.ServiceProvider.GetRequiredService<IEffectiveAccessService>()).Allowed = false;
        await rig.RestartAsync();
        await using var restarted = rig.Scope();
        if (revocation == "removed-permission")
            ((PairingBusinessAuthorityFixture.Access)restarted.ServiceProvider.GetRequiredService<IEffectiveAccessService>()).Allowed = false;
        Assert.Equal(403, (await Assert.ThrowsAsync<PairingException>(() => restarted.ServiceProvider.GetRequiredService<PairingService>().ExchangeAsync(request, default))).StatusCode);
    }

    [Fact]
    public async Task Fresh_redemption_for_the_same_peer_invalidates_the_prior_consumed_operation()
    {
        await using var rig = await Rig.CreateAsync(postgres); await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var request = rig.Peer.Request((await service.GenerateAsync(Human, default)).Code);
        var first = await service.ExchangeAsync(request, default);
        var replacement = rig.Peer.Request((await service.GenerateAsync(Human, default)).Code);
        var current = await service.ExchangeAsync(replacement, default);
        Assert.Equal(first.PairId, current.PairId); Assert.NotEqual(first.InboundSecret, current.InboundSecret);
        await rig.RestartAsync(); await using var restarted = rig.Scope();
        Assert.Equal("pairing-retry-unavailable", (await Assert.ThrowsAsync<PairingException>(() => restarted.ServiceProvider.GetRequiredService<PairingService>().ExchangeAsync(request, default))).Code);
        Assert.Equal(current, await restarted.ServiceProvider.GetRequiredService<PairingService>().ExchangeAsync(replacement, default));
    }

    [Fact]
    public async Task Connect_with_a_changed_peer_secret_retires_old_business_authority_and_retries_its_new_operation()
    {
        await using var rig = await Rig.CreateAsync(postgres); await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Tenants.Add(new() { Id = 71, Name = "Automation tenant", CreatedAtUtc = rig.Clock.Now, UpdatedAtUtc = rig.Clock.Now }); await db.SaveChangesAsync();
        var paired = await service.ExchangeAsync(rig.Peer.Request((await service.GenerateAsync(Human, default)).Code), default);
        var mapping = new PairingMapping(Guid.NewGuid(), paired.PairId, "Old business mapping", "71", PairingBusinessAuthorityFixture.Organization, null, false, true);
        await service.SaveAsync(paired.PairId, mapping.Id, mapping, Human, default);
        var profiles = scope.ServiceProvider.GetRequiredService<PairingBusinessProfileService>();
        Assert.Equal(mapping.Id, (await profiles.ResolveAsync(71, mapping.Id.ToString("D"), "rateldesk.orchestration.callback", default)).MappingId);
        var before = await db.Set<SystemPairRecord>().AsNoTracking().SingleAsync();
        var attempt = new PairingConnectRequest("https://peer.example.test", "2345-6789", Guid.NewGuid());
        Assert.Equal("Systems paired", (await service.ConnectAsync(attempt, Human, default)).Status);
        var current = await db.Set<SystemPairRecord>().AsNoTracking().SingleAsync();
        Assert.True(current.Revision > before.Revision); Assert.Equal(before.InboundSecretHash, current.InboundSecretHash);
        var retired = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleAsync();
        Assert.False(retired.Active); Assert.NotNull(retired.DeletedAtUtc); Assert.Null(retired.ProtectedOutboundCredential);
        Assert.Equal("revoked", (await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync()).Status);
        Assert.All(await db.Set<ServicePrincipalSecret>().AsNoTracking().ToArrayAsync(), x => Assert.Equal("revoked", x.Status));
        Assert.Equal("connection-revoked", (await Assert.ThrowsAsync<PairingException>(() => profiles.ResolveAsync(71, mapping.Id.ToString("D"), "rateldesk.orchestration.callback", default))).Code);
        await rig.RestartAsync(); await using var restarted = rig.Scope();
        var calls = rig.Peer.Exchanges;
        Assert.Equal("Systems paired", (await restarted.ServiceProvider.GetRequiredService<PairingService>().ConnectAsync(attempt, Human, default)).Status);
        Assert.Equal(calls, rig.Peer.Exchanges);
    }

    [Fact]
    public async Task Fresh_automation_Save_accepts_an_empty_catalog_and_activates_exact_tenant_authority()
    {
        await using var rig = await Rig.CreateAsync(postgres); await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Tenants.Add(new() { Id = 71, Name = "Empty automation tenant", CreatedAtUtc = rig.Clock.Now, UpdatedAtUtc = rig.Clock.Now });
        await db.SaveChangesAsync();
        var code = await service.GenerateAsync(Human, default);
        var paired = await service.ExchangeAsync(rig.Peer.Request(code.Code), default);
        var mapping = new PairingMapping(Guid.NewGuid(), paired.PairId, "Empty automation catalog", "71",
            PairingBusinessAuthorityFixture.Organization, null, false, true);
        Assert.Equal("Connected", (await service.SaveAsync(paired.PairId, mapping.Id, mapping, Human, default)).Status);
        Assert.Empty(await db.Agents.ToArrayAsync()); Assert.Empty(await db.Jobs.ToArrayAsync());
        var row = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleAsync(); Assert.True(row.Active);
        var principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync();
        Assert.Equal("active", principal.Status); Assert.Equal(71, principal.TenantId);
        var constraints = ServicePrincipalRegistry.ReadConstraints(principal);
        Assert.Equal("71", constraints.TenantId); Assert.Empty(constraints.ResourceIds); Assert.Empty(constraints.RequestDefinitionIds);
        Assert.Equal(1, rig.Peer.Saves); Assert.Equal(1, rig.Peer.Tests);
        Assert.Empty(await db.Requests.ToArrayAsync()); Assert.Empty(await db.JobRuns.ToArrayAsync());
    }

    [Fact]
    public async Task Invalid_peer_credential_rejects_Save_and_keeps_the_nonsecret_draft_inactive()
    {
        await using var rig = await Rig.CreateAsync(postgres); await using var scope = rig.Scope();
        var service = scope.ServiceProvider.GetRequiredService<PairingService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Tenants.Add(new() { Id = 71, Name = "Automation tenant", CreatedAtUtc = rig.Clock.Now, UpdatedAtUtc = rig.Clock.Now }); await db.SaveChangesAsync();
        var paired = await service.ExchangeAsync(rig.Peer.Request((await service.GenerateAsync(Human, default)).Code), default);
        var mapping = new PairingMapping(Guid.NewGuid(), paired.PairId, "Retained draft", "71", PairingBusinessAuthorityFixture.Organization, null, false, true);
        rig.Peer.InvalidSourceIds = true;
        Assert.Equal("business-credential-mismatch", (await Assert.ThrowsAsync<PairingException>(() => service.SaveAsync(paired.PairId, mapping.Id, mapping, Human, default))).Code);
        var retained = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleAsync();
        Assert.False(retained.Active); Assert.Equal(mapping, JsonSerializer.Deserialize<PairingMapping>(retained.MappingJson, PairingTransport.Json));
        Assert.Null(retained.ProtectedOutboundCredential);
        Assert.Equal("pending", (await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync()).Status);
        var pair = await db.Set<SystemPairRecord>().AsNoTracking().SingleAsync();
        var invalid = new PairingBusinessCredential("peer-fixture", new string('s', 43), "https://peer.example.test/connect/token",
            "rateldesk-api", "https://peer.example.test/services", ["rateldesk.orchestration.callback"], Guid.NewGuid().ToString("D"), mapping.Id.ToString("D"));
        var other = mapping with { Id = Guid.NewGuid(), Name = "Rejected inbound mapping" };
        Assert.Equal("business-credential-mismatch", (await Assert.ThrowsAsync<PairingException>(() => service.ReceiveSaveAsync(pair, other.Id,
            new(Guid.NewGuid(), 1, other, invalid), default))).Code);
        Assert.Single(await db.Set<PairingConnectionRecord>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Requests.ToArrayAsync()); Assert.Empty(await db.JobRuns.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_Save_validation_preserves_the_diagnostic_and_inactive_draft_for_the_same_retry(bool peerHttpError)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var diagnostic = new PairingReadinessDiagnostic("receiver-capabilities", "receiver-endpoint-outside-approved-api-base",
            "67f452da36d541b892c21f50dd7d8f83");
        PairingConnectionRecord rejected;
        PairingMapping mapping;
        await using (var scope = rig.Scope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PairingService>();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Tenants.Add(new() { Id = 71, Name = "Automation tenant", CreatedAtUtc = rig.Clock.Now, UpdatedAtUtc = rig.Clock.Now });
            await db.SaveChangesAsync();
            var paired = await service.ExchangeAsync(rig.Peer.Request((await service.GenerateAsync(Human, default)).Code), default);
            mapping = new(Guid.NewGuid(), paired.PairId, "Retained validation draft", "71", PairingBusinessAuthorityFixture.Organization, null, false, true);
            rig.Peer.TestFailure = diagnostic; rig.Peer.TestHttpError = peerHttpError;
            var error = await Assert.ThrowsAsync<PairingException>(() => service.SaveAsync(paired.PairId, mapping.Id, mapping, Human, default));
            Assert.Equal(diagnostic, error.Diagnostic); Assert.Equal(PairingReadinessDiagnostics.Describe(diagnostic), error.Message);
            rejected = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleAsync();
            Assert.False(rejected.Active); Assert.Null(rejected.DeletedAtUtc);
            Assert.Equal(mapping, Assert.Single(await service.ListAsync(Human, default)).Mapping);
            Assert.Single(await db.Set<SystemPairRecord>().AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.Requests.ToArrayAsync()); Assert.Empty(await db.JobRuns.ToArrayAsync());
        }
        rig.Peer.TestFailure = null;
        await rig.RestartAsync();
        await using var retry = rig.Scope();
        var connected = await retry.ServiceProvider.GetRequiredService<PairingService>().SaveAsync(mapping.PairId, mapping.Id, mapping, Human, default);
        Assert.Equal("Connected", connected.Status); Assert.Equal(mapping, connected.Mapping);
        var accepted = await retry.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<PairingConnectionRecord>().AsNoTracking().SingleAsync();
        Assert.True(accepted.Active); Assert.Equal(rejected.OperationId, accepted.OperationId);
        Assert.Equal(rejected.Revision, accepted.Revision); Assert.Equal(rejected.InboundPrincipalId, accepted.InboundPrincipalId);
        Assert.Equal(2, rig.Peer.Saves); Assert.Equal(2, rig.Peer.Tests);
    }

    private static ClaimsPrincipal Human => PairingAuthority.Retained(PairingBusinessAuthorityFixture.Administrator);
    private sealed class Clock : TimeProvider { internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Monitor : IOptionsMonitor<ServiceIdentityOptions>
    {
        public ServiceIdentityOptions CurrentValue => new() { Enabled = true, InstanceId = "c0c8e681-b1d0-4e44-92c2-50dce9d0c2ce", Issuer = "https://api.example.test/services", ApiBaseUrl = "https://api.example.test", WebBaseUrl = "https://web.example.test" };
        public ServiceIdentityOptions Get(string? name) => CurrentValue; public IDisposable? OnChange(Action<ServiceIdentityOptions, string?> listener) => null;
    }
    private sealed class Settings(Monitor options) : IServicePublicSettingsResolver
    { public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(new ServicePublicSettingsEffective(options.CurrentValue, 1, [])); }
    private sealed class Rig(string connection) : IAsyncDisposable
    {
        private ServiceProvider services = null!;
        private readonly string keyDirectory = Path.Combine(Path.GetTempPath(), "netratel-pairing-code-" + Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new(); internal Peer Peer { get; } = new();
        internal AsyncServiceScope Scope() => services.CreateAsyncScope();
        internal static async Task<Rig> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var rig = new Rig(await postgres.CreateDatabaseAsync()); Directory.CreateDirectory(rig.keyDirectory); rig.Build();
            await using var scope = rig.Scope(); await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>(); await identity.Database.MigrateAsync();
            await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity);
            _ = await scope.ServiceProvider.GetRequiredService<PairingService>().MetadataAsync(default); // Initialize the shared actual signing key before replica races.
            return rig;
        }
        private void Build()
        {
            var registrations = new ServiceCollection(); registrations.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection));
            registrations.AddDbContext<NetRatelIdentityDbContext>(o => o.UseNpgsql(connection));
            registrations.AddSingleton<TimeProvider>(Clock); registrations.AddSingleton<IOptionsMonitor<ServiceIdentityOptions>>(new Monitor());
            registrations.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(new DirectoryInfo(keyDirectory)));
            registrations.AddSingleton<IEffectiveAccessService, PairingBusinessAuthorityFixture.Access>(); registrations.AddScoped<PairingAuthority>();
            registrations.AddSingleton<FlowPersistenceService>(); registrations.AddSingleton<IFlowSourceIdentityResolver>(p => p.GetRequiredService<FlowPersistenceService>());
            registrations.AddScoped<InstallationIdentityStore>();
            registrations.AddScoped<IServicePublicSettingsResolver>(_ => new Settings(new Monitor())); registrations.AddScoped<IServiceIdentityRuntimeOptions, ServiceIdentityRuntimeOptions>();
            registrations.AddScoped<ServiceSigningKeyStore>(); registrations.AddScoped(_ => new PairingTransport(new HttpClient(Peer, disposeHandler: false)));
            registrations.AddSingleton<IServiceClientDeploymentCatalog, EmptyServiceClientDeploymentCatalog>();
            registrations.AddScoped<IServicePrincipalRegistry, ServicePrincipalRegistry>();
            registrations.AddScoped<IRatelDeskConnectorStore, RatelDeskConnectorStore>();
            registrations.AddScoped<PairingBusinessProfileService>();
            registrations.AddScoped<PairingService>(p => new(p.GetRequiredService<OrchestratorDbContext>(), p.GetRequiredService<IDataProtectionProvider>(),
                p.GetRequiredService<InstallationIdentityStore>(), p.GetRequiredService<IServicePublicSettingsResolver>(), p.GetRequiredService<ServiceSigningKeyStore>(),
                p.GetRequiredService<PairingAuthority>(), p.GetRequiredService<IServicePrincipalRegistry>(), p.GetRequiredService<PairingTransport>(), p.GetRequiredService<IRatelDeskConnectorStore>(), null!, Clock));
            services = registrations.BuildServiceProvider();
        }
        internal async Task RestartAsync() { await services.DisposeAsync(); Build(); }
        public async ValueTask DisposeAsync() { await services.DisposeAsync(); Peer.Dispose(); Directory.Delete(keyDirectory, true); }
    }
    private sealed class Peer : HttpMessageHandler
    {
        private readonly RSA signing = RSA.Create(2048); private readonly object sync = new();
        internal int Reads; internal int Saves; internal int Tests; internal int Exchanges; internal bool InvalidSourceIds;
        internal PairingReadinessDiagnostic? TestFailure;
        internal bool TestHttpError;
        internal PairingMetadata Metadata => new(PairingProtocol.Contract, "rateldesk", "00000000-0000-4000-8000-000000000073", "Fixture peer",
            "https://peer.example.test", "https://peer.example.test", null, Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()), "00000000-0000-4000-8000-000000000073");
        internal PairingExchangeRequest Request(string code) => Sign(new(code, Guid.NewGuid(), Metadata, Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))));
        internal PairingExchangeRequest Sign(PairingExchangeRequest value) => value with { Signature = Signature(JsonSerializer.Serialize(value with { Signature = "" }, PairingTransport.Json)) };
        private string Signature(string value) { lock (sync) return Convert.ToBase64String(signing.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Reads);
            Assert.Equal("", request.RequestUri!.Query);
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == PairingProtocol.Route + "/exchange")
            {
                var exchange = (await request.Content!.ReadFromJsonAsync<PairingExchangeRequest>(PairingTransport.Json, ct))!;
                using var callerKey = RSA.Create(); callerKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(exchange.Peer.SigningPublicKey), out _);
                Assert.True(callerKey.VerifyData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(exchange with { Signature = "" }, PairingTransport.Json)),
                    Convert.FromBase64String(exchange.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                Interlocked.Increment(ref Exchanges);
                var response = new PairingExchangeResponse(PairingService.PairId(exchange.Peer.InstallationId, Metadata.InstallationId), Metadata,
                    Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)));
                response = response with { Signature = Signature(JsonSerializer.Serialize(response, PairingTransport.Json)) };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: PairingTransport.Json) };
            }
            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.StartsWith(PairingProtocol.Route + "/mappings/", StringComparison.Ordinal))
            {
                Assert.NotNull(request.Headers.Authorization); Assert.Single(request.Headers.GetValues("X-Pairing-Caller"));
                var save = (await request.Content!.ReadFromJsonAsync<PairingSaveRequest>(PairingTransport.Json, ct))!;
                Assert.Equal(PairingProtocol.Route + "/mappings/" + save.Mapping.Id.ToString("D"), request.RequestUri.AbsolutePath);
                Assert.Equal(ServiceIdentityScopes.Business.Order(StringComparer.Ordinal), save.Credential!.Scopes.Order(StringComparer.Ordinal));
                Interlocked.Increment(ref Saves);
                var credential = new PairingBusinessCredential("peer-fixture", new string('s', 43), "https://peer.example.test/connect/token",
                    "rateldesk-api", "https://peer.example.test/services", ["rateldesk.orchestration.callback"],
                    InvalidSourceIds ? Guid.NewGuid().ToString("D") : null, InvalidSourceIds ? save.Mapping.Id.ToString("D") : null);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PairingSaveResponse(save.Mapping, credential), options: PairingTransport.Json) };
            }
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/test", StringComparison.Ordinal))
            {
                Assert.NotNull(request.Headers.Authorization); Assert.Single(request.Headers.GetValues("X-Pairing-Caller"));
                Interlocked.Increment(ref Tests);
                if (TestFailure is { } diagnostic)
                {
                    if (TestHttpError) return new(HttpStatusCode.UnprocessableEntity)
                    { Content = JsonContent.Create(new { code = "saved-access-unavailable", message = "synthetic-private-peer-message", diagnostic }, options: PairingTransport.Json) };
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PairingTestResult(false,
                        "synthetic-private-peer-message", DateTimeOffset.UtcNow, diagnostic), options: PairingTransport.Json) };
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PairingTestResult(true, "Owning-layer peer access is current", DateTimeOffset.UtcNow), options: PairingTransport.Json) };
            }
            Assert.Equal(PairingProtocol.Route + "/metadata", request.RequestUri.AbsolutePath);
            var nonce = request.Headers.GetValues("X-Pairing-Nonce").Single(); var metadata = Metadata;
            var proof = new PairingMetadataProof(metadata, nonce, Signature(JsonSerializer.Serialize(metadata, PairingTransport.Json) + ":" + nonce));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(proof, options: PairingTransport.Json) };
        }
        protected override void Dispose(bool disposing) { if (disposing) signing.Dispose(); base.Dispose(disposing); }
    }
}
