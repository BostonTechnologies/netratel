using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.Web.Services.Script;

public class ScriptService : IScriptLibraryService
{
    private readonly IHttpClientFactory _clients;

    public ScriptService(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    private HttpClient CreateClient() => _clients.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<ScriptModel>> GetScriptsAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var result = await client.GetFromJsonAsync<List<ScriptModel>>("/api/v1/script-library", cancellationToken);
        return result ?? new List<ScriptModel>();
    }

    public async Task<ScriptModel?> GetScriptAsync(ulong id, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        return await client.GetFromJsonAsync<ScriptModel>($"/api/v1/script-library/{id}", cancellationToken);
    }

    public async Task<PagedResult<ScriptSummaryDto>> SearchAsync(
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedPageSize = Math.Max(pageSize, 1);

        var scripts = await GetScriptsAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ScriptModel> filtered = scripts;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(s =>
                s.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (s.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.FolderPath?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.ScriptType?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = filtered
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.FolderPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = ordered.Count;
        var pageItems = ordered
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(MapSummary)
            .ToList();

        return new PagedResult<ScriptSummaryDto>(pageItems, normalizedPage, normalizedPageSize, totalCount);
    }

    public async Task<ScriptSummaryDto?> GetByIdAsync(ulong id, CancellationToken cancellationToken = default)
    {
        var script = await GetScriptAsync(id, cancellationToken).ConfigureAwait(false);
        return script is null ? null : MapSummary(script);
    }

    public async Task CreateScriptAsync(ScriptCreateRequest request, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/script-library", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateScriptAsync(ulong scriptId, ScriptUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.PutAsJsonAsync($"/api/v1/script-library/{scriptId}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteScriptAsync(ulong scriptId, long expectedSourceRevision, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var response = await client.DeleteAsync($"/api/v1/script-library/{scriptId}?expectedSourceRevision={expectedSourceRevision}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static ScriptSummaryDto MapSummary(ScriptModel model)
    {
        string[]? tags = null;
        if (!string.IsNullOrWhiteSpace(model.ScriptType))
        {
            tags = model.ScriptType.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tags.Length == 0)
            {
                tags = null;
            }
        }

        return new ScriptSummaryDto(
            model.Id,
            model.Name,
            model.Description,
            model.ScriptType,
            tags);
    }
}

public record ScriptModel(
    ulong Id,
    string Name,
    string FolderPath,
    string? Description,
    string? Content,
    string? ScriptType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long SourceRevision = 1);

public record ScriptCreateRequest(
    string Name,
    string FolderPath,
    string? Description,
    string? Content,
    string? ScriptType);

public record ScriptUpdateRequest(
    string? Name,
    string? FolderPath,
    string? Description,
    string? Content,
    string? ScriptType,
    long? ExpectedSourceRevision = null);
