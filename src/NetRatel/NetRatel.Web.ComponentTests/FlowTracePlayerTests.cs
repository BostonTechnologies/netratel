using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowTracePlayerTests : AsyncBunitContext
{
    public FlowTracePlayerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/flows-playback.js").Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Playback_labels_simulated_data_missing_output_and_presentation_controls()
    {
        var node = Guid.NewGuid();
        var trace = new FlowTraceDto(1, "graph-a", FlowTraceProvenance.Simulated,
            [new(0, node, "Synthetic investigation", FlowTraceStepStatus.Planned, "sample-result-unavailable",
                FlowTraceProvenance.Simulated, new(FlowTraceDataState.Present)
                { Value = JsonSerializer.SerializeToElement(new { ruleName = "Synthetic CPU threshold", value = 94 }) },
                new(FlowTraceDataState.Missing))]);
        var cut = Render<FlowTracePlayer>(p => p.Add(x => x.Trace, trace).Add(x => x.ContextKey, "tenant-17-draft-a")
            .Add(x => x.ExpectedConfigurationHash, "graph-a"));
        cut.Markup.Should().Contain("Simulated playback").And.Contain("no actions run").And.Contain("Synthetic CPU threshold")
            .And.Contain("Data unavailable").And.Contain("explicit sample result").And.Contain("Not captured");
        cut.FindAll("[data-trace-action]").Should().HaveCount(5);
        cut.FindAll("[data-trace-panel]").Should().ContainSingle();
        cut.Find("[data-trace-panel]").GetAttribute("data-node-id").Should().Be(node.ToString());
        cut.FindAll("[data-testid=flow-trace-stale]").Should().BeEmpty();
    }

    [Fact]
    public void A_trace_for_another_revision_never_presents_its_node_snapshots_as_current_data()
    {
        var trace = new FlowTraceDto(1, "old-graph", FlowTraceProvenance.Recorded,
            [new(0, Guid.NewGuid(), "Old graph node", FlowTraceStepStatus.Completed, "succeeded", FlowTraceProvenance.Recorded,
                new(FlowTraceDataState.Present) { Value = JsonSerializer.SerializeToElement("old recorded facts") },
                new(FlowTraceDataState.Empty) { Value = JsonSerializer.SerializeToElement("") })]);
        var cut = Render<FlowTracePlayer>(p => p.Add(x => x.Trace, trace).Add(x => x.ContextKey, "tenant-91-draft-b")
            .Add(x => x.ExpectedConfigurationHash, "new-graph"));
        cut.Find("[data-testid=flow-trace-stale]").TextContent.Should().Contain("different graph revision");
        cut.Markup.Should().NotContain("Old graph node").And.NotContain("old recorded facts");
        cut.FindAll("[data-trace-panel]").Should().BeEmpty();
        cut.FindAll("[data-trace-action]").Should().BeEmpty();
    }

    [Fact]
    public void Older_run_without_snapshots_reports_unavailable_instead_of_reconstructed_history()
    {
        var cut = Render<FlowTracePlayer>(p => p.Add(x => x.ContextKey, "tenant-17-run-a"));
        cut.Find("[data-testid=flow-trace-unavailable]").TextContent.Should().Contain("no captured node snapshots");
        cut.FindAll("[data-trace-panel]").Should().BeEmpty();
    }
    [Fact]
    public void Unsupported_trace_format_and_mismatched_graph_schema_never_expose_node_data()
    {
        var trace = new FlowTraceDto(2, "same-hash", FlowTraceProvenance.Recorded,
            [new(0, Guid.NewGuid(), "Mismatched node", FlowTraceStepStatus.Completed, "succeeded", FlowTraceProvenance.Recorded,
                new(FlowTraceDataState.Present) { Value = JsonSerializer.SerializeToElement("Mismatched snapshot") },
                new(FlowTraceDataState.Missing))]);
        var cut = Render<FlowTracePlayer>(p => p.Add(x => x.Trace, trace).Add(x => x.ContextKey, "immutable-run")
            .Add(x => x.ExpectedConfigurationHash, "same-hash").Add(x => x.ExpectedGraphSchemaVersion, 1));
        cut.FindAll("[data-testid=flow-trace-stale]").Should().ContainSingle();
        cut.Markup.Should().NotContain("Mismatched snapshot").And.NotContain("Mismatched node");
        cut.Render(p => p.Add(x => x.Trace, trace with { SchemaVersion = 2 }).Add(x => x.ContextKey, "future-format")
            .Add(x => x.ExpectedGraphSchemaVersion, 2));
        cut.Find("[data-testid=flow-trace-unsupported]").TextContent.Should().Contain("trace format is unsupported");
        cut.FindAll("[data-trace-panel]").Should().BeEmpty();
    }

    [Fact]
    public async Task A_late_module_import_is_disposed_without_attaching_to_a_new_context()
    {
        var runtime = new DeferredImportRuntime();
        Services.AddSingleton<IJSRuntime>(runtime);
        var first = new FlowTraceDto(1, "same-graph", FlowTraceProvenance.Recorded,
            [new(0, Guid.NewGuid(), "Old recorded node", FlowTraceStepStatus.Completed, "event",
                FlowTraceProvenance.Recorded, new(FlowTraceDataState.Missing), new(FlowTraceDataState.Missing))]);
        var latest = first with { Steps = [first.Steps[0] with { NodeName = "Current recorded node" }] };
        var cut = Render<FlowTracePlayer>(p => p.Add(x => x.Trace, first).Add(x => x.ContextKey, "old-run"));
        await cut.WaitForAssertionAsync(() => runtime.Imports.Count.Should().Be(1));
        cut.Render(p => p.Add(x => x.Trace, latest).Add(x => x.ContextKey, "current-run"));
        await cut.WaitForAssertionAsync(() => runtime.Imports.Count.Should().Be(2));
        var pending = runtime.Imports.ToArray();
        var currentModule = new TrackedJavaScriptReference();
        pending[1].SetResult(currentModule);
        await cut.WaitForAssertionAsync(() => currentModule.AttachCalls.Should().Be(1));

        var staleModule = new TrackedJavaScriptReference();
        pending[0].SetResult(staleModule);
        await cut.WaitForAssertionAsync(() => staleModule.DisposeCalls.Should().Be(1));
        staleModule.AttachCalls.Should().Be(0);
        currentModule.DisposeCalls.Should().Be(0);
        cut.Markup.Should().Contain("Current recorded node").And.NotContain("Old recorded node");
    }

    private sealed class DeferredImportRuntime : IJSRuntime
    {
        public ConcurrentQueue<TaskCompletionSource<IJSObjectReference>> Imports { get; } = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import") throw new InvalidOperationException("Only a module import is expected.");
            var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
            Imports.Enqueue(pending);
            return Resolve<TValue>(pending.Task);
        }
        private static async ValueTask<TValue> Resolve<TValue>(Task<IJSObjectReference> pending) =>
            (TValue)(object)await pending;
    }

    private sealed class TrackedJavaScriptReference : IJSObjectReference
    {
        public int AttachCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "attach") return ValueTask.FromResult(default(TValue)!);
            AttachCalls++;
            return ValueTask.FromResult((TValue)(object)new TrackedJavaScriptReference());
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }

}
