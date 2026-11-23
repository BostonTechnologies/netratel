using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Services.Jobs;

internal sealed class JobProviderExecutionInputs(OrchestratorDbContext db, ITenantSecretService vault)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed record Binding(string ParameterName, SecretReference Reference, long ScopeRevision);

    public async Task<string?> AdmitAsync(int tenantId, PinnedJobExecutionSnapshot snapshot, RunJobRequest request, CancellationToken ct)
    {
        if (request.ProviderInputs is not { Count: > 0 } inputs) return null;
        if (inputs.Count > 32) throw Invalid("Supply at most 32 protected job parameter bindings.");
        var manual = JobTemplateHelper.ParseInputs(request.InputsJson);
        var connections = await vault.ListConnectionsAsync(tenantId, includeAdministration: false, ct);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var admitted = new List<Binding>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input is null || input.Reference is not { } reference || !ProtectedJobParameterRules.IsName(input.ParameterName) ||
                !names.Add(input.ParameterName) || manual.ContainsKey(input.ParameterName) ||
                snapshot.Definition.Params.Count(p => p.Name.Equals(input.ParameterName, StringComparison.OrdinalIgnoreCase) && IsSecret(p.Type)) != 1)
                throw Invalid("Each protected reference must bind one declared Secret parameter without a manual value.");
            if (reference.SchemaVersion != SecretReference.CurrentSchemaVersion || reference.Locator is not { Length: > 0 and <= 1024 } ||
                (reference.VersionMode switch
                {
                    SecretVersionMode.LatestAtExecution => reference.Version is not null,
                    SecretVersionMode.Pinned => string.IsNullOrWhiteSpace(reference.Version),
                    _ => true
                })) throw Invalid("Select a supported Latest or Pinned secret reference.");
            if (snapshot.Definition.Job.Target?.RuntimeInput?.Name.Equals(input.ParameterName, StringComparison.OrdinalIgnoreCase) == true)
                throw Invalid("Protected parameters cannot select execution targets.");
            if (!snapshot.Definition.Steps.Any(step => step.Enabled && MapName(snapshot, step, input.ParameterName) is not null))
                throw Invalid("A protected parameter requires a matching Secret parameter on a PowerShell or Bash library step.");
            var placeholder = $"@option.{input.ParameterName}@";
            if (snapshot.Definition.Steps.Any(step => step.Enabled &&
                (step.Command?.Contains(placeholder, StringComparison.OrdinalIgnoreCase) == true ||
                step.PayloadJson?.Contains(placeholder, StringComparison.OrdinalIgnoreCase) == true)))
                throw Invalid("Use a library script Secret parameter instead of interpolating a protected parameter into a job step.");
            var connection = connections.SingleOrDefault(current => current.Id == reference.ConnectionId && current.Enabled)
                ?? throw Invalid("A referenced provider is unavailable in this tenant.");
            var metadata = await vault.GetMetadataAsync(tenantId, reference.ConnectionId, reference.Locator, reference.Version, ct);
            if (metadata.Entry.IsFolder || metadata.Entry.Enabled == false ||
                (connection.Capabilities.StructuredFields ? reference.Field is null || !metadata.Fields.Any(field => field.Name == reference.Field) : reference.Field is not null))
                throw Invalid("Select an available secret value or field from this provider.");
            admitted.Add(new(input.ParameterName, reference with { Locator = metadata.Entry.Locator }, connection.ScopeRevision));
        }
        return JsonSerializer.Serialize(admitted, Json);
    }

    public static Binding[] ReadBindings(string? json)
    {
        if (json is null) return [];
        try
        {
            if (JsonSerializer.Deserialize<Binding[]>(json, Json) is { Length: > 0 and <= 32 } bindings &&
                bindings.All(binding => binding is { Reference: not null, ScopeRevision: > 0 } && ProtectedJobParameterRules.IsName(binding.ParameterName)) &&
                bindings.Select(binding => binding.ParameterName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == bindings.Length)
                return bindings;
        }
        catch (JsonException) { throw new ExecutionAdmissionDeniedException("Protected parameter bindings are unavailable."); }
        throw new ExecutionAdmissionDeniedException("Protected parameter bindings are unavailable.");
    }

    public static string? MapName(PinnedJobExecutionSnapshot snapshot, JobStepInfo step, string name)
    {
        if (step.Type != JobStepKind.LibraryScript || step.ScriptId is not { } scriptId) return null;
        var source = snapshot.Scripts.SingleOrDefault(script => script.Script.Id == scriptId);
        if (source is null || !Enum.TryParse<ScriptType>(source.Script.ScriptType, ignoreCase: true, out var type) ||
            type is not (ScriptType.PowerShell or ScriptType.Bash)) return null;
        return source.Parameters.SingleOrDefault(parameter => IsSecret(parameter.Type) &&
            parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && ProtectedJobParameterRules.IsName(parameter.Name))?.Name;
    }

    private static bool IsSecret(string type) => type.Equals("secret", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, string>> ResolveForStepAsync(long parentId, int tenantId, Binding[] admitted,
        IReadOnlyDictionary<string, string> stepNames, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || !db.Database.IsNpgsql())
            throw new ExecutionAdmissionDeniedException("The durable protected input selection is unavailable.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var parent = await db.Set<JobAggregateControlRecord>().FromSqlInterpolated($"""
            SELECT * FROM "JobAggregateControls" WHERE "ParentRunId" = {parentId} FOR UPDATE
            """).AsNoTracking().SingleAsync(ct);
        if (!ReadBindings(parent.ProviderInputBindingsJson).SequenceEqual(admitted) || parent.CancellationRequestedAtUtc.HasValue)
            throw new ExecutionAdmissionDeniedException("The protected parameter admission changed or was cancelled.");

        var pinned = parent.PinnedProviderInputBindingsJson is null ? null : ReadBindings(parent.PinnedProviderInputBindingsJson);
        if (pinned is not null && (pinned.Length != admitted.Length || pinned.Where((item, index) =>
                item.ParameterName != admitted[index].ParameterName || item.ScopeRevision != admitted[index].ScopeRevision ||
                item.Reference.VersionMode != SecretVersionMode.Pinned ||
                item.Reference with { VersionMode = admitted[index].Reference.VersionMode, Version = admitted[index].Reference.Version } != admitted[index].Reference).Any()))
            throw new ExecutionAdmissionDeniedException("The pinned protected input selection differs from its admission.");
        var requested = pinned is null ? admitted : pinned.Where(binding => stepNames.ContainsKey(binding.ParameterName)).ToArray();
        if (requested.Length == 0) return new Dictionary<string, string>();
        var connections = await vault.ListConnectionsAsync(tenantId, includeAdministration: false, ct);
        if (requested.Any(input => !connections.Any(connection => connection.Id == input.Reference.ConnectionId && connection.Enabled &&
                connection.ScopeRevision == input.ScopeRevision)))
            throw new ExecutionAdmissionDeniedException("A referenced provider scope changed after admission.");
        var resolved = await vault.ResolveForExecutionAsync(tenantId, requested.Select(input => input.Reference).ToArray(), ct);
        if (resolved.Count != requested.Length || resolved.Where((item, index) => item.ScopeRevision != requested[index].ScopeRevision).Any())
            throw new ExecutionAdmissionDeniedException("A referenced provider scope changed during resolution.");
        if (pinned is null)
        {
            var exact = resolved.Select((item, index) => requested[index] with { Reference = item.ExactReference }).ToArray();
            var serialized = JsonSerializer.Serialize(exact, Json);
            await db.Set<JobAggregateControlRecord>().Where(row => row.ParentRunId == parentId).ExecuteUpdateAsync(
                update => update.SetProperty(row => row.PinnedProviderInputBindingsJson, serialized), ct);
        }
        await tx.CommitAsync(ct);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < requested.Length; index++)
            if (stepNames.TryGetValue(requested[index].ParameterName, out var name)) values.Add(name, resolved[index].Value);
        if (values.Count != 0 && !ProtectedJobParameterRules.AreValid(values))
            throw Invalid("The resolved protected parameters exceed the agent's transport bounds.");
        return values;
    }

    private static JobTargetValidationException Invalid(string message) => new("job_provider_input_invalid", message);
}
