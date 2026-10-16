using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Components.Shared.Integrations;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class IntegrationPermissionSelectorTests : AsyncBunitContext
{
    [Fact]
    public void Explicit_scope_group_and_filtered_selection_preserve_other_scopes_and_use_only_catalog_ids()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var grants = new HashSet<IntegrationPairingGrant>();
        var instance = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<IntegrationPermissionScope> scopes = [
            new(7, "Tenant A", [new("telemetry.read", "Read telemetry", "Read tenant telemetry."), new("telemetry.write", "Write telemetry", "Update telemetry."), new("jobs.run", "Run jobs", "Execute jobs.")]),
            new(8, "Tenant B", [new("telemetry.read", "Read telemetry", "Read tenant telemetry.")]),
            new(null, "Server-level access", [new("mcp.discovery.read", "Read connection status", "Read server connection status.")])];
        var selector = Render<IntegrationPermissionSelector>(parameters => parameters
            .Add(component => component.Scopes, scopes)
            .Add(component => component.Grants, grants)
            .Add(component => component.InstancePermissions, instance)
            .Add(component => component.Changed, change => {
                foreach (var permission in change.Permissions)
                    if (change.TenantId is int tenant) { if (change.Selected) grants.Add(new(tenant, permission)); else grants.Remove(new(tenant, permission)); }
                    else { if (change.Selected) instance.Add(permission); else instance.Remove(permission); }
            }));
        grants.Should().BeEmpty(); instance.Should().BeEmpty();
        selector.Find("[data-testid='credential-permission-7-telemetry.read']").Change(true);
        selector.FindAll("[data-testid='permission-group-toggle']").Last().GetAttribute("aria-checked").Should().Be("mixed");
        selector.FindAll("[data-testid='permission-group-toggle']").Last().Change(true);
        grants.Should().HaveCount(2);
        selector.Find("[aria-label='Clear Monitoring and services group']").Click();
        grants.Should().BeEmpty();
        selector.Find("[data-testid='credential-permission-7-telemetry.read']").Change(true);
        selector.Find("[data-testid='permission-search']").Input("Run jobs");
        selector.Find("[data-testid='permission-select-search']").TextContent.Should().Contain("(1)");
        selector.Find("[data-testid='permission-select-search']").Click();
        grants.Should().BeEquivalentTo([new IntegrationPairingGrant(7, "telemetry.read"), new IntegrationPairingGrant(7, "jobs.run")]);
        selector.Find("[data-testid='permission-scope']").Change("8");
        grants.Should().HaveCount(2);
        selector.Find("[data-testid='permission-search']").Input("");
        selector.Find("[data-testid='permission-select-scope']").Click();
        grants.Should().Contain(new IntegrationPairingGrant(8, "telemetry.read"));
        selector.Find("[data-testid='permission-scope']").Change("server");
        instance.Should().BeEmpty();
        selector.Find("[data-testid='permission-select-scope']").Click();
        instance.Should().Equal("mcp.discovery.read");
        selector.Find("[data-testid='permission-clear-scope']").Click();
        instance.Should().BeEmpty(); grants.Should().HaveCount(3);
        selector.Find("[data-testid='permission-scope']").Change("7");
        selector.Find("[data-testid='permission-search']").Input("no results");
        selector.Find("[data-testid='permission-select-search']").HasAttribute("disabled").Should().BeTrue();
        grants.Should().HaveCount(3);
        selector.Find("[data-testid='permission-select-scope']").Click();
        grants.Should().HaveCount(4).And.NotContain(grant => grant.Permission == "*");
        selector.Find("[data-testid='permission-clear-scope']").Click();
        grants.Should().Equal(new IntegrationPairingGrant(8, "telemetry.read"));
    }
}
