using System.ComponentModel;
using ModelContextProtocol.Server;

namespace NetRatel.Mcp.Core.Prompts;

/// <summary>
/// Transport-aware prompt guidance that only describes tools available to the current MCP host.
/// </summary>
[McpServerPromptType]
public sealed class NetRatelPrompts(NetRatelMcpHostContext hostContext)
{
    [McpServerPrompt(Name = "inspect_client"), Description("Guide a policy-admitted, read-only inspection of one NetRatel client.")]
    public string InspectClient([Description("Client identity or name to inspect")] string client) =>
        IsHttp
            ? HttpReadGuidance($"Inspect known NetRatel client '{client}' read-only with netratel_clients telemetry, presence, binding, or update_attempts. First resolve the exact tenant and AgentId through netratel_access; the required observe scope and target policy are evaluated server-side. V1 client CRUD and latency routes are retired.")
            : $"Inspect known NetRatel client '{client}' read-only with netratel_clients telemetry. Use netratel_health if connectivity is relevant. Do not run commands or change client state.";

    [McpServerPrompt(Name = "diagnose_job_run"), Description("Guide a policy-admitted, read-only diagnosis of one NetRatel job run.")]
    public string DiagnoseJobRun([Description("Job run identifier")] string jobRunId) =>
        IsHttp
            ? HttpReadGuidance($"Diagnose NetRatel job run '{jobRunId}' read-only. Resolve the exact tenant and AgentId, then use netratel_job_runs get, steps, or bounded redacted logs. Summarize evidence, preserve copy-safe IDs, and propose a separately previewed next action only when the caller has the required scope and policy.")
            : $"Diagnose job run '{jobRunId}' read-only. Use netratel_job_runs get, then netratel_job_runs logs for the relevant step ordinal. Summarize evidence and propose next actions. Do not use a local stdio context to bypass API policy, confirmation, or ownership controls.";

    [McpServerPrompt(Name = "controlled_remote_execution"), Description("Guide a policy-admitted, previewed and confirmed remote-operation workflow.")]
    public string ControlledRemoteExecution() =>
        IsHttp
            ? ProductionWorkflowGuidance("Resolve the exact tenant and AgentId, then use netratel_access evaluate for the intended operation. For one bounded command use netratel_commands; for a typed task use netratel_tasks; for an approved script or job run use their owned V2 lifecycle. A preview must return opaque plan credentials before one unchanged confirmed request. Inspect only caller-owned, bounded redacted task, command, script, terminal, or run evidence; cancel or clean up through the same owned lifecycle when applicable.")
            : "Resolve the exact target and use only catalogued, caller-owned operator contracts. Obtain a preview before a confirmed mutation, preserve its opaque plan credentials, and inspect only bounded redacted evidence. Do not use a local stdio context to bypass target policy, ownership, confirmation, idempotency, or audit controls.";

    [McpServerPrompt(Name = "create_workflow"), Description("Guide a safe, owned policy-admitted V2 workflow and its approval points.")]
    public string CreateWorkflow() =>
        IsHttp
            ? ProductionWorkflowGuidance("Plan an owned workflow from read evidence first: validate or preview an exact netratel_scripts revision, create or update an owned netratel_jobs definition with ETags, then preview and confirm one netratel_job_runs start or typed netratel_tasks action. Each write uses its matching closed schema, opaque plan and idempotency credentials, and immutable accepted audit. Keep onboarding separate: it remains a dedicated credential-issuance workflow and never returns a raw code after creation.")
            : "Plan only catalogued, owned workflows. Keep target policy, scope, preview/confirmation, idempotency, ETags, and immutable audit at each mutation boundary. Do not use a workflow to bypass arbitrary-command, raw-file, secret, onboarding, or Remote Support safeguards.";

    [McpServerPrompt(Name = "manage_operator_access"), Description("Guide a bounded operator-policy inspection or administration workflow.")]
    public string ManageOperatorAccess() =>
        IsHttp
            ? ProductionWorkflowGuidance("Use netratel_policy to inspect policies, target matches, expiry, and accepted-operation audit first. Only a delegated PolicyAdministrator with netratel.mcp.admin may preview and confirm one create, replace, disable, or revoke action. Policy changes cannot self-escalate the delegated administrator; retain the returned version and correlation ID for a safe next step.")
            : "Use netratel_policy only with a delegated PolicyAdministrator context. Inspect first, then preview and confirm one versioned policy action. Never add a policy that grants the caller new authority or exposes a secret.";

    [McpServerPrompt(Name = "safe_file_operation"), Description("Guide a bounded, policy-admitted file operation.")]
    public string SafeFileOperation() =>
        IsHttp
            ? ProductionWorkflowGuidance("Resolve the exact target, then use netratel_files browse or read only within server-enforced canonical read roots. For a write_text or upload operation, obtain its preview and submit the unchanged confirmed request against an explicit policy write root. Do not place secret values in file content, logs, or the confirmation request; inspect only the bounded result.")
            : "Use only catalogued file operations and never bypass canonical-root, confirmation, or secret-handling constraints.";

    [McpServerPrompt(Name = "safe_terminal_operation"), Description("Guide a bounded, caller-owned terminal session workflow.")]
    public string SafeTerminalOperation() =>
        IsHttp
            ? ProductionWorkflowGuidance("Check netratel_terminal availability for the exact target. Preview and confirm open, then use the caller-owned session ID for bounded stream_window, input, resize, diagnostics, and close. Poll get until opened before sending input. Read stream_window after input; carry its decimal-string nextSequence as afterSequence on later calls, page while hasMore, and disclose gap as missing output. Replay is bounded and transient. Respect the policy shell, working-directory, output, idle, and lifetime limits. Terminal bytes are not audit evidence and secret values must not be entered.")
            : "Use only catalogued terminal session operations with the exact target, owned session ID, bounded controls, and no secret input.";

    [McpServerPrompt(Name = "onboard_client"), Description("Guide a policy-admitted, credential-safe client onboarding workflow.")]
    public string OnboardClient() =>
        IsHttp
            ? ProductionWorkflowGuidance("Inspect the exact onboarding collateral and existing enrollment metadata before requesting an enrollment action. The current typed onboarding contract is credential issuance: it requires onboarding scope, target policy, a preview, confirmation, idempotency, and audit. Treat a raw enrollment code as immediate-use material only: do not echo it, persist it, place it in logs, or expect later reads to return it. Revoke with the same owned lifecycle when required.")
            : "Use the dedicated onboarding contract only. Treat enrollment codes as immediate-use material and never repeat them in output, logs, or stored workflow state.";

    private bool IsHttp => hostContext.Transport == NetRatelMcpTransport.StreamableHttp;

    private static string HttpReadGuidance(string request) =>
        $"{request} First read netratel://capabilities, then use only catalogued read operations. Do not invoke a mutation, stdio-only local configuration, or Remote Support operation during this read-only diagnosis.";

    private static string ProductionWorkflowGuidance(string request) =>
        $"{request} First read netratel://capabilities and verify the required OAuth scope, delegated identity, exact target, and current target policy. Do not invoke a mutation until its no-write preview returns opaque credentials for the unchanged request. HTTP never accepts raw routes, inbound bearer forwarding, plaintext secrets, unrestricted commands, direct database access, or Remote Support operations.";
}
