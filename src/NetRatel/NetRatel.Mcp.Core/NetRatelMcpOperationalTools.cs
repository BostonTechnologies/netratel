using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace NetRatel.Mcp.Core;

/// <summary>
/// The bounded, API-backed operational surface shared by MCP hosts. Read
/// operations are available directly; Development mutations are included only
/// through dedicated API adapters that enforce target eligibility and explicit
/// confirmation independently of this client.
/// </summary>
[McpServerToolType]
public sealed partial class NetRatelMcpOperationalTools
{
    private const ulong JavaScriptSafeIntegerMaximum = 9_007_199_254_740_991;
    private const long JavaScriptSafeIntegerMinimum = -9_007_199_254_740_991;
    private const int MaximumUploadBase64Characters = 87384;
    private readonly INetRatelMcpApiClient client;
    private readonly NetRatelMcpHostContext? hostContext;

    /// <summary>
    /// Compatibility constructor for callers that do not yet supply an MCP
    /// host context. Such callers retain the Development-compatible endpoint
    /// selection until they opt into the environment-aware constructor.
    /// </summary>
    public NetRatelMcpOperationalTools(INetRatelMcpApiClient client)
        : this(client, null)
    {
    }

    /// <summary>
    /// Creates the shared operation handler with its immutable host context.
    /// HTTP dependency injection supplies this overload so policy-admitted V2
    /// catalog operations can select their explicit operator route.
    /// </summary>
    public NetRatelMcpOperationalTools(INetRatelMcpApiClient client, NetRatelMcpHostContext? hostContext)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.hostContext = hostContext;
    }

    [McpServerTool(UseStructuredContent = true), Description("Read NetRatel AI-agent authentication status. Operation must be status.")]
    public Task<NetRatelToolResponse> netratel_auth(string operation = "status", CancellationToken cancellationToken = default)
        => GetAsync("netratel_auth", operation, ["status"], "/api/v2/mcp/operator/auth/status", cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read authenticated NetRatel health. Operation must be get.")]
    public Task<NetRatelToolResponse> netratel_health(string operation = "get", CancellationToken cancellationToken = default)
        => GetAsync("netratel_health", operation, ["get"], "/health/ready", cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read safe NetRatel system metadata. Operation must be version.")]
    public Task<NetRatelToolResponse> netratel_system(string operation = "version", CancellationToken cancellationToken = default)
        => GetAsync("netratel_system", operation, ["version"], "/api/v1/system/version", cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Inspect the caller's effective operator access without performing a target operation. Operations are whoami, target, effective, and evaluate. evaluate requires one catalogued target tool and operation inside request.")]
    public Task<NetRatelToolResponse> netratel_access(string operation, JsonElement? request = null, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "whoami" when request is null => GetAsync("netratel_access", operation, AccessOperations, "/api/v2/mcp/operator/access/whoami", cancellationToken),
            "whoami" => Task.FromResult(Invalid("whoami does not accept a request object.")),
            "target" => AccessTargetAsync(operation, request, cancellationToken),
            "effective" => AccessTargetAsync(operation, request, cancellationToken),
            "evaluate" => AccessEvaluateAsync(request, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_access", operation, AccessOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect bounded operator-policy, target-profile, immutable audit metadata, policy selector/classification matches, and one exact access decision for the signed delegated administrator. Policy and target-profile mutations require target-bound previews and explicit confirmed idempotent requests. Operations are policies, policy, change_audits, accepted_audits, target, matches, evaluate, preview_create, confirm_create, preview_replace, confirm_replace, preview_disable, confirm_disable, preview_revoke, confirm_revoke, preview_target_profile, and confirm_target_profile. Revocation preserves policy evidence but is terminal for authorization. Evaluation cannot simulate another principal or an unpersisted policy draft. Replacement cannot move a policy between environments or target selectors. Every operation requires the PolicyAdministrator role and the netratel.mcp.admin OAuth scope.")]
    public Task<NetRatelToolResponse> netratel_policy(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "policies" => PolicyListAsync(request, cancellationToken),
            "policy" => PolicyByIdAsync(request, cancellationToken),
            "change_audits" => PolicyChangeAuditsAsync(request, cancellationToken),
            "accepted_audits" => PolicyAcceptedAuditsAsync(request, cancellationToken),
            "target" => PolicyTargetAsync(request, cancellationToken),
            "matches" => PolicyMatchesAsync(request, cancellationToken),
            "evaluate" => PolicyEvaluateAsync(request, cancellationToken),
            "preview_create" => PolicyCreatePreviewAsync(request, cancellationToken),
            "confirm_create" => PolicyCreateConfirmAsync(request, confirm, cancellationToken),
            "preview_replace" => PolicyReplacePreviewAsync(request, cancellationToken),
            "confirm_replace" => PolicyReplaceConfirmAsync(request, confirm, cancellationToken),
            "preview_disable" => PolicyDisablePreviewAsync(request, cancellationToken),
            "confirm_disable" => PolicyDisableConfirmAsync(request, confirm, cancellationToken),
            "preview_revoke" => PolicyRevokePreviewAsync(request, cancellationToken),
            "confirm_revoke" => PolicyRevokeConfirmAsync(request, confirm, cancellationToken),
            "preview_target_profile" => TargetProfilePreviewAsync(request, cancellationToken),
            "confirm_target_profile" => TargetProfileConfirmAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_policy", operation, PolicyOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect source-backed clients. Development retains compatibility presence, binding, telemetry, and update_attempts. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) requires an exact persisted tenantId and agentId with current operator policy; get returns durable administrative metadata plus a curated display-name, hostname, operating-system, and architecture projection, presence and capabilities return bounded gateway facts, binding returns optional migration provenance (hasBinding=false is normal for a native V2 client; no bridge is needed for current V2 operations), telemetry returns one bounded current V2 gateway snapshot, update_attempts returns bounded redacted update lifecycle facts, and update_metadata returns durable tenant and client update policy state. V2 ping first returns a no-write server plan, then requires unchanged execute-scoped confirmation before it reaches the V2 control gateway. V2 software_update can only resume an already suspended target's automatic-update eligibility; it never selects, uploads, or directly pushes a release. Disable, enable, and delete use a distinct admin-scoped server preview, opaque confirmation, idempotency, and V2 lifecycle authority; delete decommissions credentials and retains historical evidence. No operation invokes a legacy client-identity route.")]
    public Task<NetRatelToolResponse> netratel_clients(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        return operation switch
        {
            "presence" when UsesProductionOperatorRoutes => ProductionClientReadAsync("presence", request, cancellationToken),
            "binding" when UsesProductionOperatorRoutes => ProductionClientReadAsync("binding", request, cancellationToken),
            "get" or "capabilities" or "telemetry" or "update_attempts" or "update_metadata" when UsesProductionOperatorRoutes => ProductionClientReadAsync(operation, request, cancellationToken),
            "preview_ping" when UsesProductionOperatorRoutes => ProductionClientPingAsync(request, preview: true, confirm: false, cancellationToken),
            "ping" when UsesProductionOperatorRoutes => ProductionClientPingAsync(request, preview: false, confirm, cancellationToken),
            "preview_software_update" when UsesProductionOperatorRoutes => ProductionClientSoftwareUpdateAsync(request, preview: true, confirm: false, cancellationToken),
            "software_update" when UsesProductionOperatorRoutes => ProductionClientSoftwareUpdateAsync(request, preview: false, confirm, cancellationToken),
            "preview_disable" when UsesProductionOperatorRoutes => ProductionClientLifecycleAsync("disable", request, preview: true, confirm: false, cancellationToken),
            "disable" when UsesProductionOperatorRoutes => ProductionClientLifecycleAsync("disable", request, preview: false, confirm, cancellationToken),
            "preview_enable" when UsesProductionOperatorRoutes => ProductionClientLifecycleAsync("enable", request, preview: true, confirm: false, cancellationToken),
            "enable" when UsesProductionOperatorRoutes => ProductionClientLifecycleAsync("enable", request, preview: false, confirm, cancellationToken),
            "preview_delete" when UsesProductionOperatorRoutes => ProductionClientLifecycleAsync("delete", request, preview: true, confirm: false, cancellationToken),
            "delete" when UsesProductionOperatorRoutes => ProductionClientLifecycleAsync("delete", request, preview: false, confirm, cancellationToken),
            "presence" => ClientPresenceAsync(request, cancellationToken),
            "binding" => ClientBindingAsync(request, cancellationToken),
            "update_attempts" => ClientUpdateAttemptsAsync(request, cancellationToken),
            "telemetry" => ClientTelemetryAsync(request, payload, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_clients", operation, UsesProductionOperatorRoutes ? ProductionClientOperations : DevelopmentClientReadOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect bounded files. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) requires a canonical read root and an agent gateway that enforces it across links and reparse points. V2 stat returns bounded metadata only; it does not read content. V2 collect requires preview_collect followed by confirm_collect and captures one caller-bound, expiring binary artifact; artifact_status and download re-check caller and policy roots, while cleanup uses preview_artifact_cleanup followed by confirm_artifact_cleanup to wipe bytes. V2 write_text and upload require server-issued previews followed by unchanged destructive idempotent confirmations against a canonical write root. V2 delete is non-recursive and only removes one policy-root-bounded file or empty directory after destructive confirmation. Development retains fixture browse, inline read, status, download, collect, and cleanup compatibility actions.")]
    public Task<NetRatelToolResponse> netratel_files(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "browse" => OperatorFileReadAsync(operation, request, allowPageSize: true, cancellationToken),
            "stat" => OperatorFileReadAsync(operation, request, allowPageSize: false, cancellationToken),
            "read" => OperatorFileReadAsync(operation, request, allowPageSize: false, cancellationToken),
            "artifact_status" => ProductionFileArtifactStatusAsync(request, cancellationToken),
            "download" when UsesProductionOperatorRoutes => ProductionFileArtifactDownloadAsync(request, cancellationToken),
            "preview_collect" => ProductionFileArtifactCollectPreviewAsync(request, cancellationToken),
            "confirm_collect" => ProductionFileArtifactCollectConfirmAsync(request, confirm, cancellationToken),
            "preview_artifact_cleanup" => ProductionFileArtifactCleanupPreviewAsync(request, cancellationToken),
            "confirm_artifact_cleanup" => ProductionFileArtifactCleanupConfirmAsync(request, confirm, cancellationToken),
            "preview_write_text" => ProductionFileWriteTextPreviewAsync(request, cancellationToken),
            "confirm_write_text" => ProductionFileWriteTextConfirmAsync(request, confirm, cancellationToken),
            "preview_upload" => ProductionFileUploadPreviewAsync(request, cancellationToken),
            "confirm_upload" => ProductionFileUploadConfirmAsync(request, confirm, cancellationToken),
            "preview_create_directory" => ProductionFileCreateDirectoryPreviewAsync(request, cancellationToken),
            "confirm_create_directory" => ProductionFileCreateDirectoryConfirmAsync(request, confirm, cancellationToken),
            "preview_delete" => ProductionFileDeletePreviewAsync(request, cancellationToken),
            "confirm_delete" => ProductionFileDeleteConfirmAsync(request, confirm, cancellationToken),
            "preview_copy" => ProductionFileRelocationPreviewAsync("copy", request, cancellationToken),
            "confirm_copy" => ProductionFileRelocationConfirmAsync("copy", request, confirm, cancellationToken),
            "preview_move" => ProductionFileRelocationPreviewAsync("move", request, cancellationToken),
            "confirm_move" => ProductionFileRelocationConfirmAsync("move", request, confirm, cancellationToken),
            "status" when UsesProductionOperatorRoutes => Task.FromResult(Unsupported("netratel_files", operation, ProductionFileOperations)),
            "status" or "download" => DevelopmentArtifactReadAsync(operation, request, cancellationToken),
            "collect" => DevelopmentFileCollectAsync(request, confirm, cancellationToken),
            "cleanup" => DevelopmentArtifactCleanupAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_files", operation, DevelopmentFileOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect a policy-admitted client's logs. Read operations: sources, history, search, tail use the current V2 gateway in Development and Production. History, search, and tail are bounded; search requires text and tail starts and stops a short-lived client log subscription. Development retains confirmed resync compatibility. The policy-admitted V2 resync (explicitly enabled in Development and default in Production) requires preview_resync to issue a server-bound plan, then confirm_resync with unchanged target, source, planToken, idempotencyKey, and confirm: true.")]
    public Task<NetRatelToolResponse> netratel_client_logs(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "sources" => DevelopmentClientLogSourcesAsync(request, cancellationToken),
            "history" => DevelopmentClientLogHistoryAsync(request, cancellationToken),
            "search" => DevelopmentClientLogSearchAsync(request, cancellationToken),
            "tail" => DevelopmentClientLogTailAsync(request, cancellationToken),
            "resync" when UsesProductionOperatorRoutes => Task.FromResult(Unsupported("netratel_client_logs", operation, ProductionClientLogOperations)),
            "resync" => DevelopmentClientLogResyncAsync(request, confirm, cancellationToken),
            "preview_resync" => ClientLogResyncPreviewAsync(request, cancellationToken),
            "confirm_resync" => ClientLogResyncConfirmAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_client_logs", operation, DevelopmentClientLogOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect a policy-admitted client's telemetry through the current V2 gateway. snapshot returns the current accepted sample; stream_window returns at most 20 samples within 15 seconds and does not request interactive sampling.")]
    public Task<NetRatelToolResponse> netratel_client_telemetry(string operation, JsonElement? request = null, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "snapshot" => DevelopmentClientTelemetrySnapshotAsync(request, cancellationToken),
            "stream_window" => DevelopmentClientTelemetryWindowAsync(request, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_client_telemetry", operation, DevelopmentClientTelemetryOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect and manage explicitly-owned jobs. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) provides list, get, details, params, steps, create, update, delete, parameter and library-script-step lifecycle through target policy, ETags, preview/confirm, idempotency, immutable audit, and exact script revisions. Development retains its existing read-only job compatibility path.")]
    public Task<NetRatelToolResponse> netratel_jobs(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (UsesProductionOperatorRoutes)
        {
            return operation switch
            {
                "list" or "get" or "details" or "params" or "steps" => ProductionJobReadAsync(operation, request, cancellationToken),
                "create" or "update" or "delete" or "param_add" or "param_update" or "param_delete" or "step_add" or "step_update" or "step_reorder" or "step_delete" => ProductionJobMutationAsync(operation, request, confirm, cancellationToken),
                _ => Task.FromResult(Unsupported("netratel_jobs", operation, ProductionJobOperations))
            };
        }
        return operation switch
        {
            "list" => JobListAsync(request, cancellationToken),
            "get" or "details" or "params" or "steps" => JobByIdAsync(operation, request, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_jobs", operation, JobReadOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect owned job runs and bounded redacted logs. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) additionally starts, cancels, or retention-deletes an owned run only through exact target policy, preview/confirm, idempotency, and immutable run audit. Development retains the existing read-only compatibility path.")]
    public Task<NetRatelToolResponse> netratel_job_runs(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (UsesProductionOperatorRoutes)
        {
            return operation switch
            {
                "list" or "query" or "get" or "steps" or "logs" => ProductionJobRunReadAsync(operation, request, cancellationToken),
                "start" or "cancel" or "delete" => ProductionJobRunMutationAsync(operation, request, confirm, cancellationToken),
                _ => Task.FromResult(Unsupported("netratel_job_runs", operation, ProductionJobRunOperations))
            };
        }
        return operation switch
        {
            "list" => JobRunListAsync(request, cancellationToken),
            "query" => JobRunQueryAsync(request, cancellationToken),
            "get" or "steps" => JobRunByIdAsync(operation, request, cancellationToken),
            "logs" => JobRunLogsAsync(request, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_job_runs", operation, JobRunReadOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Create, run, cancel, or delete only server-generated, target-owned Development marker jobs. Every operation requires explicit confirmation. Caller-supplied commands, payloads, working directories, and timeouts are unavailable.")]
    public Task<NetRatelToolResponse> netratel_marker_jobs(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "create" => DevelopmentMarkerJobCreateAsync(request, confirm, cancellationToken),
            "run" => DevelopmentMarkerJobRunAsync(request, confirm, cancellationToken),
            "cancel" => DevelopmentMarkerJobCancelAsync(request, confirm, cancellationToken),
            "delete" => DevelopmentMarkerJobDeleteAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_marker_jobs", operation, DevelopmentMarkerJobOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect and administer typed NetRatel tenants. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) requires an explicit ControlPlane policy, netratel.mcp.admin, preview/confirmation, idempotency, immutable audit, and ETag versions for list/get/create/update/delete. Delete is non-cascading and previews dependent-object counts.")]
    public Task<NetRatelToolResponse> netratel_tenants(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (UsesProductionOperatorRoutes)
        {
            return operation switch
            {
                "list" or "get" => ProductionTenantReadAsync(operation, request, cancellationToken),
                "create" or "update" or "delete" => ProductionTenantMutationAsync(operation, request, confirm, cancellationToken),
                _ => Task.FromResult(Unsupported("netratel_tenants", operation, ProductionTenantOperations))
            };
        }
        return operation switch
        {
            "list" => GetAsync("netratel_tenants", operation, TenantReadOperations, "/api/v1/tenants/", cancellationToken),
            "get" => TenantByIdAsync(request, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_tenants", operation, TenantReadOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Manage explicitly-owned script definitions. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) provides list, get, params, validate, create, update, parse_manifest, run, and delete through target policy, a content-hash preview, explicit confirmation, idempotency, ETags, and immutable revision evidence. Development retains its isolated server-generated marker-script compatibility workflow.")]
    public Task<NetRatelToolResponse> netratel_scripts(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (UsesProductionOperatorRoutes)
        {
            return operation switch
            {
                "list" => ProductionScriptListAsync(request, cancellationToken),
                "get" or "params" => ProductionScriptByIdAsync(operation, request, cancellationToken),
                "validate" => ProductionScriptValidateAsync(request, cancellationToken),
                "create" or "update" or "parse_manifest" or "delete" => ProductionScriptMutationAsync(operation, request, confirm, cancellationToken),
                "run" => ProductionScriptRunAsync(request, confirm, cancellationToken),
                _ => Task.FromResult(Unsupported("netratel_scripts", operation, ProductionScriptOperations))
            };
        }
        return operation switch
        {
            "list" => DevelopmentScriptListAsync(request, cancellationToken),
            "get" or "params" => DevelopmentScriptByIdAsync(operation, request, cancellationToken),
            "create_marker" => DevelopmentScriptCreateAsync(request, confirm, cancellationToken),
            "update_marker" => DevelopmentScriptUpdateAsync(request, confirm, cancellationToken),
            "parse_manifest" => DevelopmentScriptParseManifestAsync(request, confirm, cancellationToken),
            "run" => DevelopmentScriptRunAsync(request, confirm, cancellationToken),
            "delete" => DevelopmentScriptDeleteAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_scripts", operation, DevelopmentScriptOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect and exercise bounded terminal sessions. Development retains target-owned QA sessions only. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) supports availability, preview_open, open, get, stream_window, send_input, resize, close, and diagnostics only through exact signed delegations, a policy-frozen durable ownership lease, explicit shell and working-directory constraints, bounded output, and idempotent close recovery. V2 open consumes preview credentials with confirm: true; terminal input remains content-free in audit records. Poll get until opened before input. stream_window replays up to 64 retained frames (1 MiB) even after send_input; carry its decimal-string nextSequence as afterSequence, page while hasMore, and report gap=true as missing output. Reads are non-consuming and may return before windowSeconds when output is available. History is transient and does not survive API restart.")]
    public Task<NetRatelToolResponse> netratel_terminal(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "availability" => OperatorTerminalAvailabilityAsync(request, cancellationToken),
            "preview_open" => ProductionTerminalOpenPreviewAsync(request, cancellationToken),
            "get" when UsesProductionOperatorRoutes => ProductionTerminalSessionReadAsync("get", request, cancellationToken),
            "get" => DevelopmentTerminalGetAsync(request, cancellationToken),
            "stream" => DevelopmentTerminalStreamAsync(request, cancellationToken),
            "stream_window" => ProductionTerminalStreamWindowAsync(request, cancellationToken),
            "diagnostics" => ProductionTerminalSessionReadAsync("diagnostics", request, cancellationToken),
            "open" when UsesProductionOperatorRoutes => ProductionTerminalOpenConfirmAsync(request, confirm, cancellationToken),
            "open" => DevelopmentTerminalOpenAsync(request, confirm, cancellationToken),
            "send_input" => ProductionTerminalInputAsync(request, cancellationToken),
            "self_test" => DevelopmentTerminalSessionMutationAsync("self_test", request, confirm, cancellationToken),
            "deployment-control-plane_inspect" => DevelopmentTerminalSessionMutationAsync("deployment-control-plane_inspect", request, confirm, cancellationToken),
            "fixture" => DevelopmentTerminalFixtureAsync(request, confirm, cancellationToken),
            "resize" when UsesProductionOperatorRoutes => ProductionTerminalResizeAsync(request, confirm, cancellationToken),
            "resize" => DevelopmentTerminalResizeAsync(request, confirm, cancellationToken),
            "close" when UsesProductionOperatorRoutes => ProductionTerminalCloseAsync(request, confirm, cancellationToken),
            "close" => DevelopmentTerminalSessionMutationAsync("close", request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_terminal", operation, UsesProductionOperatorRoutes ? ProductionTerminalOperations : DevelopmentTerminalOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Run one bounded policy-admitted V2 command only after preview_execute returns opaque credentials and execute is invoked with confirm: true. This V2 profile is explicitly enabled in Development and default in Production. Arbitrary shell text is always destructive, must meet the exact target policy, and is never persisted by the operator ownership record. Environment references are client-local names only; values are never accepted or returned. Operations: availability, preview_execute, execute, get, cancel.")]
    public Task<NetRatelToolResponse> netratel_commands(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        return operation switch
        {
            "availability" => ProductionCommandAvailabilityAsync(request, cancellationToken),
            "preview_execute" => ProductionCommandPreviewAsync(request, cancellationToken),
            "execute" => ProductionCommandConfirmAsync(request, confirm, cancellationToken),
            "get" => ProductionCommandReadAsync(request, cancellationToken),
            "cancel" => ProductionCommandCancelAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_commands", operation, ProductionCommandOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect installer collateral and manage short-lived enrollment codes. Development retains target-owned QA enrollment codes. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) uses a tenant-scoped pre-enrollment policy because a prospective client has no agent ID yet: collateral, get_enrollment, and list_enrollments are policy-admitted reads; create_enrollment and revoke_enrollment issue a no-write preview, then consume unchanged opaque plan credentials with confirm: true. The raw code appears only in the first confirmed create result and must never be persisted or reused.")]
    public Task<NetRatelToolResponse> netratel_onboarding(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (UsesProductionOperatorRoutes)
        {
            return operation switch
            {
                "collateral" or "collateral_download" or "get_enrollment" or "list_enrollments" => ProductionOnboardingReadAsync(operation, request, cancellationToken),
                "create_enrollment" or "revoke_enrollment" => ProductionOnboardingMutationAsync(operation, request, confirm, cancellationToken),
                _ => Task.FromResult(Unsupported("netratel_onboarding", operation, ProductionOnboardingOperations))
            };
        }
        return operation switch
        {
            "collateral" => DevelopmentOnboardingCollateralAsync(request, cancellationToken),
            "get_enrollment" => DevelopmentOnboardingEnrollmentAsync(request, cancellationToken),
            "create_enrollment" => DevelopmentOnboardingCreateAsync(request, confirm, cancellationToken),
            "revoke_enrollment" => DevelopmentOnboardingRevokeAsync(request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_onboarding", operation, DevelopmentOnboardingOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect caller-owned V2 tasks and bounded redacted logs. Without persisted logs, available terminal stdout/stderr snapshots are grouped by stream with the terminal timestamp, not real-time per-line timing. Snapshot logIds are stable per-task sinceId cursors allocated before stream filtering. The policy-admitted V2 profile (explicitly enabled in Development and default in Production) uses exact targets, current policy, preview/confirmation, idempotency, audit, and fenced delivery for reads and typed create_command, run_library_script, and cancel operations. Development retains only its compatibility reads.")]
    public Task<NetRatelToolResponse> netratel_tasks(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (UsesProductionOperatorRoutes)
        {
            return operation switch
            {
                "list" or "recent" or "get" or "logs" or "logs_by_request" => ProductionTaskReadAsync(operation, request, cancellationToken),
                "create_command" or "run_library_script" or "cancel" => ProductionTaskMutationAsync(operation, request, confirm, cancellationToken),
                _ => Task.FromResult(Unsupported("netratel_tasks", operation, ProductionTaskOperations))
            };
        }
        return operation switch
        {
            "list" => TaskByRequestIdAsync(request, cancellationToken),
            "recent" => TaskRecentAsync(request, cancellationToken),
            "get" => TaskByIdAsync(request, cancellationToken),
            "logs" or "logs_by_request" => TaskLogsAsync(operation, request, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_tasks", operation, TaskReadOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect and manage caller-owned policy-admitted V2 requests. This V2 profile is explicitly enabled in Development and default in Production. Requests are linked to one exact owned job and target; list/get are bounded and all create, update, claim, complete, fail, and cancel operations use policy re-evaluation, ETags, preview/confirmation, idempotency, and immutable audit. Inputs and unbounded legacy request history are never exposed.")]
    public Task<NetRatelToolResponse> netratel_requests(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_requests", operation, ProductionRequestOperations));
        return operation switch
        {
            "list" or "get" => ProductionRequestReadAsync(operation, request, cancellationToken),
            "create" or "update" or "claim" or "complete" or "fail" or "cancel" => ProductionRequestMutationAsync(operation, request, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_requests", operation, ProductionRequestOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Search approved NetRatel records. Operation is tenants, scripts, jobs, requests, clients, or tasks; request.q is optional and bounded.")]
    public Task<NetRatelToolResponse> netratel_search(string operation, JsonElement? request = null, CancellationToken cancellationToken = default)
    {
        var http = hostContext?.Transport == NetRatelMcpTransport.StreamableHttp;
        string[] availableOperations = SearchOperations;
        if (!availableOperations.Contains(operation, StringComparer.Ordinal))
        {
            return Task.FromResult(Unsupported("netratel_search", operation, availableOperations));
        }

        var payload = ToObject(request) ?? new JsonObject();
        var paged = http && operation is "clients" or "tenants";
        if (!ContainsOnly(payload, paged ? ["q", "offset", "limit"] : ["q"]) ||
            (payload["q"] is not null && !TryOptionalString(payload, "q", 512, out _)) ||
            !TryOptionalInt(payload, "offset", 0, 1_000_000, out var offset) ||
            !TryOptionalInt(payload, "limit", 1, 100, out var limit))
            return Task.FromResult(Invalid("Search accepts q (at most 512 characters); HTTP tenant/client discovery also accepts offset (0–1000000) and limit (1–100)."));
        var query = payload["q"]?.ToString();
        var path = http
            ? $"/api/v2/mcp/operator/search/{operation}"
            : $"/api/v1/global-search/{operation}";
        var parameters = new List<string>();
        if (query is not null) parameters.Add($"q={Uri.EscapeDataString(query)}");
        if (offset is not null) parameters.Add($"offset={offset.Value.ToString(CultureInfo.InvariantCulture)}");
        if (limit is not null) parameters.Add($"limit={limit.Value.ToString(CultureInfo.InvariantCulture)}");
        var suffix = parameters.Count > 0 ? "?" + string.Join("&", parameters) : "";
        return ExecuteAsync("netratel_search", operation, () => client.GetAsync(path + suffix, cancellationToken), cancellationToken);
    }

    [McpServerTool(UseStructuredContent = true), Description("Read the bounded NetRatel telemetry overview. Operation must be overview.")]
    public Task<NetRatelToolResponse> netratel_telemetry(string operation = "overview", CancellationToken cancellationToken = default)
        => GetAsync("netratel_telemetry", operation, ["overview"],
            hostContext?.Transport == NetRatelMcpTransport.StreamableHttp
                ? "/api/v2/mcp/operator/telemetry/overview" : "/api/v1/telemetry/overview", cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Inspect notifications belonging to the signed delegated operator, or mark bounded notification identifiers as read. The policy-admitted V2 mark_read operation (explicitly enabled in Development and default in Production) first returns a server-issued preview plan and then requires unchanged plan credentials with confirm: true.")]
    public Task<NetRatelToolResponse> netratel_notifications(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        return operation switch
        {
            "list" when UsesProductionOperatorRoutes => ControlPlanePageAsync("netratel_notifications", ProductionNotificationOperations, "/api/v2/mcp/operator/notifications", payload, cancellationToken),
            "summary" when UsesProductionOperatorRoutes => GetAsync("netratel_notifications", operation, ProductionNotificationOperations, "/api/v2/mcp/operator/notifications/summary", cancellationToken),
            "unread_errors" when UsesProductionOperatorRoutes => GetAsync("netratel_notifications", operation, ProductionNotificationOperations, "/api/v2/mcp/operator/notifications/unread-errors", cancellationToken),
            "get" when UsesProductionOperatorRoutes => RequireIdAsync("netratel_notifications", operation, ProductionNotificationOperations, payload, "id", id => $"/api/v2/mcp/operator/notifications/{Uri.EscapeDataString(id)}", cancellationToken),
            "mark_read" => NotificationMarkReadAsync(payload, confirm, cancellationToken),
            "list" => GetAsync("netratel_notifications", operation, NotificationReadOperations, "/api/v1/notifications", cancellationToken),
            "summary" => GetAsync("netratel_notifications", operation, NotificationReadOperations, "/api/v1/notifications/summary", cancellationToken),
            "unread_errors" => GetAsync("netratel_notifications", operation, NotificationReadOperations, "/api/v1/notifications/unread-errors", cancellationToken),
            "get" => RequireIdAsync("netratel_notifications", operation, NotificationReadOperations, payload, "id", id => $"/api/v1/notifications/{Uri.EscapeDataString(id)}", cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_notifications", operation, NotificationReadOperations))
        };
    }

    private Task<NetRatelToolResponse> NotificationMarkReadAsync(JsonObject? payload, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryNotificationMarkRead(payload, UsesProductionOperatorRoutes, confirm && UsesProductionOperatorRoutes, out var body, out var ids))
        {
            return Task.FromResult(Invalid(UsesProductionOperatorRoutes
                ? "request.ids must contain 1 through 200 unique notification UUIDs; confirmed Production requests also require opaque planToken and idempotencyKey."
                : "request.ids must contain 1 through 200 unique notification UUIDs."));
        }

        if (UsesProductionOperatorRoutes)
        {
            var path = confirm
                ? "/api/v2/mcp/operator/notifications/confirm/mark-read"
                : "/api/v2/mcp/operator/notifications/preview/mark-read";
            return ExecuteAsync("netratel_notifications", "mark_read", () => client.SendAsync(HttpMethod.Post, path, body, cancellationToken), cancellationToken);
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "mark_read",
                $"This operation would mark {ids.Count.ToString(CultureInfo.InvariantCulture)} notifications as read.",
                ids));
        }

        return ExecuteAsync("netratel_notifications", "mark_read", () => client.SendAsync(HttpMethod.Post, "/api/v1/notifications/mark-read", body, cancellationToken), cancellationToken);
    }

    [McpServerTool(UseStructuredContent = true), Description("Search bounded NetRatel AI-agent operation logs. Operation must be search; request supports since, level, contains, correlationId, and limit.")]
    public Task<NetRatelToolResponse> netratel_logs(string operation = "search", JsonElement? request = null, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "search", StringComparison.Ordinal))
        {
            return Task.FromResult(Unsupported("netratel_logs", operation, ["search"]));
        }

        var payload = ToObject(request);
        var limit = payload?["limit"]?.GetValue<int?>();
        if (limit is < 1 or > 200)
        {
            return Task.FromResult(Invalid("request.limit must be between 1 and 200 when supplied."));
        }

        return ExecuteAsync("netratel_logs", operation, () => client.GetAsync(WithQuery(hostContext?.Transport == NetRatelMcpTransport.StreamableHttp
            ? "/api/v2/mcp/operator/logs" : "/api/v1/ops/ai-agent/logs", payload, ["since", "level", "contains", "correlationId", "limit"]), cancellationToken), cancellationToken);
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect redacted server-owned connectivity state, or run one bounded ExternalService M2M probe. preview_test returns opaque credentials; test requires confirm: true and unchanged credentials.")]
    public Task<NetRatelToolResponse> netratel_connectivity(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        return operation switch
        {
            "settings" when UsesProductionOperatorRoutes => GetAsync("netratel_connectivity", operation, ProductionConnectivityOperations, "/api/v2/mcp/operator/connectivity/settings", cancellationToken),
            "netratel" when UsesProductionOperatorRoutes => GetAsync("netratel_connectivity", operation, ProductionConnectivityOperations, "/api/v2/mcp/operator/connectivity/netratel", cancellationToken),
            "preview_test" when UsesProductionOperatorRoutes => ProductionConnectivityTestAsync(payload, preview: true, confirm: false, cancellationToken),
            "test" when UsesProductionOperatorRoutes => ProductionConnectivityTestAsync(payload, preview: false, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_connectivity", operation, ProductionConnectivityOperations))
        };
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect bounded redacted domain-event summaries or use a preview/confirmed event control. preview_retry and preview_disable return opaque credentials; retry and disable require confirm: true and unchanged credentials.")]
    public Task<NetRatelToolResponse> netratel_events(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        return operation switch
        {
            "list" when UsesProductionOperatorRoutes => ControlPlanePageAsync("netratel_events", ProductionEventOperations, "/api/v2/mcp/operator/events", payload, cancellationToken),
            "get" when UsesProductionOperatorRoutes => ProductionEventGetAsync(payload, cancellationToken),
            "preview_retry" when UsesProductionOperatorRoutes => ProductionEventMutationAsync("retry", payload, preview: true, confirm: false, cancellationToken),
            "retry" when UsesProductionOperatorRoutes => ProductionEventMutationAsync("retry", payload, preview: false, confirm, cancellationToken),
            "preview_disable" when UsesProductionOperatorRoutes => ProductionEventMutationAsync("disable", payload, preview: true, confirm: false, cancellationToken),
            "disable" when UsesProductionOperatorRoutes => ProductionEventMutationAsync("disable", payload, preview: false, confirm, cancellationToken),
            _ => Task.FromResult(Unsupported("netratel_events", operation, ProductionEventOperations))
        };
    }

    private Task<NetRatelToolResponse> ControlPlanePageAsync(string tool, string[] operations, string path, JsonObject? payload, CancellationToken cancellationToken)
    {
        if (!ContainsOnly(payload, "page", "pageSize") ||
            !TryOptionalInt(payload, "page", 1, 10_000, out var page) ||
            !TryOptionalInt(payload, "pageSize", 1, 100, out var pageSize))
            return Task.FromResult(Invalid("request supports page from 1 through 10000 and pageSize from 1 through 100."));
        return GetAsync(tool, "list", operations, WithQuery(path, ("page", Format(page)), ("pageSize", Format(pageSize))), cancellationToken);
    }

    private static readonly string[] DevelopmentClientReadOperations = ["presence", "binding", "telemetry", "update_attempts"];
    private static readonly string[] ProductionClientReadOperations = ["get", "presence", "capabilities", "binding", "telemetry", "update_attempts", "update_metadata"];
    private static readonly string[] ProductionClientOperations = [.. ProductionClientReadOperations, "preview_ping", "ping", "preview_software_update", "software_update", "preview_disable", "disable", "preview_enable", "enable", "preview_delete", "delete"];
    private static readonly string[] AccessOperations = ["whoami", "effective", "evaluate", "target"];
    private static readonly string[] PolicyOperations = ["policies", "policy", "change_audits", "accepted_audits", "target", "matches", "evaluate", "preview_create", "confirm_create", "preview_replace", "confirm_replace", "preview_disable", "confirm_disable", "preview_revoke", "confirm_revoke", "preview_target_profile", "confirm_target_profile"];
    private static readonly string[] DevelopmentFileReadOperations = ["browse", "read", "status", "download"];
    private static readonly string[] DevelopmentFileOperations = ["browse", "read", "status", "download", "collect", "cleanup"];
    private static readonly string[] ProductionFileReadOperations = ["browse", "stat", "read", "artifact_status", "download"];
    private static readonly string[] ProductionFileOperations = ["browse", "stat", "read", "artifact_status", "download", "preview_collect", "confirm_collect", "preview_artifact_cleanup", "confirm_artifact_cleanup", "preview_write_text", "preview_upload", "preview_create_directory", "preview_delete", "preview_copy", "preview_move", "confirm_write_text", "confirm_upload", "confirm_create_directory", "confirm_delete", "confirm_copy", "confirm_move"];
    private static readonly string[] DevelopmentClientLogOperations = ["sources", "history", "search", "tail", "resync", "preview_resync", "confirm_resync"];
    private static readonly string[] ProductionClientLogOperations = ["sources", "history", "search", "tail", "preview_resync", "confirm_resync"];
    private static readonly string[] DevelopmentClientTelemetryOperations = ["snapshot", "stream_window"];
    private static readonly string[] JobReadOperations = ["list", "get", "details", "params", "steps"];
    private static readonly string[] JobRunReadOperations = ["list", "query", "get", "steps", "logs"];
    private static readonly string[] ProductionJobOperations = ["list", "get", "details", "params", "steps", "create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete"];
    private static readonly string[] ProductionJobRunOperations = ["list", "query", "get", "steps", "logs", "start", "cancel", "delete"];
    private static readonly string[] DevelopmentMarkerJobOperations = ["create", "run", "cancel", "delete"];
    private static readonly string[] TenantReadOperations = ["list", "get"];
    private static readonly string[] ProductionTenantOperations = ["list", "get", "create", "update", "delete"];
    private static readonly string[] DevelopmentScriptReadOperations = ["list", "get", "params"];
    private static readonly string[] DevelopmentScriptOperations = ["list", "get", "params", "create_marker", "update_marker", "parse_manifest", "run", "delete"];
    private static readonly string[] ProductionScriptOperations = ["list", "get", "params", "validate", "create", "update", "parse_manifest", "run", "delete"];
    private static readonly string[] DevelopmentTerminalReadOperations = ["availability", "get", "stream"];
    private static readonly string[] DevelopmentTerminalOperations = ["availability", "get", "stream", "open", "self_test", "deployment-control-plane_inspect", "fixture", "resize", "close"];
    private static readonly string[] ProductionTerminalOperations = ["availability", "preview_open", "open", "get", "stream_window", "send_input", "resize", "close", "diagnostics"];
    private static readonly string[] ProductionCommandOperations = ["availability", "preview_execute", "execute", "get", "cancel"];
    private static readonly string[] DevelopmentOnboardingReadOperations = ["collateral", "get_enrollment"];
    private static readonly string[] DevelopmentOnboardingOperations = ["collateral", "get_enrollment", "create_enrollment", "revoke_enrollment"];
    private static readonly string[] ProductionOnboardingReadOperations = ["collateral", "collateral_download", "get_enrollment", "list_enrollments"];
    private static readonly string[] ProductionOnboardingOperations = ["collateral", "collateral_download", "get_enrollment", "list_enrollments", "create_enrollment", "revoke_enrollment"];
    private static readonly string[] TaskReadOperations = ["list", "recent", "get", "logs", "logs_by_request"];
    private static readonly string[] ProductionTaskOperations = ["list", "recent", "get", "logs", "logs_by_request", "create_command", "run_library_script", "cancel"];
    private static readonly string[] ProductionRequestOperations = ["list", "get", "create", "update", "claim", "complete", "fail", "cancel"];
    private static readonly string[] SearchOperations = ["tenants", "scripts", "jobs", "requests", "clients", "tasks"];
    private static readonly string[] NotificationReadOperations = ["list", "get", "summary", "unread_errors"];
    private static readonly string[] ProductionNotificationOperations = ["list", "get", "summary", "unread_errors", "mark_read"];
    private static readonly string[] ProductionConnectivityOperations = ["settings", "netratel", "preview_test", "test"];
    private static readonly string[] ProductionEventOperations = ["list", "get", "preview_retry", "retry", "preview_disable", "disable"];

    private Task<NetRatelToolResponse> AccessTargetAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        var path = $"/api/v2/mcp/operator/access/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}";
        if (operation == "effective")
        {
            path += "/effective";
        }

        return GetAsync("netratel_access", operation, AccessOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> AccessEvaluateAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "tool", "operation") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "tool", 128, out var tool) ||
            !TryRequiredString(payload, "operation", 128, out var targetOperation) ||
            !IsAccessToken(tool) || !IsAccessToken(targetOperation))
        {
            return Task.FromResult(Invalid("request requires target identifiers plus a catalogued tool and operation token."));
        }

        var path = WithQuery(
            $"/api/v2/mcp/operator/access/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/evaluate",
            ("tool", tool),
            ("operation", targetOperation));
        return GetAsync("netratel_access", "evaluate", AccessOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyListAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "environment", "tenantId") ||
            !TryOptionalString(payload, "environment", 16, out var environment) ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            environment is not null && environment is not ("Development" or "Production"))
        {
            return Task.FromResult(Invalid("request supports only environment Development or Production and an optional positive tenantId."));
        }

        return GetAsync(
            "netratel_policy",
            "policies",
            PolicyOperations,
            WithQuery("/api/v2/mcp/operator/policy/policies",
                ("environment", environment),
                ("tenantId", Format(tenantId))),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyByIdAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "policyId") ||
            !TryRequiredGuid(payload, "policyId", out var policyId))
        {
            return Task.FromResult(Invalid("request.policyId must be a non-empty operator policy UUID."));
        }

        return GetAsync("netratel_policy", "policy", PolicyOperations,
            $"/api/v2/mcp/operator/policy/policies/{policyId:D}", cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyChangeAuditsAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "policyId", "agentId") ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryOptionalGuid(payload, "policyId", out var policyId) ||
            !TryOptionalGuid(payload, "agentId", out var agentId) ||
            agentId.HasValue && !tenantId.HasValue)
        {
            return Task.FromResult(Invalid("request supports only optional bounded tenantId, policyId, and agentId filters."));
        }

        return GetAsync(
            "netratel_policy",
            "change_audits",
            PolicyOperations,
            WithQuery("/api/v2/mcp/operator/policy/change-audits",
                ("tenantId", Format(tenantId)),
                ("policyId", policyId?.ToString("D")),
                ("agentId", agentId?.ToString("D"))),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyAcceptedAuditsAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "subject", "limit") ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryOptionalGuid(payload, "agentId", out var agentId) ||
            !TryOptionalString(payload, "subject", 256, out var subject) ||
            !TryOptionalInt(payload, "limit", 1, 250, out var limit) ||
            agentId.HasValue && !tenantId.HasValue)
        {
            return Task.FromResult(Invalid("request supports optional bounded tenantId, agentId, subject, and limit filters."));
        }

        return GetAsync(
            "netratel_policy",
            "accepted_audits",
            PolicyOperations,
            WithQuery("/api/v2/mcp/operator/policy/accepted-audits",
                ("tenantId", Format(tenantId)),
                ("agentId", agentId?.ToString("D")),
                ("subject", subject),
                ("limit", Format(limit))),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyTargetAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        return GetAsync("netratel_policy", "target", PolicyOperations,
            $"/api/v2/mcp/operator/policy/targets/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}", cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyMatchesAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        return GetAsync("netratel_policy", "matches", PolicyOperations,
            $"/api/v2/mcp/operator/policy/matches/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}", cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyEvaluateAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "tool", "operation") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "tool", 128, out var tool) ||
            !TryRequiredString(payload, "operation", 128, out var operation))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and one catalogued target tool and operation."));
        }

        return GetAsync(
            "netratel_policy",
            "evaluate",
            PolicyOperations,
            WithQuery(
                $"/api/v2/mcp/operator/policy/evaluate/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}",
                ("tool", tool),
                ("operation", operation)),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyCreatePreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryPolicyCreateRequest(request, requireConfirmationCredentials: false, out var body))
        {
            return Task.FromResult(Invalid("request.policy must be the closed policy-draft object documented by netratel_capabilities."));
        }

        return ExecuteAsync(
            "netratel_policy",
            "preview_create",
            () => client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/create/preview", body, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyCreateConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryPolicyCreateRequest(request, requireConfirmationCredentials: true, out var body))
        {
            return Task.FromResult(Invalid("request requires the unchanged closed policy draft plus opaque planToken and idempotencyKey returned by preview_create."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "confirm_create",
                "This operation would consume the supplied server-issued policy confirmation plan and create exactly one reviewed policy if its authorization is still current.",
                ["policy-create-plan"]));
        }

        return ExecuteAsync(
            "netratel_policy",
            "confirm_create",
            () => client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/create/confirm", body, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyReplacePreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryPolicyReplaceRequest(request, requireConfirmationCredentials: false, out var body))
            return Task.FromResult(Invalid("request requires policyId UUID, positive expectedVersion, and the closed replacement policy draft."));

        return ExecuteAsync("netratel_policy", "preview_replace", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/replace/preview", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyReplaceConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryPolicyReplaceRequest(request, requireConfirmationCredentials: true, out var body))
            return Task.FromResult(Invalid("request requires policyId, expectedVersion, unchanged replacement draft, and opaque planToken and idempotencyKey returned by preview_replace."));
        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "confirm_replace",
                "This operation would consume the supplied server-issued policy confirmation plan and replace exactly one unchanged policy version if authorization is still current.",
                ["policy-replace-plan"]));
        }

        return ExecuteAsync("netratel_policy", "confirm_replace", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/replace/confirm", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyDisablePreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryPolicyDisableRequest(request, requireConfirmationCredentials: false, out var body))
            return Task.FromResult(Invalid("request requires policyId UUID and positive expectedVersion."));

        return ExecuteAsync("netratel_policy", "preview_disable", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/disable/preview", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyDisableConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryPolicyDisableRequest(request, requireConfirmationCredentials: true, out var body))
            return Task.FromResult(Invalid("request requires policyId, expectedVersion, and opaque planToken and idempotencyKey returned by preview_disable."));
        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "confirm_disable",
                "This operation would consume the supplied server-issued policy confirmation plan and disable exactly one unchanged policy version if authorization is still current.",
                ["policy-disable-plan"]));
        }

        return ExecuteAsync("netratel_policy", "confirm_disable", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/disable/confirm", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyRevokePreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryPolicyDisableRequest(request, requireConfirmationCredentials: false, out var body))
            return Task.FromResult(Invalid("request requires policyId UUID, positive expectedVersion, and the exact persisted target selector."));

        return ExecuteAsync("netratel_policy", "preview_revoke", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/revoke/preview", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> PolicyRevokeConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryPolicyDisableRequest(request, requireConfirmationCredentials: true, out var body))
            return Task.FromResult(Invalid("request requires policyId, expectedVersion, exact target selector, and opaque planToken and idempotencyKey returned by preview_revoke."));
        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "confirm_revoke",
                "This operation would consume the supplied server-issued policy confirmation plan and permanently revoke exactly one unchanged policy version if authorization is still current.",
                ["policy-revoke-plan"]));
        }

        return ExecuteAsync("netratel_policy", "confirm_revoke", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/revoke/confirm", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> TargetProfilePreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryTargetProfileRequest(request, requireConfirmationCredentials: false, out var body))
        {
            return Task.FromResult(Invalid("request requires an exact tenantId and agentId, a non-Unknown classification, bounded distinct tags, and no expectedVersion for a new profile or its current positive version for an existing profile."));
        }

        return ExecuteAsync("netratel_policy", "preview_target_profile", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/target-profile/preview", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> TargetProfileConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryTargetProfileRequest(request, requireConfirmationCredentials: true, out var body))
        {
            return Task.FromResult(Invalid("request requires the unchanged exact target-profile draft plus opaque planToken and idempotencyKey returned by preview_target_profile."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "confirm_target_profile",
                "This operation would consume the supplied server-issued policy-administration plan and persist exactly one server-owned target classification and bounded tag set if its authorization and version are still current.",
                ["target-profile-plan"]));
        }

        return ExecuteAsync("netratel_policy", "confirm_target_profile", () =>
            client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/policy/target-profile/confirm", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> JobRunListAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        string? error = null;
        if (!TryObject(request, out var payload) || !TryBuildJobRunListPath(payload, out var path, out error))
        {
            return Task.FromResult(Invalid(error ?? "request must be a closed bounded job-run list filter object."));
        }

        return GetAsync("netratel_job_runs", "list", JobRunReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentMarkerJobCreateAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "scriptId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryPositiveLong(payload, "scriptId", out var scriptId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive target-owned marker scriptId."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/script/{scriptId.ToString(CultureInfo.InvariantCulture)}" };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("create", "This operation would create one server-generated Development marker job with one owned marker-script step.", affected));
        }

        return ExecuteAsync("netratel_marker_jobs", "create", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentMarkerJobPath(tenantId, agentId)}/{scriptId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentMarkerJobRunAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentMarkerJobIdentity(request, out var tenantId, out var agentId, out var jobId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive target-owned marker jobId."));
        }

        var affected = new[] { jobId.ToString(CultureInfo.InvariantCulture) };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("run", "This operation would submit one target-owned Development marker job with no caller-supplied inputs.", affected));
        }

        return ExecuteAsync("netratel_marker_jobs", "run", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentMarkerJobPath(tenantId, agentId)}/{jobId.ToString(CultureInfo.InvariantCulture)}/runs", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentMarkerJobCancelAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "jobId", "runId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryPositiveLong(payload, "jobId", out var jobId) ||
            !TryPositiveUlong(payload, "runId", out var runId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive target-owned jobId and runId."));
        }

        var affected = new[] { $"{jobId.ToString(CultureInfo.InvariantCulture)}/{runId.ToString(CultureInfo.InvariantCulture)}" };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("cancel", "This operation would cancel one target-owned Development marker job run.", affected));
        }

        return ExecuteAsync("netratel_marker_jobs", "cancel", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentMarkerJobPath(tenantId, agentId)}/{jobId.ToString(CultureInfo.InvariantCulture)}/runs/{runId.ToString(CultureInfo.InvariantCulture)}/cancel", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentMarkerJobDeleteAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentMarkerJobIdentity(request, out var tenantId, out var agentId, out var jobId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive target-owned marker jobId."));
        }

        var affected = new[] { jobId.ToString(CultureInfo.InvariantCulture) };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("delete", "This operation would delete one target-owned Development marker job after verifying it has no active run.", affected));
        }

        return ExecuteAsync("netratel_marker_jobs", "delete", () => client.SendAsync(HttpMethod.Delete, $"{DevelopmentMarkerJobPath(tenantId, agentId)}/{jobId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ClientUpdateAttemptsAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        string? error = null;
        if (!TryObject(request, out var payload) || !TryBuildClientUpdateAttemptsPath(payload, out var path, out error))
        {
            return Task.FromResult(Invalid(error ?? "request must be a closed bounded client-update-attempt filter object."));
        }

        return GetAsync("netratel_clients", "update_attempts", DevelopmentClientReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> ClientPresenceAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        string? error = null;
        if (!TryObject(request, out var payload) || !TryBuildClientPresencePath(payload, out var path, out error))
        {
            return Task.FromResult(Invalid(error ?? "request must be a closed bounded V2 client-presence filter object."));
        }

        return GetAsync("netratel_clients", "presence", DevelopmentClientReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> ClientBindingAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId))
        {
            return Task.FromResult(Invalid("request.tenantId must be a positive integer and request.agentId must be a persisted V2 UUID."));
        }

        return GetAsync(
            "netratel_clients",
            "binding",
            DevelopmentClientReadOperations,
            $"/api/v2/tenants/{tenantId.ToString(CultureInfo.InvariantCulture)}/primary-client-bindings/agents/{agentId:D}",
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionClientReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryDevelopmentClient(request, out var tenantId, out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        }

        var suffix = operation switch
        {
            "get" => string.Empty,
            "presence" => "/presence",
            "capabilities" => "/capabilities",
            "binding" => "/binding",
            "telemetry" => "/telemetry",
            "update_attempts" => "/update-attempts",
            "update_metadata" => "/update-metadata",
            _ => null
        };
        return suffix is null
            ? Task.FromResult(Unsupported("netratel_clients", operation, ProductionClientReadOperations))
            : GetAsync("netratel_clients", operation, ProductionClientReadOperations,
                $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients{suffix}", cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionClientPingAsync(JsonElement? request, bool preview, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null)
        {
            return Task.FromResult(Invalid("request requires a positive Production tenantId, persisted agentId UUID, and (for ping) only opaque preview credentials."));
        }

        if (!TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !ContainsOnly(payload, "tenantId", "agentId", "planToken", "idempotencyKey"))
        {
            return Task.FromResult(Invalid("request requires a positive Production tenantId, persisted agentId UUID, and (for ping) only opaque preview credentials."));
        }

        if (preview)
        {
            if (payload.ContainsKey("planToken") || payload.ContainsKey("idempotencyKey"))
            {
                return Task.FromResult(Invalid("preview_ping accepts only tenantId and agentId; call ping with confirm: true and unchanged preview credentials."));
            }
            var previewPath = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients/ping/preview";
            return ExecuteAsync("netratel_clients", "preview_ping", () => client.SendAsync(HttpMethod.Post, previewPath, null, cancellationToken), cancellationToken);
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "ping",
                "Ping requires preview_ping followed by confirm: true with the unchanged opaque preview credentials.",
                [agentId.ToString("D")]));
        }

        var body = new JsonObject();
        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
        {
            return Task.FromResult(Invalid("request.planToken and request.idempotencyKey must be opaque credentials returned by preview_ping."));
        }
        body["planToken"] = planToken;
        body["idempotencyKey"] = idempotencyKey;
        var path = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients/ping/confirm";
        return ExecuteAsync("netratel_clients", "ping", () => client.SendAsync(HttpMethod.Post, path, body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionClientSoftwareUpdateAsync(JsonElement? request, bool preview, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !ContainsOnly(payload, "tenantId", "agentId", "planToken", "idempotencyKey"))
        {
            return Task.FromResult(Invalid("request requires a positive Production tenantId, persisted agentId UUID, and (for software_update) only opaque preview credentials."));
        }

        if (preview)
        {
            if (payload.ContainsKey("planToken") || payload.ContainsKey("idempotencyKey"))
            {
                return Task.FromResult(Invalid("preview_software_update accepts only tenantId and agentId; call software_update with confirm: true and unchanged preview credentials."));
            }
            var previewPath = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients/software-update/preview";
            return ExecuteAsync("netratel_clients", "preview_software_update", () => client.SendAsync(HttpMethod.Post, previewPath, null, cancellationToken), cancellationToken);
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "software_update",
                "software_update resumes a server-suspended automatic update only after preview_software_update and confirm: true with unchanged opaque preview credentials.",
                [agentId.ToString("D")]));
        }

        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
        {
            return Task.FromResult(Invalid("request.planToken and request.idempotencyKey must be opaque credentials returned by preview_software_update."));
        }

        var body = new JsonObject { ["planToken"] = planToken, ["idempotencyKey"] = idempotencyKey };
        var path = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients/software-update/confirm";
        return ExecuteAsync("netratel_clients", "software_update", () => client.SendAsync(HttpMethod.Post, path, body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionConnectivityTestAsync(JsonObject? payload, bool preview, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || (preview && payload is { Count: > 0 }) || (!preview && payload is null))
        {
            return Task.FromResult(Invalid("connectivity test accepts no target or override; confirmed test requests require only opaque preview credentials."));
        }

        if (preview)
        {
            return ExecuteAsync("netratel_connectivity", "preview_test", () => client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/connectivity/test/preview", null, cancellationToken), cancellationToken);
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "test",
                "test runs only the server-owned bounded ExternalService M2M probe after preview_test and confirm: true with unchanged opaque preview credentials.",
                ["server-owned-external-service-m2m-probe"]));
        }

        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey) ||
            !ContainsOnly(payload, "planToken", "idempotencyKey"))
        {
            return Task.FromResult(Invalid("request.planToken and request.idempotencyKey must be unchanged opaque credentials returned by preview_test."));
        }

        var body = new JsonObject { ["planToken"] = planToken, ["idempotencyKey"] = idempotencyKey };
        return ExecuteAsync("netratel_connectivity", "test", () => client.SendAsync(HttpMethod.Post, "/api/v2/mcp/operator/connectivity/test/confirm", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionEventGetAsync(JsonObject? payload, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || payload is null ||
            !ContainsOnly(payload, "eventId") || !TryRequiredGuid(payload, "eventId", out var eventId))
        {
            return Task.FromResult(Invalid("request.eventId must be a persisted non-empty event UUID."));
        }

        return GetAsync("netratel_events", "get", ProductionEventOperations, $"/api/v2/mcp/operator/events/{eventId:D}", cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionEventMutationAsync(string action, JsonObject? payload, bool preview, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || payload is null ||
            !TryRequiredGuid(payload, "eventId", out var eventId) ||
            !ContainsOnly(payload, "eventId", "planToken", "idempotencyKey"))
        {
            return Task.FromResult(Invalid("request.eventId must be a persisted non-empty event UUID; confirmed requests also require opaque preview credentials."));
        }

        if (preview)
        {
            if (payload.ContainsKey("planToken") || payload.ContainsKey("idempotencyKey"))
            {
                return Task.FromResult(Invalid($"preview_{action} accepts only eventId; call {action} with confirm: true and unchanged opaque preview credentials."));
            }
            return ExecuteAsync("netratel_events", $"preview_{action}", () => client.SendAsync(HttpMethod.Post, $"/api/v2/mcp/operator/events/{eventId:D}/{action}/preview", null, cancellationToken), cancellationToken);
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                action,
                $"{action} requires preview_{action} followed by confirm: true with unchanged opaque preview credentials.",
                [eventId.ToString("D")]));
        }

        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
        {
            return Task.FromResult(Invalid($"request.planToken and request.idempotencyKey must be opaque credentials returned by preview_{action}."));
        }

        var body = new JsonObject { ["planToken"] = planToken, ["idempotencyKey"] = idempotencyKey };
        return ExecuteAsync("netratel_events", action, () => client.SendAsync(HttpMethod.Post, $"/api/v2/mcp/operator/events/{eventId:D}/{action}/confirm", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionClientLifecycleAsync(string action, JsonElement? request, bool preview, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null)
        {
            return Task.FromResult(Invalid("request requires a positive Production tenantId, persisted agentId UUID, and the closed lifecycle fields for the selected action."));
        }

        var allowReason = action is "disable" or "delete";
        if (!TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !ContainsOnly(payload, allowReason ? ["tenantId", "agentId", "reason", "planToken", "idempotencyKey"] : ["tenantId", "agentId", "planToken", "idempotencyKey"]))
        {
            return Task.FromResult(Invalid("request requires a positive Production tenantId, persisted agentId UUID, and the closed lifecycle fields for the selected action."));
        }

        string? reason = null;
        if (allowReason && (!TryRequiredString(payload, "reason", 256, out reason) || string.IsNullOrWhiteSpace(reason) || reason.Any(char.IsControl)))
        {
            return Task.FromResult(Invalid("request.reason must be a bounded non-secret lifecycle reason."));
        }

        var operation = preview ? $"preview_{action}" : action;
        if (preview)
        {
            if (payload.ContainsKey("planToken") || payload.ContainsKey("idempotencyKey"))
            {
                return Task.FromResult(Invalid($"{operation} does not accept confirmation credentials."));
            }
            var previewBody = reason is null ? null : new JsonObject { ["reason"] = reason };
            var previewPath = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients/{action}/preview";
            return ExecuteAsync("netratel_clients", operation, () => client.SendAsync(HttpMethod.Post, previewPath, previewBody, cancellationToken), cancellationToken);
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                action,
                $"{action} requires preview_{action} followed by confirm: true with the unchanged opaque preview credentials.",
                [agentId.ToString("D")]));
        }

        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
        {
            return Task.FromResult(Invalid($"request.planToken and request.idempotencyKey must be opaque credentials returned by preview_{action}."));
        }

        var body = new JsonObject { ["planToken"] = planToken, ["idempotencyKey"] = idempotencyKey };
        if (reason is not null) body["reason"] = reason;
        var path = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients/{action}/confirm";
        return ExecuteAsync("netratel_clients", action, () => client.SendAsync(HttpMethod.Post, path, body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ClientTelemetryAsync(JsonElement? request, JsonObject? payload, CancellationToken cancellationToken)
    {
        if (TryDevelopmentClient(request, out var tenantId, out var agentId))
        {
            return GetAsync(
                "netratel_clients",
                "telemetry",
                DevelopmentClientReadOperations,
                $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/telemetry/snapshot",
                cancellationToken);
        }

        return RequireIdAsync(
            "netratel_clients",
            "telemetry",
            DevelopmentClientReadOperations,
            payload,
            "clientIdentity",
            id => $"/api/v1/clients/{Uri.EscapeDataString(id)}/telemetry",
            cancellationToken);
    }

    private Task<NetRatelToolResponse> OperatorFileReadAsync(string operation, JsonElement? request, bool allowPageSize, CancellationToken cancellationToken)
    {
        int? pageSize = null;
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, allowPageSize ? ["tenantId", "agentId", "path", "pageSize"] : ["tenantId", "agentId", "path"]) ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            (allowPageSize && !TryOptionalInt(payload, "pageSize", 1, 100, out pageSize)))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, a bounded canonical path, and (for browse) pageSize from 1 through 100."));
        }

        if (UsesProductionOperatorRoutes)
        {
            var productionEndpoint = OperatorFilePath(tenantId, agentId, operation switch
            {
                "read" => "/files/read",
                "stat" => "/files/stat",
                _ => "/files/browse"
            });
            productionEndpoint = WithQuery(productionEndpoint, ("path", path), ("pageSize", allowPageSize ? Format(pageSize) : null));
            return GetAsync("netratel_files", operation, ProductionFileReadOperations, productionEndpoint, cancellationToken);
        }

        var endpoint = $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/files";
        endpoint += operation == "read" ? "/inline" : string.Empty;
        endpoint = WithQuery(endpoint, ("path", path), ("pageSize", allowPageSize ? Format(pageSize) : null));
        return GetAsync("netratel_files", operation, DevelopmentFileReadOperations, endpoint, cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileArtifactStatusAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "artifact_status", DevelopmentFileOperations));
        if (!TryProductionFileArtifactRequest(request, requirePlanCredentials: false, out var tenantId, out var agentId, out var artifactId, out _, out _))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and caller-bound artifactId UUID."));

        return GetAsync(
            "netratel_files",
            "artifact_status",
            ProductionFileReadOperations,
            OperatorFilePath(tenantId, agentId, $"/files/artifacts/{artifactId:D}"),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileArtifactDownloadAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "download", DevelopmentFileOperations));
        if (!TryProductionFileArtifactRequest(request, requirePlanCredentials: false, out var tenantId, out var agentId, out var artifactId, out _, out _))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and caller-bound artifactId UUID."));

        return GetAsync(
            "netratel_files",
            "download",
            ProductionFileReadOperations,
            OperatorFilePath(tenantId, agentId, $"/files/artifacts/{artifactId:D}/download"),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileArtifactCollectPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "preview_collect", DevelopmentFileOperations));
        if (!TryProductionFileArtifactCollectRequest(request, requirePlanCredentials: false, out var tenantId, out var agentId, out var path, out _, out _))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and bounded canonical file path."));

        return ExecuteAsync(
            "netratel_files",
            "preview_collect",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/artifacts/collect/preview"), new JsonObject { ["path"] = path }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileArtifactCollectConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "confirm_collect", DevelopmentFileOperations));
        if (!TryProductionFileArtifactCollectRequest(request, requirePlanCredentials: true, out var tenantId, out var agentId, out var path, out var planToken, out var idempotencyKey))
            return Task.FromResult(Invalid("request requires the unchanged canonical file path, planToken, and idempotencyKey from preview_collect."));

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{path}" };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                "This operation would collect one policy-read-root-bounded regular file as a short-lived caller-bound artifact.",
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, "collect", affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            "confirm_collect",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/artifacts/collect/confirm"), new JsonObject
            {
                ["path"] = path,
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileArtifactCleanupPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "preview_artifact_cleanup", DevelopmentFileOperations));
        if (!TryProductionFileArtifactRequest(request, requirePlanCredentials: false, out var tenantId, out var agentId, out var artifactId, out _, out _))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and caller-bound artifactId UUID."));

        return ExecuteAsync(
            "netratel_files",
            "preview_artifact_cleanup",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, $"/files/artifacts/{artifactId:D}/cleanup/preview"), null, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileArtifactCleanupConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "confirm_artifact_cleanup", DevelopmentFileOperations));
        if (!TryProductionFileArtifactRequest(request, requirePlanCredentials: true, out var tenantId, out var agentId, out var artifactId, out var planToken, out var idempotencyKey))
            return Task.FromResult(Invalid("request requires artifactId plus planToken and idempotencyKey from preview_artifact_cleanup."));

        var affected = new[] { artifactId.ToString("D") };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                "This destructive operation would wipe one caller-bound Production file artifact.",
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, "artifact_cleanup", affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            "confirm_artifact_cleanup",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, $"/files/artifacts/{artifactId:D}/cleanup/confirm"), new JsonObject
            {
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private static bool TryProductionFileArtifactCollectRequest(
        JsonElement? request,
        bool requirePlanCredentials,
        out int tenantId,
        out Guid agentId,
        out string path,
        out string planToken,
        out string idempotencyKey)
    {
        tenantId = default;
        agentId = default;
        path = string.Empty;
        planToken = string.Empty;
        idempotencyKey = string.Empty;
        var fields = requirePlanCredentials
            ? new[] { "tenantId", "agentId", "path", "planToken", "idempotencyKey" }
            : new[] { "tenantId", "agentId", "path" };
        return TryObject(request, out var payload) &&
               ContainsOnly(payload, fields) &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryRequiredString(payload, "path", 4096, out path) &&
               (!requirePlanCredentials ||
                (TryRequiredString(payload, "planToken", 128, out planToken) &&
                 TryRequiredString(payload, "idempotencyKey", 128, out idempotencyKey)));
    }

    private static bool TryProductionFileArtifactRequest(
        JsonElement? request,
        bool requirePlanCredentials,
        out int tenantId,
        out Guid agentId,
        out Guid artifactId,
        out string planToken,
        out string idempotencyKey)
    {
        tenantId = default;
        agentId = default;
        artifactId = default;
        planToken = string.Empty;
        idempotencyKey = string.Empty;
        var fields = requirePlanCredentials
            ? new[] { "tenantId", "agentId", "artifactId", "planToken", "idempotencyKey" }
            : new[] { "tenantId", "agentId", "artifactId" };
        return TryObject(request, out var payload) &&
               ContainsOnly(payload, fields) &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryRequiredGuid(payload, "artifactId", out artifactId) &&
               (!requirePlanCredentials ||
                (TryRequiredString(payload, "planToken", 128, out planToken) &&
                 TryRequiredString(payload, "idempotencyKey", 128, out idempotencyKey)));
    }

    private Task<NetRatelToolResponse> ProductionFileWriteTextPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "preview_write_text", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "text") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryFileText(payload, out var text))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, canonical path, and bounded text."));
        }

        return ExecuteAsync(
            "netratel_files",
            "preview_write_text",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/write-text/preview"), new JsonObject { ["path"] = path, ["text"] = text }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileWriteTextConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "confirm_write_text", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "text", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryFileText(payload, out var text) ||
            !TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey))
        {
            return Task.FromResult(Invalid("request requires the unchanged preview target, bounded text, planToken, and idempotencyKey."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{path}" };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                "This operation atomically replaces or creates one policy-root-bounded file.",
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, "write_text", affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            "confirm_write_text",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/write-text/confirm"), new JsonObject
            {
                ["path"] = path,
                ["text"] = text,
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileUploadPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "preview_upload", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "contentBase64") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryRequiredString(payload, "contentBase64", MaximumUploadBase64Characters, out var contentBase64))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, canonical path, and bounded canonical base64 content."));
        }

        return ExecuteAsync(
            "netratel_files",
            "preview_upload",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/upload/preview"), new JsonObject { ["path"] = path, ["contentBase64"] = contentBase64 }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileUploadConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "confirm_upload", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "contentBase64", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryRequiredString(payload, "contentBase64", MaximumUploadBase64Characters, out var contentBase64) ||
            !TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey))
        {
            return Task.FromResult(Invalid("request requires the unchanged preview target, canonical base64 content, planToken, and idempotencyKey."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{path}" };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                "This operation atomically replaces or creates one policy-root-bounded file from bounded base64 content.",
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, "upload", affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            "confirm_upload",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/upload/confirm"), new JsonObject
            {
                ["path"] = path,
                ["contentBase64"] = contentBase64,
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileCreateDirectoryPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "preview_create_directory", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and a new canonical directory path."));
        }

        return ExecuteAsync(
            "netratel_files",
            "preview_create_directory",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/create-directory/preview"), new JsonObject { ["path"] = path }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileCreateDirectoryConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "confirm_create_directory", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey))
        {
            return Task.FromResult(Invalid("request requires the unchanged new directory path, planToken, and idempotencyKey."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{path}" };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                "This operation creates one policy-root-bounded directory.",
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, "create_directory", affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            "confirm_create_directory",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/create-directory/confirm"), new JsonObject
            {
                ["path"] = path,
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileDeletePreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "preview_delete", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and an existing canonical file or empty directory path."));
        }

        return ExecuteAsync(
            "netratel_files",
            "preview_delete",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/delete/preview"), new JsonObject { ["path"] = path }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileDeleteConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", "confirm_delete", DevelopmentFileOperations));
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey))
        {
            return Task.FromResult(Invalid("request requires the unchanged file or empty directory path, planToken, and idempotencyKey."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{path}" };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                "This destructive operation deletes one policy-root-bounded file or empty directory and never recurses.",
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, "delete", affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            "confirm_delete",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, "/files/delete/confirm"), new JsonObject
            {
                ["path"] = path,
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileRelocationPreviewAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", $"preview_{operation}", DevelopmentFileOperations));
        if (!TryProductionFileRelocationRequest(request, requirePlanCredentials: false, out var tenantId, out var agentId, out var sourcePath, out var destinationPath, out _, out _))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and distinct canonical sourcePath and destinationPath values."));
        }

        return ExecuteAsync(
            "netratel_files",
            $"preview_{operation}",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, $"/files/{operation}/preview"), new JsonObject
            {
                ["sourcePath"] = sourcePath,
                ["destinationPath"] = destinationPath
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionFileRelocationConfirmAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes)
            return Task.FromResult(Unsupported("netratel_files", $"confirm_{operation}", DevelopmentFileOperations));
        if (!TryProductionFileRelocationRequest(request, requirePlanCredentials: true, out var tenantId, out var agentId, out var sourcePath, out var destinationPath, out var planToken, out var idempotencyKey))
        {
            return Task.FromResult(Invalid("request requires unchanged distinct sourcePath and destinationPath values plus the opaque planToken and idempotencyKey from preview."));
        }

        var affected = new[]
        {
            $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{sourcePath}",
            $"{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}:{destinationPath}"
        };
        if (!confirm)
        {
            var summary = string.Equals(operation, "move", StringComparison.Ordinal)
                ? "This destructive operation moves one policy-root-bounded regular file to a distinct new path and never replaces an existing destination."
                : "This operation copies one policy-read-root-bounded regular file to a distinct new policy-write-root path and never replaces an existing destination.";
            return Task.FromResult(new NetRatelToolResponse(
                false,
                "confirmation_required",
                summary,
                AffectedIds: affected,
                RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, operation, affected)));
        }

        return ExecuteAsync(
            "netratel_files",
            $"confirm_{operation}",
            () => client.SendAsync(HttpMethod.Post, OperatorFilePath(tenantId, agentId, $"/files/{operation}/confirm"), new JsonObject
            {
                ["sourcePath"] = sourcePath,
                ["destinationPath"] = destinationPath,
                ["planToken"] = planToken,
                ["idempotencyKey"] = idempotencyKey
            }, cancellationToken),
            cancellationToken);
    }

    private static bool TryProductionFileRelocationRequest(
        JsonElement? request,
        bool requirePlanCredentials,
        out int tenantId,
        out Guid agentId,
        out string sourcePath,
        out string destinationPath,
        out string planToken,
        out string idempotencyKey)
    {
        tenantId = default;
        agentId = default;
        sourcePath = string.Empty;
        destinationPath = string.Empty;
        planToken = string.Empty;
        idempotencyKey = string.Empty;
        var fields = requirePlanCredentials
            ? new[] { "tenantId", "agentId", "sourcePath", "destinationPath", "planToken", "idempotencyKey" }
            : new[] { "tenantId", "agentId", "sourcePath", "destinationPath" };
        return TryObject(request, out var payload) &&
            ContainsOnly(payload, fields) &&
            TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
            TryRequiredGuid(payload, "agentId", out agentId) &&
            TryRequiredString(payload, "sourcePath", 4096, out sourcePath) &&
            TryRequiredString(payload, "destinationPath", 4096, out destinationPath) &&
            !string.Equals(sourcePath, destinationPath, StringComparison.Ordinal) &&
            (!requirePlanCredentials ||
                (TryRequiredString(payload, "planToken", 128, out planToken) &&
                    TryRequiredString(payload, "idempotencyKey", 128, out idempotencyKey)));
    }

    private Task<NetRatelToolResponse> DevelopmentClientLogSourcesAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentClient(request, out var tenantId, out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        return GetAsync("netratel_client_logs", "sources", DevelopmentClientLogOperations,
            OperatorObservabilityPath(tenantId, agentId, "/logs/sources"), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentClientLogHistoryAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "sourceId", "cursor", "pageSize", "severity", "prefix", "category", "text") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "sourceId", 128, out var sourceId) ||
            !TryOptionalString(payload, "cursor", 256, out var cursor) ||
            !TryOptionalInt(payload, "pageSize", 1, 100, out var pageSize) ||
            !TryOptionalString(payload, "severity", 64, out var severity) ||
            !TryOptionalString(payload, "prefix", 256, out var prefix) ||
            !TryOptionalString(payload, "category", 256, out var category) ||
            !TryOptionalString(payload, "text", 512, out var text))
        {
            return Task.FromResult(Invalid("request requires target identifiers and sourceId; optional cursor, pageSize, and text are bounded."));
        }

        var endpoint = OperatorObservabilityPath(tenantId, agentId, "/logs/history");
        endpoint = WithQuery(endpoint, ("sourceId", sourceId), ("cursor", cursor), ("pageSize", Format(pageSize)), ("severity", severity), ("prefix", prefix), ("category", category), ("text", text));
        return GetAsync("netratel_client_logs", "history", DevelopmentClientLogOperations, endpoint, cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentClientLogSearchAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "sourceId", "cursor", "pageSize", "severity", "prefix", "category", "text") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "sourceId", 128, out var sourceId) ||
            !TryRequiredString(payload, "text", 512, out var text) ||
            !TryOptionalString(payload, "cursor", 256, out var cursor) ||
            !TryOptionalInt(payload, "pageSize", 1, 100, out var pageSize) ||
            !TryOptionalString(payload, "severity", 64, out var severity) ||
            !TryOptionalString(payload, "prefix", 256, out var prefix) ||
            !TryOptionalString(payload, "category", 256, out var category))
        {
            return Task.FromResult(Invalid("request requires target identifiers, sourceId, and bounded search text; optional cursor, pageSize, and filters are bounded."));
        }

        var endpoint = OperatorObservabilityPath(tenantId, agentId, "/logs/search");
        endpoint = WithQuery(endpoint, ("sourceId", sourceId), ("cursor", cursor), ("pageSize", Format(pageSize)), ("severity", severity), ("prefix", prefix), ("category", category), ("text", text));
        return GetAsync("netratel_client_logs", "search", DevelopmentClientLogOperations, endpoint, cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentClientLogTailAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "sourceId", "windowSeconds", "maxRecords") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "sourceId", 128, out var sourceId) ||
            !TryOptionalInt(payload, "windowSeconds", 1, 15, out var windowSeconds) ||
            !TryOptionalInt(payload, "maxRecords", 1, 100, out var maxRecords))
        {
            return Task.FromResult(Invalid("request requires target identifiers and sourceId; windowSeconds is 1 through 15 and maxRecords is 1 through 100."));
        }

        var endpoint = OperatorObservabilityPath(tenantId, agentId, "/logs/tail");
        endpoint = WithQuery(endpoint, ("sourceId", sourceId), ("windowSeconds", Format(windowSeconds)), ("maxRecords", Format(maxRecords)));
        return GetAsync("netratel_client_logs", "tail", DevelopmentClientLogOperations, endpoint, cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentClientLogResyncAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "sourceId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "sourceId", 128, out var sourceId))
        {
            return Task.FromResult(Invalid("request requires target identifiers and one advertised sourceId."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/{sourceId}" };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("resync", "This operation would request one bounded current history window and clear a previously reported log gap only if that refresh succeeds.", affected));
        }

        var endpoint = $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/logs/resync";
        return ExecuteAsync("netratel_client_logs", "resync", () => client.SendAsync(HttpMethod.Post, endpoint, new JsonObject { ["sourceId"] = sourceId }, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ClientLogResyncPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "sourceId") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "sourceId", 128, out var sourceId))
        {
            return Task.FromResult(Invalid("request requires target identifiers and one advertised sourceId."));
        }

        var endpoint = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/logs/resync/preview";
        return ExecuteAsync(
            "netratel_client_logs",
            "preview_resync",
            () => client.SendAsync(HttpMethod.Post, endpoint, new JsonObject { ["sourceId"] = sourceId }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> ClientLogResyncConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || payload is null)
        {
            return Task.FromResult(Invalid("request requires unchanged target identifiers and sourceId plus opaque planToken and idempotencyKey returned by preview_resync."));
        }

        if (!ContainsOnly(payload, "tenantId", "agentId", "sourceId", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "sourceId", 128, out var sourceId) ||
            !TryPlanCredentials(payload))
        {
            return Task.FromResult(Invalid("request requires unchanged target identifiers and sourceId plus opaque planToken and idempotencyKey returned by preview_resync."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(
                "confirm_resync",
                "This operation would consume the supplied server-issued resync plan and refresh one bounded current log-history baseline only if authorization remains current.",
                [$"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/{sourceId}"]));
        }

        var endpoint = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/logs/resync/confirm";
        return ExecuteAsync(
            "netratel_client_logs",
            "confirm_resync",
            () => client.SendAsync(HttpMethod.Post, endpoint, new JsonObject
            {
                ["sourceId"] = sourceId,
                ["planToken"] = payload["planToken"]!.GetValue<string>(),
                ["idempotencyKey"] = payload["idempotencyKey"]!.GetValue<string>()
            }, cancellationToken),
            cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentClientTelemetrySnapshotAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentClient(request, out var tenantId, out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        return GetAsync("netratel_client_telemetry", "snapshot", DevelopmentClientTelemetryOperations,
            OperatorObservabilityPath(tenantId, agentId, "/telemetry/snapshot"), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentClientTelemetryWindowAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "windowSeconds", "maxSamples") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryOptionalInt(payload, "windowSeconds", 1, 15, out var windowSeconds) ||
            !TryOptionalInt(payload, "maxSamples", 1, 20, out var maxSamples))
        {
            return Task.FromResult(Invalid("request requires target identifiers; windowSeconds is 1 through 15 and maxSamples is 1 through 20."));
        }

        var endpoint = OperatorObservabilityPath(tenantId, agentId, "/telemetry/stream-window");
        endpoint = WithQuery(endpoint, ("windowSeconds", Format(windowSeconds)), ("maxSamples", Format(maxSamples)));
        return GetAsync("netratel_client_telemetry", "stream_window", DevelopmentClientTelemetryOperations, endpoint, cancellationToken);
    }

    private static bool TryDevelopmentClient(JsonElement? request, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryObject(request, out var payload) &&
               ContainsOnly(payload, "tenantId", "agentId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId);
    }

    /// <summary>
    /// Selects the policy-admitted V2 operator routes. The name is retained
    /// for internal call-site compatibility while Development can opt in via
    /// its immutable host configuration.
    /// </summary>
    internal bool UsesProductionOperatorRoutes => hostContext?.OperatorSurfaceEnabled == true;

    private string OperatorObservabilityPath(int tenantId, Guid agentId, string suffix)
    {
        var root = UsesProductionOperatorRoutes
            ? "/api/v2/mcp/operator/agents"
            : "/api/v2/development/mcp/agents";
        return $"{root}/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}{suffix}";
    }

    private string OperatorFilePath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}{suffix}";

    private Task<NetRatelToolResponse> DevelopmentArtifactReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentArtifactRequest(request, out var tenantId, out var agentId, out var artifactId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and artifactId UUID."));
        }

        var endpoint = $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/files/artifacts/{artifactId:D}";
        if (operation == "download")
        {
            endpoint += "/download";
        }

        return GetAsync("netratel_files", operation, DevelopmentFileReadOperations, endpoint, cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentFileCollectAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "path", "marker") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "path", 4096, out var path) ||
            !TryCampaignMarker(payload, out var marker))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, bounded fixture path, and a safe MCP-QA campaign marker."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}" };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(false, "confirmation_required", "This operation would collect one marker-owned Development fixture file as a short-lived artifact.", AffectedIds: affected, RequiresConfirmation: true, Confirmation: new NetRatelConfirmation("confirm", true, "collect", affected)));
        }

        var endpoint = $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/files/collect";
        return ExecuteAsync("netratel_files", "collect", () => client.SendAsync(HttpMethod.Post, endpoint, new JsonObject { ["path"] = path, ["marker"] = marker }, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentArtifactCleanupAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentArtifactRequest(request, out var tenantId, out var agentId, out var artifactId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId, and artifactId UUID."));
        }

        var affected = new[] { artifactId.ToString("D") };
        if (!confirm)
        {
            return Task.FromResult(new NetRatelToolResponse(false, "confirmation_required", "This operation would delete one marker-owned Development file artifact.", AffectedIds: affected, RequiresConfirmation: true, Confirmation: new NetRatelConfirmation("confirm", true, "cleanup", affected)));
        }

        var endpoint = $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/files/artifacts/{artifactId:D}";
        return ExecuteAsync("netratel_files", "cleanup", () => client.SendAsync(HttpMethod.Delete, endpoint, null, cancellationToken), cancellationToken);
    }

    private static bool TryDevelopmentArtifactRequest(JsonElement? request, out int tenantId, out Guid agentId, out Guid artifactId)
    {
        tenantId = default;
        agentId = default;
        artifactId = default;
        return TryObject(request, out var payload) &&
               ContainsOnly(payload, "tenantId", "agentId", "artifactId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryRequiredGuid(payload, "artifactId", out artifactId);
    }

    private Task<NetRatelToolResponse> ProductionJobReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionJobTarget(payload, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        if (operation == "list")
        {
            if (!ContainsOnly(payload, "tenantId", "agentId")) return Task.FromResult(Invalid("Production jobs.list requires only tenantId and agentId."));
            return GetAsync(JobsToolName, operation, ProductionJobOperations, ProductionJobPath(tenantId, agentId, string.Empty), cancellationToken);
        }
        if (!TryPositiveLong(payload, "jobId", out var jobId) || !ContainsOnly(payload, "tenantId", "agentId", "jobId"))
            return Task.FromResult(Invalid("request requires only tenantId, agentId, and a positive owned jobId."));
        var suffix = operation switch { "details" => "/details", "params" => "/params", "steps" => "/steps", _ => string.Empty };
        return GetAsync(JobsToolName, operation, ProductionJobOperations, ProductionJobPath(tenantId, agentId, $"/{jobId.ToString(CultureInfo.InvariantCulture)}{suffix}"), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionJobMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionJobTarget(payload, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        if (!TryProductionJobMutationShape(operation, payload, out var body))
            return Task.FromResult(Invalid("request does not match the typed Production job mutation shape; non-create changes require jobId and expectedVersion, while parameter and step changes require their exact identifier."));
        var endpoint = ProductionJobPath(tenantId, agentId, $"/{(confirm ? "confirm" : "preview")}/{operation}");
        if (confirm && !TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production job mutation requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync(JobsToolName, operation, () => client.SendAsync(HttpMethod.Post, endpoint, body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionJobRunReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionJobTarget(payload, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        if (operation is "list" or "query")
        {
            long? jobId = null;
            if (!ContainsOnly(payload, "tenantId", "agentId", "jobId"))
                return Task.FromResult(Invalid("Production job-runs list/query accepts tenantId, agentId, and optional positive jobId."));
            if (payload?["jobId"] is not null)
            {
                if (!TryPositiveLong(payload, "jobId", out var parsedJobId))
                    return Task.FromResult(Invalid("Production job-runs list/query accepts tenantId, agentId, and optional positive jobId."));
                jobId = parsedJobId;
            }
            return GetAsync(RunsToolName, operation, ProductionJobRunOperations,
                WithQuery(ProductionJobPath(tenantId, agentId, operation == "query" ? "/runs/query" : "/runs"), ("jobId", Format(jobId))), cancellationToken);
        }
        if (!ContainsOnly(payload, "tenantId", "agentId", "jobRunId") || !TryPositiveUlong(payload, "jobRunId", out var runId))
            return Task.FromResult(Invalid("request requires only tenantId, agentId, and a positive owned jobRunId."));
        var suffix = operation switch { "steps" => "/steps", "logs" => "/logs", _ => string.Empty };
        return GetAsync(RunsToolName, operation, ProductionJobRunOperations,
            ProductionJobPath(tenantId, agentId, $"/runs/{runId.ToString(CultureInfo.InvariantCulture)}{suffix}"), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionJobRunMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionJobTarget(payload, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        if (operation == "start")
        {
            if (!TryPositiveLong(payload, "jobId", out var jobId) || !ContainsOnly(payload, "tenantId", "agentId", "jobId", "inputs", "planToken", "idempotencyKey"))
                return Task.FromResult(Invalid("Production job-runs.start requires tenantId, agentId, jobId, optional typed inputs, and plan credentials only after preview."));
            var body = CloneWithout(payload!, "tenantId", "agentId", "jobId");
            if (confirm && !TryPlanCredentials(body)) return Task.FromResult(Invalid("A confirmed Production job start requires unchanged opaque planToken and idempotencyKey from its preview."));
            return ExecuteAsync(RunsToolName, operation, () => client.SendAsync(HttpMethod.Post,
                ProductionJobPath(tenantId, agentId, $"/{jobId.ToString(CultureInfo.InvariantCulture)}/runs/{(confirm ? "confirm" : "preview")}"), body, cancellationToken), cancellationToken);
        }
        if (!TryPositiveUlong(payload, "jobRunId", out var runId) || !ContainsOnly(payload, "tenantId", "agentId", "jobRunId", "reason", "planToken", "idempotencyKey"))
            return Task.FromResult(Invalid("Production job-runs cancel/delete requires tenantId, agentId, jobRunId, optional bounded reason, and plan credentials only after preview."));
        var actionBody = CloneWithout(payload!, "tenantId", "agentId", "jobRunId");
        if (confirm && !TryPlanCredentials(actionBody)) return Task.FromResult(Invalid("A confirmed Production job run action requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync(RunsToolName, operation, () => client.SendAsync(HttpMethod.Post,
            ProductionJobPath(tenantId, agentId, $"/runs/{runId.ToString(CultureInfo.InvariantCulture)}/{operation}/{(confirm ? "confirm" : "preview")}"), actionBody, cancellationToken), cancellationToken);
    }

    private static bool TryProductionJobTarget(JsonObject? payload, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) && TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryProductionJobMutationShape(string operation, JsonObject? payload, out JsonObject body)
    {
        body = new JsonObject();
        var allowed = operation switch
        {
            "create" => new[] { "tenantId", "agentId", "job", "planToken", "idempotencyKey" },
            "update" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "job", "planToken", "idempotencyKey" },
            "delete" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "planToken", "idempotencyKey" },
            "param_add" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "parameter", "planToken", "idempotencyKey" },
            "param_update" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "parameterId", "parameter", "planToken", "idempotencyKey" },
            "param_delete" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "parameterId", "planToken", "idempotencyKey" },
            "step_add" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "step", "planToken", "idempotencyKey" },
            "step_update" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "stepId", "step", "planToken", "idempotencyKey" },
            "step_reorder" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "stepId", "ordinal", "planToken", "idempotencyKey" },
            "step_delete" => new[] { "tenantId", "agentId", "jobId", "expectedVersion", "stepId", "planToken", "idempotencyKey" },
            _ => []
        };
        if (allowed.Length == 0 || !ContainsOnly(payload, allowed)) return false;
        if (operation != "create" && (!TryPositiveLong(payload, "jobId", out _) || !TryPositiveLong(payload, "expectedVersion", out _))) return false;
        if (operation is "param_update" or "param_delete" && !TryPositiveLong(payload, "parameterId", out _)) return false;
        if (operation is "step_update" or "step_reorder" or "step_delete" && !TryPositiveLong(payload, "stepId", out _)) return false;
        if (operation == "step_reorder" && !TryPositiveInt(payload, "ordinal", out _)) return false;
        if (operation is "create" or "update" && payload?["job"] is not JsonObject) return false;
        if (operation.StartsWith("param_", StringComparison.Ordinal) && operation != "param_delete" && payload?["parameter"] is not JsonObject) return false;
        if (operation.StartsWith("step_", StringComparison.Ordinal) && operation is not "step_reorder" and not "step_delete" && payload?["step"] is not JsonObject) return false;
        body = CloneWithout(payload!, "tenantId", "agentId");
        return true;
    }

    private static JsonObject CloneWithout(JsonObject payload, params string[] fields)
    {
        var result = payload.DeepClone().AsObject();
        foreach (var field in fields) result.Remove(field);
        return result;
    }

    private static string ProductionJobPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/jobs{suffix}";

    private Task<NetRatelToolResponse> ProductionTaskReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionTaskTarget(payload, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        var basePath = ProductionTaskPath(tenantId, agentId, string.Empty);
        if (operation is "list" or "recent")
        {
            if (!ContainsOnly(payload, "tenantId", "agentId", "state", "sinceUtc", "limit") ||
                !TryOptionalTaskState(payload, out var state) || !TryOptionalDateTimeOffset(payload, "sinceUtc", out var sinceUtc) ||
                !TryOptionalInt(payload, "limit", 1, 100, out var limit))
                return Task.FromResult(Invalid("Production task list/recent accepts target, optional bounded state, ISO-8601 sinceUtc, and limit from 1 through 100."));
            return GetAsync("netratel_tasks", operation, ProductionTaskOperations,
                WithQuery(operation == "recent" ? basePath + "/recent" : basePath,
                    ("state", state), ("sinceUtc", sinceUtc?.ToString("O", CultureInfo.InvariantCulture)), ("limit", Format(limit))), cancellationToken);
        }
        if (operation == "get")
        {
            if (!ContainsOnly(payload, "tenantId", "agentId", "taskId") || !TryPositiveLong(payload, "taskId", out var taskId))
                return Task.FromResult(Invalid("Production task get requires only target and a positive owned taskId."));
            return GetAsync("netratel_tasks", operation, ProductionTaskOperations, $"{basePath}/{taskId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        }
        if (operation == "logs")
        {
            if (!ContainsOnly(payload, "tenantId", "agentId", "taskId", "sinceId", "stream", "limit") || !TryPositiveLong(payload, "taskId", out var taskId) ||
                !TryOptionalNonNegativeLong(payload, "sinceId", out var sinceId) || !TryOptionalTaskLogStream(payload, out var stream) || !TryOptionalInt(payload, "limit", 1, 100, out var limit))
                return Task.FromResult(Invalid("Production task logs requires target, taskId, optional non-negative sinceId, stream all/stdout/stderr, and limit from 1 through 100."));
            return GetAsync("netratel_tasks", operation, ProductionTaskOperations,
                WithQuery($"{basePath}/{taskId.ToString(CultureInfo.InvariantCulture)}/logs", ("sinceId", Format(sinceId)), ("stream", stream), ("limit", Format(limit))), cancellationToken);
        }
        if (!ContainsOnly(payload, "tenantId", "agentId", "requestId", "sinceId", "stream", "limit") || !TryRequiredString(payload, "requestId", 32, out var requestId) ||
            !IsHexIdentifier(requestId) || !TryOptionalNonNegativeLong(payload, "sinceId", out var byRequestSince) || !TryOptionalTaskLogStream(payload, out var byRequestStream) || !TryOptionalInt(payload, "limit", 1, 100, out var byRequestLimit))
            return Task.FromResult(Invalid("Production task logs_by_request requires target, an exact command requestId, optional non-negative sinceId, stream all/stdout/stderr, and limit from 1 through 100."));
        return GetAsync("netratel_tasks", operation, ProductionTaskOperations,
            WithQuery(basePath + "/logs", ("requestId", requestId), ("sinceId", Format(byRequestSince)), ("stream", byRequestStream), ("limit", Format(byRequestLimit))), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTaskMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionTaskTarget(payload, out var tenantId, out var agentId) || !TryProductionTaskMutationShape(operation, payload, out var body))
            return Task.FromResult(Invalid("request does not match the typed closed Production task mutation shape."));
        if (confirm && !TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production task mutation requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync("netratel_tasks", operation, () => client.SendAsync(HttpMethod.Post,
            ProductionTaskPath(tenantId, agentId, $"/{(confirm ? "confirm" : "preview")}/{operation}"), body, cancellationToken), cancellationToken);
    }

    private static bool TryProductionTaskTarget(JsonObject? payload, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) && TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryProductionTaskMutationShape(string operation, JsonObject? payload, out JsonObject body)
    {
        body = new JsonObject();
        var allowed = operation switch
        {
            "create_command" => new[] { "tenantId", "agentId", "command", "planToken", "idempotencyKey" },
            "run_library_script" => new[] { "tenantId", "agentId", "script", "planToken", "idempotencyKey" },
            "cancel" => new[] { "tenantId", "agentId", "taskId", "planToken", "idempotencyKey" },
            _ => []
        };
        if (allowed.Length == 0 || !ContainsOnly(payload, allowed)) return false;
        if (operation == "create_command" && !IsTaskCommandPayload(payload?["command"] as JsonObject)) return false;
        if (operation == "run_library_script" && !IsTaskScriptPayload(payload?["script"] as JsonObject)) return false;
        if (operation == "cancel" && !TryPositiveLong(payload, "taskId", out _)) return false;
        body = CloneWithout(payload!, "tenantId", "agentId");
        return true;
    }

    private static bool IsTaskCommandPayload(JsonObject? value) =>
        value is not null && ContainsOnly(value, "shell", "command", "workingDirectory", "timeoutSeconds", "maximumOutputBytes", "environmentReferences") &&
        TryRequiredString(value, "shell", 32, out _) && TryRequiredString(value, "command", 32 * 1024, out _) &&
        TryRequiredString(value, "workingDirectory", 4096, out _) && TryRequiredInt(value, "timeoutSeconds", 1, 60 * 60, out _) &&
        TryRequiredInt(value, "maximumOutputBytes", 1, 48 * 1024, out _) &&
        (value["environmentReferences"] is null || TryStringArray(value, "environmentReferences", 32, 128));

    private static bool IsTaskScriptPayload(JsonObject? value) =>
        value is not null && ContainsOnly(value, "scriptId", "version", "contentHash", "parameters") &&
        TryPositiveLong(value, "scriptId", out _) && TryPositiveLong(value, "version", out _) &&
        TryRequiredString(value, "contentHash", 64, out var hash) && hash.Length == 64 && hash.All(char.IsAsciiHexDigit) &&
        (value["parameters"] is null || IsStringMap(value["parameters"] as JsonObject, 32, 128, 1024));

    private static bool TryOptionalTaskState(JsonObject? payload, out string? state)
    {
        state = null;
        if (payload?["state"] is null) return true;
        return TryRequiredString(payload, "state", 32, out state) && state is "Pending" or "Processing" or "CancelRequested" or "Completed" or "Failed" or "Cancelled";
    }

    private static bool TryOptionalDateTimeOffset(JsonObject? payload, string property, out DateTimeOffset? value)
    {
        value = null;
        if (payload?[property] is null) return true;
        if (payload[property] is not JsonValue node || !node.TryGetValue<string>(out var raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryStringArray(JsonObject payload, string property, int maximumItems, int maximumLength) =>
        payload[property] is JsonArray values && values.Count <= maximumItems && values.All(node => node is JsonValue value && value.TryGetValue<string>(out var entry) && entry.Length > 0 && entry.Length <= maximumLength && IsIdentifier(entry));

    private static bool IsStringMap(JsonObject? value, int maximumItems, int maximumKeyLength, int maximumValueLength) =>
        value is not null && value.Count <= maximumItems && value.All(pair => IsIdentifier(pair.Key) && pair.Key.Length <= maximumKeyLength && pair.Value is JsonValue node && node.TryGetValue<string>(out var entry) && entry.Length <= maximumValueLength);

    private static bool IsHexIdentifier(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);
    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string ProductionTaskPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/tasks{suffix}";

    private Task<NetRatelToolResponse> ProductionRequestReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionRequestTarget(payload, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        var basePath = ProductionRequestPath(tenantId, agentId, string.Empty);
        if (operation == "list")
        {
            if (!ContainsOnly(payload, "tenantId", "agentId", "state", "jobId", "sinceUtc", "limit") ||
                !TryOptionalRequestState(payload, out var state) || !TryOptionalPositiveLong(payload, "jobId", out var jobId) ||
                !TryOptionalDateTimeOffset(payload, "sinceUtc", out var sinceUtc) || !TryOptionalInt(payload, "limit", 1, 100, out var limit))
                return Task.FromResult(Invalid("Production request list accepts target, optional exact state, jobId, ISO-8601 sinceUtc, and limit from 1 through 100."));
            return GetAsync("netratel_requests", operation, ProductionRequestOperations,
                WithQuery(basePath, ("state", state), ("jobId", Format(jobId)), ("sinceUtc", sinceUtc?.ToString("O", CultureInfo.InvariantCulture)), ("limit", Format(limit))), cancellationToken);
        }
        if (!ContainsOnly(payload, "tenantId", "agentId", "requestId") || !TryPositiveInt(payload, "requestId", out var requestId))
            return Task.FromResult(Invalid("Production request get requires only target and a positive owned requestId."));
        return GetAsync("netratel_requests", operation, ProductionRequestOperations, $"{basePath}/{requestId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionRequestMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionRequestTarget(payload, out var tenantId, out var agentId) || !TryProductionRequestMutationShape(operation, payload, out var body))
            return Task.FromResult(Invalid("request does not match the typed closed Production request mutation shape."));
        if (confirm && !TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production request mutation requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync("netratel_requests", operation, () => client.SendAsync(HttpMethod.Post,
            ProductionRequestPath(tenantId, agentId, $"/{(confirm ? "confirm" : "preview")}/{operation}"), body, cancellationToken), cancellationToken);
    }

    private static bool TryProductionRequestTarget(JsonObject? payload, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) && TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryProductionRequestMutationShape(string operation, JsonObject? payload, out JsonObject body)
    {
        body = new JsonObject();
        var allowed = operation switch
        {
            "create" => new[] { "tenantId", "agentId", "jobId", "summary", "planToken", "idempotencyKey" },
            "update" => new[] { "tenantId", "agentId", "requestId", "expectedVersion", "summary", "planToken", "idempotencyKey" },
            "claim" => new[] { "tenantId", "agentId", "requestId", "expectedVersion", "claimReference", "planToken", "idempotencyKey" },
            "complete" or "fail" => new[] { "tenantId", "agentId", "requestId", "expectedVersion", "resultSummary", "planToken", "idempotencyKey" },
            "cancel" => new[] { "tenantId", "agentId", "requestId", "expectedVersion", "resultSummary", "planToken", "idempotencyKey" },
            _ => []
        };
        if (allowed.Length == 0 || !ContainsOnly(payload, allowed)) return false;
        var mutationTarget = operation != "create" && (!TryPositiveInt(payload, "requestId", out _) || !TryPositiveLong(payload, "expectedVersion", out _));
        if (mutationTarget) return false;
        var valid = operation switch
        {
            "create" => TryPositiveLong(payload, "jobId", out _) && TryRequiredString(payload, "summary", 4096, out _),
            "update" => TryRequiredString(payload, "summary", 4096, out _),
            "claim" => TryRequiredString(payload, "claimReference", 128, out _),
            "complete" or "fail" => TryRequiredString(payload, "resultSummary", 48 * 1024, out _),
            "cancel" => payload?["resultSummary"] is null || TryRequiredString(payload, "resultSummary", 48 * 1024, out _),
            _ => false
        };
        if (!valid) return false;
        body = CloneWithout(payload!, "tenantId", "agentId");
        return true;
    }

    private static bool TryOptionalRequestState(JsonObject? payload, out string? state)
    {
        state = null;
        if (payload?["state"] is null) return true;
        return TryRequiredString(payload, "state", 32, out state) && state is "Pending" or "Claimed" or "Completed" or "Failed" or "Cancelled";
    }

    private static bool TryOptionalPositiveLong(JsonObject? payload, string property, out long? value)
    {
        value = null;
        if (payload?[property] is null) return true;
        if (!TryPositiveLong(payload, property, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static string ProductionRequestPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/requests{suffix}";

    private Task<NetRatelToolResponse> ProductionTenantReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (operation == "list")
        {
            if (request is null)
                return GetAsync("netratel_tenants", operation, ProductionTenantOperations, "/api/v2/mcp/operator/tenants", cancellationToken);
            if (!TryObject(request, out var payload) || !ContainsOnly(payload, "cursor", "limit") ||
                !TryOptionalInt(payload, "cursor", 0, int.MaxValue, out var cursor) || !TryOptionalInt(payload, "limit", 1, 100, out var limit))
                return Task.FromResult(Invalid("Production tenant list accepts only optional non-negative cursor and limit from 1 through 100."));
            return GetAsync("netratel_tenants", operation, ProductionTenantOperations,
                WithQuery("/api/v2/mcp/operator/tenants", ("cursor", Format(cursor)), ("limit", Format(limit))), cancellationToken);
        }

        if (!TryObject(request, out var getPayload) || !ContainsOnly(getPayload, "tenantId") || !TryPositiveInt(getPayload, "tenantId", out var tenantId))
            return Task.FromResult(Invalid("Production tenant get requires only a positive tenantId."));
        return GetAsync("netratel_tenants", operation, ProductionTenantOperations,
            $"/api/v2/mcp/operator/tenants/{tenantId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTenantMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionTenantMutationShape(operation, payload, out var body))
            return Task.FromResult(Invalid("request does not match the typed closed Production tenant mutation schema."));
        if (confirm && !TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production tenant mutation requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync("netratel_tenants", operation, () => client.SendAsync(HttpMethod.Post,
            $"/api/v2/mcp/operator/tenants/{(confirm ? "confirm" : "preview")}/{operation}", body, cancellationToken), cancellationToken);
    }

    private static bool TryProductionTenantMutationShape(string operation, JsonObject? payload, out JsonObject body)
    {
        body = new JsonObject();
        var common = new[] { "name", "description", "location", "domains", "contactPerson", "contactEmail", "autoUpdate", "autoUpdateChannel", "autoUpdateTargetVersion", "planToken", "idempotencyKey" };
        var allowed = operation switch
        {
            "create" => common,
            "update" => common.Concat(["tenantId", "expectedVersion"]).ToArray(),
            "delete" => new[] { "tenantId", "expectedVersion", "cascade", "planToken", "idempotencyKey" },
            _ => []
        };
        if (allowed.Length == 0 || !ContainsOnly(payload, allowed)) return false;
        if (operation == "delete")
        {
            if (!TryPositiveInt(payload, "tenantId", out _) || !TryPositiveLong(payload, "expectedVersion", out _) ||
                !TryOptionalBoolean(payload, "cascade", out var cascade) || !cascade.HasValue)
                return false;
            body = payload!.DeepClone().AsObject();
            return true;
        }
        if (!TryRequiredString(payload, "name", 160, out _) || !TryTenantDomains(payload!, out _) ||
            !TryOptionalBoolean(payload, "autoUpdate", out var autoUpdate) || !autoUpdate.HasValue ||
            !TryOptionalString(payload, "description", 4096, out _) || !TryOptionalString(payload, "location", 256, out _) ||
            !TryOptionalString(payload, "contactPerson", 256, out _) || !TryOptionalString(payload, "contactEmail", 320, out _) ||
            !TryOptionalString(payload, "autoUpdateTargetVersion", 128, out _) || !TryOptionalString(payload, "autoUpdateChannel", 16, out var channel) ||
            channel is not null and not "stable" and not "prerelease")
            return false;
        if (operation == "update" && (!TryPositiveInt(payload, "tenantId", out _) || !TryPositiveLong(payload, "expectedVersion", out _)))
            return false;
        body = payload!.DeepClone().AsObject();
        return true;
    }

    private static bool TryTenantDomains(JsonObject payload, out IReadOnlyList<string> domains)
    {
        domains = [];
        if (payload["domains"] is not JsonArray values || values.Count > 64 || values.Any(node => node is not JsonValue value || !value.TryGetValue<string>(out var domain) || domain.Length is 0 or > 253 || domain.Any(char.IsControl)))
            return false;
        domains = values.Select(node => node!.GetValue<string>()).ToArray();
        return domains.Distinct(StringComparer.OrdinalIgnoreCase).Count() == domains.Count;
    }

    private const string JobsToolName = "netratel_jobs";
    private const string RunsToolName = "netratel_job_runs";

    private Task<NetRatelToolResponse> JobListAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        string? error = null;
        if (!TryObject(request, out var payload) || !TryBuildJobListPath(payload, out var path, out error))
        {
            return Task.FromResult(Invalid(error ?? "request must be a closed bounded job-list filter object."));
        }

        return GetAsync("netratel_jobs", "list", JobReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> JobByIdAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "jobId") ||
            !TryPositiveLong(payload, "jobId", out var jobId))
        {
            return Task.FromResult(Invalid("request.jobId must be a positive integer job identifier."));
        }

        var suffix = operation switch
        {
            "details" => "/details",
            "params" => "/params",
            "steps" => "/steps",
            _ => string.Empty
        };
        return GetAsync("netratel_jobs", operation, JobReadOperations,
            $"/api/v1/jobs/{jobId.ToString(CultureInfo.InvariantCulture)}{suffix}", cancellationToken);
    }

    private Task<NetRatelToolResponse> JobRunQueryAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        string? error = null;
        if (!TryObject(request, out var payload) || !TryBuildJobRunQueryPath(payload, out var path, out error))
        {
            return Task.FromResult(Invalid(error ?? "request must be a closed bounded job-run query object."));
        }

        return GetAsync("netratel_job_runs", "query", JobRunReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> JobRunByIdAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryPositiveUlong(payload, "jobRunId", out var jobRunId))
        {
            return Task.FromResult(Invalid("request.jobRunId must be a positive integer job-run identifier."));
        }

        var suffix = operation == "steps" ? "/steps" : string.Empty;
        return GetAsync("netratel_job_runs", operation, JobRunReadOperations,
            $"/api/v1/jobruns/{jobRunId.ToString(CultureInfo.InvariantCulture)}{suffix}", cancellationToken);
    }

    private Task<NetRatelToolResponse> JobRunLogsAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !TryPositiveUlong(payload, "jobRunId", out var jobRunId) ||
            !TryNonNegativeInt(payload, "ordinal", out var ordinal))
        {
            return Task.FromResult(Invalid("request.jobRunId must be a positive integer and request.ordinal must be a non-negative integer."));
        }

        return GetAsync("netratel_job_runs", "logs", JobRunReadOperations,
            $"/api/v1/jobruns/{jobRunId.ToString(CultureInfo.InvariantCulture)}/steps/{ordinal.ToString(CultureInfo.InvariantCulture)}/logs", cancellationToken);
    }

    private Task<NetRatelToolResponse> TaskByRequestIdAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "requestId") ||
            !TryRequiredString(payload, "requestId", 200, out var requestId))
        {
            return Task.FromResult(Invalid("request.requestId must be a non-empty string containing at most 200 characters."));
        }

        return GetAsync("netratel_tasks", "list", TaskReadOperations,
            WithQuery("/api/v2/tasks", ("requestId", requestId)), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptListAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentScriptTarget(request, out var tenantId, out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        return GetAsync("netratel_scripts", "list", DevelopmentScriptReadOperations,
            DevelopmentScriptPath(tenantId, agentId), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptByIdAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentScriptIdentity(request, out var tenantId, out var agentId, out var scriptId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive scriptId."));
        }

        var suffix = operation == "params" ? "/params" : string.Empty;
        return GetAsync("netratel_scripts", operation, DevelopmentScriptReadOperations,
            $"{DevelopmentScriptPath(tenantId, agentId)}/{scriptId.ToString(CultureInfo.InvariantCulture)}{suffix}", cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptCreateAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "marker", "name", "description", "shell", "executionMode") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryMarker(payload, "marker", required: true, out var marker) ||
            !TryOptionalString(payload, "name", 120, out var name) ||
            !TryOptionalString(payload, "description", 512, out var description) ||
            !TryOptionalString(payload, "executionMode", 32, out var executionMode) ||
            (executionMode is not null && executionMode is not "standard" and not "cancellation_probe") ||
            !TryRequiredShell(payload, "shell", out var shell))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, safe marker, shell (sh or powershell), optional bounded name and description, and optional executionMode (standard or cancellation_probe)."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/marker/{marker}" };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("create_marker", "This operation would create one server-generated harmless Development marker script.", affected));
        }

        var body = new JsonObject { ["marker"] = marker, ["shell"] = shell };
        if (name is not null) body["name"] = name;
        if (description is not null) body["description"] = description;
        if (executionMode is not null) body["executionMode"] = executionMode;
        return ExecuteAsync("netratel_scripts", "create_marker", () => client.SendAsync(HttpMethod.Post, DevelopmentScriptPath(tenantId, agentId), body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptUpdateAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "scriptId", "marker", "name", "description") ||
            !TryDevelopmentScriptIdentityValues(payload, out var tenantId, out var agentId, out var scriptId) ||
            !TryMarker(payload, "marker", required: false, out var marker) ||
            !TryOptionalString(payload, "name", 120, out var name) ||
            !TryOptionalString(payload, "description", 512, out var description) ||
            (marker is null && name is null && description is null))
        {
            return Task.FromResult(Invalid("request requires target identifiers, scriptId, and at least one safe marker, bounded name, or bounded description."));
        }

        var affected = new[] { scriptId.ToString(CultureInfo.InvariantCulture) };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("update_marker", "This operation would update one target-owned server-generated Development marker script.", affected));
        }

        var body = new JsonObject();
        if (marker is not null) body["marker"] = marker;
        if (name is not null) body["name"] = name;
        if (description is not null) body["description"] = description;
        return ExecuteAsync("netratel_scripts", "update_marker", () => client.SendAsync(HttpMethod.Put, $"{DevelopmentScriptPath(tenantId, agentId)}/{scriptId.ToString(CultureInfo.InvariantCulture)}", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptParseManifestAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentScriptIdentity(request, out var tenantId, out var agentId, out var scriptId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive scriptId."));
        }

        var affected = new[] { scriptId.ToString(CultureInfo.InvariantCulture) };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("parse_manifest", "This operation would refresh the fixed manifest of one target-owned Development marker script.", affected));
        }

        return ExecuteAsync("netratel_scripts", "parse_manifest", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentScriptPath(tenantId, agentId)}/{scriptId.ToString(CultureInfo.InvariantCulture)}/parse-manifest", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptDeleteAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentScriptIdentity(request, out var tenantId, out var agentId, out var scriptId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive scriptId."));
        }

        var affected = new[] { scriptId.ToString(CultureInfo.InvariantCulture) };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("delete", "This operation would delete one target-owned Development marker script.", affected));
        }

        return ExecuteAsync("netratel_scripts", "delete", () => client.SendAsync(HttpMethod.Delete, $"{DevelopmentScriptPath(tenantId, agentId)}/{scriptId.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentScriptRunAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentScriptIdentity(request, out var tenantId, out var agentId, out var scriptId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted V2 agentId UUID, and positive scriptId."));
        }

        var affected = new[] { scriptId.ToString(CultureInfo.InvariantCulture) };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("run", "This operation would submit one target-owned server-generated harmless Development marker script with a server-generated request identifier.", affected));
        }

        return ExecuteAsync("netratel_scripts", "run", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentScriptPath(tenantId, agentId)}/{scriptId.ToString(CultureInfo.InvariantCulture)}/runs", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionScriptListAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (hostContext?.Target.Instance == "dev" && TryObject(request, out var payload) && payload is not null &&
            ContainsOnly(payload, "tenantId", "agentId", "afterId", "limit") &&
            TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var developmentTenant) &&
            TryRequiredGuid(payload, "agentId", out var developmentAgent) &&
            TryOptionalNonNegativeLong(payload, "afterId", out var afterId) &&
            TryOptionalInt(payload, "limit", 1, 100, out var limit))
            return GetAsync("netratel_scripts", "list", ProductionScriptOperations,
                ProductionScriptPath(developmentTenant, developmentAgent, $"?afterId={afterId ?? 0}&limit={limit ?? 100}"), cancellationToken);
        if (!TryProductionScriptTarget(request, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted agentId UUID; full-Dev list also accepts bounded afterId/limit."));
        return GetAsync("netratel_scripts", "list", ProductionScriptOperations, ProductionScriptPath(tenantId, agentId, string.Empty), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionScriptByIdAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryProductionScriptIdentity(request, out var tenantId, out var agentId, out var scriptId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId, persisted agentId UUID, and positive owned scriptId."));
        var suffix = operation == "params" ? "/params" : string.Empty;
        return GetAsync("netratel_scripts", operation, ProductionScriptOperations,
            ProductionScriptPath(tenantId, agentId, $"/{scriptId.ToString(CultureInfo.InvariantCulture)}{suffix}"), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionScriptValidateAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryProductionScriptDraft(request, out var tenantId, out var agentId, out var draft))
            return Task.FromResult(Invalid("request requires a Production target and a typed bounded script draft with a matching SHA-256 contentHash; secret parameters may name references but cannot contain values or defaults."));
        return ExecuteAsync("netratel_scripts", "validate", () => client.SendAsync(HttpMethod.Post,
            ProductionScriptPath(tenantId, agentId, "/validate"), draft, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionScriptMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryProductionScriptMutation(operation, request, out var tenantId, out var agentId, out var body))
            return Task.FromResult(Invalid("request does not match the closed Production script mutation schema. Updates, manifest parses, and deletes require the current ETag version; creates and updates require the complete typed script draft."));
        if (!confirm)
        {
            return ExecuteAsync("netratel_scripts", operation, () => client.SendAsync(HttpMethod.Post,
                ProductionScriptPath(tenantId, agentId, $"/preview/{operation}"), body, cancellationToken), cancellationToken);
        }
        if (!TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production script mutation requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync("netratel_scripts", operation, () => client.SendAsync(HttpMethod.Post,
            ProductionScriptPath(tenantId, agentId, $"/confirm/{operation}"), body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionScriptRunAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryProductionScriptRun(request, out var tenantId, out var agentId, out var scriptId, out var body))
            return Task.FromResult(Invalid("request requires the Production target, owned scriptId, exact current version and contentHash, and optional typed parameter values. Secret parameters accept named references only."));
        var endpoint = ProductionScriptPath(tenantId, agentId, $"/{scriptId.ToString(CultureInfo.InvariantCulture)}/runs/{(confirm ? "confirm" : "preview")}");
        if (confirm && !TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production script run requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync("netratel_scripts", "run", () => client.SendAsync(HttpMethod.Post, endpoint, body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> OperatorTerminalAvailabilityAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId") ||
            !TryDevelopmentTerminalTarget(payload, out var tenantId, out var agentId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId and persisted V2 agentId UUID."));
        }

        var path = UsesProductionOperatorRoutes
            ? $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/terminal/availability"
            : $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/terminal/availability";
        return GetAsync("netratel_terminal", "availability", UsesProductionOperatorRoutes ? ProductionTerminalOperations : DevelopmentTerminalReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalOpenPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionTerminalOpen(request, out var tenantId, out var agentId, out var shell, out var workingDirectory, out var columns, out var rows))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted agentId, bounded shell and workingDirectory, and optional columns (40-300) and rows (10-120)."));

        var body = new JsonObject { ["shell"] = shell, ["workingDirectory"] = workingDirectory };
        if (columns.HasValue) body["columns"] = columns.Value;
        if (rows.HasValue) body["rows"] = rows.Value;
        return ExecuteAsync("netratel_terminal", "preview_open", () => client.SendAsync(HttpMethod.Post,
            ProductionTerminalPath(tenantId, agentId, "/sessions/preview"), body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalOpenConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "shell", "workingDirectory", "columns", "rows", "planToken", "idempotencyKey") ||
            !TryProductionTerminalOpen(payload, out var tenantId, out var agentId, out var shell, out var workingDirectory, out var columns, out var rows) ||
            !TryPlanCredentials(payload))
        {
            return Task.FromResult(Invalid("request requires the unchanged Production open target, shell, workingDirectory, optional dimensions, and opaque preview credentials."));
        }
        if (!confirm)
        {
            return Task.FromResult(Confirmation("open", "This operation would consume the supplied policy-bound Production terminal plan and open one durable, bounded target session.", [$"{tenantId}/{agentId:D}/terminal"]));
        }

        var body = new JsonObject
        {
            ["shell"] = shell,
            ["workingDirectory"] = workingDirectory,
            ["planToken"] = payload["planToken"]!.GetValue<string>(),
            ["idempotencyKey"] = payload["idempotencyKey"]!.GetValue<string>()
        };
        if (columns.HasValue) body["columns"] = columns.Value;
        if (rows.HasValue) body["rows"] = rows.Value;
        return ExecuteAsync("netratel_terminal", "open", () => client.SendAsync(HttpMethod.Post,
            ProductionTerminalPath(tenantId, agentId, "/sessions/confirm"), body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalSessionReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionTerminalSession(request, out var tenantId, out var agentId, out var sessionId))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted agentId, and an owned Production terminal sessionId."));
        var suffix = operation == "diagnostics" ? "/diagnostics" : string.Empty;
        return GetAsync("netratel_terminal", operation, ProductionTerminalOperations,
            ProductionTerminalPath(tenantId, agentId, $"/sessions/{sessionId}{suffix}"), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalStreamWindowAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "sessionId", "windowSeconds", "maxRecords", "afterSequence") ||
            !TryProductionTerminalSession(payload, out var tenantId, out var agentId, out var sessionId) ||
            !TryOptionalInt(payload, "windowSeconds", 1, 15, out var seconds) || !TryOptionalInt(payload, "maxRecords", 1, 100, out var records) ||
            !TryOptionalTerminalOutputCursor(payload, out var afterSequence))
        {
            return Task.FromResult(Invalid("request requires the Production target and owned sessionId; windowSeconds is 1-15 and maxRecords is 1-100; afterSequence is the decimal-string nextSequence from the previous response (omit for the initial read)."));
        }
        return GetAsync("netratel_terminal", "stream_window", ProductionTerminalOperations,
            WithQuery(ProductionTerminalPath(tenantId, agentId, $"/sessions/{sessionId}/stream-window"), ("windowSeconds", Format(seconds)), ("maxRecords", Format(records)), ("afterSequence", afterSequence)), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalInputAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "sessionId", "input") ||
            !TryProductionTerminalSession(payload, out var tenantId, out var agentId, out var sessionId) ||
            !TryTerminalInput(payload, out var input))
        {
            return Task.FromResult(Invalid("request requires the Production target, owned sessionId, and 1-16384 bytes of UTF-8 terminal input."));
        }
        return ExecuteAsync("netratel_terminal", "send_input", () => client.SendAsync(HttpMethod.Post,
            ProductionTerminalPath(tenantId, agentId, $"/sessions/{sessionId}/input"), new JsonObject { ["input"] = input }, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalResizeAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "sessionId", "columns", "rows") ||
            !TryProductionTerminalSession(payload, out var tenantId, out var agentId, out var sessionId) ||
            !TryRequiredInt(payload, "columns", 40, 300, out var columns) || !TryRequiredInt(payload, "rows", 10, 120, out var rows))
        {
            return Task.FromResult(Invalid("request requires the Production target, owned sessionId, columns (40-300), and rows (10-120)."));
        }
        if (!confirm) return Task.FromResult(Confirmation("resize", "This operation would resize one policy-frozen Production terminal session.", [sessionId]));
        return ExecuteAsync("netratel_terminal", "resize", () => client.SendAsync(HttpMethod.Post,
            ProductionTerminalPath(tenantId, agentId, $"/sessions/{sessionId}/resize"), new JsonObject { ["columns"] = columns, ["rows"] = rows }, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionTerminalCloseAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionTerminalSession(request, out var tenantId, out var agentId, out var sessionId))
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted agentId, and an owned Production terminal sessionId."));
        if (!confirm) return Task.FromResult(Confirmation("close", "This operation would request an idempotent close for one durable Production terminal session.", [sessionId]));
        return ExecuteAsync("netratel_terminal", "close", () => client.SendAsync(HttpMethod.Post,
            ProductionTerminalPath(tenantId, agentId, $"/sessions/{sessionId}/close"), null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionCommandAvailabilityAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionCommandTarget(request, out var tenantId, out var agentId))
            return Task.FromResult(Invalid("request requires a positive Production tenantId and persisted agentId UUID."));
        return GetAsync("netratel_commands", "availability", ProductionCommandOperations,
            ProductionCommandPath(tenantId, agentId, "/availability"), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionCommandPreviewAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionCommandExecute(request, out var command))
            return Task.FromResult(Invalid("request requires a positive Production target, policy-allowlisted shell and workingDirectory, one 1-32768 byte command, optional safe environmentReferences, optional timeoutSeconds (1-3600), and optional maximumOutputBytes (1-49152)."));
        return ExecuteAsync("netratel_commands", "preview_execute", () => client.SendAsync(HttpMethod.Post,
            ProductionCommandPath(command.TenantId, command.AgentId, "/preview"), CommandBody(command), cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionCommandConfirmAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "shell", "command", "workingDirectory", "environmentReferences", "timeoutSeconds", "maximumOutputBytes", "planToken", "idempotencyKey") ||
            !TryProductionCommandExecute(payload, out var command) || !TryPlanCredentials(payload))
        {
            return Task.FromResult(Invalid("request requires the unchanged Production command preview target, shell, command, workingDirectory, optional environment references and limits, plus opaque preview credentials."));
        }
        if (!confirm)
        {
            return Task.FromResult(Confirmation("execute", "This operation would consume the supplied policy-bound command plan and dispatch one destructive bounded command to the exact target.", [$"{command.TenantId}/{command.AgentId:D}/command"]));
        }

        var body = CommandBody(command);
        body["planToken"] = payload["planToken"]!.GetValue<string>();
        body["idempotencyKey"] = payload["idempotencyKey"]!.GetValue<string>();
        return ExecuteAsync("netratel_commands", "execute", () => client.SendAsync(HttpMethod.Post,
            ProductionCommandPath(command.TenantId, command.AgentId, "/confirm"), body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionCommandReadAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionCommandReference(request, out var tenantId, out var agentId, out var commandId))
            return Task.FromResult(Invalid("request requires a positive Production target and an owned commandId."));
        return GetAsync("netratel_commands", "get", ProductionCommandOperations,
            ProductionCommandPath(tenantId, agentId, $"/{commandId}"), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionCommandCancelAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!UsesProductionOperatorRoutes || !TryProductionCommandReference(request, out var tenantId, out var agentId, out var commandId))
            return Task.FromResult(Invalid("request requires a positive Production target and an owned commandId."));
        if (!confirm)
            return Task.FromResult(Confirmation("cancel", "This operation would request cancellation of one caller-owned Production command.", [commandId]));
        return ExecuteAsync("netratel_commands", "cancel", () => client.SendAsync(HttpMethod.Post,
            ProductionCommandPath(tenantId, agentId, $"/{commandId}/cancel"), null, cancellationToken), cancellationToken);
    }

    private static JsonObject CommandBody(ProductionCommand command)
    {
        var body = new JsonObject
        {
            ["shell"] = command.Shell,
            ["command"] = command.Command,
            ["workingDirectory"] = command.WorkingDirectory
        };
        if (command.EnvironmentReferences.Count > 0)
            body["environmentReferences"] = new JsonArray(command.EnvironmentReferences.Select(reference => JsonValue.Create(reference)).ToArray());
        if (command.TimeoutSeconds.HasValue)
            body["timeoutSeconds"] = command.TimeoutSeconds.Value;
        if (command.MaximumOutputBytes.HasValue)
            body["maximumOutputBytes"] = command.MaximumOutputBytes.Value;
        return body;
    }

    private static string ProductionCommandPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/commands{suffix}";

    private static string ProductionTerminalPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/terminal{suffix}";

    private Task<NetRatelToolResponse> DevelopmentTerminalGetAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentTerminalSession(request, out var sessionId))
        {
            return Task.FromResult(Invalid("request.sessionId must be a target-owned Development terminal session identifier."));
        }

        return GetAsync("netratel_terminal", "get", DevelopmentTerminalReadOperations, DevelopmentTerminalSessionPath(sessionId), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentTerminalStreamAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "sessionId", "windowSeconds", "maxRecords") ||
            !TryDevelopmentTerminalSessionValue(payload, out var sessionId) ||
            !TryOptionalInt(payload, "windowSeconds", 1, 15, out var windowSeconds) ||
            !TryOptionalInt(payload, "maxRecords", 1, 100, out var maxRecords))
        {
            return Task.FromResult(Invalid("request requires a target-owned terminal sessionId and optional bounded windowSeconds (1-15) and maxRecords (1-100)."));
        }

        return GetAsync("netratel_terminal", "stream", DevelopmentTerminalReadOperations,
            WithQuery($"{DevelopmentTerminalSessionPath(sessionId)}/stream", ("windowSeconds", Format(windowSeconds)), ("maxRecords", Format(maxRecords))), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentTerminalOpenAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "shell", "cols", "rows") ||
            !TryDevelopmentTerminalTarget(payload, out var tenantId, out var agentId) ||
            !TryRequiredShell(payload, "shell", out var shell) ||
            !TryOptionalInt(payload, "cols", 40, 300, out var columns) ||
            !TryOptionalInt(payload, "rows", 10, 120, out var rows))
        {
            return Task.FromResult(Invalid("request requires target identifiers, shell (sh or powershell), and optional bounded cols and rows."));
        }

        var affected = new[] { $"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/terminal" };
        if (!confirm)
        {
            return Task.FromResult(Confirmation("open", "This operation would open one target-owned ephemeral Development terminal session without a working directory or arbitrary input.", affected));
        }

        var body = new JsonObject { ["shell"] = shell };
        if (columns.HasValue) body["cols"] = columns.Value;
        if (rows.HasValue) body["rows"] = rows.Value;
        return ExecuteAsync("netratel_terminal", "open", () => client.SendAsync(HttpMethod.Post,
            $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/terminal/sessions", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentTerminalSessionMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentTerminalSession(request, out var sessionId))
        {
            return Task.FromResult(Invalid("request.sessionId must be a target-owned Development terminal session identifier."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation(operation, $"This operation would {operation.Replace('_', ' ')} one target-owned Development terminal session.", [sessionId]));
        }

        var suffix = operation switch
        {
            "self_test" => "/self-test",
            "deployment-control-plane_inspect" => "/deployment-control-plane-inspect",
            _ => "/close"
        };
        return ExecuteAsync("netratel_terminal", operation, () => client.SendAsync(HttpMethod.Post, DevelopmentTerminalSessionPath(sessionId) + suffix, null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentTerminalFixtureAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "sessionId", "action", "marker") ||
            !TryDevelopmentTerminalSessionValue(payload, out var sessionId) ||
            !TryRequiredString(payload, "action", 6, out var action) ||
            action is not ("create" or "delete") ||
            !TryMarker(payload, "marker", required: true, out var marker))
        {
            return Task.FromResult(Invalid("request requires a target-owned terminal sessionId, fixture action (create or delete), and a safe marker."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation("fixture", $"This operation would {action} one server-generated marker file under the target's persisted Development fixture root.", [sessionId]));
        }

        var body = new JsonObject { ["action"] = action, ["marker"] = marker };
        return ExecuteAsync("netratel_terminal", "fixture", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentTerminalSessionPath(sessionId)}/fixture", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentTerminalResizeAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "sessionId", "cols", "rows") ||
            !TryDevelopmentTerminalSessionValue(payload, out var sessionId) ||
            !TryRequiredInt(payload, "cols", 40, 300, out var columns) ||
            !TryRequiredInt(payload, "rows", 10, 120, out var rows))
        {
            return Task.FromResult(Invalid("request requires a target-owned terminal sessionId and cols (40-300) and rows (10-120)."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation("resize", "This operation would resize one target-owned Development terminal session.", [sessionId]));
        }

        var body = new JsonObject { ["cols"] = columns, ["rows"] = rows };
        return ExecuteAsync("netratel_terminal", "resize", () => client.SendAsync(HttpMethod.Post, $"{DevelopmentTerminalSessionPath(sessionId)}/resize", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentOnboardingCollateralAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "runtime") ||
            !TryDevelopmentOnboardingTarget(payload, out var tenantId, out var agentId) ||
            !TryRequiredShellRuntime(payload, out var runtime))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted Development agentId, and runtime linux-x64 or win-x64."));
        }

        return GetAsync("netratel_onboarding", "collateral", DevelopmentOnboardingReadOperations,
            $"{DevelopmentOnboardingPath(tenantId, agentId)}/collateral/{runtime}", cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentOnboardingEnrollmentAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentOnboardingEnrollment(request, out var tenantId, out var agentId, out var enrollmentCodeId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted Development agentId, and target-owned enrollmentCodeId UUID."));
        }

        return GetAsync("netratel_onboarding", "get_enrollment", DevelopmentOnboardingReadOperations,
            $"{DevelopmentOnboardingPath(tenantId, agentId)}/enrollment-codes/{enrollmentCodeId:D}", cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentOnboardingCreateAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId", "agentId", "validForMinutes", "marker") ||
            !TryDevelopmentOnboardingTarget(payload, out var tenantId, out var agentId) ||
            !TryRequiredInt(payload, "validForMinutes", 5, 10, out var validForMinutes) ||
            !TryCampaignMarker(payload, out var marker))
        {
            return Task.FromResult(Invalid("request requires target identifiers, validForMinutes from 5 through 10, and a safe MCP-QA campaign marker."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation("create_enrollment", "This operation would create one target-owned single-use Development enrollment code. Its raw code is shown only in the immediate confirmed result and must not be persisted.", [$"{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}"]));
        }

        var body = new JsonObject { ["validForMinutes"] = validForMinutes, ["marker"] = marker };
        return ExecuteAsync("netratel_onboarding", "create_enrollment", () => client.SendAsync(HttpMethod.Post,
            $"{DevelopmentOnboardingPath(tenantId, agentId)}/enrollment-codes", body, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> DevelopmentOnboardingRevokeAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryDevelopmentOnboardingEnrollment(request, out var tenantId, out var agentId, out var enrollmentCodeId))
        {
            return Task.FromResult(Invalid("request requires a positive tenantId, persisted Development agentId, and target-owned enrollmentCodeId UUID."));
        }

        if (!confirm)
        {
            return Task.FromResult(Confirmation("revoke_enrollment", "This operation would revoke one target-owned Development enrollment code.", [enrollmentCodeId.ToString("D")]));
        }

        return ExecuteAsync("netratel_onboarding", "revoke_enrollment", () => client.SendAsync(HttpMethod.Post,
            $"{DevelopmentOnboardingPath(tenantId, agentId)}/enrollment-codes/{enrollmentCodeId:D}/revoke", null, cancellationToken), cancellationToken);
    }

    private Task<NetRatelToolResponse> ProductionOnboardingReadAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionOnboardingTenant(payload, out var tenantId))
            return Task.FromResult(Invalid("Production onboarding requests require a positive tenantId and must not supply an agentId."));

        return operation switch
        {
            "collateral" when ContainsOnly(payload, "tenantId", "runtime") && TryRequiredShellRuntime(payload, out var runtime) =>
                GetAsync(ToolName(), operation, ProductionOnboardingReadOperations, $"{ProductionOnboardingPath(tenantId)}/collateral/{runtime}", cancellationToken),
            "collateral_download" when ContainsOnly(payload, "tenantId", "runtime") && TryRequiredShellRuntime(payload, out var downloadRuntime) =>
                GetAsync(ToolName(), operation, ProductionOnboardingReadOperations, $"{ProductionOnboardingPath(tenantId)}/collateral/{downloadRuntime}/download", cancellationToken),
            "get_enrollment" when ContainsOnly(payload, "tenantId", "enrollmentCodeId") && TryRequiredGuid(payload, "enrollmentCodeId", out var enrollmentCodeId) =>
                GetAsync(ToolName(), operation, ProductionOnboardingReadOperations, $"{ProductionOnboardingPath(tenantId)}/enrollments/{enrollmentCodeId:D}", cancellationToken),
            "list_enrollments" when ContainsOnly(payload, "tenantId", "status", "cursor", "limit") &&
                TryOptionalString(payload, "status", 16, out var status) && (status is null or "active" or "expired" or "revoked" or "all") &&
                TryOptionalNonNegativeLong(payload, "cursor", out var cursor) && TryOptionalInt(payload, "limit", 1, 100, out var limit) =>
                GetAsync(ToolName(), operation, ProductionOnboardingReadOperations, WithQuery($"{ProductionOnboardingPath(tenantId)}/enrollments", ("status", status), ("cursor", Format(cursor)), ("limit", Format(limit))), cancellationToken),
            _ => Task.FromResult(Invalid("Production onboarding read request does not match its typed closed schema."))
        };
    }

    private Task<NetRatelToolResponse> ProductionOnboardingMutationAsync(string operation, JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) || !TryProductionOnboardingMutation(operation, payload, out var tenantId, out var body))
            return Task.FromResult(Invalid("Production onboarding mutation request does not match its typed closed schema."));
        if (confirm && !TryPlanCredentials(body))
            return Task.FromResult(Invalid("A confirmed Production onboarding mutation requires unchanged opaque planToken and idempotencyKey from its preview."));
        return ExecuteAsync(ToolName(), operation, () => client.SendAsync(HttpMethod.Post,
            $"{ProductionOnboardingPath(tenantId)}/{(confirm ? "confirm" : "preview")}/{ProductionOnboardingAction(operation)}", body, cancellationToken), cancellationToken);
    }

    private static bool TryProductionOnboardingMutation(string operation, JsonObject? payload, out int tenantId, out JsonObject body)
    {
        tenantId = default;
        body = new JsonObject();
        if (!TryProductionOnboardingTenant(payload, out tenantId)) return false;
        var allowed = operation switch
        {
            "create_enrollment" => new[] { "tenantId", "runtime", "validForMinutes", "maxUses", "planToken", "idempotencyKey" },
            "revoke_enrollment" => new[] { "tenantId", "enrollmentCodeId", "planToken", "idempotencyKey" },
            _ => []
        };
        if (allowed.Length == 0 || !ContainsOnly(payload, allowed)) return false;
        var valid = operation == "create_enrollment"
            ? TryRequiredShellRuntime(payload, out _) && TryRequiredInt(payload, "validForMinutes", 5, 10, out _) && TryRequiredInt(payload, "maxUses", 1, 10, out _)
            : TryRequiredGuid(payload, "enrollmentCodeId", out _);
        if (!valid || !TryOptionalString(payload, "planToken", 128, out _) || !TryOptionalString(payload, "idempotencyKey", 128, out _)) return false;
        body = payload!.DeepClone().AsObject();
        body.Remove("tenantId");
        return true;
    }

    private static bool TryProductionOnboardingTenant(JsonObject? payload, out int tenantId) =>
        TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) && !payload!.ContainsKey("agentId");

    private static string ProductionOnboardingPath(int tenantId) =>
        $"/api/v2/mcp/operator/tenants/{tenantId.ToString(CultureInfo.InvariantCulture)}/onboarding";

    private static string ProductionOnboardingAction(string operation) => operation.Replace('_', '-');

    private static string ToolName() => "netratel_onboarding";

    private static bool TryDevelopmentScriptTarget(JsonElement? request, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryObject(request, out var payload) && TryDevelopmentScriptTarget(payload, out tenantId, out agentId);
    }

    private static bool TryDevelopmentScriptTarget(JsonObject? payload, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return ContainsOnly(payload, "tenantId", "agentId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryDevelopmentScriptIdentity(JsonElement? request, out int tenantId, out Guid agentId, out long scriptId)
    {
        tenantId = default;
        agentId = default;
        scriptId = default;
        return TryObject(request, out var payload) && TryDevelopmentScriptIdentity(payload, out tenantId, out agentId, out scriptId);
    }

    private static bool TryDevelopmentScriptIdentity(JsonObject? payload, out int tenantId, out Guid agentId, out long scriptId)
    {
        tenantId = default;
        agentId = default;
        scriptId = default;
        return ContainsOnly(payload, "tenantId", "agentId", "scriptId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryPositiveLong(payload, "scriptId", out scriptId);
    }

    private static bool TryDevelopmentScriptIdentityValues(JsonObject? payload, out int tenantId, out Guid agentId, out long scriptId)
    {
        tenantId = default;
        agentId = default;
        scriptId = default;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryPositiveLong(payload, "scriptId", out scriptId);
    }

    private static bool TryProductionScriptTarget(JsonElement? request, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryObject(request, out var payload) && payload is not null &&
               ContainsOnly(payload, "tenantId", "agentId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryProductionScriptIdentity(JsonElement? request, out int tenantId, out Guid agentId, out long scriptId)
    {
        tenantId = default;
        agentId = default;
        scriptId = default;
        return TryObject(request, out var payload) && payload is not null &&
               ContainsOnly(payload, "tenantId", "agentId", "scriptId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryPositiveLong(payload, "scriptId", out scriptId);
    }

    private static bool TryProductionScriptDraft(JsonElement? request, out int tenantId, out Guid agentId, out JsonObject draft)
    {
        tenantId = default;
        agentId = default;
        draft = new JsonObject();
        if (!TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "name", "description", "shellType", "content", "contentHash", "parameters", "timeoutSeconds", "workingDirectory", "declaredSideEffects", "manifestJson") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) ||
            !TryRequiredGuid(payload, "agentId", out agentId) ||
            !TryRequiredString(payload, "name", 120, out _) ||
            !TryRequiredString(payload, "description", 512, out _) ||
            !TryRequiredString(payload, "shellType", 32, out var shell) ||
            shell is not ("sh" or "bash" or "powershell" or "pwsh") ||
            !TryScriptContent(payload, out _) ||
            !TrySha256(payload, "contentHash") ||
            !TryRequiredInt(payload, "timeoutSeconds", 1, 60 * 60, out _) ||
            !TryRequiredString(payload, "workingDirectory", 4096, out var workingDirectory) || workingDirectory.Any(char.IsControl) ||
            payload["parameters"] is not JsonArray parameters || parameters.Count > 32 ||
            payload["declaredSideEffects"] is not JsonArray sideEffects || sideEffects.Count is < 1 or > 5 ||
            !TryOptionalString(payload, "manifestJson", 16 * 1024, out _))
        {
            return false;
        }

        foreach (var name in new[] { "name", "description", "shellType", "content", "contentHash", "parameters", "timeoutSeconds", "workingDirectory", "declaredSideEffects", "manifestJson" })
        {
            if (payload.TryGetPropertyValue(name, out var value))
                draft[name] = value?.DeepClone();
        }
        return true;
    }

    private static bool TryProductionScriptMutation(string action, JsonElement? request, out int tenantId, out Guid agentId, out JsonObject body)
    {
        tenantId = default;
        agentId = default;
        body = new JsonObject();
        if (!TryObject(request, out var payload) || payload is null)
            return false;

        if (payload.ContainsKey("source") || payload.ContainsKey("expectedSourceRevision") || payload.ContainsKey("expectedContentHash"))
            return TryCanonicalScriptMutation(action, payload, out tenantId, out agentId, out body);

        var allowed = action switch
        {
            "create" => new[] { "tenantId", "agentId", "name", "description", "shellType", "content", "contentHash", "parameters", "timeoutSeconds", "workingDirectory", "declaredSideEffects", "manifestJson", "planToken", "idempotencyKey" },
            "update" => new[] { "tenantId", "agentId", "scriptId", "expectedVersion", "name", "description", "shellType", "content", "contentHash", "parameters", "timeoutSeconds", "workingDirectory", "declaredSideEffects", "manifestJson", "planToken", "idempotencyKey" },
            "parse_manifest" => new[] { "tenantId", "agentId", "scriptId", "expectedVersion", "manifestJson", "planToken", "idempotencyKey" },
            "delete" => new[] { "tenantId", "agentId", "scriptId", "expectedVersion", "planToken", "idempotencyKey" },
            _ => []
        };
        if (allowed.Length == 0 || !ContainsOnly(payload, allowed) ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) || !TryRequiredGuid(payload, "agentId", out agentId))
        {
            return false;
        }

        if (action is "create" or "update")
        {
            var draftRequest = payload.DeepClone()!.AsObject();
            draftRequest.Remove("scriptId");
            draftRequest.Remove("expectedVersion");
            draftRequest.Remove("planToken");
            draftRequest.Remove("idempotencyKey");
            if (!TryProductionScriptDraft(JsonSerializer.SerializeToElement(draftRequest), out _, out _, out var draft)) return false;
            body["script"] = draft;
        }
        if (action is "update" or "parse_manifest" or "delete")
        {
            if (!TryPositiveLong(payload, "scriptId", out var scriptId) || !TryPositiveLong(payload, "expectedVersion", out var expectedVersion)) return false;
            body["scriptId"] = scriptId;
            body["expectedVersion"] = expectedVersion;
        }
        if (action == "parse_manifest")
        {
            if (!TryRequiredString(payload, "manifestJson", 16 * 1024, out _)) return false;
            body["manifestJson"] = payload["manifestJson"]!.DeepClone();
        }
        if (payload.TryGetPropertyValue("planToken", out var token)) body["planToken"] = token?.DeepClone();
        if (payload.TryGetPropertyValue("idempotencyKey", out var key)) body["idempotencyKey"] = key?.DeepClone();
        return true;
    }

    private static bool TryCanonicalScriptMutation(string action, JsonObject payload,
        out int tenantId, out Guid agentId, out JsonObject body)
    {
        tenantId = default;
        agentId = default;
        body = new JsonObject();
        if (action is not ("create" or "update" or "parse_manifest" or "delete") ||
            !ContainsOnly(payload, "tenantId", "agentId", "scriptId", "source", "expectedSourceRevision", "expectedContentHash", "manifestJson", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) || !TryRequiredGuid(payload, "agentId", out agentId)) return false;
        if (action is "create" or "update")
        {
            if (payload["source"] is not JsonObject source ||
                !ContainsOnly(source, "name", "folderPath", "description", "content", "scriptType", "manifestJson") ||
                !TryRequiredString(source, "name", 120, out _) || !TryRequiredString(source, "scriptType", 64, out _) ||
                source["content"] is not JsonValue contentValue || !contentValue.TryGetValue<string>(out var content) ||
                content is null || Encoding.UTF8.GetByteCount(content) > 64 * 1024 ||
                !TryOptionalString(source, "folderPath", 512, out _) || !TryOptionalString(source, "description", 4096, out _) ||
                !TryOptionalString(source, "manifestJson", 16 * 1024, out _) || payload.ContainsKey("manifestJson")) return false;
            body["source"] = source.DeepClone();
        }
        else if (payload.ContainsKey("source")) return false;
        if (action == "create")
        {
            if (payload.ContainsKey("scriptId") || payload.ContainsKey("expectedSourceRevision") || payload.ContainsKey("expectedContentHash")) return false;
        }
        else
        {
            if (!TryPositiveLong(payload, "scriptId", out var scriptId) || !TryPositiveLong(payload, "expectedSourceRevision", out var revision) ||
                !TrySha256(payload, "expectedContentHash")) return false;
            body["scriptId"] = scriptId;
            body["expectedSourceRevision"] = revision;
            body["expectedContentHash"] = payload["expectedContentHash"]!.DeepClone();
        }
        if (action == "parse_manifest")
        {
            if (!TryRequiredString(payload, "manifestJson", 16 * 1024, out _)) return false;
            body["manifestJson"] = payload["manifestJson"]!.DeepClone();
        }
        else if (payload.ContainsKey("manifestJson")) return false;
        if (payload.TryGetPropertyValue("planToken", out var token)) body["planToken"] = token?.DeepClone();
        if (payload.TryGetPropertyValue("idempotencyKey", out var key)) body["idempotencyKey"] = key?.DeepClone();
        return true;
    }

    private static bool TryProductionScriptRun(JsonElement? request, out int tenantId, out Guid agentId, out long scriptId, out JsonObject body)
    {
        tenantId = default;
        agentId = default;
        scriptId = default;
        body = new JsonObject();
        if (!TryObject(request, out var payload) || payload is null ||
            !ContainsOnly(payload, "tenantId", "agentId", "scriptId", "version", "contentHash", "parameters", "planToken", "idempotencyKey") ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) || !TryRequiredGuid(payload, "agentId", out agentId) ||
            !TryPositiveLong(payload, "scriptId", out scriptId) || !TryPositiveLong(payload, "version", out var version) ||
            !TrySha256(payload, "contentHash") ||
            (payload.TryGetPropertyValue("parameters", out var parameterNode) && parameterNode is not JsonObject))
        {
            return false;
        }
        body["version"] = version;
        body["contentHash"] = payload["contentHash"]!.DeepClone();
        if (payload.TryGetPropertyValue("parameters", out var parameters)) body["parameters"] = parameters?.DeepClone();
        if (payload.TryGetPropertyValue("planToken", out var token)) body["planToken"] = token?.DeepClone();
        if (payload.TryGetPropertyValue("idempotencyKey", out var key)) body["idempotencyKey"] = key?.DeepClone();
        return true;
    }

    private static bool TryScriptContent(JsonObject payload, out string content)
    {
        content = string.Empty;
        if (!TryRequiredString(payload, "content", 64 * 1024, out content) || content.IndexOf('\0') >= 0)
            return false;
        try { return new UTF8Encoding(false, true).GetByteCount(content) is > 0 and <= 64 * 1024; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool TrySha256(JsonObject payload, string name) =>
        TryRequiredString(payload, name, 64, out var hash) && hash.Length == 64 && hash.All(char.IsAsciiHexDigit);

    private static string ProductionScriptPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/scripts{suffix}";

    private static bool TryMarker(JsonObject? payload, string name, bool required, out string? marker)
    {
        marker = null;
        if (!TryGetOptionalString(payload, name, 128, out var value))
        {
            return false;
        }

        if (value is null)
        {
            return !required;
        }

        if (value.Length == 0 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
        {
            return false;
        }

        marker = value;
        return true;
    }

    private static bool TryRequiredShell(JsonObject? payload, string name, out string shell)
    {
        shell = string.Empty;
        if (!TryRequiredString(payload, name, 32, out var value))
        {
            return false;
        }

        shell = value.ToLowerInvariant();
        return shell is "sh" or "powershell";
    }

    private static string DevelopmentScriptPath(int tenantId, Guid agentId) =>
        $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/scripts";

    private static bool TryDevelopmentMarkerJobIdentity(JsonElement? request, out int tenantId, out Guid agentId, out long jobId)
    {
        tenantId = default;
        agentId = default;
        jobId = default;
        return TryObject(request, out var payload) &&
               ContainsOnly(payload, "tenantId", "agentId", "jobId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryPositiveLong(payload, "jobId", out jobId);
    }

    private static string DevelopmentMarkerJobPath(int tenantId, Guid agentId) =>
        $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/marker-jobs";

    private static bool TryDevelopmentTerminalTarget(JsonElement? request, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryObject(request, out var payload) && TryDevelopmentTerminalTarget(payload, out tenantId, out agentId);
    }

    private static bool TryDevelopmentTerminalTarget(JsonObject? payload, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) && TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryDevelopmentTerminalSession(JsonElement? request, out string sessionId)
    {
        sessionId = string.Empty;
        return TryObject(request, out var payload) && TryDevelopmentTerminalSession(payload, out sessionId);
    }

    private static bool TryDevelopmentTerminalSession(JsonObject? payload, out string sessionId)
    {
        sessionId = string.Empty;
        return ContainsOnly(payload, "sessionId") && TryDevelopmentTerminalSessionValue(payload, out sessionId);
    }

    private static bool TryDevelopmentTerminalSessionValue(JsonObject? payload, out string sessionId)
    {
        sessionId = string.Empty;
        return TryRequiredString(payload, "sessionId", 32, out sessionId) &&
               sessionId.Length == 32 && sessionId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string DevelopmentTerminalSessionPath(string sessionId) =>
        $"/api/v2/development/mcp/terminal-sessions/{sessionId}";

    private static bool TryProductionTerminalOpen(JsonElement? request, out int tenantId, out Guid agentId, out string shell, out string workingDirectory, out int? columns, out int? rows)
    {
        tenantId = default;
        agentId = default;
        shell = string.Empty;
        workingDirectory = string.Empty;
        columns = null;
        rows = null;
        return TryObject(request, out var payload) && payload is not null &&
               ContainsOnly(payload, "tenantId", "agentId", "shell", "workingDirectory", "columns", "rows") &&
               TryProductionTerminalOpen(payload, out tenantId, out agentId, out shell, out workingDirectory, out columns, out rows);
    }

    private static bool TryProductionTerminalOpen(JsonObject? payload, out int tenantId, out Guid agentId, out string shell, out string workingDirectory, out int? columns, out int? rows)
    {
        tenantId = default;
        agentId = default;
        shell = string.Empty;
        workingDirectory = string.Empty;
        columns = null;
        rows = null;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryRequiredString(payload, "shell", 32, out shell) &&
               shell.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') &&
               TryRequiredString(payload, "workingDirectory", 4096, out workingDirectory) &&
               !workingDirectory.Any(char.IsControl) &&
               TryOptionalInt(payload, "columns", 40, 300, out columns) &&
               TryOptionalInt(payload, "rows", 10, 120, out rows);
    }

    private static bool TryProductionTerminalSession(JsonElement? request, out int tenantId, out Guid agentId, out string sessionId)
    {
        tenantId = default;
        agentId = default;
        sessionId = string.Empty;
        return TryObject(request, out var payload) && TryProductionTerminalSession(payload, out tenantId, out agentId, out sessionId);
    }

    private static bool TryProductionTerminalSession(JsonObject? payload, out int tenantId, out Guid agentId, out string sessionId)
    {
        tenantId = default;
        agentId = default;
        sessionId = string.Empty;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryRequiredString(payload, "sessionId", 32, out sessionId) &&
               sessionId.Length == 32 && sessionId.All(char.IsAsciiHexDigit);
    }

    private static bool TryProductionCommandTarget(JsonElement? request, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryObject(request, out var payload) && payload is not null &&
               ContainsOnly(payload, "tenantId", "agentId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryProductionCommandReference(JsonElement? request, out int tenantId, out Guid agentId, out string commandId)
    {
        tenantId = default;
        agentId = default;
        commandId = string.Empty;
        return TryObject(request, out var payload) && payload is not null &&
               ContainsOnly(payload, "tenantId", "agentId", "commandId") &&
               TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) &&
               TryRequiredGuid(payload, "agentId", out agentId) &&
               TryRequiredString(payload, "commandId", 32, out commandId) &&
               commandId.Length == 32 && commandId.All(char.IsAsciiHexDigit);
    }

    private static bool TryProductionCommandExecute(JsonElement? request, out ProductionCommand command)
    {
        command = default!;
        return TryObject(request, out var payload) && payload is not null &&
               ContainsOnly(payload, "tenantId", "agentId", "shell", "command", "workingDirectory", "environmentReferences", "timeoutSeconds", "maximumOutputBytes") &&
               TryProductionCommandExecute(payload, out command);
    }

    private static bool TryProductionCommandExecute(JsonObject? payload, out ProductionCommand command)
    {
        command = default!;
        if (!TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !TryRequiredString(payload, "shell", 32, out var shell) ||
            !IsCommandShell(shell) ||
            !TryCommandText(payload, out var text) ||
            !TryRequiredString(payload, "workingDirectory", 4096, out var workingDirectory) ||
            workingDirectory.Any(char.IsControl) ||
            !TryEnvironmentReferences(payload, out var environmentReferences) ||
            !TryOptionalInt(payload, "timeoutSeconds", 1, 60 * 60, out var timeout) ||
            !TryOptionalInt(payload, "maximumOutputBytes", 1, 48 * 1024, out var maximumOutput))
        {
            return false;
        }

        command = new ProductionCommand(tenantId, agentId, shell.ToLowerInvariant(), text, workingDirectory, environmentReferences, timeout, maximumOutput);
        return true;
    }

    private static bool TryCommandText(JsonObject? payload, out string command)
    {
        command = string.Empty;
        if (payload is null || payload["command"] is not JsonValue value || !value.TryGetValue<string>(out var raw) ||
            string.IsNullOrWhiteSpace(raw) || raw.IndexOf('\0') >= 0)
        {
            return false;
        }
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(raw) is < 1 or > 32 * 1024)
                return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        command = raw;
        return true;
    }

    private static bool TryEnvironmentReferences(JsonObject? payload, out IReadOnlyList<string> references)
    {
        references = [];
        if (payload is null || !payload.TryGetPropertyValue("environmentReferences", out var node))
            return true;
        if (node is not JsonArray values || values.Count > 32)
            return false;

        var parsed = new List<string>(values.Count);
        foreach (var entry in values)
        {
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var reference) ||
                reference is not { Length: > 0 and <= 128 } ||
                (!char.IsAsciiLetter(reference[0]) && reference[0] != '_') ||
                reference.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            {
                return false;
            }
            parsed.Add(reference);
        }
        if (parsed.Distinct(StringComparer.Ordinal).Count() != parsed.Count)
            return false;
        references = parsed.Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool IsCommandShell(string shell) =>
        shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
        shell.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
        shell.Equals("windows-powershell", StringComparison.OrdinalIgnoreCase) ||
        shell.Equals("windows_powershell", StringComparison.OrdinalIgnoreCase) ||
        shell.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
        shell.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
        shell.Equals("cmd", StringComparison.OrdinalIgnoreCase);

    private sealed record ProductionCommand(
        int TenantId,
        Guid AgentId,
        string Shell,
        string Command,
        string WorkingDirectory,
        IReadOnlyList<string> EnvironmentReferences,
        int? TimeoutSeconds,
        int? MaximumOutputBytes);

    private static bool TryTerminalInput(JsonObject payload, out string input)
    {
        input = string.Empty;
        if (payload["input"] is not JsonValue value || !value.TryGetValue<string>(out var candidate) ||
            string.IsNullOrEmpty(candidate) || candidate.Length > 16 * 1024 ||
            candidate.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            return false;
        }
        try
        {
            input = candidate;
            return Encoding.UTF8.GetByteCount(input) is > 0 and <= 16 * 1024;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryDevelopmentOnboardingTarget(JsonObject? payload, out int tenantId, out Guid agentId)
    {
        tenantId = default;
        agentId = default;
        return TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out tenantId) && TryRequiredGuid(payload, "agentId", out agentId);
    }

    private static bool TryDevelopmentOnboardingEnrollment(JsonElement? request, out int tenantId, out Guid agentId, out Guid enrollmentCodeId)
    {
        tenantId = default;
        agentId = default;
        enrollmentCodeId = default;
        return TryObject(request, out var payload) &&
               ContainsOnly(payload, "tenantId", "agentId", "enrollmentCodeId") &&
               TryDevelopmentOnboardingTarget(payload, out tenantId, out agentId) &&
               TryRequiredGuid(payload, "enrollmentCodeId", out enrollmentCodeId);
    }

    private static bool TryRequiredShellRuntime(JsonObject? payload, out string runtime)
    {
        runtime = string.Empty;
        return TryRequiredString(payload, "runtime", 16, out runtime) &&
               runtime is "linux-x64" or "win-x64";
    }

    private static bool TryCampaignMarker(JsonObject? payload, out string marker)
    {
        marker = string.Empty;
        return TryRequiredString(payload, "marker", 128, out marker) &&
               marker.Length > "MCP-QA-".Length &&
               marker.StartsWith("MCP-QA-", StringComparison.Ordinal) &&
               marker.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static string DevelopmentOnboardingPath(int tenantId, Guid agentId) =>
        $"/api/v2/development/mcp/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/onboarding";

    private static NetRatelToolResponse Confirmation(string operation, string summary, IReadOnlyList<string> affectedIds) => new(
        false,
        "confirmation_required",
        summary,
        AffectedIds: affectedIds,
        RequiresConfirmation: true,
        Confirmation: new NetRatelConfirmation("confirm", true, operation, affectedIds));

    private Task<NetRatelToolResponse> TenantByIdAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "tenantId") ||
            !TryPositiveInt(payload, "tenantId", out var tenantId))
        {
            return Task.FromResult(Invalid("request.tenantId must be a positive tenant identifier."));
        }

        return GetAsync("netratel_tenants", "get", TenantReadOperations,
            $"/api/v1/tenants/{tenantId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    private Task<NetRatelToolResponse> TaskRecentAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        string? error = null;
        if (!TryObject(request, out var payload) || !TryBuildTaskRecentPath(payload, out var path, out error))
        {
            return Task.FromResult(Invalid(error ?? "request must be a closed bounded V2 task-recent filter object."));
        }

        return GetAsync("netratel_tasks", "recent", TaskReadOperations, path, cancellationToken);
    }

    private Task<NetRatelToolResponse> TaskByIdAsync(JsonElement? request, CancellationToken cancellationToken)
    {
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, "taskId") ||
            !TryPositiveLong(payload, "taskId", out var taskId))
        {
            return Task.FromResult(Invalid("request.taskId must be a positive V2 task identifier."));
        }

        return GetAsync("netratel_tasks", "get", TaskReadOperations,
            $"/api/v2/tasks/{taskId.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
    }

    private Task<NetRatelToolResponse> TaskLogsAsync(string operation, JsonElement? request, CancellationToken cancellationToken)
    {
        var idName = operation == "logs_by_request" ? "requestId" : "taskId";
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, idName, "sinceId", "stream") ||
            !TryOptionalNonNegativeLong(payload, "sinceId", out var sinceId) ||
            !TryOptionalTaskLogStream(payload, out var stream))
        {
            return Task.FromResult(Invalid($"request.{idName} must be valid; sinceId must be a non-negative integer and stream must be all, stdout, or stderr."));
        }

        if (operation == "logs")
        {
            if (!TryPositiveLong(payload, "taskId", out var taskId))
            {
                return Task.FromResult(Invalid("request.taskId must be a positive V2 task identifier."));
            }

            return GetAsync("netratel_tasks", operation, TaskReadOperations,
                WithQuery($"/api/v2/tasks/{taskId.ToString(CultureInfo.InvariantCulture)}/logs", ("sinceId", Format<long>(sinceId ?? 0L)), ("stream", stream ?? "all")), cancellationToken);
        }

        if (!TryRequiredString(payload, "requestId", 200, out var requestId))
        {
            return Task.FromResult(Invalid("request.requestId must be a non-empty string containing at most 200 characters."));
        }

        return GetAsync("netratel_tasks", operation, TaskReadOperations,
            WithQuery("/api/v2/tasks/logs", ("requestId", requestId), ("sinceId", Format<long>(sinceId ?? 0L)), ("stream", stream ?? "all")), cancellationToken);
    }

    private Task<NetRatelToolResponse> RequireIdAsync(
        string tool,
        string operation,
        IReadOnlyList<string> allowedOperations,
        JsonObject? payload,
        string field,
        Func<string, string> path,
        CancellationToken cancellationToken)
    {
        var id = RequiredId(payload, field);
        return id is null
            ? Task.FromResult(Invalid($"request.{field} is required."))
            : GetAsync(tool, operation, allowedOperations, path(id), cancellationToken);
    }

    private Task<NetRatelToolResponse> GetAsync(string tool, string operation, IReadOnlyList<string> allowedOperations, string path, CancellationToken cancellationToken)
        => allowedOperations.Contains(operation, StringComparer.Ordinal)
            ? ExecuteAsync(tool, operation, () => client.GetAsync(path, cancellationToken), cancellationToken)
            : Task.FromResult(Unsupported(tool, operation, allowedOperations));

    private async Task<NetRatelToolResponse> ExecuteAsync(
        string tool,
        string operation,
        Func<Task<JsonNode?>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await action().ConfigureAwait(false);
            return new NetRatelToolResponse(true, "completed", $"{tool} {operation} completed.", NormalizeForMcp(data), Array.Empty<string>());
        }
        catch (NetRatelMcpApiException exception)
        {
            if (tool == "netratel_marker_jobs" &&
                operation == "cancel" &&
                exception.StatusCode == 409 &&
                exception.RemoteCode == "marker_job_run_terminal")
            {
                return new NetRatelToolResponse(
                    false,
                    "failed",
                    "The marker job run is already terminal; read its final state before cleanup.",
                    Error: new NetRatelToolError("marker_job_run_terminal", false, exception.StatusCode))
                    .WithFailureContext(tool, operation);
            }

            return new NetRatelToolResponse(false, "failed", exception.Message,
                Error: new NetRatelToolError(exception.RemoteCode ?? exception.Code, exception.Retryable, exception.StatusCode))
                .WithFailureContext(tool, operation);
        }
        catch (NetRatelMcpApiValidationException exception)
        {
            return new NetRatelToolResponse(false, "invalid_request", exception.Message,
                Error: new NetRatelToolError("validation_error", false));
        }
    }

    private static NetRatelToolResponse Unsupported(string tool, string operation, IReadOnlyList<string> allowedOperations)
        => new NetRatelToolResponse(false, "invalid_request", $"Unsupported {tool} operation '{operation}'.",
            Error: new NetRatelToolError("unsupported_operation", false, AllowedOperations: allowedOperations))
            .WithFailureContext(tool, operation);

    private static NetRatelToolResponse Invalid(string summary)
        => new(false, "invalid_request", summary, Error: new NetRatelToolError("validation_error", false));

    /// <summary>
    /// Keeps identifiers and other integral values exact for JavaScript-based MCP
    /// clients. JSON numbers above the IEEE-754 safe-integer range would otherwise
    /// be rounded before the caller can use them in a follow-up tool request.
    /// </summary>
    private static JsonNode? NormalizeForMcp(JsonNode? node) => node switch
    {
        null => null,
        JsonObject source => NormalizeObject(source),
        JsonArray source => NormalizeArray(source),
        JsonValue source when source.TryGetValue<ulong>(out var unsigned) && unsigned > JavaScriptSafeIntegerMaximum
            => JsonValue.Create(unsigned.ToString(CultureInfo.InvariantCulture)),
        JsonValue source when source.TryGetValue<long>(out var signed) && (signed > (long)JavaScriptSafeIntegerMaximum || signed < JavaScriptSafeIntegerMinimum)
            => JsonValue.Create(signed.ToString(CultureInfo.InvariantCulture)),
        _ => node.DeepClone()
    };

    private static JsonObject NormalizeObject(JsonObject source)
    {
        var normalized = new JsonObject();
        foreach (var (name, value) in source)
        {
            normalized[name] = NormalizeForMcp(value);
        }

        return normalized;
    }

    private static JsonArray NormalizeArray(JsonArray source)
    {
        var normalized = new JsonArray();
        foreach (var value in source)
        {
            normalized.Add(NormalizeForMcp(value));
        }

        return normalized;
    }

    private static JsonObject? ToObject(JsonElement? request)
        => request is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText())?.AsObject() : null;

    private static bool TryObject(JsonElement? request, out JsonObject? payload)
    {
        payload = null;
        if (request is null)
        {
            return true;
        }

        if (request.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        payload = JsonNode.Parse(request.Value.GetRawText())?.AsObject();
        return payload is not null;
    }

    private static bool TryBuildJobRunListPath(JsonObject? payload, out string path, out string? error)
    {
        path = "/api/v1/jobruns/";
        error = null;
        if (!ContainsOnly(payload, "status", "jobId", "tenantId", "take") ||
            !TryOptionalStatus(payload, out var status) ||
            !TryOptionalPositiveUlong(payload, "jobId", out var jobId) ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryOptionalInt(payload, "take", 1, 100, out var take))
        {
            error = "request supports only status, jobId, tenantId, and take; numeric values must be within their documented bounds.";
            return false;
        }

        path = WithQuery(path,
            ("status", status),
            ("jobId", Format(jobId)),
            ("tenantId", Format(tenantId)),
            ("take", Format(take)));
        return true;
    }

    private static bool TryBuildJobListPath(JsonObject? payload, out string path, out string? error)
    {
        path = "/api/v1/jobs/";
        error = null;
        if (!ContainsOnly(payload, "folder", "search") ||
            !TryOptionalString(payload, "folder", 512, out var folder) ||
            !TryOptionalString(payload, "search", 512, out var search))
        {
            error = "request supports only bounded folder and search filters.";
            return false;
        }

        path = WithQuery(path, ("folder", folder), ("search", search));
        return true;
    }

    private static bool TryBuildClientUpdateAttemptsPath(JsonObject? payload, out string path, out string? error)
    {
        path = "/api/v1/client-updates/attempts";
        error = null;
        if (!ContainsOnly(payload, "clientIdentity", "releaseId", "status") ||
            !TryOptionalGuid(payload, "clientIdentity", out var clientIdentity) ||
            !TryOptionalInt(payload, "releaseId", 1, int.MaxValue, out var releaseId) ||
            !TryOptionalClientUpdateAttemptStatus(payload, out var status))
        {
            error = "request supports only a UUID clientIdentity, positive releaseId, and documented update-attempt status.";
            return false;
        }

        path = WithQuery(path,
            ("clientIdentity", clientIdentity?.ToString("D")),
            ("releaseId", Format(releaseId)),
            ("status", status));
        return true;
    }

    private static bool TryBuildClientPresencePath(JsonObject? payload, out string path, out string? error)
    {
        path = "/api/v2/client-presence/";
        error = null;
        if (!ContainsOnly(payload, "tenantId", "search", "online", "limit") ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryOptionalString(payload, "search", 512, out var search) ||
            !TryOptionalBoolean(payload, "online", out var online) ||
            !TryOptionalInt(payload, "limit", 1, 100, out var limit))
        {
            error = "request supports only bounded tenantId, search, online, and limit filters.";
            return false;
        }

        path = WithQuery(path,
            ("tenantId", Format(tenantId)),
            ("search", search),
            ("online", online?.ToString().ToLowerInvariant()),
            ("limit", Format(limit)));
        return true;
    }

    private static bool TryBuildJobRunQueryPath(JsonObject? payload, out string path, out string? error)
    {
        path = "/api/v1/jobruns/query";
        error = null;
        if (!ContainsOnly(payload, "status", "jobId", "tenantId", "agentId", "search", "page", "pageSize") ||
            !TryOptionalStatus(payload, out var status) ||
            !TryOptionalPositiveUlong(payload, "jobId", out var jobId) ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryOptionalGuid(payload, "agentId", out var agentId) ||
            !TryOptionalString(payload, "search", 512, out var search) ||
            !TryOptionalInt(payload, "page", 0, 10_000, out var page) ||
            !TryOptionalInt(payload, "pageSize", 1, 100, out var pageSize))
        {
            error = "request supports only bounded status, jobId, tenantId, agentId, search, page, and pageSize filters.";
            return false;
        }

        path = WithQuery(path,
            ("status", status),
            ("jobId", Format(jobId)),
            ("tenantId", Format(tenantId)),
            ("agentId", agentId?.ToString("D")),
            ("search", search),
            ("page", Format(page)),
            ("pageSize", Format(pageSize)));
        return true;
    }

    private static bool TryBuildTaskRecentPath(JsonObject? payload, out string path, out string? error)
    {
        path = "/api/v2/tasks/recent";
        error = null;
        if (!ContainsOnly(payload, "limit", "tenantId", "agentId", "taskType", "status") ||
            !TryOptionalInt(payload, "limit", 1, 100, out var limit) ||
            !TryOptionalInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryOptionalGuid(payload, "agentId", out var agentId) ||
            !TryOptionalString(payload, "taskType", 128, out var taskType) ||
            !TryOptionalString(payload, "status", 64, out var status))
        {
            error = "request supports only bounded limit, tenantId, agentId, taskType, and status filters.";
            return false;
        }

        path = WithQuery(path,
            ("limit", Format(limit)),
            ("tenantId", Format(tenantId)),
            ("agentId", agentId?.ToString("D")),
            ("taskType", taskType),
            ("status", status));
        return true;
    }

    private static bool ContainsOnly(JsonObject? payload, params string[] names)
        => payload is null || payload.All(property => names.Contains(property.Key, StringComparer.Ordinal));

    private static bool TryPolicyCreateRequest(
        JsonElement? request,
        bool requireConfirmationCredentials,
        out JsonObject? body)
    {
        body = null;
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, requireConfirmationCredentials ? ["policy", "planToken", "idempotencyKey"] : ["policy"]) ||
            payload is null || !payload.TryGetPropertyValue("policy", out var policy) || policy is not JsonObject)
        {
            return false;
        }

        if (requireConfirmationCredentials &&
            (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
             !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
             !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey)))
        {
            return false;
        }

        body = payload;
        return true;
    }

    private static bool TryPolicyReplaceRequest(
        JsonElement? request,
        bool requireConfirmationCredentials,
        out JsonObject? body)
    {
        body = null;
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, requireConfirmationCredentials
                ? ["policyId", "expectedVersion", "policy", "planToken", "idempotencyKey"]
                : ["policyId", "expectedVersion", "policy"]) ||
            !TryRequiredGuid(payload, "policyId", out _) ||
            !TryPositiveLong(payload, "expectedVersion", out _) ||
            !payload!.TryGetPropertyValue("policy", out var policy) || policy is not JsonObject)
        {
            return false;
        }

        if (requireConfirmationCredentials && !TryPlanCredentials(payload))
            return false;

        body = payload;
        return true;
    }

    private static bool TryPolicyDisableRequest(
        JsonElement? request,
        bool requireConfirmationCredentials,
        out JsonObject? body)
    {
        body = null;
        if (!TryObject(request, out var payload) ||
            !ContainsOnly(payload, requireConfirmationCredentials
                ? ["policyId", "expectedVersion", "target", "planToken", "idempotencyKey"]
                : ["policyId", "expectedVersion", "target"]) ||
            !TryRequiredGuid(payload, "policyId", out _) ||
            !TryPositiveLong(payload, "expectedVersion", out _) ||
            !payload!.TryGetPropertyValue("target", out var target) || target is not JsonObject)
        {
            return false;
        }

        if (requireConfirmationCredentials && !TryPlanCredentials(payload))
            return false;

        body = payload;
        return true;
    }

    private static bool TryTargetProfileRequest(
        JsonElement? request,
        bool requireConfirmationCredentials,
        out JsonObject? body)
    {
        body = null;
        var allowed = requireConfirmationCredentials
            ? new[] { "tenantId", "agentId", "classification", "tags", "expectedVersion", "planToken", "idempotencyKey" }
            : new[] { "tenantId", "agentId", "classification", "tags", "expectedVersion" };
        if (!TryObject(request, out var payload) || payload is null || !ContainsOnly(payload, allowed) ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out _) ||
            !TryRequiredGuid(payload, "agentId", out _) ||
            !TryRequiredInt(payload, "classification", 1, 5, out _) ||
            !payload.TryGetPropertyValue("tags", out var tagsNode) || tagsNode is not JsonArray tags ||
            tags.Count > 32 || !tags.All(IsSafeTargetProfileTag) ||
            tags.Select(tag => tag!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != tags.Count ||
            !TryOptionalPositiveLong(payload, "expectedVersion", out _))
        {
            return false;
        }

        if (requireConfirmationCredentials && !TryPlanCredentials(payload))
            return false;

        body = payload;
        return true;
    }

    private static bool IsSafeTargetProfileTag(JsonNode? tag) =>
        tag is JsonValue value && value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text) && text.Length <= 128 &&
        text.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/' or '@' or '#');

    private static bool TryPlanCredentials(JsonObject payload) =>
        TryRequiredString(payload, "planToken", 128, out var planToken) &&
        TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) &&
        IsOpaqueCredential(planToken) && IsOpaqueCredential(idempotencyKey);

    private static bool IsOpaqueCredential(string value) =>
        value.Length is >= 32 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool TryOptionalStatus(JsonObject? payload, out string? status)
    {
        status = null;
        if (!TryGetOptionalString(payload, "status", 16, out var value))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        var matching = JobRunStatuses.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        if (matching is null)
        {
            return false;
        }

        status = matching;
        return true;
    }

    private static bool TryOptionalClientUpdateAttemptStatus(JsonObject? payload, out string? status)
    {
        status = null;
        if (!TryGetOptionalString(payload, "status", 32, out var value))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        var matching = ClientUpdateAttemptStatuses.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        if (matching is null)
        {
            return false;
        }

        status = matching;
        return true;
    }

    private static bool TryOptionalString(JsonObject? payload, string name, int maximum, out string? value)
    {
        value = null;
        if (!TryGetOptionalString(payload, name, maximum, out var raw))
        {
            return false;
        }

        if (raw is null)
        {
            return true;
        }

        value = raw;
        return true;
    }

    private static bool TryOptionalBoolean(JsonObject? payload, string name, out bool? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node))
        {
            return true;
        }

        if (node is not JsonValue raw || !raw.TryGetValue<bool>(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryNotificationMarkRead(JsonObject? payload, bool production, bool requireConfirmationCredentials, out JsonObject body, out IReadOnlyList<string> ids)
    {
        body = new JsonObject();
        ids = [];
        if (payload is null || !ContainsOnly(payload, ["ids", "planToken", "idempotencyKey"]))
        {
            return false;
        }

        if (payload["ids"] is not JsonArray values || values.Count is < 1 or > 200)
        {
            return false;
        }

        var parsed = new List<string>(values.Count);
        foreach (var entry in values)
        {
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return false;
            }
            text = text.Trim();
            if (production)
            {
                if (!Guid.TryParse(text, out var id) || id == Guid.Empty)
                {
                    return false;
                }
                parsed.Add(id.ToString("D"));
            }
            else
            {
                if (text.Length is < 1 or > 200)
                {
                    return false;
                }
                parsed.Add(text);
            }
        }
        if (parsed.Distinct(StringComparer.Ordinal).Count() != parsed.Count)
        {
            return false;
        }

        body["ids"] = new JsonArray(parsed.Select(id => JsonValue.Create(id)).ToArray());
        if (!requireConfirmationCredentials)
        {
            ids = parsed;
            return true;
        }

        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
        {
            return false;
        }
        body["planToken"] = planToken;
        body["idempotencyKey"] = idempotencyKey;
        ids = parsed;
        return true;
    }

    private static bool TryRequiredInt(JsonObject? payload, string name, int minimum, int maximum, out int value)
    {
        value = default;
        if (!TryOptionalInt(payload, name, minimum, maximum, out var parsed) || !parsed.HasValue)
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    private static bool TryRequiredGuid(JsonObject? payload, string name, out Guid value)
    {
        value = default;
        if (!TryOptionalGuid(payload, name, out var parsed) || !parsed.HasValue)
        {
            return false;
        }

        value = parsed.Value;
        return true;
    }

    private static bool TryGetOptionalString(JsonObject? payload, string name, int maximum, out string? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node))
        {
            return true;
        }

        if (node is not JsonValue raw || !raw.TryGetValue<string>(out var text))
        {
            return false;
        }

        text = text.Trim();
        if (text.Length == 0 || text.Length > maximum)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryOptionalGuid(JsonObject? payload, string name, out Guid? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node))
        {
            return true;
        }

        if (node is not JsonValue raw || !raw.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryOptionalTerminalOutputCursor(JsonObject? payload, out string? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue("afterSequence", out var node)) return true;
        if (node is not JsonValue raw || !raw.TryGetValue<string>(out var text) ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor)) return false;
        value = cursor.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryOptionalPositiveUlong(JsonObject? payload, string name, out ulong? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue(name, out _))
        {
            return true;
        }

        if (!TryPositiveUlong(payload, name, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryOptionalNonNegativeLong(JsonObject? payload, string name, out long? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node))
        {
            return true;
        }

        if (node is not JsonValue raw)
        {
            return false;
        }

        if (!raw.TryGetValue<long>(out var parsed) &&
            (!raw.TryGetValue<string>(out var text) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)))
        {
            return false;
        }

        if (parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryOptionalTaskLogStream(JsonObject? payload, out string? stream)
    {
        stream = null;
        if (!TryGetOptionalString(payload, "stream", 16, out var raw))
        {
            return false;
        }

        if (raw is null)
        {
            return true;
        }

        stream = raw.ToLowerInvariant();
        return stream is "all" or "stdout" or "stderr";
    }

    private static bool TryFileText(JsonObject? payload, out string text)
    {
        text = string.Empty;
        if (payload?["text"] is not JsonValue value || !value.TryGetValue<string>(out var raw) ||
            raw.Length is < 1 or > 16384)
            return false;
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(raw) > 16384)
                return false;
        }
        catch (EncoderFallbackException) { return false; }
        text = raw;
        return true;
    }

    private static bool TryRequiredString(JsonObject? payload, string name, int maximum, out string value)
    {
        value = string.Empty;
        if (!TryGetOptionalString(payload, name, maximum, out var parsed) || parsed is null)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool IsAccessToken(string value) => value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool TryPositiveUlong(JsonObject? payload, string name, out ulong value)
    {
        value = 0;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node) || node is not JsonValue raw)
        {
            return false;
        }

        if (raw.TryGetValue<ulong>(out var numeric))
        {
            value = numeric;
        }
        else if (!raw.TryGetValue<string>(out var text) || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return value > 0;
    }

    private static bool TryPositiveLong(JsonObject? payload, string name, out long value)
    {
        value = 0;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node) || node is not JsonValue raw)
        {
            return false;
        }

        if (raw.TryGetValue<long>(out var numeric))
        {
            value = numeric;
        }
        else if (!raw.TryGetValue<string>(out var text) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return value > 0;
    }

    private static bool TryPositiveInt(JsonObject? payload, string name, out int value)
    {
        value = 0;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node) || node is not JsonValue raw)
        {
            return false;
        }

        if (raw.TryGetValue<int>(out var numeric))
        {
            value = numeric;
        }
        else if (!raw.TryGetValue<string>(out var text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return value > 0;
    }

    private static bool TryNonNegativeInt(JsonObject? payload, string name, out int value)
    {
        value = 0;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node) || node is not JsonValue raw)
        {
            return false;
        }

        if (raw.TryGetValue<int>(out var numeric))
        {
            value = numeric;
            return value >= 0;
        }

        return raw.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static bool TryOptionalInt(JsonObject? payload, string name, int minimum, int maximum, out int? value)
    {
        value = null;
        if (payload is null || !payload.TryGetPropertyValue(name, out var node))
        {
            return true;
        }

        if (node is not JsonValue raw)
        {
            return false;
        }

        if (!raw.TryGetValue<int>(out var parsed) &&
            (!raw.TryGetValue<string>(out var text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)))
        {
            return false;
        }

        if (parsed < minimum || parsed > maximum)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? Format<T>(T? value) where T : struct, IFormattable
        => value?.ToString(null, CultureInfo.InvariantCulture);

    private static string WithQuery(string path, params (string Name, string? Value)[] values)
    {
        var query = values
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .Select(value => $"{Uri.EscapeDataString(value.Name)}={Uri.EscapeDataString(value.Value!)}")
            .ToArray();
        return query.Length == 0 ? path : $"{path}?{string.Join('&', query)}";
    }

    private static string? RequiredId(JsonObject? payload, string field)
    {
        var value = payload?[field]?.ToString()?.Trim();
        return value is { Length: > 0 and <= 200 } ? value : null;
    }

    private static string OptionalQuery(string name, string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : $"?{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    private static string WithQuery(string path, JsonObject? payload, IReadOnlyCollection<string> allowedFields)
    {
        if (payload is null || payload.Count == 0)
        {
            return path;
        }

        var values = payload
            .Where(property => allowedFields.Contains(property.Key) && property.Value is not null)
            .Select(property => $"{Uri.EscapeDataString(property.Key)}={Uri.EscapeDataString(property.Value!.ToString())}")
            .ToArray();
        return values.Length == 0 ? path : $"{path}?{string.Join('&', values)}";
    }

    private static readonly string[] JobRunStatuses = ["Pending", "Running", "Succeeded", "Failed", "Cancelled", "TimedOut"];
    private static readonly string[] ClientUpdateAttemptStatuses = ["Claimed", "Downloading", "Staged", "Activating", "GatewayReadmitted", "Accepted", "FailedPreActivation", "RolledBack", "RollbackUnverified"];
}
