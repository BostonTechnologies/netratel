using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Pages.AIOps;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NetclawWorkspaceTests : AsyncBunitContext
{
    private readonly WorkspaceApi api = new();
    public NetclawWorkspaceTests() { Services.AddSingleton<INetclawApiService>(api); }

    [Fact]
    public async Task Pairing_uses_expected_revision_and_clears_code_when_saved_verification_fails()
    {
        var cut = Render<NetclawConnection>();
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=netclaw-address]")));
        cut.Find("[data-testid=netclaw-address]").Change("https://daemon.example");
        cut.Find("[data-testid=netclaw-pairing-code]").Change("one-time-secret-code");
        await cut.Find("[data-testid=netclaw-pair-connect]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("The connection is saved", cut.Markup));
        Assert.Equal(0, Assert.Single(api.PairRequests).ExpectedRevision);
        Assert.Equal("one-time-secret-code", api.PairRequests[0].PairingCode);
        Assert.DoesNotContain("one-time-secret-code", cut.Markup);
        Assert.DoesNotContain("netclaw-pairing-code", cut.Markup);
        Assert.Contains("Saved Netclaw connection", cut.Markup);
    }

    [Fact]
    public async Task A_failed_send_preserves_transcript_and_reuses_exact_message_identity_until_admission_is_confirmed()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/ai/ops?tenantId=17&investigation={api.Id}");
        var cut = Render<Tools>();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Existing evidence", cut.Markup));
        Assert.Empty(cut.FindAll("script"));
        cut.Find("textarea").Change("Investigate the selected worker");
        await cut.FindAll("button").Single(x => x.TextContent == "Send").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("Synthetic interrupted request", cut.Markup));
        Assert.Contains("Existing evidence", cut.Markup); Assert.True(cut.Find("textarea").HasAttribute("disabled"));
        Assert.Single(api.Messages);
        await cut.FindAll("button").Single(x => x.TextContent == "Retry same request").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("Request saved", cut.Markup));
        Assert.Equal(2, api.Messages.Count); Assert.Equal(api.Messages[0], api.Messages[1]);
        Assert.Contains("Investigate the selected worker", cut.Markup);
        Assert.DoesNotContain("Synthetic interrupted request", cut.Markup);
    }

    [Fact]
    public async Task Read_only_workspace_shows_saved_evidence_without_exposing_send_or_approval_controls()
    {
        api.CanInvestigate = false;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/ai/ops?tenantId=17&investigation={api.Id}");
        var cut = Render<Tools>();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Existing evidence", cut.Markup));
        Assert.True(cut.Find("[data-testid=ai-new-investigation]").HasAttribute("disabled"));
        Assert.True(cut.Find("textarea").HasAttribute("disabled"));
        Assert.True(cut.FindAll("button").Single(x => x.TextContent == "Send").HasAttribute("disabled"));
        Assert.Empty(api.Messages);
    }

    private sealed class WorkspaceApi : INetclawApiService
    {
        public Guid Id { get; } = Guid.NewGuid(); private readonly Guid agent = Guid.NewGuid();
        public bool CanInvestigate = true; public List<InvestigationMessageRequest> Messages { get; } = [];
        public List<NetclawPairRequest> PairRequests { get; } = [];
        private InvestigationMessageRequest? accepted;
        private InvestigationSummaryDto Summary => new(Id, 17, "Worker health", accepted is null ? InvestigationState.Idle : InvestigationState.Queued, [agent], null, null, DateTimeOffset.UtcNow, "operator");
        private InvestigationClientDto Client => new(agent, "worker-01", "Linux worker", ["production"], DateTimeOffset.UtcNow);
        private InvestigationSnapshotDto Snapshot(long after = 0)
        {
            var events = new List<InvestigationEventDto> { new(1, "assistant", "Existing evidence <script>alert('untrusted')</script>", null, null, null, DateTimeOffset.UtcNow) };
            if (accepted is not null) events.Add(new(2, "operator", accepted.Text, "operator", null, null, DateTimeOffset.UtcNow, accepted.MessageId));
            return new(Summary, events.Count, accepted?.MessageId, events.Where(x => x.Sequence > after).ToArray(), [Client], []);
        }
        public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NetclawTenantDto>>([new(17, "Operations", CanInvestigate, true)]);
        public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) => Task.FromResult(new NetclawConnectionDto(Guid.Empty, 17, 0, false, null, false, null, null, null));
        public Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default)
        { PairRequests.Add(request); return Task.FromResult(new NetclawTestResult(false, "authenticated-session-unavailable", new(Guid.NewGuid(), 17, 1, true, request.Address, true, DateTimeOffset.UtcNow, false, "authenticated-session-unavailable"))); }
        public Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InvestigationClientDto>>([Client]);
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InvestigationSummaryDto>>([Summary]);
        public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default) => Task.FromResult(Snapshot(after));
        public Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default)
        {
            Messages.Add(request);
            if (Messages.Count == 1) throw new HttpRequestException("Synthetic interrupted request", null, HttpStatusCode.BadGateway);
            accepted = request; return Task.FromResult(Snapshot());
        }
        public Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
