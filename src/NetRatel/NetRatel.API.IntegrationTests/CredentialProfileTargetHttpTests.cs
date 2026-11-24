using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class CredentialProfileTargetHttpTests(ApiFactory factory)
{
    [Fact]
    public async Task Credential_manager_can_discover_and_save_exact_local_targets_before_account_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] permissions = [NetRatelPermissions.CredentialProfileManagement, NetRatelPermissions.SecretBrowse,
            NetRatelPermissions.SecretUse, NetRatelPermissions.SecretManage];
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory, permissions,
            [NetRatelPermissions.SecretBrowse], ct: ct);
        var windows = Client(authority.TenantId, "windows-test", "Windows");
        var linux = Client(authority.TenantId, "linux-test", "Linux");
        var disabled = Client(authority.TenantId, "disabled-test", "Windows");
        disabled.IsEnabled = false;
        var foreign = Client(authority.ForeignTenantId, "foreign-test", "Windows");
        await using (var scope = authority.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.AddRange(windows, linux, disabled, foreign);
            await db.SaveChangesAsync(ct);
        }

        var client = authority.LocalAccount;
        client.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");
        var vault = $"/api/v1/tenants/{authority.TenantId}/secret-vault";
        var targets = vault + "/profile-targets";
        var page = await client.GetFromJsonAsync<JobNodePageDto>(targets + "?search=windows", ct);
        Assert.Equal(windows.Id, Assert.Single(page!.Items).AgentId);
        Assert.Equal(new[] { windows.Id }, await client.GetFromJsonAsync<Guid[]>(targets + "/all?windowsOnly=true", ct));
        var all = await client.GetFromJsonAsync<Guid[]>(targets + "/all", ct);
        Assert.Equal(new[] { windows.Id, linux.Id }.Order(), all!.Order());
        using var denied = await client.GetAsync($"/api/v1/tenants/{authority.ForeignTenantId}/secret-vault/profile-targets/all", ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var foreignIdentity = await client.PostAsJsonAsync(targets + "/identities", new[] { foreign.Id }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreignIdentity.StatusCode);
        using var identities = await client.PostAsJsonAsync(targets + "/identities", new[] { windows.Id }, ct);
        identities.EnsureSuccessStatusCode();
        Assert.Equal("windows-test", Assert.Single((await identities.Content.ReadFromJsonAsync<JobNodeInventoryDto[]>(ct))!).Hostname);

        var local = Assert.Single((await client.GetFromJsonAsync<SecretProviderConnection[]>(vault + "/connections", ct))!);
        using var write = await client.PutAsJsonAsync(vault + $"/connections/{local.Id:D}/entry",
            new WriteSecretEntryRequest { Name = "Test password", Value = "synthetic test password" }, ct);
        write.EnsureSuccessStatusCode();
        var entry = (await write.Content.ReadFromJsonAsync<SecretEntryMetadata>(ct))!;
        var request = new SaveCredentialProfileRequest("Test Windows credential", ExecutionIdentityMode.WindowsPassword,
            "test-operator", null, null, new SecretReference(1, local.Id, entry.Locator, null, SecretVersionMode.LatestAtExecution, null),
            new([windows.Id], null, true, false), null);
        using var otherTenant = await client.PostAsJsonAsync(vault + "/profiles",
            request with { Scope = request.Scope with { AgentIds = [foreign.Id] } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, otherTenant.StatusCode);
        using var save = await client.PostAsJsonAsync(vault + "/profiles", request, ct);
        save.EnsureSuccessStatusCode();
        var saved = (await save.Content.ReadFromJsonAsync<CredentialProfile>(ct))!;
        Assert.Null(saved.Scope.AccountId);
        Assert.Equal(windows.Id, Assert.Single(saved.Scope.AgentIds));
    }

    private static Agent Client(int tenantId, string hostname, string os) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, Name = hostname,
        DeviceInfoJson = System.Text.Json.JsonSerializer.Serialize(new { hostName = hostname, os })
    };
}
