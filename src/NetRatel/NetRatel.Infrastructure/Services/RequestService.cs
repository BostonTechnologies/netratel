using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class RequestService(OrchestratorDbContext db) : IRequestService
{
    private readonly OrchestratorDbContext _db = db;

    public async Task<IReadOnlyList<RequestInfo>> ListAsync(CancellationToken ct = default)
        => await _db.Requests
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(MapRequest())
            .ToListAsync(ct);

    public async Task<RequestInfo?> GetAsync(int requestId, CancellationToken ct = default)
        => await _db.Requests
            .AsNoTracking()
            .Where(x => x.Id == requestId)
            .Select(MapRequest())
            .FirstOrDefaultAsync(ct);

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
            JobInputs = command.JobInputsJson,
            Status = "New",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Logs = [$"[{now:O}] Request submitted by {command.SourceSystem.Trim()}."]
        };

        _db.Requests.Add(row);
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<RequestInfo?> UpdateAsync(UpdateRequestCommand command, CancellationToken ct = default)
    {
        var row = await _db.Requests.FirstOrDefaultAsync(x => x.Id == command.RequestId, ct);
        if (row is null)
        {
            return null;
        }

        if (command.SourceSystem is not null) row.SourceSystem = command.SourceSystem.Trim();
        if (command.TargetClientIdentity is not null) row.TargetClientIdentity = command.TargetClientIdentity.Trim();
        if (command.JobDefinitionId is not null) row.JobDefinitionId = NormalizeNullable(command.JobDefinitionId);
        if (command.ExecutionId is not null) row.ExecutionId = NormalizeNullable(command.ExecutionId);
        if (command.Status is not null) row.Status = command.Status.Trim();
        if (command.ResultMessage is not null) row.ResultMessage = NormalizeNullable(command.ResultMessage);
        if (command.ResultData is not null) row.ResultData = NormalizeNullable(command.ResultData);
        if (command.JobInputs is not null) row.JobInputs = NormalizeNullable(command.JobInputs);
        if (command.Logs is not null) row.Logs = [.. command.Logs];
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(row);
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

    private static System.Linq.Expressions.Expression<Func<RequestRecord, RequestInfo>> MapRequest()
        => row => new RequestInfo(
            row.Id,
            row.SourceSystem,
            row.TargetClientIdentity,
            row.JobDefinitionId,
            row.ExecutionId,
            row.Status,
            row.ResultMessage,
            row.ResultData,
            row.JobInputs,
            row.Logs,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.TargetTenantId,
            row.TargetAgentId);
}
