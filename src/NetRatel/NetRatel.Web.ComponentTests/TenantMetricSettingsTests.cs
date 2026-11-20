using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Components.Pages.Tenants;
using NetRatel.Web.Services.Monitoring;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TenantMetricSettingsTests : AsyncBunitContext
{
    private readonly TenantTransport _transport = new();

    public TenantMetricSettingsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddSingleton<ITenantApiService, TenantApiService>();
        Services.AddSingleton<IMonitoringApiService, MonitoringApiService>();
    }

    [Fact]
    public async Task Lazy_policy_draft_survives_tabs_and_each_save_uses_only_its_own_endpoint()
    {
        var popovers = Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var page = Render<TenantsPage>();
        var editor = await EditAsync();
        Assert.Empty(_transport.PolicyReads);
        await editor.Find("select[aria-label='Tenant section']").ChangeAsync("4");
        Assert.Equal([7], _transport.PolicyReads);
        Assert.Contains("Example workstation", editor.Markup);
        Assert.Contains(TenantTransport.Agent.ToString("D"), editor.Markup);

        await editor.Find("input[type=number]").ChangeAsync(21);
        await editor.Find("select[aria-label='Tenant section']").ChangeAsync("0");
        Assert.Contains("Metrics tab has unsaved changes", editor.Markup);
        var tryGeneralSave = Button(editor, "Save tenant changes").ClickAsync(new());
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Discard the unsaved changes in Metrics", dialogs.Markup));
        await Button(dialogs, "Keep editing").ClickAsync(new());
        await tryGeneralSave;
        Assert.Empty(_transport.ProfileWrites);
        Assert.Empty(_transport.PolicyWrites);

        await editor.Find("select[aria-label='Tenant section']").ChangeAsync("4");
        Assert.Equal("21", editor.Find("input[type=number]").GetAttribute("value"));
        await editor.Find("input[maxlength='512']").ChangeAsync("Keep more recent detail for the canary.");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.HoldSave = held.Task;
        var saving = Button(editor, "Save metrics settings").ClickAsync(new());
        await editor.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Saving metrics settings", editor.Markup);
            Assert.True(Button(editor, "Cancel tenant changes").HasAttribute("disabled"));
            Assert.True(Button(editor, "Save tenant changes").HasAttribute("disabled"));
        });
        held.SetResult();
        await saving;
        var write = Assert.Single(_transport.PolicyWrites);
        Assert.Equal(21, write.BaseDays);
        Assert.Equal(4, write.ExpectedRevision);
        Assert.Equal(TenantTransport.Agent, Assert.Single(write.Watches).AgentId);
        Assert.Contains("saved as revision 5", editor.Markup);
        Assert.Empty(_transport.ProfileWrites);
        await Button(editor, "Cancel tenant changes").ClickAsync(new());
        Assert.Equal(21, _transport.Policy.BaseDays);

        editor = await EditAsync();
        await Button(editor, "Save tenant changes").ClickAsync(new());
        await page.WaitForAssertionAsync(() => Assert.Single(_transport.ProfileWrites));
        Assert.Single(_transport.PolicyWrites);

        async Task<IRenderedComponent<TenantEditorDialog>> EditAsync()
        {
            await page.Find("button[aria-label='Manage Example estate']").ClickAsync(new());
            await popovers.InvokeAsync(() => popovers.FindAll("[role=menuitem]")
                .Single(item => item.TextContent.Trim() == "Edit Tenant").Click());
            return dialogs.FindComponent<TenantEditorDialog>();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scoped_route_uses_exact_manage_permission_before_any_profile_or_policy_read(bool canManage)
    {
        _transport.CanManage = canManage;
        _transport.CanRead = false;
        var dialogs = Render<MudDialogProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/tenants?editTenant=7&tab=metrics");
        var page = Render<TenantsPage>();
        Assert.Equal(0, _transport.ProfileReads);
        Assert.Equal(0, _transport.DirectoryReads);
        Assert.DoesNotContain("Add Tenant", page.Markup);
        if (canManage)
        {
            await dialogs.WaitForAssertionAsync(() => Assert.Contains("Metrics collection", dialogs.Markup));
            var editor = dialogs.FindComponent<TenantEditorDialog>();
            Assert.Contains("Metrics collection", editor.Markup);
            Assert.DoesNotContain("Save tenant changes", editor.Markup);
            Assert.Empty(editor.FindAll("[role=tab]"));
            Assert.Equal([7], _transport.PolicyReads);
        }
        else
        {
            Assert.Contains("Metrics management is unavailable", page.Markup);
            Assert.Empty(dialogs.FindComponents<TenantEditorDialog>());
            Assert.Empty(_transport.PolicyReads);
        }
    }

    [Fact]
    public async Task Conflict_keeps_draft_and_permission_loss_clears_it_without_rewriting()
    {
        var dialogs = Render<MudDialogProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/tenants?editTenant=7&tab=metrics");
        Render<TenantsPage>();
        var editor = dialogs.FindComponent<TenantEditorDialog>();
        await editor.Find("input[type=number]").ChangeAsync(20);
        await editor.Find("input[maxlength='512']").ChangeAsync("Extend detailed retention.");
        _transport.Policy = _transport.Policy with { Revision = 5, BaseDays = 17 };
        await Button(editor, "Save metrics settings").ClickAsync(new());
        Assert.Contains("Revision 5 is current", editor.Markup);
        Assert.Equal("20", editor.Find("input[type=number]").GetAttribute("value"));
        Assert.True(Button(editor, "Save metrics settings").HasAttribute("disabled"));
        await Button(editor, "Keep my edits against the reviewed revision").ClickAsync(new());

        _transport.CanManage = false;
        await Button(editor, "Save metrics settings").ClickAsync(new());
        Assert.Contains("You do not have permission to manage metrics settings", editor.Markup);
        Assert.Empty(editor.FindAll("input[type=number]"));
        Assert.DoesNotContain("Example workstation", editor.Markup);
        Assert.False(Button(editor, "Close").HasAttribute("disabled"));
        Assert.Equal(5, _transport.Policy.Revision);
        Assert.Equal(17, _transport.Policy.BaseDays);
        Assert.Empty(_transport.ProfileWrites);
    }

    [Fact]
    public async Task Scoped_replacement_cancels_the_previous_policy_read_and_ignores_its_late_result()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.HoldFirstRead = held.Task;
        var dialogs = Render<MudDialogProvider>();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/tenants?editTenant=7&tab=metrics");
        var page = Render<TenantsPage>();
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Loading metrics settings", dialogs.Markup));
        await page.InvokeAsync(() => nav.NavigateTo("/tenants?editTenant=8&tab=metrics"));
        await dialogs.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Tenant #8", dialogs.Markup);
            Assert.Contains("Save metrics settings", dialogs.Markup);
        });
        Assert.True(_transport.FirstReadToken.IsCancellationRequested);
        held.SetResult();
        Assert.Equal([7, 8], _transport.PolicyReads);
        Assert.DoesNotContain("Tenant #7", dialogs.Markup);
        Assert.Equal(0, _transport.ProfileReads);
    }

    [Fact]
    public async Task Fallback_directory_only_offers_manage_links_and_create_sends_no_policy_request()
    {
        _transport.DenyProfileList = true;
        var page = Render<TenantsPage>();
        await page.WaitForAssertionAsync(() => Assert.NotNull(page.Find("a[href='/tenants?editTenant=7&tab=metrics']")));
        Assert.DoesNotContain("Read only estate", page.Markup);
        Assert.DoesNotContain("Add Tenant", page.Markup);
        var dialogs = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<TenantEditorDialog>("Add Tenant");
        var create = dialogs.FindComponent<TenantEditorDialog>();
        await create.Find("select[aria-label='Tenant section']").ChangeAsync("4");
        Assert.Contains("Create the tenant before editing metrics settings", create.Markup);
        Assert.Empty(_transport.PolicyReads);
        Assert.Empty(_transport.PolicyWrites);
    }

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> cut, string label) where T : IComponent =>
        cut.FindAll("button").Single(item => item.TextContent.Trim() == label);

    private sealed class TenantTransport : HttpMessageHandler, IHttpClientFactory
    {
        public static readonly Guid Agent = Guid.Parse("11111111-1111-4111-8111-111111111111");
        public readonly List<int> PolicyReads = [];
        public readonly List<MetricHistoryPolicyWrite> PolicyWrites = [];
        public readonly List<UpdateTenantRequest> ProfileWrites = [];
        public MetricHistoryPolicy Policy = MetricHistoryPolicy.Default with
        {
            Revision = 4, Watches = [new(Agent, ClientServicePlatform.Windows, "SPOOLER")]
        };
        public bool CanManage = true, CanRead = true, DenyProfileList;
        public Task? HoldSave, HoldFirstRead;
        public CancellationToken FirstReadToken;
        public int ProfileReads, DirectoryReads;
        private static readonly MetricCollectionStatus Status =
            new(true, null, null, null, null, null, 0, 1000, 10000, 0, 0, null, 0, 0, 0, null, null);

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://netratel.test") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/tenants")
            {
                ProfileReads++;
                if (DenyProfileList) return Reply(HttpStatusCode.Forbidden, new { });
                return Reply(HttpStatusCode.OK, new[] { new { tenantId = 7, name = "Example estate", domains = Array.Empty<string>(),
                    createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow, version = 3 } });
            }
            if (path == "/api/v1/tenants/7" && request.Method == HttpMethod.Put)
            {
                ProfileWrites.Add((await request.Content!.ReadFromJsonAsync<UpdateTenantRequest>(token))!);
                return Reply(HttpStatusCode.Accepted, new { });
            }
            if (path == "/api/v2/monitoring/tenants")
            {
                DirectoryReads++;
                return Reply(HttpStatusCode.OK, new MonitoringTenantDto[] { new(7, "Example estate"), new(9, "Read only estate") });
            }
            var tenant = int.Parse(path.Split('/')[4]);
            if (path.EndsWith("/permissions", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new MonitoringPermissionsDto(tenant, CanRead, CanManage && tenant != 9, false, false, false, false));
            if (path.EndsWith("/clients/identities", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new MonitoringClientIdentityDto[] { new(Agent, "Example workstation") });
            if (path.EndsWith("/clients", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new MonitoringClientPageDto([], null, 0));
            Assert.EndsWith("/metrics/settings", path);
            if (request.Method == HttpMethod.Get)
            {
                PolicyReads.Add(tenant);
                if (tenant == 7 && HoldFirstRead is { } held)
                {
                    FirstReadToken = token;
                    await held;
                }
            }
            else
            {
                Assert.Equal(7, tenant);
                if (!CanManage) return Reply(HttpStatusCode.Forbidden, new { });
                var write = (await request.Content!.ReadFromJsonAsync<MetricHistoryPolicyWrite>(token))!;
                PolicyWrites.Add(write);
                if (write.ExpectedRevision != Policy.Revision)
                    return Reply(HttpStatusCode.Conflict, new { title = "metrics_revision_conflict" });
                if (HoldSave is { } held) await held;
                Policy = new(Policy.Revision + 1, write.Enabled, write.BaseDays, write.FiveMinuteDays, write.HourlyDays, write.Watches);
            }
            return Reply(HttpStatusCode.OK, new MetricHistorySettings(Policy, Status, []));
        }

        private static HttpResponseMessage Reply<T>(HttpStatusCode status, T body) => new(status) { Content = JsonContent.Create(body) };
    }
}
