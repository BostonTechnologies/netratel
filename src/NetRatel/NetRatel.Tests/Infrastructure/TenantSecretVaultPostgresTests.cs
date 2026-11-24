using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Jobs;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Secrets;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed partial class TenantSecretVaultPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    internal static IReadOnlyList<string> MigrationIds { get; } =
    [
        "20261123150934_AddTenantSecretProviders",
        "20261123153551_ProtectJobSecretDefaults",
        "20261123155559_PinJobProviderInputs",
        "20261123160739_StoreJobProviderDefaults"
    ];

    [Fact]
    public async Task Local_rotation_preserves_pins_and_legacy_scope_and_blocks_referenced_deletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await CreateServicesAsync(ct);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ITenantSecretService>();
        var legacy = scope.ServiceProvider.GetRequiredService<ISecretService>();
        var tenant = new Tenant { Name = "Canary" };
        var other = new Tenant { Name = "Other Canary" };
        db.Tenants.AddRange(tenant, other);
        await db.SaveChangesAsync(ct);
        var agent = new Agent { Id = Guid.NewGuid(), TenantId = tenant.Id };
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);

        const string original = "synthetic first value\n◆";
        const string rotated = "synthetic rotated value\n◇";
        var local = Assert.Single(await vault.ListConnectionsAsync(tenant.Id, false, ct));
        var otherLocal = Assert.Single(await vault.ListConnectionsAsync(other.Id, false, ct));
        Assert.True(local.IsDefault);
        Assert.NotEqual(local.Id, otherLocal.Id);
        var instance = await legacy.CreateAsync(new(null, null, "synthetic instance value", null), ct);
        var created = await vault.WriteAsync(tenant.Id, local.Id, new() { Name = "Canary login", Value = original }, ct);
        Assert.Equal("1", created.Version);

        var page = await vault.BrowseAsync(tenant.Id, local.Id, new(Limit: 1), ct);
        Assert.Equal(created.Locator, Assert.Single(page.Entries).Locator);
        Assert.Empty((await vault.BrowseAsync(other.Id, otherLocal.Id, new(), ct)).Entries);
        Assert.Equal(SecretProviderFailure.NotFound, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.BrowseAsync(other.Id, local.Id, new(), ct))).Reason);
        Assert.Equal(SecretProviderFailure.NotFound, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.GetMetadataAsync(tenant.Id, local.Id, instance.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), null, ct))).Reason);

        var latest = new SecretReference(1, local.Id, created.Locator, null, SecretVersionMode.LatestAtExecution, null);
        var first = Assert.Single(await vault.ResolveForExecutionAsync(tenant.Id, [latest], ct));
        var profile = await vault.SaveProfileAsync(tenant.Id, null, new("Canary Windows service user", ExecutionIdentityMode.WindowsPassword,
            "canary-operator", null, null, latest, new([agent.Id], "S-1-5-21-1001", true, true), null), ct);
        var updated = await vault.WriteAsync(tenant.Id, local.Id, new()
        {
            Locator = created.Locator, Value = rotated, ExpectedRevision = created.Revision, ExpectedVersion = created.Version
        }, ct);
        Assert.Equal("2", updated.Version);
        Assert.Equal(original, (await vault.RevealAsync(tenant.Id, first.ExactReference, ct)).Value);
        Assert.Equal(rotated, (await vault.RevealAsync(tenant.Id, latest, ct)).Value);
        Assert.Equal(profile.MappingRevision, (await vault.GetProfileAsync(tenant.Id, profile.Id, ct)).MappingRevision);
        var history = await db.SecretVersions.AsNoTracking().SingleAsync(ct);
        Assert.DoesNotContain(original, history.ProtectedValue);
        Assert.DoesNotContain(rotated, (await db.Secrets.AsNoTracking().SingleAsync(x => x.TenantId == tenant.Id, ct)).Value);

        Assert.Equal(SecretProviderFailure.Conflict, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.WriteAsync(tenant.Id, local.Id, new() { Locator = created.Locator, ExpectedRevision = "1", ExpectedVersion = "1", Value = "stale" }, ct))).Reason);
        var request = new DeleteSecretEntryRequest(updated.Locator, updated.Revision!, updated.Version!);
        Assert.Equal(SecretProviderFailure.InUse, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.DeleteAsync(tenant.Id, local.Id, local.Revision, request, ct))).Reason);
        await vault.DeleteProfileAsync(tenant.Id, profile.Id, profile.Revision, ct);
        Assert.False((await vault.DeleteAsync(tenant.Id, local.Id, local.Revision, request, ct)).Pending);
        Assert.Empty(await db.SecretVersions.AsNoTracking().ToArrayAsync(ct));
    }

    [Fact]
    public async Task Connection_rotation_changes_authentication_revision_and_keeps_reference_scope_and_tenant_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await CreateServicesAsync(ct);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ITenantSecretService>();
        var tenant = new Tenant { Name = "Vault tenant" };
        var other = new Tenant { Name = "Independent tenant" };
        db.Tenants.AddRange(tenant, other);
        await db.SaveChangesAsync(ct);

        var local = Assert.Single(await vault.ListConnectionsAsync(tenant.Id, true, ct));
        var localEntry = await vault.WriteAsync(tenant.Id, local.Id, new() { Name = "Local", Value = "synthetic persisted value" }, ct);
        var config = new SecretProviderConfiguration("https://openbao.example.test/", Mount: "kv", BasePath: "team", AuthenticationMount: "approle");
        var saved = await vault.SaveConnectionAsync(tenant.Id, null, new()
        {
            Kind = SecretProviderKind.OpenBao, DisplayName = "OpenBao", Configuration = config, RoleId = "fixture-role", SecretId = "fixture-secret"
        }, ct);
        Assert.True(saved.HasStoredAuthentication);
        var selected = await vault.SetDefaultAsync(tenant.Id, saved.Id, new(local.Id, saved.Revision), ct);
        Assert.True(selected.IsDefault);
        var localReference = new SecretReference(1, local.Id, localEntry.Locator, null, SecretVersionMode.LatestAtExecution, null);
        Assert.Equal("synthetic persisted value", (await vault.RevealAsync(tenant.Id, localReference, ct)).Value);
        Assert.True(Assert.Single(await vault.ListConnectionsAsync(other.Id, true, ct)).IsDefault);
        Assert.Equal(SecretProviderFailure.NotFound, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.TestConnectionAsync(other.Id, saved.Id, selected.Revision, ct))).Reason);
        var before = await db.SecretProviderConnections.AsNoTracking().SingleAsync(ct);
        var rotated = await vault.SaveConnectionAsync(tenant.Id, saved.Id, new()
        {
            Kind = SecretProviderKind.OpenBao, DisplayName = "OpenBao", Configuration = config,
            ExpectedRevision = selected.Revision, SecretId = "fixture-rotation"
        }, ct);
        var after = await db.SecretProviderConnections.AsNoTracking().SingleAsync(ct);
        Assert.Equal(saved.ScopeRevision, rotated.ScopeRevision);
        Assert.Equal(before.AuthenticationRevision + 1, after.AuthenticationRevision);
        Assert.NotEqual(before.ProtectedAuthentication, after.ProtectedAuthentication);
        Assert.DoesNotContain("fixture-rotation", after.ProtectedAuthentication!);
        var projected = Assert.Single((await vault.ListConnectionsAsync(tenant.Id, false, ct)).Where(x => x.Id == saved.Id));
        Assert.Null(projected.Configuration);
        Assert.False(projected.HasStoredAuthentication);
        Assert.Equal(SecretProviderFailure.InvalidRequest, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.SaveConnectionAsync(tenant.Id, saved.Id, new()
            {
                Kind = saved.Kind, DisplayName = saved.DisplayName, ExpectedRevision = rotated.Revision, Configuration = config with { BasePath = "other-team" }
            }, ct))).Reason);
    }

    [Fact]
    public async Task Infisical_v4_protocol_checks_scoped_crud_and_versions_without_projecting_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new InfisicalMetadataFixture();
        await using var services = await CreateServicesAsync(ct, configure: collection => collection.AddHttpClient("SecretProviderHttp")
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ITenantSecretService>();
        var tenant = new Tenant { Name = "Infisical protocol fixture" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        var connection = await vault.SaveConnectionAsync(tenant.Id, null, new()
        {
            Kind = SecretProviderKind.Infisical, DisplayName = "Infisical fixture", ClientSecret = "synthetic-universal-auth",
            Configuration = new("https://infisical.example.test/", ClientId: "synthetic-client", ProjectId: "fixture-project",
                Environment: "stage", BasePath: "tools")
        }, ct);
        var page = await vault.BrowseAsync(tenant.Id, connection.Id, new("jobs"), ct);
        Assert.Equal("jobs/KEY", Assert.Single(page.Entries).Locator);
        Assert.DoesNotContain(InfisicalMetadataFixture.CanaryValue, JsonSerializer.Serialize(page));
        const string original = "synthetic infisical first\n◆";
        const string changed = "synthetic infisical replacement\n◇";
        var created = await vault.WriteAsync(tenant.Id, connection.Id, new() { Locator = "jobs/NEW", Value = original }, ct);
        var latest = new SecretReference(1, connection.Id, created.Locator, null, SecretVersionMode.LatestAtExecution, null);
        var pinned = latest with { VersionMode = SecretVersionMode.Pinned, Version = created.Version };
        Assert.DoesNotContain("synthetic infisical first", JsonSerializer.Serialize(created));
        Assert.Equal(original, (await vault.RevealAsync(tenant.Id, pinned, ct)).Value);
        var updated = await vault.WriteAsync(tenant.Id, connection.Id, new()
        { Locator = created.Locator, ExpectedRevision = created.Revision, ExpectedVersion = created.Version, Value = changed }, ct);
        Assert.Equal("2", updated.Version);
        Assert.Equal(original, (await vault.RevealAsync(tenant.Id, pinned, ct)).Value);
        Assert.Equal(changed, (await vault.RevealAsync(tenant.Id, latest, ct)).Value);
        Assert.Equal(SecretProviderFailure.Conflict, (await Assert.ThrowsAsync<SecretProviderException>(() => vault.WriteAsync(tenant.Id,
            connection.Id, new() { Locator = created.Locator, ExpectedRevision = created.Revision, ExpectedVersion = created.Version, Value = original }, ct))).Reason);
        var removed = await vault.DeleteAsync(tenant.Id, connection.Id, connection.Revision,
            new(created.Locator, updated.Revision!, updated.Version!), ct);
        Assert.Equal(SecretDeletionMode.RemoveEntry, removed.Mode);
        Assert.Equal(SecretProviderFailure.VersionUnavailable, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.RevealAsync(tenant.Id, pinned, ct))).Reason);
        Assert.Equal(1, handler.LoginCount);
        Assert.Equal(1, handler.ListCount);
    }

    [Fact]
    public async Task Legacy_job_default_is_protected_and_captured_per_run_without_entering_read_projections()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await CreateServicesAsync(ct);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobDefinitionService>();
        var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
        var tenant = new Tenant { Name = "Synthetic job default" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        var agent = new Agent { Id = Guid.NewGuid(), TenantId = tenant.Id };
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        var job = await jobs.CreateAsync(new("Synthetic job", "/", null, tenant.Id, "", AgentId: agent.Id), ct);
        const string legacy = "old synthetic default\n◇";
        var stored = new JobParameterDefinition { JobId = checked((long)job.Id), Name = "Credential", Type = "Secret", Required = true, DefaultValue = legacy };
        db.JobParameters.Add(stored);
        await db.SaveChangesAsync(ct);
        await scope.ServiceProvider.GetRequiredService<JobSecretDefaultProtection>().ProtectLegacyDefaultsAsync(ct);
        var view = Assert.Single(await jobs.ListParamsAsync(job.Id, ct));
        Assert.True(view.HasProtectedDefault);
        Assert.Null(view.DefaultValue);
        Assert.Null(stored.DefaultValue);
        Assert.DoesNotContain(legacy, stored.ProtectedDefaultValue!);

        var snapshot = await scope.ServiceProvider.GetRequiredService<IJobExecutionSnapshotCapture>().CaptureAsync(job.Id, tenant.Id, ct);
        var updated = await jobs.UpdateParamAsync(new(view.Id, null, null, null, "new synthetic default", null, null), ct);
        Assert.True(updated?.HasProtectedDefault);
        runs.BindPinnedExecutionSnapshot(9001, snapshot);
        var now = DateTimeOffset.UtcNow;
        var run = await runs.UpsertRunAsync(new(9001, job.Id, tenant.Id, "", "synthetic actor", JobRunState.Pending, 0,
            now, null, null, null, "{}", null, agent.Id), ct);
        Assert.DoesNotContain(legacy, run.InputsJson!);
        using var execution = JsonDocument.Parse((await runs.GetExecutionInputsAsync(run.Id, ct))!);
        Assert.Equal(legacy, execution.RootElement.GetProperty("Credential").GetString());

        var clear = await jobs.UpdateParamAsync(new(view.Id, null, null, null, null, null, null) { ClearProtectedDefault = true }, ct);
        Assert.False(clear?.HasProtectedDefault);
        Assert.Null(stored.ProtectedDefaultValue);
    }

    [Fact]
    [Trait("Category", "manual-integration")]
    public async Task OpenBao_AppRole_preserves_immutable_structured_versions_and_soft_deletes_only_the_reviewed_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var container = new ContainerBuilder("openbao/openbao:2.7.3")
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", "netratel-test-root")
            .WithEnvironment("BAO_TOKEN", "netratel-test-root")
            .WithEnvironment("BAO_ADDR", "http://127.0.0.1:8200")
            .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
            .WithCreateParameterModifier(command => command.HostConfig.CapAdd = ["IPC_LOCK"])
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8200).ForPath("/v1/sys/health")))
            .Build();
        await container.StartAsync(ct);
        await CheckedExecAsync(container, ct, "bao", "auth", "enable", "approle");
        await CheckedExecAsync(container, ct, "sh", "-ec", "bao policy write fixture - <<'POLICY'\n" + """
            path "secret/metadata/canary" { capabilities = ["list"] }
            path "secret/metadata/canary/*" { capabilities = ["read", "list"] }
            path "secret/data/canary/*" { capabilities = ["create", "read", "update"] }
            path "secret/subkeys/canary/*" { capabilities = ["read"] }
            path "secret/delete/canary/*" { capabilities = ["update"] }
            """ + "\nPOLICY");
        await CheckedExecAsync(container, ct, "bao", "write", "auth/approle/role/fixture", "token_policies=fixture", "token_ttl=5m");
        var role = await CheckedExecAsync(container, ct, "bao", "read", "-field=role_id", "auth/approle/role/fixture/role-id");
        var seed = await CheckedExecAsync(container, ct, "bao", "write", "-f", "-field=secret_id", "auth/approle/role/fixture/secret-id");
        var origin = $"http://{container.Hostname}:{container.GetMappedPublicPort(8200)}/";
        await using var services = await CreateServicesAsync(ct, origin);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ITenantSecretService>();
        var tenant = new Tenant { Name = "OpenBao fixture" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        var connection = await vault.SaveConnectionAsync(tenant.Id, null, new()
        {
            Kind = SecretProviderKind.OpenBao, DisplayName = "OpenBao fixture",
            Configuration = new(origin, Mount: "secret", BasePath: "canary", AuthenticationMount: "approle"), RoleId = role.Trim(), SecretId = seed.Trim()
        }, ct);

        var entry = await vault.WriteAsync(tenant.Id, connection.Id, new()
        {
            Locator = "login", Fields = new() { ["username"] = JsonSerializer.SerializeToElement("fixture-user"), ["password"] = JsonSerializer.SerializeToElement("synthetic first") }
        }, ct);
        Assert.True((await vault.TestConnectionAsync(tenant.Id, connection.Id, connection.Revision, ct)).ListedMetadata);
        Assert.Equal("login", Assert.Single((await vault.BrowseAsync(tenant.Id, connection.Id, new(), ct)).Entries).Locator);
        var detail = await vault.GetMetadataAsync(tenant.Id, connection.Id, "login", null, ct);
        Assert.Equal(new[] { "password", "username" }, detail.Fields.Select(x => x.Name).Order().ToArray());
        var password = new SecretReference(1, connection.Id, "login", "password", SecretVersionMode.LatestAtExecution, null);
        var user = password with { Field = "username" };
        var initial = await vault.ResolveForExecutionAsync(tenant.Id, [user, password], ct);
        Assert.Equal(initial[0].ExactReference.Version, initial[1].ExactReference.Version);
        var update = new WriteSecretEntryRequest
        {
            Locator = "login", ExpectedVersion = entry.Version, ExpectedRevision = entry.Revision,
            Fields = new() { ["password"] = JsonSerializer.SerializeToElement("synthetic rotation") }
        };
        var rotated = await vault.WriteAsync(tenant.Id, connection.Id, update, ct);
        Assert.Equal("fixture-user", (await vault.RevealAsync(tenant.Id, user, ct)).Value);
        Assert.Equal("synthetic rotation", (await vault.RevealAsync(tenant.Id, password, ct)).Value);
        Assert.Equal(SecretProviderFailure.Conflict, (await Assert.ThrowsAsync<SecretProviderException>(() => vault.WriteAsync(tenant.Id, connection.Id, update, ct))).Reason);
        var deleted = await vault.DeleteAsync(tenant.Id, connection.Id, connection.Revision, new("login", rotated.Revision!, rotated.Version!), ct);
        Assert.Equal(SecretDeletionMode.SoftDeleteCurrentVersion, deleted.Mode);
        Assert.Equal("synthetic first", (await vault.RevealAsync(tenant.Id, initial[1].ExactReference, ct)).Value);
        Assert.Equal(SecretProviderFailure.VersionUnavailable, (await Assert.ThrowsAsync<SecretProviderException>(() =>
            vault.RevealAsync(tenant.Id, password, ct))).Reason);
    }

    private static async Task<string> CheckedExecAsync(IContainer container, CancellationToken ct, params string[] arguments)
    {
        var result = await container.ExecAsync(arguments, ct);
        Assert.Equal(0L, result.ExitCode);
        return result.Stdout;
    }

    private async Task<ServiceProvider> CreateServicesAsync(CancellationToken ct, string? testOrigin = null, Action<IServiceCollection>? configure = null)
    {
        var connection = await postgres.CreateOrchestratorDatabaseAsync(ct);
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        host.Configuration["ConnectionStrings:NetRatelDb"] = connection;
        if (testOrigin is not null) host.Configuration["SecretProviders:DevelopmentHttpOrigins:0"] = testOrigin;
        host.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        host.Services.AddNetRatelInfrastructure(host.Configuration);
        configure?.Invoke(host.Services);
        return host.Services.BuildServiceProvider();
    }

    private sealed class InfisicalMetadataFixture : HttpMessageHandler
    {
        public const string CanaryValue = "synthetic provider response value";
        public int LoginCount { get; private set; }
        public int ListCount { get; private set; }
        private readonly Dictionary<int, string> _versions = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Assert.Equal("infisical.example.test", uri.Host);
            switch (uri.AbsolutePath)
            {
                case "/api/v1/auth/universal-auth/login":
                    LoginCount++;
                    Assert.Equal(HttpMethod.Post, request.Method);
                    return Respond(new { accessToken = "fixture-token", expiresIn = 120 });
                case "/api/v2/folders":
                    Assert.Contains("path=/tools/jobs", Uri.UnescapeDataString(uri.Query));
                    Assert.Contains("recursive=false", uri.Query);
                    return Respond(new { folders = Array.Empty<object>() });
                case "/api/v4/secrets":
                    ListCount++;
                    Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                    var query = Uri.UnescapeDataString(uri.Query);
                    foreach (var expected in new[] { "projectId=fixture-project", "environment=stage", "secretPath=/tools/jobs", "viewSecretValue=false",
                        "includeImports=false", "expandSecretReferences=false", "includePersonalOverrides=false", "recursive=false" })
                        Assert.Contains(expected, query);
                    return Respond(new { secrets = new[] { new { _id = "fixture", workspace = "fixture-project", environment = "stage", version = 1,
                        type = "shared", secretKey = "KEY", secretPath = "/tools/jobs", secretValue = CanaryValue } } });
                case "/api/v4/secrets/NEW":
                    Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                    if (request.Method == HttpMethod.Get)
                    {
                        var actual = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                        Assert.Equal("fixture-project", actual["projectId"].ToString());
                        Assert.Equal("stage", actual["environment"].ToString());
                        Assert.Equal("/tools/jobs", actual["secretPath"].ToString());
                        Assert.Equal("shared", actual["type"].ToString());
                        Assert.Equal("false", actual["expandSecretReferences"].ToString());
                        Assert.Equal("false", actual["includeImports"].ToString());
                        var version = int.TryParse(actual.GetValueOrDefault("version").ToString(), out var exact) ? exact : _versions.Keys.DefaultIfEmpty().Max();
                        return _versions.TryGetValue(version, out var value) ? Entry(version, value) : new HttpResponseMessage(HttpStatusCode.NotFound);
                    }
                    var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                    Assert.Equal("fixture-project", body.GetProperty("projectId").GetString());
                    Assert.Equal("stage", body.GetProperty("environment").GetString());
                    Assert.Equal("/tools/jobs", body.GetProperty("secretPath").GetString());
                    Assert.Equal("shared", body.GetProperty("type").GetString());
                    if (request.Method == HttpMethod.Delete)
                    {
                        _versions.Clear();
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }
                    Assert.True(request.Method == HttpMethod.Post && _versions.Count == 0 || request.Method == HttpMethod.Patch && _versions.Count > 0);
                    Assert.True(body.GetProperty("skipMultilineEncoding").GetBoolean());
                    var next = _versions.Keys.DefaultIfEmpty().Max() + 1;
                    var changed = body.GetProperty("secretValue").GetString()!;
                    _versions.Add(next, changed);
                    return Entry(next, changed);
                default:
                    throw new InvalidOperationException("Unexpected Infisical fixture route.");
            }
        }

        private static HttpResponseMessage Entry(int version, string value) => Respond(new { secret = new
        {
            _id = "fixture-new", workspace = "fixture-project", environment = "stage", version, type = "shared", secretKey = "NEW",
            secretPath = "/tools/jobs", secretValue = value
        } });

        private static HttpResponseMessage Respond(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
