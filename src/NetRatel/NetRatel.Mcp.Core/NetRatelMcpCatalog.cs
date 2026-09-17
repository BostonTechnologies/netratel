namespace NetRatel.Mcp.Core;

public enum NetRatelMcpOperationSafety
{
    Read,
    DevelopmentMutation,
    OperatorMutation,
    Excluded
}

[Flags]
public enum NetRatelMcpOperationEnvironment
{
    None = 0,
    Development = 1,
    Production = 2,
    All = Development | Production
}

public sealed record NetRatelMcpOperationDescriptor(
    string Name,
    NetRatelMcpOperationSafety Safety,
    bool RequiresConfirmation,
    bool AvailableOverHttp,
    NetRatelMcpOperationEnvironment Environments,
    NetRatelMcpOperationEnvironment? HttpEnvironments = null)
{
    /// <summary>
    /// Whether the API persists a server-owned idempotency admission before
    /// this operation can dispatch. This is independent of whether the MCP
    /// tool itself exposes a two-stage <c>confirm</c> workflow.
    /// </summary>
    public bool RequiresIdempotency { get; init; } = RequiresConfirmation;

    public bool IsAvailableIn(string instance) => instance.ToLowerInvariant() switch
    {
        "dev" => Environments.HasFlag(NetRatelMcpOperationEnvironment.Development),
        "prod" => Environments.HasFlag(NetRatelMcpOperationEnvironment.Production),
        _ => false
    };

    public IReadOnlyList<string> AvailableInstances =>
    [
        .. (Environments.HasFlag(NetRatelMcpOperationEnvironment.Development) ? new[] { "dev" } : []),
        .. (Environments.HasFlag(NetRatelMcpOperationEnvironment.Production) ? new[] { "prod" } : [])
    ];

    public bool IsAvailableOverHttpIn(string instance)
    {
        var environments = HttpEnvironments ?? Environments;
        return AvailableOverHttp && (instance.ToLowerInvariant() switch
        {
            "dev" => environments.HasFlag(NetRatelMcpOperationEnvironment.Development),
            "prod" => environments.HasFlag(NetRatelMcpOperationEnvironment.Production),
            _ => false
        });
    }
}

public sealed record NetRatelMcpToolDescriptor(
    string Name,
    string Description,
    NetRatelMcpOperationSafety Safety,
    bool AvailableOverHttp,
    IReadOnlyList<NetRatelMcpOperationDescriptor> Operations);

/// <summary>
/// Stable, transport-neutral catalog metadata. Keeping descriptors explicit
/// prevents either transport from publishing accidental reflection-discovered
/// surface area.
/// </summary>
public static class NetRatelMcpCatalog
{
    private const string PolicyAdmittedV2Availability = " The policy-admitted V2 contract is explicitly enabled on Development HTTP hosts and enabled by default on Production HTTP hosts; enabling it never changes the target environment.";

    public const string Revision = "2026-09-06.56";

    public static IReadOnlyList<NetRatelMcpToolDescriptor> Tools { get; } =
    [
        Read("netratel_auth", "Read NetRatel AI-agent authentication status.", "status"),
        Read("netratel_health", "Read NetRatel service health.", "get"),
        Read("netratel_system", "Read safe NetRatel system information.", "version"),
        Read("netratel_capabilities", "Read the NetRatel MCP catalog.", "get"),
        Read("netratel_access", "Inspect the caller's bounded effective operator access, target classification, and exact-operation policy decision. whoami has no request. target and effective require tenantId and agentId. evaluate additionally requires the catalogued target tool and operation to assess; it does not execute that operation.", "whoami", "effective", "evaluate", "target"),
        PolicyMixed("netratel_policy", "Inspect bounded operator-policy, target-profile, immutable audit metadata, selector/classification matches, and one exact access decision for the signed delegated PolicyAdministrator. A PolicyAdministrator can create, replace, disable, or permanently revoke one policy, or persist one exact server-owned target profile, only through a target-bound server preview followed by an explicit confirmed idempotent request. Target-profile writes require the profile's current version when one exists. Revocation preserves the policy and its audit history but is terminal for authorization. Replacement cannot move a policy between environments or target selectors, and no lifecycle mutation may grant the delegated administrator authority or alter a policy that selects that administrator. Evaluation cannot simulate another principal or an unpersisted policy draft. Deletion remains unavailable.", ["policies", "policy", "change_audits", "accepted_audits", "target", "matches", "evaluate"], ["preview_create", "preview_replace", "preview_disable", "preview_revoke", "preview_target_profile"], ["confirm_create", "confirm_replace", "confirm_disable", "confirm_revoke", "confirm_target_profile"]),
        DevelopmentReadAndProductionOperator("netratel_clients", "Development retains source-backed client presence, bindings, telemetry, and update attempts. The policy-admitted V2 profile, explicitly enabled in Development and default in Production, inspects one exact persisted client: get returns durable administrative metadata and a curated display-name, hostname, operating-system, and architecture projection; presence and capabilities return current bounded gateway facts, binding returns optional migration provenance (hasBinding=false is normal for a native V2 client; no bridge is needed for current V2 operations), telemetry returns one bounded current V2 gateway snapshot, update_attempts returns a bounded redacted lifecycle history, and update_metadata returns durable tenant and client update policy state. Ping is a current online-control action: a no-write preview mints opaque credentials, and only the confirmed execute-scoped request reaches the V2 control gateway. Software update can only resume an already suspended target's automatic-update eligibility; it cannot select, upload, or directly push a release. Disabled clients retain policy-authorized administrative metadata reads, enable, and delete; remote execution remains blocked until enabled. Disable, enable, and delete use their own admin-scoped server preview, opaque confirmation, idempotency, and immutable accepted-audit workflow against the existing V2 agent lifecycle authority. Delete is an irreversible credential-revoking tombstone that retains historical evidence rather than a physical database purge. None of these operations use a legacy client-identity route.", ["presence", "binding", "telemetry", "update_attempts"], ["get", "presence", "capabilities", "binding", "telemetry", "update_attempts", "update_metadata"], ["ping", "software_update", "disable", "enable", "delete"], ["preview_ping", "preview_software_update", "preview_disable", "preview_enable", "preview_delete"]),
        DevelopmentAndProductionFileMixed("netratel_files", "Inspect bounded files. Production browse, stat, and read require a current policy with explicit canonical read roots, and the admitted agent rechecks those roots against symlinks and reparse points before transferring a result. Production collect snapshots one bounded regular file into a caller-bound artifact only after a standard preview, explicit confirmation, immutable accepted audit, and idempotency check; artifact_status and download re-evaluate the same caller and root fingerprint, while artifact cleanup uses a destructive preview and confirmation to wipe retained bytes. Production write_text and upload each use a server-issued preview and destructive idempotent confirmation against explicit write roots. Production create_directory uses the same explicit write-root preview, standard-mutation confirmation, and idempotency contract. Production delete is non-recursive: it removes one policy-root-bounded file or empty directory only after a destructive confirmation and never follows links or reparse points. Production copy creates one new regular file only, with independently constrained read-root source and write-root destination. Production move is a destructive, no-replace regular-file relocation with independent write-root constraints at both ends. Development retains fixture browse, inline read, and marker-artifact compatibility actions.", ["browse", "read", "status", "download"], ["collect", "cleanup"], new HashSet<string>(["browse", "stat", "read", "artifact_status", "download"], StringComparer.Ordinal), ["preview_collect", "preview_artifact_cleanup", "preview_write_text", "preview_upload", "preview_create_directory", "preview_delete", "preview_copy", "preview_move"], ["confirm_collect", "confirm_artifact_cleanup", "confirm_write_text", "confirm_upload", "confirm_create_directory", "confirm_delete", "confirm_copy", "confirm_move"]),
        DevelopmentAndProductionConfirmationMixed("netratel_client_logs", "Inspect policy-admitted client log sources, bounded history, text-filtered search, and a bounded live tail window through the current V2 gateway. Development retains its compatibility resync action. Production resync uses a source- and target-bound server-issued preview plan followed by an explicit idempotent confirmation.", ["sources", "history", "search", "tail"], ["resync"], ["preview_resync"], ["confirm_resync"]),
        DevelopmentRead("netratel_client_telemetry", "Inspect policy-admitted client telemetry snapshots and a bounded live sample window through the current V2 gateway.", new HashSet<string>(["snapshot", "stream_window"], StringComparer.Ordinal), "snapshot", "stream_window"),
        DevelopmentAndProductionJobMixed("netratel_jobs", "Production job definitions are visible only when a caller-bound ownership record, current target policy, frozen target digest, ETag revision, and immutable accepted-operation audit exist. Production steps reference only an exact current owned library-script revision; arbitrary command and HTTP steps remain excluded. Development retains the legacy read compatibility surface.", ["list", "get", "details", "params", "steps"], ["list", "get", "details", "params", "steps"], ["create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete"]),
        DevelopmentAndProductionJobMixed("netratel_job_runs", "Runs are caller-owned and return exact decimal-string IDs. Poll get until Succeeded, Failed, Cancelled, or TimedOut. logs returns bounded redacted stdout/stderr from streamed logs or the persisted gateway result. Retained runs remain readable and deletable after their job definition is deleted. Start, cancel, and retention deletion require target re-evaluation, preview, confirmation, idempotency, and immutable run audit. Development retains its legacy read compatibility surface.", ["list", "query", "get", "steps", "logs"], ["list", "query", "get", "steps", "logs"], ["start", "cancel", "delete"]),
        DevelopmentMixed("netratel_marker_jobs", "Create, run, cancel, or delete only target-owned Development marker jobs with one server-generated marker-library-script step.", [], ["create", "run", "cancel", "delete"]),
        DevelopmentReadAndProductionOperator("netratel_tenants", "For read-only tenant discovery use netratel_search/tenants with netratel.mcp.read. Tenant administration is a control-plane operation, not an agent-target operation. It requires the PolicyAdministrator role, netratel.mcp.admin, and an explicit ControlPlane or full DevelopmentEnvironment policy, ETag concurrency, no-write preview, confirmation, idempotency, and immutable audit. Delete previews known dependent-object counts and supports only an explicit non-cascading delete.", ["list", "get"], ["list", "get"], ["create", "update", "delete"]),
        DevelopmentAndProductionScriptMixed("netratel_scripts", "Production definitions are tenant-scoped only when an explicit caller ownership record exists. A caller first validates or previews the exact immutable content hash and risk declaration, then repeats the unchanged request with confirm: true and opaque credentials. Updates, manifest parses, and deletes require the current ETag version; runs require the exact current version/hash and target execution policy. A confirmed run returns taskId and requestId: poll netratel_tasks/get with taskId until Completed/Failed/Cancelled; inspect resultSummary and netratel_tasks/logs or logs_by_request. Use netratel_tasks/cancel for a running execution. Dispatch acceptance alone is not execution success. Development retains its isolated server-generated marker-script compatibility workflow.", ["list", "get", "params"], ["create_marker", "update_marker", "parse_manifest", "run", "delete"], ["list", "get", "params", "validate"], ["create", "update", "parse_manifest", "run", "delete"]),
        DevelopmentAndProductionTerminalMixed("netratel_terminal", "Inspect and operate bounded terminal sessions. Development retains its target-owned QA workflow. Production exposes a durable, policy-frozen session lease: preview_open returns opaque credentials; open consumes them with explicit confirmation; get, stream_window, diagnostics, bounded input, resize, and idempotent close are limited to the same delegated caller and target. Poll get until opened before input. stream_window reads retained output without consuming it; send returned decimal-string nextSequence as afterSequence for subsequent reads. hasMore requests another page; gap means frames were lost. History is bounded to 64 frames / 1 MiB per session and is lost on API restart. No terminal bytes are persisted in policy or audit records.", ["availability", "get", "stream"], ["self_test", "deployment-control-plane_inspect", "fixture"], ["availability", "get", "stream_window", "diagnostics"], ["open", "resize", "close"], ["preview_open"], ["send_input"]),
        ProductionOperator("netratel_commands", "Run one bounded Production command only through an exact signed delegation, current target policy, opaque preview credentials, explicit confirmation, idempotency, and a durable caller-bound command lease. Every arbitrary shell command is classified as destructive. Poll get until terminal state, then inspect output.exitCode, output.stdout, output.stderr and output.outputTruncated. Output is redacted, byte-bounded and retained with the command; output.unavailableReason explains a missing or malformed agent result. Raw command text and environment values are not persisted; environmentReferences are names of pre-provisioned client-local values, never values.", ["availability", "get", "preview_execute"], ["execute", "cancel"]),
        DevelopmentAndProductionScriptMixed("netratel_onboarding", "Development retains target-owned QA enrollment codes, while Production uses a tenant-scoped pre-enrollment policy because a prospective client has no agent ID yet. collateral_download returns transferMode inline with contentBase64 for small files; larger installers return transferMode enrollment_download and a version-pinned download.path on the same API origin as the MCP resource. Create an enrollment first, then GET that path using download.tenantHeader and download.enrollmentCodeHeader; never put credentials in URLs or logs. Verify downloaded size and SHA-256 against collateral. The transfer validates but does not consume the enrollment code. Collateral, enrollment metadata, and bounded lists require the onboarding scope and a tenant Onboarding policy. Creation and revocation use server-issued previews, confirmation, idempotency, and immutable audit. The raw enrollment code is returned only by the first confirmed creation response and must never be persisted or reused.", ["collateral", "get_enrollment"], ["create_enrollment", "revoke_enrollment"], ["collateral", "collateral_download", "get_enrollment", "list_enrollments"], ["create_enrollment", "revoke_enrollment"]),
        DevelopmentAndProductionJobMixed("netratel_tasks", "Production tasks are caller-owned one-shot operations with exact tenant and agent targeting, target-policy re-evaluation, typed bounded inputs, preview/confirmation, idempotency, immutable audit, cancellation, and bounded redacted result aggregation. When no persisted task logs exist, logs and logs_by_request project available terminal stdout/stderr snapshots, grouped stdout then stderr with the terminal timestamp. These snapshots do not establish real-time per-line timing. Their positive IDs are stable per-task cursors allocated before stream filtering; pass the last logId as sinceId. Unavailable or malformed result payloads produce no log entries. Development retains the legacy read compatibility surface.", ["list", "recent", "get", "logs", "logs_by_request"], ["list", "recent", "get", "logs", "logs_by_request"], ["create_command", "run_library_script", "cancel"]),
        ProductionOperator("netratel_requests", "Production requests are caller-owned and always linked to one owned job and exact target. List and get are bounded; all lifecycle writes re-evaluate policy, require ETag concurrency, preview/confirmation, idempotency, and immutable audit. Request inputs and unbounded legacy history are never exposed; result summaries are bounded and redacted.", ["list", "get"], ["create", "update", "claim", "complete", "fail", "cancel"]),
        Search(),
        Read("netratel_telemetry", "Read current gateway telemetry snapshots across the environment. Requires observe scope and an Observability ControlPlane or full Dev testing policy. Returns snapshots keyed by tenantId/agentId, timestamps, totalCount and a truncation flag; use netratel_client_telemetry/snapshot for one target. A disabled telemetry authority returns an actionable availability failure.", "overview"),
        DevelopmentAndProductionOperator("netratel_notifications", "Inspect only notifications belonging to the signed delegated operator. Production mark_read is a ControlPlane policy-admitted mutation with a server-issued preview, confirmation, idempotency, and immutable accepted audit.", ["list", "get", "summary", "unread_errors"], ["mark_read"]),
        Read("netratel_logs", "Read recent redacted API operational logs. Requires observe scope and an Observability ControlPlane or full Dev testing policy. Optional filters: since (last returned numeric ID), level, contains, correlationId, limit (1–500). This is the API log buffer; use netratel_client_logs for endpoint logs.", "search"),
        ProductionOperator("netratel_connectivity", "Inspect redacted server-owned connectivity configuration presence, or run one bounded ExternalService M2M probe. Settings and probe output never expose endpoints, authorities, credentials, request headers, or response bodies. The probe accepts no target or override and uses ControlPlane policy, execute scope, preview, confirmation, idempotency, and immutable accepted audit.", ["settings", "netratel", "preview_test"], ["test"]),
        ProductionOperator("netratel_events", "Inspect bounded redacted outbox event summaries, replay one eligible event, or disable one event. Event reads and controls use an explicit Events ControlPlane policy; callbacks and payload content remain server-side. Retry is execute-scoped; disable requires the PolicyAdministrator role and admin scope. Both mutations use preview, confirmation, idempotency, and immutable accepted audit.", ["list", "get", "preview_retry", "preview_disable"], ["retry", "disable"]),
        StdioOnlyMixed("netratel_config", "Inspect or deliberately change the two allowlisted persisted stdio settings. This local compatibility surface is never available over HTTP.", ["show", "get"], ["set", "unset"]),
        RemoteSupport()
    ];

    public static IReadOnlyList<string> Resources { get; } =
    ["netratel://server/status", "netratel://health", "netratel://config/redacted", "netratel://capabilities", "netratel://schemas/catalog", "netratel://schemas/response"];

    public static IReadOnlyList<string> Prompts { get; } =
    ["inspect_client", "diagnose_job_run", "controlled_remote_execution", "create_workflow", "manage_operator_access", "safe_file_operation", "safe_terminal_operation", "onboard_client"];

    public static NetRatelMcpOperationDescriptor? FindOperation(string toolName, string? operationName)
        => string.IsNullOrWhiteSpace(operationName)
            ? null
            : Tools.SingleOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))?.Operations
                .SingleOrDefault(operation => string.Equals(operation.Name, operationName, StringComparison.Ordinal));

    /// <summary>
    /// Applies the immutable host surface selection to one catalogued
    /// operation. Development can opt into the same policy-admitted V2
    /// operations as Production without changing its target instance.
    /// </summary>
    public static bool IsAvailableIn(NetRatelMcpOperationDescriptor operation, NetRatelMcpHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(hostContext);
        return operation.IsAvailableIn(hostContext.Target.Instance) ||
               (hostContext.OperatorSurfaceEnabled &&
                string.Equals(hostContext.Target.Instance, "dev", StringComparison.Ordinal) &&
                operation.IsAvailableIn("prod"));
    }

    /// <summary>
    /// Returns the instances in which an operation is effectively available
    /// from the selected host. Development may explicitly expose a
    /// Production-classified V2 operation without retargeting the host, so
    /// discovery metadata must advertise that enabled Development availability.
    /// </summary>
    public static IReadOnlyList<string> EffectiveAvailableInstances(
        NetRatelMcpOperationDescriptor operation,
        NetRatelMcpHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(hostContext);

        return hostContext.OperatorSurfaceEnabled &&
               string.Equals(hostContext.Target.Instance, "dev", StringComparison.Ordinal) &&
               operation.IsAvailableIn("prod") &&
               !operation.IsAvailableIn("dev")
            ? ["dev", .. operation.AvailableInstances]
            : operation.AvailableInstances;
    }

    /// <summary>Returns whether the operation is available on this host's HTTP surface.</summary>
    public static bool IsAvailableOverHttpIn(NetRatelMcpOperationDescriptor operation, NetRatelMcpHostContext hostContext)
        => hostContext.OperatorSurfaceEnabled && string.Equals(hostContext.Target.Instance, "dev", StringComparison.Ordinal)
            ? operation.IsAvailableOverHttpIn("prod")
            : operation.IsAvailableOverHttpIn(hostContext.Target.Instance);

    private static NetRatelMcpToolDescriptor Search() => Read(
        "netratel_search",
        "Discover tenants, clients, scripts, jobs, requests and tasks with netratel.mcp.read. request.q is optional (up to 512 characters). Tenant/client discovery respects target visibility and accepts limit (1–100, default25) and offset. Follow hasMore and nextOffset with the same q until hasMore is false, including after an empty page. totalCount is the returned-page count, not a global total. Concurrent directory edits may shift offsets; deduplicate IDs and restart discovery if completeness matters. The other four directories require an Observability ControlPlane or full Dev testing policy and return metadata with exact string IDs, never script contents or task/request payloads. Directory results have limit 25 and hasMore; narrow q when hasMore is true. Use returned tenantId/agentId for target operations. All operations are read-only.",
        "tenants", "scripts", "jobs", "requests", "clients", "tasks");

    private static NetRatelMcpToolDescriptor Read(string name, string description, params string[] operations)
        => new(
            name,
            $"{description} Operations: {OperationList(operations)}. All operations are read-only and available over HTTP. Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.Read,
            true,
            operations.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                NetRatelMcpOperationEnvironment.All)).ToArray());

    private static NetRatelMcpToolDescriptor DevelopmentRead(string name, string description, params string[] operations) =>
        DevelopmentRead(name, description, null, operations);

    private static NetRatelMcpToolDescriptor DevelopmentRead(
        string name,
        string description,
        IReadOnlySet<string>? productionReads,
        params string[] operations)
        => new(
            name,
            $"{description} Operations: {OperationList(operations)}. HTTP availability is declared per operation in this catalog. Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.Read,
            true,
            operations.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                productionReads?.Contains(operation) is true
                    ? NetRatelMcpOperationEnvironment.All
                    : NetRatelMcpOperationEnvironment.Development)).ToArray());

    private static NetRatelMcpToolDescriptor MixedOperator(string name, string description, IReadOnlyList<string> reads, IReadOnlyList<string> mutations)
        => new(
            name,
            $"{description} Read-only HTTP operations: {OperationList(reads)}. Confirmed operator mutations are stdio-only and require confirm: true; they are deliberately not advertised over HTTP. Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                NetRatelMcpOperationEnvironment.All))
                .Concat(mutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.OperatorMutation,
                    true,
                    false,
                    NetRatelMcpOperationEnvironment.All)))
                .ToArray());

    private static NetRatelMcpToolDescriptor PolicyMixed(
        string name,
        string description,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> previews,
        IReadOnlyList<string> confirmations)
        => new(
            name,
            $"{description} HTTP exposes the bounded read, preview, and confirmed lifecycle operations. Read-only operations: {OperationList(reads)}. Preview operations: {OperationList(previews)}; each creates no policy and returns opaque plan credentials. Confirmed operations: {OperationList(confirmations)}; each requires confirm: true and consumes those credentials with the unchanged request. Every operation requires netratel.mcp.admin and the PolicyAdministrator role. Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                NetRatelMcpOperationEnvironment.All))
                .Concat(previews.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.Read,
                    false,
                    true,
                    NetRatelMcpOperationEnvironment.All)))
                .Concat(confirmations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.OperatorMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.All)))
                .ToArray());

    private static NetRatelMcpToolDescriptor DevelopmentMixed(
        string name,
        string description,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> mutations,
        IReadOnlySet<string>? productionReads = null)
        => new(
            name,
            $"{description} Read-only HTTP operations: {OperationList(reads)}. Confirmed Development mutations: {OperationList(mutations)}; each requires confirm: true and the Development write scope. Production availability is declared per operation in this catalog. Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.DevelopmentMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                productionReads?.Contains(operation) is true
                    ? NetRatelMcpOperationEnvironment.All
                    : NetRatelMcpOperationEnvironment.Development))
                .Concat(mutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.DevelopmentMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.Development)))
                .ToArray());

    private static NetRatelMcpToolDescriptor DevelopmentAndProductionConfirmationMixed(
        string name,
        string description,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> developmentMutations,
        IReadOnlyList<string> previews,
        IReadOnlyList<string> confirmations)
        => new(
            name,
            $"{description} Read-only HTTP operations: {OperationList(reads)}. Development compatibility mutations: {OperationList(developmentMutations)}; each requires confirm: true and the Development write scope. Policy-admitted V2 preview operations: {OperationList(previews)}; each creates no target change and returns opaque plan credentials. Policy-admitted V2 confirmed operations: {OperationList(confirmations)}; each requires confirm: true and consumes those credentials with the unchanged request.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                NetRatelMcpOperationEnvironment.All))
                .Concat(developmentMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.DevelopmentMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.Development)))
                .Concat(previews.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.Read,
                    false,
                    true,
                    NetRatelMcpOperationEnvironment.All)))
                .Concat(confirmations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.OperatorMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.All)))
                .ToArray());

    private static NetRatelMcpToolDescriptor DevelopmentAndProductionFileMixed(
        string name,
        string description,
        IReadOnlyList<string> developmentReads,
        IReadOnlyList<string> developmentMutations,
        IReadOnlySet<string> productionReads,
        IReadOnlyList<string> productionPreviews,
        IReadOnlyList<string> productionConfirmations)
    {
        var productionOnlyReads = productionReads.Except(developmentReads, StringComparer.Ordinal).ToArray();
        return new(
            name,
            $"{description} HTTP availability is declared per operation in this catalog. Policy-admitted V2 read operations: {OperationList(productionReads.Order(StringComparer.Ordinal).ToArray())}. Policy-admitted V2 preview operations: {OperationList(productionPreviews)}; each creates no target change and returns opaque plan credentials. Policy-admitted V2 confirmed operations: {OperationList(productionConfirmations)}; each requires confirm: true and consumes those credentials with the unchanged request. Development compatibility read operations: {OperationList(developmentReads)}. Development compatibility confirmed operations: {OperationList(developmentMutations)}.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            developmentReads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                productionReads.Contains(operation)
                    ? NetRatelMcpOperationEnvironment.All
                    : NetRatelMcpOperationEnvironment.Development))
                .Concat(productionOnlyReads.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.Read,
                    false,
                    true,
                    NetRatelMcpOperationEnvironment.Production)))
                .Concat(developmentMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.DevelopmentMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.Development)))
                .Concat(productionPreviews.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.Read,
                    false,
                    true,
                    NetRatelMcpOperationEnvironment.Production)))
                .Concat(productionConfirmations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.OperatorMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.Production)))
                .ToArray());
    }

    private static NetRatelMcpToolDescriptor DevelopmentAndProductionScriptMixed(
        string name,
        string description,
        IReadOnlyList<string> developmentReads,
        IReadOnlyList<string> developmentMutations,
        IReadOnlyList<string> productionReadsAndValidation,
        IReadOnlyList<string> productionMutations)
    {
        var sharedReads = developmentReads.Intersect(productionReadsAndValidation, StringComparer.Ordinal).ToArray();
        var developmentOnlyReads = developmentReads.Except(sharedReads, StringComparer.Ordinal).ToArray();
        var productionOnlyReads = productionReadsAndValidation.Except(sharedReads, StringComparer.Ordinal).ToArray();
        var sharedMutations = developmentMutations.Intersect(productionMutations, StringComparer.Ordinal).ToArray();
        var developmentOnlyMutations = developmentMutations.Except(sharedMutations, StringComparer.Ordinal).ToArray();
        var productionOnlyMutations = productionMutations.Except(sharedMutations, StringComparer.Ordinal).ToArray();
        return new(
            name,
            $"{description} Policy-admitted V2 HTTP list/get/params/validate operations: {OperationList(productionReadsAndValidation)}. Calling a policy-admitted V2 mutation without confirm returns its content-free hash/risk preview; repeating it with confirm: true and the unchanged opaque plan credentials performs the idempotent operation. Development read operations: {OperationList(developmentReads)}. Development compatibility mutations: {OperationList(developmentMutations)}.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            sharedReads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.All))
                .Concat(developmentOnlyReads.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.Development)))
                .Concat(productionOnlyReads.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.Production)))
                .Concat(developmentOnlyMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.DevelopmentMutation, true, true, NetRatelMcpOperationEnvironment.Development)))
                .Concat(sharedMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, true, true, NetRatelMcpOperationEnvironment.All)))
                .Concat(productionOnlyMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, true, true, NetRatelMcpOperationEnvironment.Production)))
                .ToArray());
    }

    private static NetRatelMcpToolDescriptor DevelopmentAndProductionJobMixed(
        string name,
        string description,
        IReadOnlyList<string> developmentReads,
        IReadOnlyList<string> productionReads,
        IReadOnlyList<string> productionMutations)
    {
        var reads = developmentReads.Concat(productionReads).Distinct(StringComparer.Ordinal).ToArray();
        return new(
            name,
            $"{description} Policy-admitted V2 HTTP reads: {OperationList(productionReads)}. Calling a policy-admitted V2 HTTP mutation without confirm returns an opaque target-bound preview; repeating the unchanged request with confirm: true and the plan credentials performs the idempotent operation. Policy-admitted V2 HTTP mutations: {OperationList(productionMutations)}.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                (developmentReads.Contains(operation, StringComparer.Ordinal) ? NetRatelMcpOperationEnvironment.Development : NetRatelMcpOperationEnvironment.None) |
                (productionReads.Contains(operation, StringComparer.Ordinal) ? NetRatelMcpOperationEnvironment.Production : NetRatelMcpOperationEnvironment.None)))
                .Concat(productionMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, true, true, NetRatelMcpOperationEnvironment.Production)))
                .ToArray());
    }

    private static NetRatelMcpToolDescriptor DevelopmentAndProductionTerminalMixed(
        string name,
        string description,
        IReadOnlyList<string> developmentReads,
        IReadOnlyList<string> developmentOnlyMutations,
        IReadOnlyList<string> productionReads,
        IReadOnlyList<string> sharedConfirmedMutations,
        IReadOnlyList<string> productionPreviews,
        IReadOnlyList<string> productionControls)
    {
        var reads = developmentReads.Concat(productionReads).Distinct(StringComparer.Ordinal).ToArray();
        return new(
            name,
            $"{description} Policy-admitted V2 HTTP read operations: {OperationList(productionReads)}. Policy-admitted V2 HTTP preview operations: {OperationList(productionPreviews)}. Shared confirmed HTTP open/control operations: {OperationList(sharedConfirmedMutations)}; each requires confirm: true. Policy-admitted V2 HTTP bounded session controls: {OperationList(productionControls)}; each has a server-owned signed-delivery idempotency record before dispatch. Development-only compatibility mutations: {OperationList(developmentOnlyMutations)}.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                (developmentReads.Contains(operation, StringComparer.Ordinal) ? NetRatelMcpOperationEnvironment.Development : NetRatelMcpOperationEnvironment.None) |
                (productionReads.Contains(operation, StringComparer.Ordinal) ? NetRatelMcpOperationEnvironment.Production : NetRatelMcpOperationEnvironment.None)))
                .Concat(developmentOnlyMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.DevelopmentMutation, true, true, NetRatelMcpOperationEnvironment.Development)))
                .Concat(sharedConfirmedMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, true, true, NetRatelMcpOperationEnvironment.All)))
                .Concat(productionPreviews.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.Production)))
                .Concat(productionControls.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, false, true, NetRatelMcpOperationEnvironment.Production)
                {
                    RequiresIdempotency = true
                }))
                .ToArray());
    }

    private static NetRatelMcpToolDescriptor ProductionOperator(
        string name,
        string description,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> mutations)
        => new(
            name,
            $"{description} Policy-admitted V2 HTTP read and preview operations: {OperationList(reads)}. Confirmed policy-admitted V2 mutations: {OperationList(mutations)}; each requires confirm: true.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.Production))
                .Concat(mutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, true, true, NetRatelMcpOperationEnvironment.Production)))
                .ToArray());

    private static NetRatelMcpToolDescriptor DevelopmentReadAndProductionOperator(
        string name,
        string description,
        IReadOnlyList<string> developmentReads,
        IReadOnlyList<string> productionReads,
        IReadOnlyList<string> productionMutations,
        IReadOnlyList<string>? productionPreviews = null)
    {
        productionPreviews ??= [];
        var reads = developmentReads
            .Select(operation => (Operation: operation, Environment: NetRatelMcpOperationEnvironment.Development))
            .Concat(productionReads.Select(operation => (Operation: operation, Environment: NetRatelMcpOperationEnvironment.Production)))
            .GroupBy(item => item.Operation, StringComparer.Ordinal)
            .Select(group => new NetRatelMcpOperationDescriptor(
                group.Key,
                NetRatelMcpOperationSafety.Read,
                false,
                true,
                group.Aggregate(NetRatelMcpOperationEnvironment.None, (environment, item) => environment | item.Environment)));
        return new(
            name,
            $"{description} Development compatibility HTTP reads: {OperationList(developmentReads)}. Policy-admitted V2 HTTP reads: {OperationList(productionReads)}. Policy-admitted V2 no-write previews: {OperationList(productionPreviews)}. Confirmed policy-admitted V2 mutations: {OperationList(productionMutations)}; each requires confirm: true after a no-write preview.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Concat(productionPreviews.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.Production)))
                .Concat(productionMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation, NetRatelMcpOperationSafety.OperatorMutation, true, true, NetRatelMcpOperationEnvironment.Production)))
                .ToArray());
    }

    private static NetRatelMcpToolDescriptor DevelopmentAndProductionOperator(
        string name,
        string description,
        IReadOnlyList<string> reads,
        IReadOnlyList<string> productionHttpMutations)
        => new(
            name,
            $"{description} Read operations: {OperationList(reads)}. Development preserves its stdio-only confirmed mutation compatibility. Policy-admitted V2 HTTP confirmed mutations: {OperationList(productionHttpMutations)}; each requires confirm: true after a no-write preview.{PolicyAdmittedV2Availability} Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            true,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation, NetRatelMcpOperationSafety.Read, false, true, NetRatelMcpOperationEnvironment.All))
                .Concat(productionHttpMutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.OperatorMutation,
                    true,
                    true,
                    NetRatelMcpOperationEnvironment.All,
                    NetRatelMcpOperationEnvironment.Production)))
                .ToArray());

    private static NetRatelMcpToolDescriptor RemoteSupport() => new(
        "netratel_remote_support_v2",
        "Inspect Remote Support V2 presence, capabilities and inventory for an exact tenantId and agentId. Reads require observe scope and target policy. Check enabled, hasSnapshot, transportAvailable and fresh before selecting a target; a cached snapshot can survive a transport disconnect and does not prove a usable desktop. refresh_inventory requires execute scope: first call without confirm to obtain a plan, then repeat the unchanged target with planToken, idempotencyKey and confirm: true. A refresh receipt proves the request was sent, not that a new snapshot arrived; poll capabilities and inventory for a newer inventorySequence and observedAtUtc within the same connectionId and connectionEpoch. Inventory fresh uses the API receipt-based projectionExpiresAtUtc; snapshot.expiresAtUtc is agent-provided. If the connection changes, establish a new baseline. These operations do not open a desktop or prove media/input readiness. The explicit stdio compatibility profile retains its original contracts. Available over HTTP on V2 operator hosts. Set operation and supply only the matching closed request schema.",
        NetRatelMcpOperationSafety.OperatorMutation, true,
        new[] { "presence", "capabilities", "inventory", "refresh_inventory" }.Select(operation =>
            new NetRatelMcpOperationDescriptor(operation,
                operation == "refresh_inventory" ? NetRatelMcpOperationSafety.OperatorMutation : NetRatelMcpOperationSafety.Read,
                operation == "refresh_inventory", true, NetRatelMcpOperationEnvironment.All,
                NetRatelMcpOperationEnvironment.Production)).ToArray());

    private static NetRatelMcpToolDescriptor StdioOnlyMixed(string name, string description, IReadOnlyList<string> reads, IReadOnlyList<string> mutations)
        => new(
            name,
            $"{description} Read-only stdio operations: {OperationList(reads)}. Confirmed operator mutations: {OperationList(mutations)}; each requires confirm: true. This tool is unavailable over HTTP. Set operation and supply only the matching closed request schema.",
            NetRatelMcpOperationSafety.OperatorMutation,
            false,
            reads.Select(operation => new NetRatelMcpOperationDescriptor(
                operation,
                NetRatelMcpOperationSafety.Read,
                false,
                false,
                NetRatelMcpOperationEnvironment.All))
                .Concat(mutations.Select(operation => new NetRatelMcpOperationDescriptor(
                    operation,
                    NetRatelMcpOperationSafety.OperatorMutation,
                    true,
                    false,
                    NetRatelMcpOperationEnvironment.All)))
                .ToArray());

    private static string OperationList(IReadOnlyList<string> operations)
        => operations.Count == 0 ? "none" : string.Join(", ", operations);
}
