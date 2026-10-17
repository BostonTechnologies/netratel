using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class RequestService(OrchestratorDbContext db, IDataProtectionProvider protection) : IRequestService
{
    private readonly OrchestratorDbContext _db = db;
    private readonly JobRunInputProtection _protection = new(protection);

    public async Task<IReadOnlyList<RequestInfo>> ListAsync(CancellationToken ct = default)
    {
        await ProtectLegacyAsync(null, ct);
        return (await _db.Requests.AsNoTracking().OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id)
            .ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<RequestInfo?> GetAsync(int requestId, CancellationToken ct = default)
    {
        await ProtectLegacyAsync(requestId, ct);
        var row = await _db.Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == requestId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<RequestInfo> CreateAsync(CreateRequestCommand command, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new RequestRecord
        {
            SourceSystem = command.SourceSystem.Trim(),
            TargetClientIdentity = command.TargetClientIdentity.Trim(),
            TargetTenantId = command.TargetTenantId,
            TargetAgentId = command.TargetAgentId,
            JobDefinitionId = NormalizeNullable(command.JobDefinitionId),
            JobInputs = null,
            Status = "New",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Logs = [$"[{now:O}] Request submitted by {command.SourceSystem.Trim()}."]
        };

        var definition = long.TryParse(row.JobDefinitionId, out var jobId)
            ? await _db.Jobs.AsNoTracking().Include(x => x.Parameters).AsSingleQuery().SingleOrDefaultAsync(x => x.Id == jobId && x.TenantId == row.TargetTenantId, ct) : null;
        var metadata = JobRunInputProtection.Capture(definition, definition?.Parameters ?? []);
        row.InputSensitivityJson = System.Text.Json.JsonSerializer.Serialize(metadata);
        row.JobInputs = JobRunInputProtection.RedactInputs(command.JobInputsJson, metadata);
        _db.Requests.Add(row);
        await _db.SaveChangesAsync(ct);
        // The database allocates Request.Id. Only the sanitized projection is ever inserted;
        // bind the separately protected duplicate payload to that stable request identity.
        Store(row, metadata, command.JobInputsJson);
        SanitizeDiagnostics(row);
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<RequestInfo?> UpdateAsync(UpdateRequestCommand command, CancellationToken ct = default)
    {
        await ProtectLegacyAsync(command.RequestId, ct);
        var row = await _db.Requests.FirstOrDefaultAsync(x => x.Id == command.RequestId, ct);
        if (row is null)
        {
            return null;
        }

        if (command.SourceSystem is not null) row.SourceSystem = command.SourceSystem.Trim();
        if (command.TargetClientIdentity is not null) row.TargetClientIdentity = command.TargetClientIdentity.Trim();
        if (command.JobDefinitionId is not null && NormalizeNullable(command.JobDefinitionId) != row.JobDefinitionId)
            throw new InvalidOperationException("A submitted request cannot change its job definition binding.");
        if (command.ExecutionId is not null) row.ExecutionId = NormalizeNullable(command.ExecutionId);
        if (command.Status is not null) row.Status = command.Status.Trim();
        if (command.ResultMessage is not null) row.ResultMessage = NormalizeNullable(command.ResultMessage);
        if (command.ResultData is not null) row.ResultData = NormalizeNullable(command.ResultData);
        if (command.JobInputs is not null) Store(row, JobRunInputProtection.Metadata(Material(row)), command.JobInputs);
        if (command.Logs is not null) row.Logs = [.. command.Logs];
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        SanitizeDiagnostics(row);
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    private static JobRunRecord Material(RequestRecord row) => new()
    {
        // Separate DP purpose identity from the positive job-run identifier space.
        Id = -(long)row.Id, JobId = long.TryParse(row.JobDefinitionId, out var id) ? id : 0,
        TenantId = row.TargetTenantId, InputsJson = row.JobInputs,
        InputSensitivityJson = row.InputSensitivityJson, ProtectedExecutionJson = row.ProtectedJobInputsJson
    };

    private void Store(RequestRecord row, JobRunInputProtection.Sensitivity metadata, string? inputs)
    {
        var material = Material(row);
        _protection.Store(material, metadata, inputs, null);
        row.JobInputs = material.InputsJson;
        row.InputSensitivityJson = material.InputSensitivityJson;
        row.ProtectedJobInputsJson = material.ProtectedExecutionJson;
    }

    private void SanitizeDiagnostics(RequestRecord row)
    {
        var material = Material(row);
        row.ResultMessage = _protection.Diagnostic(material, row.ResultMessage);
        row.ResultData = _protection.Diagnostic(material, row.ResultData);
        row.Logs = row.Logs.Select(x => _protection.Diagnostic(material, x)!).ToList();
    }

    private async Task ProtectLegacyAsync(int? requestId, CancellationToken ct)
    {
        var query = _db.Requests.Where(x => x.ProtectedJobInputsJson == null);
        if (requestId.HasValue) query = query.Where(x => x.Id == requestId);
        var rows = await query.ToListAsync(ct);
        foreach (var row in rows)
        {
            Store(row, new("legacy-unknown-v1", null, []), row.JobInputs);
            SanitizeDiagnostics(row);
        }
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RequestInfo Map(RequestRecord row)
        => new(
            row.Id,
            row.SourceSystem,
            row.TargetClientIdentity,
            row.JobDefinitionId,
            row.ExecutionId,
            row.Status,
            row.ResultMessage,
            row.ResultData,
            row.JobInputs,
            row.Logs.AsReadOnly(),
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.TargetTenantId,
            row.TargetAgentId);

}
