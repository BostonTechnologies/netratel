using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
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
        panel.Find("[data-testid='pairing-address']").Input("http://10.20.30.40:8080///");
        panel.Find("[data-testid='pairing-code']").Input("ABCD-EFGH");
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
        panel.Find("[data-testid='pairing-connection-name']").Input("On call incidents");
        var incidents = panel.FindComponents<MudCheckBox<bool>>().Single(x => x.Instance.Label!.StartsWith("Create incidents", StringComparison.Ordinal));
        await panel.InvokeAsync(() => incidents.Instance.ValueChanged.InvokeAsync(true));
        var customer = panel.FindComponents<MudSelect<string>>().Single(x => x.Instance.Label == "RatelDesk customer");
        await panel.InvokeAsync(() => customer.Instance.ValueChanged.InvokeAsync("customer-1"));
        await panel.Find("[data-testid='pairing-save']").ClickAsync(new());
        panel.Find("[data-testid='pairing-save-error']").TextContent.Should().Contain("customer permission changed").And.Contain("ref-save");
        Field<string>(panel.Instance, "_name").Should().Be("On call incidents");
        panel.FindAll("[data-testid='connection-card']").Should().HaveCount(1);
        var mappingId = api.LastSave!.Id;
        api.RejectSave = false;
        await panel.Find("[data-testid='pairing-save']").ClickAsync(new());
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
        panel.Find("[data-testid='pairing-address']").Input("desk.internal:8443/");
        panel.Find("[data-testid='pairing-code']").Input("ABCD-EFGH");
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
    public async Task Actual_peer_code_rejection_keeps_entered_form_and_fresh_code_pairs_without_signing_in_again()
    {
        using var transport = new PeerCodeRejectionTransport();
        Services.AddSingleton<IPairingApiClient>(new PairingApiClient(transport));
        var panel = Render<PairingConnectionsPanel>();
        panel.WaitForAssertion(() => panel.Find("[data-testid='create-connection']").HasAttribute("disabled").Should().BeFalse());
        await panel.Find("[data-testid='create-connection']").ClickAsync(new());
        const string address = "desk.internal:8443/";
        panel.Find("[data-testid='pairing-address']").Input(address);
        panel.Find("[data-testid='pairing-code']").Input("ABCD-EFGH");
        await panel.Find("[data-testid='pair-and-connect']").ClickAsync(new());
        panel.Find("[data-testid='pairing-error']").TextContent.Should()
            .Contain("Generate a new code").And.Contain("peer-code-ref").And.NotContain("sign-in");
        panel.FindAll("[data-testid='pairing-create-form']").Should().HaveCount(1);
        ((AngleSharp.Html.Dom.IHtmlInputElement)panel.Find("[data-testid='pairing-address']")).Value.Should().Be(address);
        ((AngleSharp.Html.Dom.IHtmlInputElement)panel.Find("[data-testid='pairing-code']")).Value.Should().Be("ABCD-EFGH");
        panel.Find("[data-testid='pair-and-connect']").HasAttribute("disabled").Should().BeFalse();
        var firstOperation = transport.Connects.Single().OperationId;
        panel.Find("[data-testid='pairing-code']").Input("JKLM-NPQR");
        await panel.Find("[data-testid='pair-and-connect']").ClickAsync(new());
        transport.Connects.Should().HaveCount(2);
        transport.Connects[1].Address.Should().Be("https://desk.internal:8443");
        transport.Connects[1].OperationId.Should().NotBe(firstOperation);
        panel.FindAll("[data-testid='pairing-create-form']").Should().BeEmpty();
        panel.FindAll("[data-testid='pairing-error']").Should().BeEmpty();
        panel.FindAll("[data-testid='pairing-final-form']").Should().HaveCount(1);
        panel.Markup.Should().Contain("Systems paired");
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
        var selectedCard = panel.FindAll("[data-testid='connection-card']").Single(x => x.TextContent.Contains("First mapping", StringComparison.Ordinal));
        var pendingTest = selectedCard.QuerySelector("[data-testid='connection-test']")!.ClickAsync(new());
        panel.WaitForAssertion(() => api.TestCalls.Should().Be(1));
        selectedCard = panel.FindAll("[data-testid='connection-card']").Single(x => x.TextContent.Contains("First mapping", StringComparison.Ordinal));
        await selectedCard.QuerySelector("[data-testid='connection-delete']")!.ClickAsync(new());
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

    private sealed class PeerCodeRejectionTransport : HttpMessageHandler, IHttpClientFactory
    {
        public List<PairingConnectRequest> Connects { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://api.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == PairingProtocol.AdminRoute)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<PairingConnectionDto>()) };
            if (request.Method == HttpMethod.Post && path == PairingProtocol.AdminRoute + "/pair")
            {
                Connects.Add((await request.Content!.ReadFromJsonAsync<PairingConnectRequest>(cancellationToken: ct))!);
                return Connects.Count == 1
                    ? new(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { code = "pairing_code_rejected", correlationId = "peer-code-ref", message = "The pairing code is wrong, expired or replaced." }) }
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(FakeApi.Unconfigured()) };
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/directory", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PairingDirectories([new("12", "Monitoring tenant")], [new("org-1", "Support organization")], [])) };
            throw new InvalidOperationException("Unexpected pairing administration request.");
        }
    }

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
