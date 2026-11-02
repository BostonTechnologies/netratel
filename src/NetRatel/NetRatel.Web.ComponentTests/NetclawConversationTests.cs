using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Shared.Netclaw;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NetclawConversationTests : AsyncBunitContext
{
    public NetclawConversationTests()
    {
        Services.AddLogging();
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/netclaw-conversation.js");
        JSInterop.SetupModule("./js/netclaw-chat.js");
    }

    [Fact]
    public void Recovered_terminal_count_keeps_business_outcome_uncertainty_visible_when_continuation_is_ready()
    {
        var now = DateTimeOffset.UtcNow;
        const string summary = "The remote turn ended. Historical success/failure is unavailable; verify side effects before continuing.";
        var snapshot = new InvestigationSnapshotDto(new(Guid.NewGuid(), 17, "Recovered conversation", InvestigationState.Idle, [], null, null, now, "operator"),
            0, null, [], [], [], Recovery: new(now, summary));
        var cut = Render<NetclawConversation>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.CanEditMessage, true).Add(x => x.CanInvestigate, true));
        Assert.Contains(summary, cut.Find(".chat-recovery").TextContent);
        Assert.False(cut.Find("textarea").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(InvestigationState.DeliveryUnknown)]
    [InlineData(InvestigationState.Processing)]
    [InlineData(InvestigationState.AwaitingApproval)]
    public void Shared_chat_and_ops_surface_keeps_recovery_result_and_last_attempt_visible(InvestigationState state)
    {
        var now = DateTimeOffset.UtcNow;
        const string summary = "The daemon cannot provide the exact historical outcome. Nothing was resent.";
        var snapshot = new InvestigationSnapshotDto(new(Guid.NewGuid(), 17, "Saved conversation", state, [], null, null, now, "operator"),
            0, Guid.NewGuid(), [], [], [], Recovery: new(now, summary));
        var cut = Render<NetclawConversation>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Busy, true).Add(x => x.CanInvestigate, true));
        Assert.Contains(summary, cut.Find(".chat-recovery").TextContent);
        Assert.Contains("Last recovery attempt:", cut.Find(".chat-recovery").TextContent);
        Assert.True(cut.Find("textarea").HasAttribute("disabled"));
        if (state == InvestigationState.DeliveryUnknown)
        {
            Assert.Contains("connection", cut.Find(".chat-recovery").TextContent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Recovering…", cut.Find(".chat-recovery button").TextContent);
            Assert.True(cut.Find(".chat-recovery button").HasAttribute("disabled"));
        }
    }

    [Fact]
    public void Attachment_limitation_is_visible_without_fabricating_a_recovery_attempt()
    {
        var now = DateTimeOffset.UtcNow;
        const string summary = "A terminal event alone cannot confirm this message without a turn snapshot.";
        var snapshot = new InvestigationSnapshotDto(new(Guid.NewGuid(), 17, "Attached session", InvestigationState.Processing, [], null, null, now, "operator"),
            0, Guid.NewGuid(), [], [], [], Recovery: new(null, summary));
        var cut = Render<NetclawConversation>(p => p.Add(x => x.Snapshot, snapshot));
        Assert.Contains("Session status", cut.Find(".chat-recovery").TextContent);
        Assert.Contains(summary, cut.Find(".chat-recovery").TextContent);
        Assert.DoesNotContain("Last recovery attempt:", cut.Find(".chat-recovery").TextContent);
    }

    [Fact]
    public async Task Fullscreen_stays_on_same_shared_surface_with_draft_trace_and_read_only_authority()
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        InvestigationEventDto[] events = [new(1, "tool_call", "netclaw/tool: started", null, "call-1", null, now)];
        var snapshot = new InvestigationSnapshotDto(new(id, 17, "Saved conversation", InvestigationState.Archived, [], null, null, now, "reader"), 1, null, events, [], []);
        var cut = Render<NetclawConversation>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Events, events).Add(x => x.Message, "retained draft").Add(x => x.Error, "Retained validation error"));
        var instance = cut.Instance;
        Assert.Empty(cut.FindAll("[aria-label='Archive chat']"));
        Assert.Contains("netclaw/tool: started", cut.Find("summary").TextContent);
        Assert.Contains("Details were not recorded", cut.Markup);
        var trace = cut.FindComponent<NetclawTraceRow>().Instance;
        await cut.Find("[data-testid=conversation-fullscreen]").ClickAsync(new());
        Assert.Same(instance, cut.Instance); Assert.Same(trace, cut.FindComponent<NetclawTraceRow>().Instance);
        Assert.Equal("retained draft", cut.Instance.Message);
        Assert.Contains("Retained validation error", cut.Find("[role=alert]").TextContent);
        Assert.NotEmpty(cut.FindAll("[aria-label='Restore conversation']"));
        await cut.InvokeAsync(() => cut.Instance.RestoreConversation());
        Assert.NotEmpty(cut.FindAll("[aria-label='Maximise conversation']"));
        Assert.Same(trace, cut.FindComponent<NetclawTraceRow>().Instance);
    }
}
