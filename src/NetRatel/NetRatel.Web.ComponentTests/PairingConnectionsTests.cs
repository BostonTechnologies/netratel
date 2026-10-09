using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.SystemPairing;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Components.Shared.Pairing;
using NetRatel.Web.Services.Pairing;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class PairingConnectionsTests : AsyncBunitContext
{
    public PairingConnectionsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    }

    [Fact]
    public async Task Code_generation_needs_no_tenant_and_pairing_opens_only_two_inputs_then_one_final_form()
    {
        var api = new FakeApi(); Services.AddSingleton<IPairingApiClient>(api);
        var panel = Render<PairingConnectionsPanel>();
        await panel.Find("[data-testid='generate-pairing-code']").ClickAsync(new());
        panel.Markup.Should().Contain("ABCD-EFGH");
        panel.FindAll("[data-testid='pairing-final-form']").Should().BeEmpty();
        await panel.Find("[data-testid='create-connection']").ClickAsync(new());
        panel.Find("[data-testid='pairing-create-form']").QuerySelectorAll("input").Should().HaveCount(2);
        panel.Find("[data-testid='pair-and-connect']").HasAttribute("disabled").Should().BeTrue();
        panel.Find("[data-testid='pairing-address'] input").Input("http://10.20.30.40:8080///");
        panel.Find("[data-testid='pairing-code'] input").Input("ABCD-EFGH");
        panel.Find("[data-testid='pair-and-connect']").HasAttribute("disabled").Should().BeFalse();
        await panel.Find("[data-testid='pair-and-connect']").ClickAsync(new());
        api.ConnectCalls.Should().Be(1); api.LastConnect!.Address.Should().Be("http://10.20.30.40:8080");
        panel.FindAll("[data-testid='pairing-create-form']").Should().BeEmpty();
        panel.FindAll("[data-testid='pairing-final-form']").Should().HaveCount(1);
        panel.FindAll("[data-testid='pairing-save']").Should().HaveCount(1);
        panel.FindAll("[data-testid='pairing-rateldesk-customer']").Should().BeEmpty();
        Field<bool>(panel.Instance, "_runAutomation").Should().BeFalse();
        panel.Markup.Should().Contain("Monitoring tenant").And.Contain("Support organization").And.Contain("Systems paired");
    }

    [Fact]
    public async Task Save_failure_keeps_the_same_nonsecret_draft_and_retry_activates_without_another_pair_or_test()
    {
        var api = new FakeApi { Connections = [FakeApi.Unconfigured()], RejectSave = true };
        Services.AddSingleton<IPairingApiClient>(api);
        var panel = Render<PairingConnectionsPanel>();
        panel.WaitForAssertion(() => panel.FindAll("[data-testid='pairing-final-form']").Should().HaveCount(1));
        Set(panel.Instance, "_name", "On call incidents"); Set(panel.Instance, "_createIncidents", true); Set(panel.Instance, "_customer", "customer-1");
        await panel.InvokeAsync(() => CallAsync(panel.Instance, "SaveAsync"));
        panel.Find("[data-testid='pairing-save-error']").TextContent.Should().Contain("customer permission changed").And.Contain("ref-save");
        Field<string>(panel.Instance, "_name").Should().Be("On call incidents");
        panel.FindAll("[data-testid='connection-card']").Should().HaveCount(1);
        var mappingId = api.LastSave!.Id;
        api.RejectSave = false;
        await panel.InvokeAsync(() => CallAsync(panel.Instance, "SaveAsync"));
        api.LastSave!.Id.Should().Be(mappingId); api.LastSave.RunAutomation.Should().BeFalse();
        api.LastSave.RatelDeskCustomerId.Should().Be("customer-1");
        api.ConnectCalls.Should().Be(0); api.TestCalls.Should().Be(0); api.SaveCalls.Should().Be(2);
        panel.FindAll("[data-testid='pairing-final-form']").Should().BeEmpty();
        panel.FindAll("[data-testid='connection-card']").Should().HaveCount(1);
        panel.Markup.Should().Contain("Connected").And.Contain("On call incidents");
    }

    [Fact]
    public async Task Pair_retry_after_lost_response_keeps_operation_identity_and_does_not_duplicate_the_setup()
    {
        var api = new FakeApi { LoseConnectResponse = true }; Services.AddSingleton<IPairingApiClient>(api);
        var panel = Render<PairingConnectionsPanel>();
        await panel.Find("[data-testid='create-connection']").ClickAsync(new());
        panel.Find("[data-testid='pairing-address'] input").Input("desk.internal:8443/");
        panel.Find("[data-testid='pairing-code'] input").Input("ABCD-EFGH");
        await panel.Find("[data-testid='pair-and-connect']").ClickAsync(new());
        var operation = api.LastConnect!.OperationId;
        panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain("Reference:");
        api.LoseConnectResponse = false;
        await panel.Find("[data-testid='pair-and-connect']").ClickAsync(new());
        api.LastConnect!.OperationId.Should().Be(operation);
        api.LastConnect.Address.Should().Be("https://desk.internal:8443");
        panel.FindAll("[data-testid='connection-card']").Should().HaveCount(1);
    }

    [Fact]
    public async Task Delete_removes_only_the_selected_mapping_and_a_late_test_cannot_restore_it()
    {
        var selected = FakeApi.Configured(Guid.Parse("11111111-1111-1111-1111-111111111111"), "First mapping");
        var retained = FakeApi.Configured(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Second mapping");
        var api = new FakeApi { Connections = [selected, retained], TestGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Services.AddSingleton<IPairingApiClient>(api);
        var panel = Render<PairingConnectionsPanel>();
        panel.WaitForAssertion(() => panel.FindAll("[data-testid='connection-card']").Should().HaveCount(2));
        var pendingTest = CallAsync(panel.Instance, "TestAsync", selected);
        await panel.InvokeAsync(() => CallAsync(panel.Instance, "DeleteAsync", selected));
        api.LastDeleted!.Mapping!.Id.Should().Be(selected.Mapping!.Id);
        api.TestGate.SetResult(new(true, "Authenticated mapping checked; no incident or task created.", DateTimeOffset.UtcNow));
        await pendingTest;
        await panel.InvokeAsync(() => Task.CompletedTask);
        panel.FindAll("[data-testid='connection-card']").Should().HaveCount(1);
        panel.Markup.Should().Contain("Second mapping").And.NotContain("First mapping");
        panel.FindAll("[data-testid='connection-test-result']").Should().BeEmpty();
    }

    [Fact]
    public void Refresh_returns_an_existing_incomplete_pair_to_the_final_form_and_alternate_page_links_to_it()
    {
        var api = new FakeApi { Connections = [FakeApi.Unconfigured()] }; Services.AddSingleton<IPairingApiClient>(api);
        var panel = Render<PairingConnectionsPanel>();
        panel.WaitForAssertion(() => panel.FindAll("[data-testid='pairing-final-form']").Should().HaveCount(1));
        api.ConnectCalls.Should().Be(0);
        var page = Render<RatelDeskConnectors>();
        page.Find("[data-testid='manage-system-connections']").GetAttribute("href").Should().Be("/account/integration-credentials");
        page.FindAll("input").Should().BeEmpty();
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static Task CallAsync(object target, string name, params object[] args) => (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

    private sealed class FakeApi : IPairingApiClient
    {
        private static readonly PairingMetadata Peer = new(PairingProtocol.Contract, "rateldesk", "33333333-3333-3333-3333-333333333333", "Support desk", "https://desk.test", "https://api.desk.test", null);
        public PairingConnectionDto[] Connections = [];
        public int ConnectCalls, SaveCalls, TestCalls;
        public bool RejectSave, LoseConnectResponse;
        public PairingConnectRequest? LastConnect;
        public PairingMapping? LastSave;
        public PairingConnectionDto? LastDeleted;
        public TaskCompletionSource<PairingTestResult>? TestGate;
        public static PairingConnectionDto Unconfigured() => new("pair-1", "pair-1", null, Peer, "paired");
        public static PairingConnectionDto Configured(Guid id, string name) => new(id.ToString("D"), "pair-1", new(id, "pair-1", name, "12", "org-1", "customer-1", true, false), Peer, "connected");
        public Task<PairingConnectionDto[]> ListAsync(CancellationToken ct = default) => Task.FromResult(Connections);
        public Task<PairingCodeResponse> GenerateCodeAsync(CancellationToken ct = default) => Task.FromResult(new PairingCodeResponse("ABCD-EFGH", DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, CancellationToken ct = default)
        {
            ConnectCalls++; LastConnect = request;
            if (LoseConnectResponse) throw new HttpRequestException("synthetic lost response");
            return Task.FromResult(Unconfigured());
        }
        public Task<PairingDirectories> DirectoryAsync(string pairId, CancellationToken ct = default) => Task.FromResult(new PairingDirectories([new("12", "Monitoring tenant")], [new("org-1", "Support organization")], [new("customer-1", "Example customer", "org-1")]));
        public Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct = default)
        {
            SaveCalls++; LastSave = mapping;
            if (RejectSave) throw new PairingApiException("access-denied", "The customer permission changed. Choose another authorized customer.", "ref-save");
            return Task.FromResult(new PairingConnectionDto(mapping.Id.ToString("D"), mapping.PairId, mapping, Peer, "connected"));
        }
        public Task<PairingTestResult> TestAsync(PairingConnectionDto connection, CancellationToken ct = default)
        { TestCalls++; return TestGate?.Task ?? Task.FromResult(new PairingTestResult(true, "Authenticated access checked.", DateTimeOffset.UtcNow)); }
        public Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct = default) { LastDeleted = connection; return Task.CompletedTask; }
    }
}
