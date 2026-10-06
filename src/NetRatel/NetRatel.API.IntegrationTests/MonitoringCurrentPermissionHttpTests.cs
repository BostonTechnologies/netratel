using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class MonitoringCurrentPermissionHttpTests(ApiFactory factory)
{
    [Fact]
    public async Task Actual_api_credential_and_local_account_read_only_the_current_owned_tenant()
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory,
            [NetRatelPermissions.MonitoringRead], [NetRatelPermissions.MonitoringRead], includeForeignCredentialGrants: true);
        using var credential = authority.CredentialClient();
        await AssertStatusAsync(credential, Configuration(authority.TenantId), HttpStatusCode.OK);
        await AssertStatusAsync(authority.LocalAccount, Configuration(authority.TenantId), HttpStatusCode.OK);
        await AssertStatusAsync(credential, Configuration(authority.ForeignTenantId), HttpStatusCode.Forbidden);
        await AssertStatusAsync(authority.LocalAccount, Configuration(authority.ForeignTenantId), HttpStatusCode.Forbidden);
        await AssertDiscoveryAsync(credential, [authority.TenantId]);
        await AssertDiscoveryAsync(authority.LocalAccount, [authority.TenantId]);
        using var foreignSummary = await credential.GetAsync(Prefix(authority.ForeignTenantId) + "/permissions");
        foreignSummary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("role")]
    public async Task Warm_credentials_on_both_real_hosts_lose_removed_current_permission_and_discovery(string change)
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory,
            [NetRatelPermissions.MonitoringRead], [NetRatelPermissions.MonitoringRead]);
        using var replica = authority.CreateReplica();
        using var first = authority.CredentialClient();
        using var second = authority.CredentialClient(replica);
        await AssertStatusAsync(first, Configuration(authority.TenantId), HttpStatusCode.OK);
        await AssertStatusAsync(second, Configuration(authority.TenantId), HttpStatusCode.OK);
        await AssertDiscoveryAsync(first, [authority.TenantId]);
        await AssertDiscoveryAsync(second, [authority.TenantId]);
        await authority.ChangeCurrentAuthorityAsync(change, NetRatelPermissions.MonitoringRead);
        await AssertStatusAsync(first, Configuration(authority.TenantId), HttpStatusCode.Forbidden);
        await AssertStatusAsync(second, Configuration(authority.TenantId), HttpStatusCode.Forbidden);
        await AssertDiscoveryAsync(first, []);
        await AssertDiscoveryAsync(second, []);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("disabled")]
    public async Task Warm_credentials_on_both_real_hosts_cannot_reuse_revoked_expired_or_disabled_authority(string change)
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory,
            [NetRatelPermissions.MonitoringRead], [NetRatelPermissions.MonitoringRead]);
        using var replica = authority.CreateReplica();
        using var first = authority.CredentialClient();
        using var second = authority.CredentialClient(replica);
        await AssertStatusAsync(first, Configuration(authority.TenantId), HttpStatusCode.OK);
        await AssertStatusAsync(second, Configuration(authority.TenantId), HttpStatusCode.OK);
        await authority.ChangeCurrentAuthorityAsync(change, NetRatelPermissions.MonitoringRead);
        await AssertStatusAsync(first, Configuration(authority.TenantId), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(second, Configuration(authority.TenantId), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(first, "/api/v2/monitoring/tenants", HttpStatusCode.Unauthorized);
        await AssertStatusAsync(second, "/api/v2/monitoring/tenants", HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Credential_grant_does_not_expand_the_owners_current_role_permission()
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory,
            [NetRatelPermissions.MonitoringManage], [NetRatelPermissions.MonitoringRead]);
        using var credential = authority.CredentialClient();
        await AssertStatusAsync(credential, Configuration(authority.TenantId), HttpStatusCode.Forbidden);
        await AssertStatusAsync(credential, Prefix(authority.TenantId) + "/permissions", HttpStatusCode.Forbidden);
        await AssertDiscoveryAsync(credential, []);
    }

    [Fact]
    public async Task Manage_only_account_keeps_read_or_manage_routes_without_becoming_a_reader()
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory,
            [NetRatelPermissions.MonitoringManage], [NetRatelPermissions.MonitoringManage]);
        using var credential = authority.CredentialClient();
        await AssertStatusAsync(credential, Prefix(authority.TenantId) + "/permissions", HttpStatusCode.OK);
        await AssertStatusAsync(credential, Prefix(authority.TenantId) + "/clients", HttpStatusCode.OK);
        using var published = await credential.GetAsync(Prefix(authority.TenantId) + "/published-flows");
        published.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await published.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty(
            "monitoring.manage does not grant the existing paired flow.read and flow.execute selection authority");
        await AssertStatusAsync(credential, Configuration(authority.TenantId), HttpStatusCode.Forbidden);
        await AssertDiscoveryAsync(credential, []);
    }

    [Theory]
    [InlineData("monitoring.ack", "canAcknowledge")]
    [InlineData("monitoring.clear", "canClear")]
    [InlineData("monitoring.bypass", "canBypass")]
    [InlineData("monitoring.targets.all", "canTargetAll")]
    public async Task One_operation_permission_admits_its_summary_without_unrelated_read_manage_or_operations(string permission, string summaryFlag)
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory, [permission], [permission]);
        using var credential = authority.CredentialClient();
        using var summaryResponse = await credential.GetAsync(Prefix(authority.TenantId) + "/permissions");
        summaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = JsonNode.Parse(await summaryResponse.Content.ReadAsStringAsync())!.AsObject();
        summary[summaryFlag]!.GetValue<bool>().Should().BeTrue();
        summary["canRead"]!.GetValue<bool>().Should().BeFalse();
        summary["canManage"]!.GetValue<bool>().Should().BeFalse();
        await AssertStatusAsync(credential, Configuration(authority.TenantId), HttpStatusCode.Forbidden);
        await AssertStatusAsync(credential, Prefix(authority.TenantId) + "/clients", HttpStatusCode.Forbidden);
        await AssertMutationDeniedAsync(credential, authority.TenantId, HttpMethod.Post, "/targets/preview");
        if (permission != NetRatelPermissions.MonitoringAcknowledge)
            await AssertMutationDeniedAsync(credential, authority.TenantId, HttpMethod.Post, $"/agents/{Guid.NewGuid()}/rules/{Guid.NewGuid()}/ack?resourceKey=cpu");
        if (permission != NetRatelPermissions.MonitoringClear)
            await AssertMutationDeniedAsync(credential, authority.TenantId, HttpMethod.Post, $"/agents/{Guid.NewGuid()}/rules/{Guid.NewGuid()}/clear?resourceKey=cpu");
        if (permission != NetRatelPermissions.MonitoringBypass)
            await AssertMutationDeniedAsync(credential, authority.TenantId, HttpMethod.Put, $"/bypasses/{Guid.NewGuid()}");
        await AssertDiscoveryAsync(credential, []);
    }

    [Fact]
    public async Task Anonymous_and_actual_agent_tokens_cannot_enter_account_monitoring()
    {
        await using var authority = await CurrentPermissionApiFixture.CreateAsync(factory,
            [NetRatelPermissions.MonitoringRead], [NetRatelPermissions.MonitoringRead]);
        using var anonymous = factory.CreateClient();
        await AssertStatusAsync(anonymous, Configuration(authority.TenantId), HttpStatusCode.Unauthorized);
        await AssertStatusAsync(anonymous, "/api/v2/monitoring/tenants", HttpStatusCode.Unauthorized);
        using var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.CreateAgentBearerToken(authority.TenantId, Guid.NewGuid()));
        await AssertStatusAsync(agent, Configuration(authority.TenantId), HttpStatusCode.Forbidden);
        await AssertStatusAsync(agent, "/api/v2/monitoring/tenants", HttpStatusCode.Forbidden);
    }

    private static string Prefix(int tenantId) => $"/api/v2/tenants/{tenantId}/monitoring";
    private static string Configuration(int tenantId) => Prefix(tenantId) + "/configuration";
    private static async Task AssertStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(expected, "the production authentication and current permission gate must decide {0}", path);
    }

    private static async Task AssertDiscoveryAsync(HttpClient client, int[] expected)
    {
        using var response = await client.GetAsync("/api/v2/monitoring/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        rows.Select(row => row!["tenantId"]!.GetValue<int>()).Should().BeEquivalentTo(expected);
    }

    private static async Task AssertMutationDeniedAsync(HttpClient client, int tenantId, HttpMethod method, string suffix)
    {
        using var request = new HttpRequestMessage(method, Prefix(tenantId) + suffix)
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the operation permission must be rejected before model binding or any business mutation");
    }
}
