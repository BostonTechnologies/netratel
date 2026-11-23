using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class JobSecretDefaultProtection(OrchestratorDbContext db, IDataProtectionProvider protection)
{
    public async Task ProtectLegacyDefaultsAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.JobParameters.FromSqlRaw("""
            SELECT * FROM "JobParameters" WHERE lower("Type") = 'secret' AND "DefaultValue" IS NOT NULL ORDER BY "Id" FOR UPDATE
            """).ToArrayAsync(ct);
        var jobIds = rows.Select(x => x.JobId).Distinct().ToArray();
        var tenants = await db.Jobs.IgnoreQueryFilters().Where(job => jobIds.Contains(job.Id)).AsNoTracking()
            .ToDictionaryAsync(job => job.Id, job => job.TenantId, ct);
        foreach (var row in rows)
        {
            if (row.ProtectedDefaultValue is not null)
                throw new InvalidOperationException("The job parameter contains inconsistent secret default state.");
            Store(row, tenants[row.JobId], row.DefaultValue);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal void Store(JobParameterDefinition row, int? tenant, string? value)
    {
        row.ProtectedDefaultValue = value is null ? null : ForParameter(tenant, row.JobId, row.Id).Protect(value);
        row.DefaultValue = null;
    }

    internal async Task<PinnedJobSecretDefault[]> CaptureAsync(long jobId, CancellationToken ct) =>
        await db.JobParameters.AsNoTracking().Where(row => row.JobId == jobId && row.ProtectedDefaultValue != null)
            .OrderBy(row => row.Id).Select(row => new PinnedJobSecretDefault((ulong)row.Id, row.Name, row.ProtectedDefaultValue!)).ToArrayAsync(ct);

    internal string? Apply(JobRunRecord run, string? inputs, IReadOnlyList<PinnedJobSecretDefault> defaults)
    {
        if (defaults.Count == 0)
            return inputs;
        JsonObject values;
        try
        {
            values = string.IsNullOrWhiteSpace(inputs) ? new JsonObject() : JsonNode.Parse(inputs) as JsonObject ??
                throw new InvalidOperationException("Job inputs must be a JSON object to use protected defaults.");
        }
        catch (JsonException) { throw new InvalidOperationException("Job inputs must be valid JSON to use protected defaults."); }

        var nested = values["input"] as JsonObject;
        var supplied = values.Select(x => x.Key).Concat(nested?.Select(x => x.Key) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var value in defaults.Where(value => !supplied.Contains(value.Name)))
                (nested ?? values)[value.Name] = ForParameter(run.TenantId, run.JobId, checked((long)value.ParameterId)).Unprotect(value.ProtectedValue);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("A protected job default is unavailable. Restore the matching API key ring.");
        }
        return values.ToJsonString();
    }

    private IDataProtector ForParameter(int? tenant, long job, long parameter) => protection.CreateProtector("NetRatel.JobParameter.SecretDefault.v1",
        tenant?.ToString(CultureInfo.InvariantCulture) ?? "instance", job.ToString(CultureInfo.InvariantCulture), parameter.ToString(CultureInfo.InvariantCulture));
}
