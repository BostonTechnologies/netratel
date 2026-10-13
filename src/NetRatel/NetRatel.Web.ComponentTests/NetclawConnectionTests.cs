using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Components.Pages.AIOps;
using NetRatel.Web.Components.Shared.Netclaw;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NetclawConnectionTests : AsyncBunitContext
{
    private readonly ConnectionApi api = new();
    public NetclawConnectionTests() => Services.AddSingleton<INetclawApiService>(api);
    private static NetclawConnectionDto Saved(int tenant = 17, bool enabled = true, bool token = true, bool? verified = true) =>
        new(Guid.NewGuid(), tenant, 4, enabled, "https://daemon.example", token, DateTimeOffset.UtcNow.AddMinutes(-2), verified, "saved-check");
    private static NetclawConnectionDiagnosticsDto Observed(NetclawConnectionDto saved, NetclawSignalRState state = NetclawSignalRState.Connected) =>
        new(saved.TenantId, saved.Id, saved.Revision, true, DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), state, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)]);

    [Fact]
    public async Task Only_an_observed_current_session_is_green_and_keyboard_focus_exposes_safe_details()
    {
        var saved = Saved() with { DaemonAddress = "https://user:synthetic-secret@daemon.example?token=synthetic-token" };
        api.Observe = (_, _) => Task.FromResult<NetclawConnectionDiagnosticsDto?>(Observed(saved));
        var cut = Render<NetclawConnectionStatus>(p => p.Add(x => x.TenantId, 17).Add(x => x.TenantName, "Operations").Add(x => x.Connection, saved));
        await cut.WaitForAssertionAsync(() => Assert.Contains("● Netclaw connected", cut.Markup));
        var badge = cut.Find("[data-testid=netclaw-connection-status]");
        Assert.Contains("connected", badge.ClassList);
        Assert.Equal("/account/integration-credentials?tab=netclaw&tenantId=17", badge.GetAttribute("href"));
        Assert.True(cut.Find("[role=tooltip]").HasAttribute("hidden"));
        await badge.FocusAsync(new());
        Assert.False(cut.Find("[role=tooltip]").HasAttribute("hidden"));
        Assert.Equal(cut.Find("[role=tooltip]").Id, badge.GetAttribute("aria-describedby"));
        Assert.Contains("SignalR: Connected", cut.Markup);
        Assert.Contains("Last activity:", cut.Markup);
        Assert.DoesNotContain("synthetic-secret", cut.Markup); Assert.DoesNotContain("synthetic-token", cut.Markup);
        Assert.Empty(api.PairRequests); Assert.Empty(api.TestRequests);
    }

    [Fact]
    public async Task Historical_verification_does_not_claim_a_live_connection()
    {
        var cut = Render<NetclawConnectionStatus>(p => p.Add(x => x.TenantId, 17).Add(x => x.Connection, Saved()));
        await cut.WaitForAssertionAsync(() => Assert.Contains("✓ Netclaw verified", cut.Markup));
        Assert.DoesNotContain("● Netclaw connected", cut.Markup);
        await cut.Find("[data-testid=netclaw-connection-status]").FocusAsync(new());
        Assert.Contains("Last successful saved verification", cut.Markup);
        Assert.Contains("No active session observation is available", cut.Markup);
        Assert.Empty(api.PairRequests); Assert.Empty(api.TestRequests);
    }

    [Theory]
    [InlineData(false, false, null, "Connect Netclaw")]
    [InlineData(false, true, true, "Netclaw disabled")]
    [InlineData(true, true, null, "Netclaw unverified")]
    [InlineData(true, true, false, "Netclaw check failed")]
    public void Configuration_states_remain_distinct(bool enabled, bool token, bool? verified, string text)
    {
        var cut = Render<NetclawConnectionStatus>(p => p.Add(x => x.TenantId, 17).Add(x => x.Connection, Saved(enabled: enabled, token: token, verified: verified)));
        Assert.Contains(text, cut.Markup);
        Assert.DoesNotContain("● Netclaw connected", cut.Markup);
    }

    [Theory]
    [InlineData(NetclawSignalRState.Reconnecting, "Netclaw reconnecting")]
    [InlineData(NetclawSignalRState.Disconnected, "Netclaw disconnected")]
    public async Task Observable_transport_states_are_not_replaced_by_a_saved_success(NetclawSignalRState state, string text)
    {
        var saved = Saved();
        api.Observe = (_, _) => Task.FromResult<NetclawConnectionDiagnosticsDto?>(Observed(saved, state));
        var cut = Render<NetclawConnectionStatus>(p => p.Add(x => x.TenantId, 17).Add(x => x.Connection, saved));
        await cut.WaitForAssertionAsync(() => Assert.Contains(text, cut.Markup));
        Assert.DoesNotContain("● Netclaw connected", cut.Markup);
    }

    [Fact]
    public async Task A_tenant_switch_clears_details_and_fences_an_old_diagnostics_response()
    {
        var old = Saved(); var next = Saved(18) with { DaemonAddress = "https://new.example" };
        var pending = new TaskCompletionSource<NetclawConnectionDiagnosticsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Observe = (tenant, _) => tenant == 17 ? pending.Task : Task.FromResult<NetclawConnectionDiagnosticsDto?>(null);
        var cut = Render<NetclawConnectionStatus>(p => p.Add(x => x.TenantId, 17).Add(x => x.TenantName, "Old tenant").Add(x => x.Connection, old));
        cut.Render(p => p.Add(x => x.TenantId, 18).Add(x => x.TenantName, "New tenant").Add(x => x.Connection, next));
        pending.SetResult(Observed(old));
        await cut.Find("[data-testid=netclaw-connection-status]").FocusAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("New tenant", cut.Markup));
        Assert.DoesNotContain("Old tenant", cut.Markup); Assert.DoesNotContain("daemon.example", cut.Markup);
        Assert.DoesNotContain("● Netclaw connected", cut.Markup);
        Assert.Contains("tenantId=18", cut.Find("[data-testid=netclaw-connection-status]").GetAttribute("href"));
    }

    [Fact]
    public async Task A_direct_link_to_an_investigation_only_tenant_does_not_load_or_manage_configuration()
    {
        api.CanConfigure = false;
        var cut = Render<NetclawConnectionPanel>(p => p.Add(x => x.InitialTenantId, 17));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Tenant administration permission is required", cut.Markup));
        Assert.Empty(api.ConnectionReads); Assert.Empty(api.PairRequests); Assert.Empty(api.TestRequests);
        Assert.Empty(cut.FindAll("[data-testid=netclaw-pair-connect]"));
    }

    [Fact]
    public async Task A_configuration_load_cannot_replace_the_selected_tenant_after_navigation()
    {
        var pending = new TaskCompletionSource<NetclawConnectionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Read = tenant => tenant == 17 ? pending.Task : Task.FromResult(Saved(18) with { DaemonAddress = "https://new.example" });
        var cut = Render<NetclawConnectionPanel>(p => p.Add(x => x.InitialTenantId, 17));
        cut.Render(p => p.Add(x => x.InitialTenantId, 18));
        pending.SetResult(Saved() with { DaemonAddress = "https://old.example" });
        await cut.WaitForAssertionAsync(() => Assert.Contains("https://new.example", cut.Markup));
        Assert.DoesNotContain("https://old.example", cut.Markup);
        Assert.Empty(api.PairRequests); Assert.Empty(api.TestRequests);
    }

    [Fact]
    public void Legacy_bookmark_redirects_to_the_scoped_integration_tab_without_api_mutations()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/ai/integration?tenantId=17");
        Render<NetclawConnection>();
        Assert.EndsWith("/account/integration-credentials?tab=netclaw&tenantId=17", navigation.Uri);
        Assert.Empty(api.ConnectionReads); Assert.Empty(api.PairRequests); Assert.Empty(api.TestRequests);
    }

    [Fact]
    public async Task Integration_direct_link_and_tab_navigation_preserve_tenant_without_credential_management_rights()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(new DeniedCredentials());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/account/integration-credentials?tab=netclaw&tenantId=18");
        var cut = Render<IntegrationCredentials>();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Saved Netclaw connection", cut.Markup));
        Assert.Equal(18, Assert.Single(api.ConnectionReads));
        Assert.DoesNotContain("Could not load integrations", cut.Markup);
        await cut.FindAll("[role=tab]").Single(x => x.TextContent.Contains("API & MCP credentials")).ClickAsync(new());
        Assert.Contains("tab=credentials", navigation.Uri); Assert.Contains("tenantId=18", navigation.Uri);
        navigation.NavigateTo("/account/integration-credentials?tab=netclaw&tenantId=17");
        await cut.WaitForAssertionAsync(() => Assert.Equal("17", cut.Find("select").GetAttribute("value")));
        Assert.Empty(api.PairRequests); Assert.Empty(api.TestRequests);
    }

    private sealed class DeniedCredentials : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.example") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "Forbidden" }) });
    }
    private sealed class ConnectionApi : INetclawApiService
    {
        public bool CanConfigure = true;
        public List<int> ConnectionReads { get; } = [];
        public List<NetclawPairRequest> PairRequests { get; } = [];
        public List<int> TestRequests { get; } = [];
        public Func<int, Task<NetclawConnectionDto>> Read = tenant => Task.FromResult(Saved(tenant));
        public Func<int, CancellationToken, Task<NetclawConnectionDiagnosticsDto?>> Observe = (_, _) => Task.FromResult<NetclawConnectionDiagnosticsDto?>(null);
        public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetclawTenantDto>>([new(17, "Operations", true, CanConfigure), new(18, "Other", true, CanConfigure)]);
        public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) { ConnectionReads.Add(tenantId); return Read(tenantId); }
        public Task<NetclawConnectionDiagnosticsDto?> ConnectionDiagnosticsAsync(int tenantId, CancellationToken ct = default) => Observe(tenantId, ct);
        public Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default) { PairRequests.Add(request); throw new NotSupportedException(); }
        public Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default) { TestRequests.Add(tenantId); throw new NotSupportedException(); }
        public Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
