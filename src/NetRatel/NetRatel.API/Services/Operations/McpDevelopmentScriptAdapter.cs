using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Client;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Services.Operations;

/// <summary>Adapts the shared script library; it creates no ownership or source copies.</summary>
public sealed class McpDevelopmentScriptAdapter(
    IScriptService scripts, OrchestratorDbContext db, IMcpOperatorConfirmationService confirmations,
    IMcpOperatorRouteAdmission admission, IServiceScopeFactory scopeFactory)
{
    public static bool IsAllowed(McpOperatorDecision decision) => decision.IsAllowed &&
        decision.Request.Environment == McpOperatorEnvironment.Development && decision.DevelopmentEnvironmentAccess;

    public static bool IsSourceMutation(McpOperatorScriptMutationRequest request) =>
        request.Source is not null || request.ExpectedSourceRevision is not null || request.ExpectedContentHash is not null;

    public async Task<IReadOnlyList<McpDevelopmentScriptView>> ListAsync(McpOperatorDecision decision,
        long afterId, int limit, CancellationToken ct)
    {
        RequireAccess(decision);
        var rows = await scripts.ListPageAsync(afterId, limit, ct);
        return await ViewsAsync(rows, decision, ct);
    }

    public async Task<McpDevelopmentScriptDetail?> GetAsync(long id, McpOperatorDecision decision, CancellationToken ct)
    {
        RequireAccess(decision);
        if (id <= 0) return null;
        var source = await scripts.GetAsync((ulong)id, ct);
        if (source is null) return null;
        var view = (await ViewsAsync([source], decision, ct)).Single();
        return new(view, source.Content, source.ManifestJson);
    }

    public async Task<IReadOnlyList<ScriptParamInfo>?> ParamsAsync(long id, McpOperatorDecision decision, CancellationToken ct)
    {
        RequireAccess(decision);
        if (id <= 0 || await scripts.GetAsync((ulong)id, ct) is null) return null;
        return await scripts.GetParamsAsync((ulong)id, ct);
    }

    public async Task<object> PreviewAsync(string action, McpOperatorScriptMutationRequest request,
        McpOperatorDecision decision, CancellationToken ct)
    {
        RequireAccess(decision);
        ValidateShape(action, request);
        await CheckSourceAsync(scripts, action, request, ct);
        var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(decision, PayloadHash(action, request)), ct);
        return new
        {
            plan.PlanToken,
            plan.IdempotencyKey,
            plan.ExpiresAtUtc,
            plan.ConfirmationClass,
            action,
            request.ScriptId,
            request.ExpectedSourceRevision,
            request.ExpectedContentHash,
            contentHash = request.Source is null ? request.ExpectedContentHash : Hash(request.Source.Content),
            definitionScope = "development_environment",
            executionReviewCreated = false
        };
    }

    public async Task<McpDevelopmentScriptMutationResult> ConfirmAsync(string action, McpOperatorScriptMutationRequest request,
        McpOperatorDecision decision, McpOperatorRouteAccessRequest route, CancellationToken ct)
    {
        RequireAccess(decision);
        ValidateShape(action, request);
        if (string.IsNullOrWhiteSpace(request.PlanToken) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new McpDevelopmentScriptException("confirmation_plan_invalid");
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(
            request.PlanToken, request.IdempotencyKey, PayloadHash(action, request), decision), ct);
        if (confirmation.FailureCode is { } failure) throw new McpDevelopmentScriptException(failure);
        if (confirmation.IsReplay)
        {
            if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
                !TryReadReceipt(confirmation.ResultReference, out var receipt))
                throw new McpDevelopmentScriptException(confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending
                    ? "idempotency_pending" : "idempotency_replay_unavailable");
            return receipt! with { Replayed = true };
        }
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            throw new McpDevelopmentScriptException("confirmation_plan_invalid");

        try
        {
            var audit = await admission.RecordAcceptedAsync(route, ct);
            var current = (await admission.EvaluateAsync(route, ct)).Decision;
            if (!IsAllowed(current) || audit.PolicyId != decision.MatchingPolicyIds.Single() ||
                current.SelectedPolicyVersion != decision.SelectedPolicyVersion ||
                !current.MatchingPolicyIds.SequenceEqual(decision.MatchingPolicyIds))
                throw new McpDevelopmentScriptException("confirmation_plan_stale");
            return await ApplyAsync(action, request, audit, idempotencyId, ct);
        }
        catch (Exception exception) when (exception is ScriptSourceConcurrencyException or DbUpdateConcurrencyException or
            ScriptSourceInUseException or McpDevelopmentScriptException or McpOperatorAdmissionRejectedException or ArgumentException or JsonException)
        {
            var code = exception switch
            {
                ScriptSourceConcurrencyException or DbUpdateConcurrencyException => "script_source_revision_conflict",
                ScriptSourceInUseException => "script_source_in_use",
                McpDevelopmentScriptException source => source.Code,
                McpOperatorAdmissionRejectedException rejected => rejected.FailureCode,
                _ => "script_invalid"
            };
            // Failed source writes were in a separate scope/transaction. A
            // fresh completion scope cannot resubmit their tracked changes.
            await using var failureScope = scopeFactory.CreateAsyncScope();
            await failureScope.ServiceProvider.GetRequiredService<IMcpOperatorConfirmationService>()
                .CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, code, CancellationToken.None);
            throw new McpDevelopmentScriptException(code);
        }
    }

    private async Task<McpDevelopmentScriptMutationResult> ApplyAsync(string action,
        McpOperatorScriptMutationRequest request, McpOperatorAcceptedAudit audit, Guid idempotencyId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sourceDb = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var library = scope.ServiceProvider.GetRequiredService<IScriptService>();
        await using var transaction = sourceDb.Database.IsRelational()
            ? await sourceDb.Database.BeginTransactionAsync(ct) : null;
        await CheckSourceAsync(library, action, request, ct);
        var source = request.Source;
        var result = action switch
        {
            "create" => await library.CreateAsync(new CreateScriptCommand(source!.Name, source.FolderPath,
                source.Description, source.Content, source.ScriptType, source.ManifestJson), ct),
            "update" => await library.UpdateAsync(new UpdateScriptCommand((ulong)request.ScriptId!.Value, source!.Name,
                source.FolderPath, source.Description ?? string.Empty, source.Content, source.ScriptType,
                source.ManifestJson, request.ExpectedSourceRevision), ct),
            "parse_manifest" => await library.ParseManifestAsync((ulong)request.ScriptId!.Value,
                request.ManifestJson, request.ExpectedSourceRevision!.Value, ct),
            "delete" => await library.DeleteAsync((ulong)request.ScriptId!.Value, request.ExpectedSourceRevision!.Value, ct),
            _ => throw new McpDevelopmentScriptException("script_invalid")
        } ?? throw new McpDevelopmentScriptException("script_not_found");
        var receipt = new McpDevelopmentScriptMutationResult((long)result.Id, result.SourceRevision, action == "delete", false);
        await scope.ServiceProvider.GetRequiredService<IEventRecorder>().RecordAsync(new DomainEvent
        {
            EventType = action == "create" ? NetRatelEventTypes.Script.Created : action == "delete"
                ? NetRatelEventTypes.Script.Deleted : NetRatelEventTypes.Script.Updated,
            Source = "McpDevelopmentScriptAdapter",
            EntityId = result.Id.ToString(),
            CorrelationId = audit.CorrelationId,
            TenantId = audit.TenantId.ToString(),
            Severity = "Info",
            Message = $"Canonical script {result.Id} {action}.",
            Payload = new
            {
                scriptId = result.Id,
                result.SourceRevision,
                contentHash = Hash(result.Content),
                acceptedAuditId = audit.AuditId,
                action,
                definitionScope = "development_environment"
            }
        }, ct);
        await scope.ServiceProvider.GetRequiredService<IMcpOperatorConfirmationService>().CompleteAsync(idempotencyId,
            McpOperatorIdempotencyOutcome.Succeeded, $"source:{receipt.ScriptId}:{receipt.SourceRevision}:{receipt.Deleted}", ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return receipt;
    }

    private async Task<IReadOnlyList<McpDevelopmentScriptView>> ViewsAsync(IReadOnlyList<ScriptInfo> sources,
        McpOperatorDecision decision, CancellationToken ct)
    {
        var ids = sources.Select(source => (long)source.Id).ToArray();
        var access = decision.Request;
        var owners = await db.McpOperatorScripts.AsNoTracking().Where(owner => ids.Contains(owner.ScriptId) &&
            owner.TenantId == access.TenantId && owner.Subject == access.Principal.Subject &&
            owner.ClientId == (access.Principal.ClientId ?? string.Empty) && owner.McpResource == access.McpResource &&
            owner.McpInstance == access.McpInstance && owner.DeletedAtUtc == null).ToDictionaryAsync(owner => owner.ScriptId, ct);
        return sources.Select(source =>
        {
            owners.TryGetValue((long)source.Id, out var owner);
            var eligible = owner is not null && McpOperatorScriptStore.IsStoredScriptIntegrityValid(owner, new ScriptDefinition
            { Name = source.Name, Description = source.Description, Content = source.Content, ScriptType = source.ScriptType, ManifestJson = source.ManifestJson });
            return new McpDevelopmentScriptView((long)source.Id, source.Name, source.FolderPath, source.Description,
                source.ScriptType, Hash(source.Content), Hash(source.ManifestJson ?? string.Empty), source.SourceRevision,
                source.CreatedAtUtc, source.UpdatedAtUtc, eligible, eligible ? owner!.Version : null,
                eligible ? null : owner is null ? "global_script_execution_not_available" : "reviewed_script_metadata_stale");
        }).ToArray();
    }

    private static async Task CheckSourceAsync(IScriptService library, string action,
        McpOperatorScriptMutationRequest request, CancellationToken ct)
    {
        if (action == "create") return;
        var existing = await library.GetAsync((ulong)request.ScriptId!.Value, ct)
            ?? throw new McpDevelopmentScriptException("script_not_found");
        if (existing.SourceRevision != request.ExpectedSourceRevision ||
            !string.Equals(Hash(existing.Content), request.ExpectedContentHash, StringComparison.OrdinalIgnoreCase))
            throw new ScriptSourceConcurrencyException();
    }

    private static void RequireAccess(McpOperatorDecision decision)
    {
        if (!IsAllowed(decision)) throw new McpDevelopmentScriptException("development_environment_access_required");
    }

    private static void ValidateShape(string action, McpOperatorScriptMutationRequest request)
    {
        if (request.Script is not null || request.ExpectedVersion is not null ||
            action is not ("create" or "update" or "parse_manifest" or "delete"))
            throw new ArgumentException("Canonical source and reviewed script mutations cannot be mixed.");
        if (action == "create")
        {
            if (request.ScriptId is not null || request.ExpectedSourceRevision is not null || request.ExpectedContentHash is not null)
                throw new ArgumentException("Creation cannot name an existing source revision.");
        }
        else if (request.ScriptId is not > 0 || request.ExpectedSourceRevision is not > 0 ||
            request.ExpectedContentHash is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
            throw new ArgumentException("An observed source revision and content hash are required.");
        if (action is "create" or "update")
        {
            var source = request.Source ?? throw new ArgumentException("A canonical source draft is required.");
            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 120 || source.Content is null ||
                Encoding.UTF8.GetByteCount(source.Content) > 64 * 1024 || string.IsNullOrWhiteSpace(source.ScriptType) ||
                source.ScriptType.Length > 64 || source.FolderPath is null || source.FolderPath.Length > 512 ||
                source.Description?.Length > 4096 || request.ManifestJson is not null)
                throw new ArgumentException("The canonical source draft exceeds its bounds.");
            ValidateManifest(source.ManifestJson);
        }
        else
        {
            if (request.Source is not null) throw new ArgumentException("This mutation does not accept a source draft.");
            if (action == "parse_manifest")
            {
                if (request.ManifestJson is null) throw new ArgumentException("A manifest is required.");
                ValidateManifest(request.ManifestJson);
            }
            else if (request.ManifestJson is not null) throw new ArgumentException("Delete does not accept a manifest.");
        }
    }

    private static void ValidateManifest(string? manifest)
    {
        if (manifest is null) return;
        if (Encoding.UTF8.GetByteCount(manifest) > 16 * 1024) throw new ArgumentException("The manifest exceeds its bound.");
        using var parsed = JsonDocument.Parse(manifest);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("A manifest must be an object.");
    }

    private static string PayloadHash(string action, McpOperatorScriptMutationRequest request) => Hash(JsonSerializer.Serialize(new
    {
        action,
        request.ScriptId,
        request.Source,
        request.ManifestJson,
        request.ExpectedSourceRevision,
        expectedContentHash = request.ExpectedContentHash?.ToUpperInvariant(),
        definitionScope = "development_environment"
    }));

    private static bool TryReadReceipt(string? value, out McpDevelopmentScriptMutationResult? receipt)
    {
        receipt = null;
        var parts = value?.Split(':');
        if (parts is not { Length: 4 } || parts[0] != "source" || !long.TryParse(parts[1], out var id) || id <= 0 ||
            !long.TryParse(parts[2], out var revision) || revision <= 0 || !bool.TryParse(parts[3], out var deleted)) return false;
        receipt = new(id, revision, deleted, true);
        return true;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record McpDevelopmentScriptSource(string Name, string Content, string ScriptType,
    string FolderPath = "/", string? Description = null, string? ManifestJson = null);
public sealed record McpDevelopmentScriptView(long ScriptId, string Name, string FolderPath, string Description,
    string ScriptType, string ContentHash, string ManifestHash, long SourceRevision, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, bool ExecutionEligible, long? ReviewedVersion, string? ExecutionLimitation)
{
    public string DefinitionScope => "development_environment";
}
public sealed record McpDevelopmentScriptDetail(McpDevelopmentScriptView Script, string Content, string? ManifestJson);
public sealed record McpDevelopmentScriptMutationResult(long ScriptId, long SourceRevision, bool Deleted, bool Replayed)
{
    public string DefinitionScope => "development_environment";
}
public sealed class McpDevelopmentScriptException(string code) : InvalidOperationException("The canonical script operation was not admitted.")
{
    public string Code { get; } = code;
}
