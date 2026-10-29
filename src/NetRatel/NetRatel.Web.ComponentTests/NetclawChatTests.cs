using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Pages.AIOps;
using NetRatel.Web.Components.Shared.Netclaw;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NetclawChatTests : AsyncBunitContext
{
    private readonly ChatApi api = new();

    public NetclawChatTests()
    {
        Services.AddSingleton<INetclawApiService>(api);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/netclaw-chat.js");
        JSInterop.SetupModule("./js/operator-time.js").Setup<string>("getTimeZone").SetResult("Africa/Johannesburg");
    }

    [Theory]
    [InlineData("Help me write a script to report disk usage.", "Help me write a script to report disk usage.")]
    [InlineData("\tGet-Process\t-Name worker\r\n  Sort-Object CPU", "Get-Process -Name worker")]
    public async Task First_send_creates_context_free_chat_and_retries_the_same_message_after_interrupted_admission(string question, string title)
    {
        api.FailFirstSend = true;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/ai/chat?tenantId=17");
        var cut = Render<Chat>();
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=chat-message]")));
        Assert.Empty(api.Creates);
        Assert.Equal(0, api.ClientReads);
        cut.Find("[data-testid=chat-message]").Input(question);
        await cut.Find("[data-testid=chat-send]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("Synthetic interrupted send", cut.Markup));
        var created = Assert.Single(api.Creates);
        Assert.Equal(title, created.Title);
        Assert.True(cut.Find("textarea").HasAttribute("disabled"));
        Assert.Equal(question.ReplaceLineEndings("\n"), cut.Find("textarea").GetAttribute("value"));
        await cut.Find("[data-testid=chat-send]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("Message saved", cut.Markup));
        Assert.Single(api.Creates);
        Assert.Equal(2, api.Messages.Count);
        Assert.Equal(api.Messages[0], api.Messages[1]);
        Assert.Equal(question.Trim(), api.Messages[0].Text);
        Assert.Equal(0, api.ClientReads);
        Assert.Contains($"chat={api.Id:D}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Validation_preserves_editable_question_and_a_disabled_tenant_connection_does_not_create_a_session()
    {
        api.RejectValidation = true;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/ai/chat?tenantId=17");
        var cut = Render<Chat>();
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=chat-message]")));
        cut.Find("textarea").Input("Keep this draft");
        await cut.Find("[data-testid=chat-send]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("Synthetic validation error", cut.Markup));
        Assert.Equal("Keep this draft", cut.Find("textarea").GetAttribute("value"));
        Assert.False(cut.Find("textarea").HasAttribute("disabled"));

        var createdCount = api.Creates.Count;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/ai/chat?tenantId=18");
        await cut.WaitForAssertionAsync(() => Assert.Contains("Connect with Netclaw", cut.Markup));
        Assert.Empty(cut.FindAll("textarea"));
        Assert.Equal(createdCount, api.Creates.Count);
        Assert.Contains("tab=netclaw&amp;tenantId=18", cut.Markup);
        Assert.DoesNotContain("Keep this draft", cut.Markup);
    }

    [Fact]
    public async Task Tenant_navigation_fences_a_delayed_history_read_and_an_investigation_link_is_not_shown_as_chat()
    {
        var delayed = new TaskCompletionSource<InvestigationSnapshotDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.DelayedSnapshot = delayed.Task;
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/ai/chat?tenantId=17&chat={api.Id:D}");
        var cut = Render<Chat>();
        await cut.WaitForAssertionAsync(() => Assert.Equal(1, api.SnapshotReads));
        navigation.NavigateTo("/ai/chat?tenantId=18");
        await cut.WaitForAssertionAsync(() => Assert.Contains("Connect with Netclaw", cut.Markup));
        delayed.SetResult(api.Snapshot("Previous tenant's private response"));
        await cut.WaitForAssertionAsync(() => Assert.DoesNotContain("Previous tenant's private response", cut.Markup));

        api.DelayedSnapshot = null;
        api.ReturnInvestigation = true;
        navigation.NavigateTo($"/ai/chat?tenantId=17&chat={api.Id:D}");
        await cut.WaitForAssertionAsync(() => Assert.Contains("This conversation is an investigation", cut.Markup));
        Assert.DoesNotContain("Saved assistant response", cut.Markup);
    }

    [Fact]
    public async Task Approval_decisions_require_the_saved_request_details_to_be_loaded()
    {
        api.ApprovalMode = true;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/ai/chat?tenantId=17&chat={api.Id:D}");
        var cut = Render<Chat>();
        await cut.WaitForAssertionAsync(() => Assert.Contains("Load the saved request details", cut.Markup));
        Assert.True(cut.FindAll("button").Single(x => x.TextContent == "Allow").HasAttribute("disabled"));
        Assert.Empty(api.ApprovalChoices);
        await cut.FindAll("button").Single(x => x.TextContent == "Load request details").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Contains("Run the reviewed disk report?", cut.Markup));
        Assert.False(cut.FindAll("button").Single(x => x.TextContent == "Allow").HasAttribute("disabled"));
        await cut.FindAll("button").Single(x => x.TextContent == "Allow").ClickAsync(new());
        Assert.Equal("allow", Assert.Single(api.ApprovalChoices));
    }

    [Fact]
    public async Task Assistant_code_is_readable_copyable_and_never_rendered_as_html()
    {
        const string code = "<script>alert('untrusted')</script>";
        var text = $"## Example\n\nUse **careful review** and `preview`.\n\n```html\n{code}\n```";
        var cut = Render<ChatMessageContent>(parameters => parameters.Add(x => x.Text, text));
        Assert.Empty(cut.FindAll("script"));
        Assert.Equal(code, cut.Find("pre code").TextContent);
        Assert.Equal("careful review", cut.Find("strong").TextContent);
        await cut.FindAll("button").Single(x => x.TextContent == "Copy code").ClickAsync(new());
        var invocation = Assert.Single(JSInterop.Invocations["navigator.clipboard.writeText"]);
        Assert.Equal(code, invocation.Arguments[0]);
        Assert.Contains("Copied", cut.Markup);
    }

    private sealed class ChatApi : INetclawApiService
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool FailFirstSend, RejectValidation, ReturnInvestigation, ApprovalMode;
        public int ClientReads, SnapshotReads;
        public Task<InvestigationSnapshotDto>? DelayedSnapshot;
        public List<GeneralChatCreateRequest> Creates { get; } = [];
        public List<InvestigationMessageRequest> Messages { get; } = [];
        public List<string> ApprovalChoices { get; } = [];
        private InvestigationMessageRequest? accepted;
        private InvestigationSummaryDto Summary => new(Id, 17, Creates.LastOrDefault()?.Title ?? "Saved chat",
            ApprovalMode ? InvestigationState.AwaitingApproval : accepted is null ? InvestigationState.Idle : InvestigationState.Queued,
            ReturnInvestigation ? [Guid.NewGuid()] : [], null, null, DateTimeOffset.UtcNow, "operator");

        public InvestigationSnapshotDto Snapshot(string text = "Saved assistant response", long after = 0)
        {
            var events = new List<InvestigationEventDto> { new(1, "assistant", text, null, null, null, DateTimeOffset.UtcNow) };
            if (accepted is not null) events.Add(new(2, "operator", accepted.Text, "operator", null, null, DateTimeOffset.UtcNow, accepted.MessageId));
            if (ApprovalMode && after > 0) events.Add(new(2, "approval_request", "Run the reviewed disk report?", null, "disk-report", null, DateTimeOffset.UtcNow));
            return new(Summary, ApprovalMode ? 2 : events.Count, accepted?.MessageId, events.Where(x => x.Sequence > after).ToArray(), [],
                ApprovalMode ? [new("disk-report", [new("allow", "Allow"), new("deny", "Deny")])] : []);
        }

        public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetclawTenantDto>>([new(17, "Operations", true, true), new(18, "Second tenant", true, true)]);
        public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) => Task.FromResult(
            new NetclawConnectionDto(Guid.Empty, tenantId, 1, tenantId == 17, "https://daemon.example", tenantId == 17, DateTimeOffset.UtcNow, true, null));
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListChatsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InvestigationSummaryDto>>(tenantId == 17 && Creates.Count > 0 ? [Summary] : []);
        public Task<InvestigationSnapshotDto> CreateChatAsync(int tenantId, GeneralChatCreateRequest request, CancellationToken ct = default)
        {
            Creates.Add(request);
            return Task.FromResult(new InvestigationSnapshotDto(Summary, 0, null, [], [], []));
        }
        public Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default)
        {
            SnapshotReads++;
            return DelayedSnapshot ?? Task.FromResult(Snapshot(after: after));
        }
        public Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default)
        {
            Messages.Add(request);
            if (RejectValidation) throw new HttpRequestException("Synthetic validation error", null, HttpStatusCode.BadRequest);
            if (FailFirstSend && Messages.Count == 1) throw new HttpRequestException("Synthetic interrupted send", null, HttpStatusCode.BadGateway);
            accepted = request;
            return Task.FromResult(Snapshot());
        }
        public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default)
        { ClientReads++; throw new NotSupportedException("General chat must not require client context."); }
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default)
        {
            ApprovalChoices.Add(key); ApprovalMode = false;
            return Task.FromResult(new InvestigationSnapshotDto(Summary, 2, null, [], [], []));
        }
        public Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
