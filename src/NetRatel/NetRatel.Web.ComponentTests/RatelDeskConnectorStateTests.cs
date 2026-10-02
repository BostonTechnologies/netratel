using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services.RatelDesk;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RatelDeskConnectorStateTests : AsyncBunitContext
{
    [Fact]
    public void Mapping_check_and_sanitized_preview_never_claim_delivery_or_render_a_credential()
    {
        var api = new FakeApi(); Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-revisions']")));
        page.Find("[data-testid='connector-credential']").Input("rdk_synthetic_password_that_must_never_be_rendered");
        Assert.DoesNotContain("rdk_synthetic_password", page.Markup);
        page.Find("[data-testid='connector-test']").Click();
        page.WaitForAssertion(() => Assert.Contains("read-only lookups", page.Markup));
        Assert.Contains("Automatic delivery remains unavailable", page.Markup);
        page.Find("[data-testid='connector-dry-run']").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-preview']")));
        Assert.Contains("no incident sent", page.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", page.Markup);
        Assert.DoesNotContain("<script>", page.Markup);
        Assert.Equal(1, api.TestCalls); Assert.Equal(1, api.DryRunCalls); Assert.Equal(0, api.RotateCalls);
        page.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_write_blocks_double_submit_and_tenant_switch_and_failure_keeps_settings(bool bodyReadFault)
    {
        var api = new FakeApi { SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-revisions']")));
        page.Find("[data-testid='connector-name']").Change("Edited mapping");
        var pending = CallAsync(page.Instance, "SaveAsync");
        await CallAsync(page.Instance, "SaveAsync");
        await CallAsync(page.Instance, "TenantChangedAsync", new ChangeEventArgs { Value = "2" });
        Assert.Equal(1, api.SaveCalls); Assert.Equal(1, api.LastSaveTenant); Assert.DoesNotContain(2, api.ListTenants);
        api.SaveGate.SetException(bodyReadFault ? new IOException("synthetic response stream failure") : new RatelDeskConnectorApiException("connector-conflict")); await pending;
        Assert.Equal("Edited mapping", Field<string>(page.Instance, "_name"));
        Assert.Contains(bodyReadFault ? "did not complete" : "revision changed", Field<string>(page.Instance, "_error"));
        Assert.False(Field<bool>(page.Instance, "_busy"));
    }

    [Fact]
    public async Task Capacity_failure_is_explicit_and_retains_the_unsaved_mapping()
    {
        var api = new FakeApi { SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-revisions']")));
        page.Find("[data-testid='connector-name']").Change("Retained capacity draft");
        var pending = CallAsync(page.Instance, "SaveAsync");
        api.SaveGate.SetException(new RatelDeskConnectorApiException("connector-capacity-exhausted")); await pending;
        Assert.Equal("Retained capacity draft", Field<string>(page.Instance, "_name"));
        Assert.Contains("limit has been reached or exceeded", Field<string>(page.Instance, "_error"));
        Assert.False(Field<bool>(page.Instance, "_busy"));
    }

    [Fact]
    public async Task Late_tenant_read_cannot_replace_the_current_tenant_connector()
    {
        var api = new FakeApi { FirstListGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Contains(1, api.ListTenants));
        await page.InvokeAsync(() => CallAsync(page.Instance, "TenantChangedAsync", new ChangeEventArgs { Value = "2" }));
        api.FirstListGate.SetResult([FakeApi.Connector(1)]);
        page.WaitForAssertion(() => Assert.Equal(2, Field<RatelDeskConnectorDto>(page.Instance, "_saved").TenantId));
        Assert.Contains("Tenant 2 connector", page.Markup);
        Assert.DoesNotContain("Tenant 1 connector", page.Markup);
    }

    [Fact]
    public async Task Dirty_tenant_switch_requires_discard_and_forgets_the_password()
    {
        var api = new FakeApi(); Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-revisions']")));
        page.Find("[data-testid='connector-credential']").Input("transient-password");
        page.Find("[data-testid='connector-name']").Input("Unsaved edit");
        await CallAsync(page.Instance, "TenantChangedAsync", new ChangeEventArgs { Value = "2" });
        Assert.Equal(1, Field<int>(page.Instance, "_tenantId")); Assert.Equal(2, Field<int?>(page.Instance, "_pendingTenant"));
        await page.InvokeAsync(() => CallAsync(page.Instance, "DiscardAndChangeAsync"));
        Assert.Equal(2, Field<int>(page.Instance, "_tenantId")); Assert.Null(Field<string?>(page.Instance, "_credential"));
        Assert.DoesNotContain("transient-password", page.Markup);
    }

    [Fact]
    public async Task Disposed_rotation_discards_password_and_ignores_its_late_result()
    {
        var api = new FakeApi { RotateGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-revisions']")));
        page.Find("[data-testid='connector-credential']").Input("rdk_synthetic_password_that_must_never_be_rendered");
        var pending = CallAsync(page.Instance, "RotateAsync");
        Assert.Null(Field<string?>(page.Instance, "_credential")); Assert.Equal(1, api.RotateCalls);
        page.Instance.Dispose();
        api.RotateGate.SetResult(FakeApi.Connector(1) with { CredentialRevision = 4 }); await pending;
        Assert.Equal(3, Field<RatelDeskConnectorDto>(page.Instance, "_saved").CredentialRevision);
        Assert.Null(Field<string?>(page.Instance, "_credential")); Assert.Null(Field<string?>(page.Instance, "_notice"));
    }

    [Fact]
    public void Foreign_requested_tenant_never_falls_back_to_a_different_tenant()
    {
        var api = new FakeApi(); Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/flows/connectors?tenantId=99");
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Contains("requested tenant is not available", page.Markup));
        Assert.Empty(api.ListTenants); Assert.Equal(0, Field<int>(page.Instance, "_tenantId"));
    }

    [Fact]
    public async Task New_connector_keeps_its_identity_when_a_write_response_is_lost()
    {
        var api = new FakeApi { SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='connector-revisions']")));
        page.Find("#rd-selection").Change(Guid.Empty.ToString());
        var pending = CallAsync(page.Instance, "SaveAsync");
        var stableId = api.LastSaveId;
        api.SaveGate.SetException(new HttpRequestException("synthetic response loss")); await pending;
        api.SaveGate = null;
        await CallAsync(page.Instance, "SaveAsync");
        Assert.NotEqual(Guid.Empty, stableId); Assert.Equal(stableId, api.LastSaveId);
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Task CallAsync(object instance, string name, params object[] arguments) =>
        (Task)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments)!;

    private sealed class FakeApi : IRatelDeskConnectorApiService
    {
        public TaskCompletionSource<IReadOnlyList<RatelDeskConnectorDto>>? FirstListGate;
        public TaskCompletionSource<RatelDeskConnectorDto>? SaveGate, RotateGate;
        public List<int> ListTenants { get; } = [];
        public int SaveCalls, LastSaveTenant, TestCalls, DryRunCalls, RotateCalls; public Guid LastSaveId;
        public static RatelDeskConnectorDto Connector(int tenant) => new(Guid.Parse($"00000000-0000-0000-0000-{tenant:D12}"), tenant, 7,
            new($"Tenant {tenant} connector", "https://desk.example.test", $"org-{tenant}", $"customer-{tenant}", null, [], new(), true), true, 3, false, RatelDeskConnectorLimits.ReceiverUnavailableCode);
        public Task<IReadOnlyList<RatelDeskConnectorTenantDto>> GetTenantsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorTenantDto>>([new(1, "Tenant 1"), new(2, "Tenant 2")]);
        public Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenant, CancellationToken ct)
        { ListTenants.Add(tenant); return tenant == 1 && FirstListGate is not null ? FirstListGate.Task : Task.FromResult<IReadOnlyList<RatelDeskConnectorDto>>([Connector(tenant)]); }
        public Task<RatelDeskConnectorDto> SaveAsync(int tenant, Guid id, SaveRatelDeskConnectorRequest request, CancellationToken ct)
        { SaveCalls++; LastSaveTenant = tenant; LastSaveId = id; return SaveGate?.Task ?? Task.FromResult(Connector(tenant) with { Id = id, Configuration = request.Configuration, Revision = request.ExpectedRevision + 1 }); }
        public Task<RatelDeskConnectorDto> RotateAsync(int tenant, Guid id, RotateRatelDeskConnectorCredentialRequest request, CancellationToken ct)
        { RotateCalls++; return RotateGate?.Task ?? Task.FromResult(Connector(tenant) with { CredentialRevision = request.ExpectedCredentialRevision + 1 }); }
        public Task<RatelDeskConnectionTestResult> TestAsync(int tenant, Guid id, CancellationToken ct)
        { TestCalls++; return Task.FromResult(new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.MappingValidated, "mapping-validated")); }
        public Task<RatelDeskDryRunResult> DryRunAsync(int tenant, Guid id, RatelDeskDryRunRequest request, CancellationToken ct)
        { DryRunCalls++; return Task.FromResult(new RatelDeskDryRunResult(new("<script>Plain text</script>", "Sanitized preview", 1, "customer", "org", null, []), "fingerprint")); }
    }
}
