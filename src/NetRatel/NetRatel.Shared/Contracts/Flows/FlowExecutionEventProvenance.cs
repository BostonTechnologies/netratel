namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Source tags are evidence labels; only immutable server-owned outbox facts establish trust.</summary>
public static class FlowExecutionEventProvenance
{
    public const string JobTerminalReady = "job-terminal-ready";
    public const string ScriptTerminalCommit = "script-terminal-commit";
    public const string RequestStateCommit = "request-state-commit";
    public const string LegacyAuthoritativeTransition = "authoritative-transition";

    public static bool IsNative(FlowExecutionEventDto value) => value.SourceKind switch
    {
        FlowExecutionSourceKind.Job => value.Provenance == JobTerminalReady,
        FlowExecutionSourceKind.Script => value.Provenance == ScriptTerminalCommit,
        FlowExecutionSourceKind.Request => value.Provenance == RequestStateCommit,
        _ => false
    };
}
