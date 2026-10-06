using System.Reflection;
using AwesomeAssertions;
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
        page.WaitForAssertion(() => Assert.Contains("did not establish receiver delivery readiness", page.Markup));
        Assert.Contains("Delivery needs a current approved connection", page.Markup);
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

    [Fact]
    public async Task Only_a_verified_receiver_result_can_enable_the_visible_delivery_state()
    {
        var api = new FakeApi { VerifiedReceiver = true }; Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => page.FindAll("[data-testid='connector-revisions']").Should().HaveCount(1));
        await page.InvokeAsync(() => CallAsync(page.Instance, "TestAsync"));
        Field<RatelDeskConnectorDto>(page.Instance, "_saved").AutomaticDeliveryAvailable.Should().BeTrue();
        page.Markup.Should().Contain("saved receiver and target passed the authenticated read-only check");
        api.VerifiedReceiver = false;
        await page.InvokeAsync(() => CallAsync(page.Instance, "TestAsync"));
        Field<RatelDeskConnectorDto>(page.Instance, "_saved").AutomaticDeliveryAvailable.Should().BeFalse();
        page.Markup.Should().Contain("did not establish receiver delivery readiness");
    }

    [Fact]
    public async Task Managed_selection_uses_approved_mapping_without_requesting_or_rendering_a_manual_secret()
    {
        var api = new FakeApi(); Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => page.FindAll("[data-testid='connector-revisions']").Should().HaveCount(1));
        await page.InvokeAsync(() => CallAsync(page.Instance, "LoadSetupAsync"));
        page.Find("[data-testid='connector-authentication']").Change("service_link");
        page.Find("[data-testid='connector-managed-link']").Change("33333333-3333-3333-3333-333333333333");
        await page.InvokeAsync(() => CallAsync(page.Instance, "SaveAsync"));
        api.LastAuthentication.Should().Be(new RatelDeskConnectorAuthenticationDto("service_link", "33333333-3333-3333-3333-333333333333"));
        Field<string>(page.Instance, "_origin").Should().Be("https://desk.example.test/helpdesk");
        Field<string>(page.Instance, "_organization").Should().Be("approved-org");
        page.FindAll("[data-testid='connector-credential']").Should().BeEmpty();
        page.FindAll("[data-testid='connector-rotate']").Should().BeEmpty();
        await page.InvokeAsync(() => CallAsync(page.Instance, "TestAsync"));
        api.TestCalls.Should().Be(1); api.RotateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Producer_setup_displays_distinct_installation_and_Flow_IDs_and_adopts_only_by_revision()
    {
        var api = new FakeApi(); Services.AddSingleton<IRatelDeskConnectorApiService>(api);
        var page = Render<RatelDeskConnectors>();
        page.WaitForAssertion(() => page.FindAll("[data-testid='connector-revisions']").Should().HaveCount(1));
        await page.InvokeAsync(() => CallAsync(page.Instance, "LoadSetupAsync"));
        page.Find("[data-testid='connector-producer']").TextContent.Should().Contain("11111111-1111-1111-1111-111111111111").And.Contain("22222222-2222-2222-2222-222222222222");
        await page.InvokeAsync(() => CallAsync(page.Instance, "AdoptFlowSourceAsync"));
        api.LastAdoptRevision.Should().Be(1);
        var setup = Field<RatelDeskConnectorSetupDto>(page.Instance, "_setup");
        setup.AdoptedSourceInstanceId.Should().Be(setup.FlowSourceInstanceId.ToString("D"));
        api.RotateCalls.Should().Be(0); api.TestCalls.Should().Be(0);
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
        public bool VerifiedReceiver; public RatelDeskConnectorAuthenticationDto? LastAuthentication; public long? LastAdoptRevision;
        public static RatelDeskConnectorDto Connector(int tenant) => new(Guid.Parse($"00000000-0000-0000-0000-{tenant:D12}"), tenant, 7,
            new($"Tenant {tenant} connector", "https://desk.example.test", $"org-{tenant}", $"customer-{tenant}", null, [], new(), true), true, 3, false, RatelDeskConnectorLimits.ReceiverUnavailableCode);
        public Task<RatelDeskConnectorSetupDto> GetSetupAsync(int tenantId, CancellationToken ct) => Task.FromResult(new RatelDeskConnectorSetupDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "22222222-2222-2222-2222-222222222222", null, 1,
            [new("33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444", "https://desk.example.test/helpdesk", "approved-org", "approved-customer", 1)]));
        public Task<RatelDeskConnectorSetupDto> AdoptFlowSourceAsync(int tenantId, AdoptRatelDeskFlowSourceRequest request, CancellationToken ct)
        { LastAdoptRevision = request.ExpectedIdentityRevision; return Task.FromResult(new RatelDeskConnectorSetupDto(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111", request.ExpectedIdentityRevision + 1, [])); }
        public Task<IReadOnlyList<RatelDeskConnectorTenantDto>> GetTenantsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorTenantDto>>([new(1, "Tenant 1"), new(2, "Tenant 2")]);
        public Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenant, CancellationToken ct)
        { ListTenants.Add(tenant); return tenant == 1 && FirstListGate is not null ? FirstListGate.Task : Task.FromResult<IReadOnlyList<RatelDeskConnectorDto>>([Connector(tenant)]); }
        public Task<RatelDeskConnectorDto> SaveAsync(int tenant, Guid id, SaveRatelDeskConnectorRequest request, CancellationToken ct)
        { SaveCalls++; LastSaveTenant = tenant; LastSaveId = id; LastAuthentication = request.Authentication; return SaveGate?.Task ?? Task.FromResult(Connector(tenant) with { Id = id, Configuration = request.Configuration, Revision = request.ExpectedRevision + 1, Authentication = request.Authentication, HasCredential = request.Authentication?.Mode != "service_link" }); }
        public Task<RatelDeskConnectorDto> RotateAsync(int tenant, Guid id, RotateRatelDeskConnectorCredentialRequest request, CancellationToken ct)
        { RotateCalls++; return RotateGate?.Task ?? Task.FromResult(Connector(tenant) with { CredentialRevision = request.ExpectedCredentialRevision + 1 }); }
        public Task<RatelDeskConnectionTestResult> TestAsync(int tenant, Guid id, CancellationToken ct)
        { TestCalls++; return Task.FromResult(new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.MappingValidated, VerifiedReceiver ? "receiver-ready" : "mapping-validated", VerifiedReceiver)); }
        public Task<RatelDeskDryRunResult> DryRunAsync(int tenant, Guid id, RatelDeskDryRunRequest request, CancellationToken ct)
        { DryRunCalls++; return Task.FromResult(new RatelDeskDryRunResult(new("<script>Plain text</script>", "Sanitized preview", 1, "customer", "org", null, []), "fingerprint")); }
    }
}
