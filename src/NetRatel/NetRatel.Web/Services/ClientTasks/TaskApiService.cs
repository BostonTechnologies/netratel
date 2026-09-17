using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Web.Services.ClientTasks
{
    public sealed class TaskApiService
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
        };

        public TaskApiService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient("OrchestratorApi");
        }

        // RETURN: (task, accepted)
        public async Task<(TaskDto? task, bool accepted)> CreateAsync(TaskCreateRequestDto request, CancellationToken cancellationToken = default)
        {
            using var resp = await _http.PostAsJsonAsync("/api/v2/tasks", request, _json, cancellationToken)
                                        .ConfigureAwait(false);

            var accepted = resp.StatusCode == HttpStatusCode.Accepted;

            if (accepted || resp.StatusCode == HttpStatusCode.Created || resp.IsSuccessStatusCode)
            {
                // Try body (may be empty on 202)
                var dto = await TryReadBodyAsync<TaskDto>(resp, cancellationToken).ConfigureAwait(false);
                if (dto is not null) return (dto, accepted);

                // Try Location
                if (resp.Headers.Location is Uri loc)
                {
                    var last = loc.Segments.LastOrDefault()?.Trim('/');
                    if (int.TryParse(last, out var idFromLocation))
                    {
                        var byId = await GetAsync(idFromLocation, cancellationToken).ConfigureAwait(false);
                        if (byId is not null) return (byId, accepted);
                    }

                    try
                    {
                        var fetched = await _http.GetFromJsonAsync<TaskDto>(loc, _json, cancellationToken).ConfigureAwait(false);
                        if (fetched is not null) return (fetched, accepted);
                    }
                    catch { /* ignore */ }
                }

                // Try custom header (if ever added)
                if (resp.Headers.TryGetValues("X-Task-Id", out var vals) &&
                    int.TryParse(vals.FirstOrDefault(), out var headerId))
                {
                    var byId = await GetAsync(headerId, cancellationToken).ConfigureAwait(false);
                    if (byId is not null) return (byId, accepted);
                }

                // Accepted (queued) but no id yet
                return (null, accepted);
            }

            // Non-success
            return (null, false);
        }

        public Task<TaskDto?> GetAsync(int id, CancellationToken cancellationToken = default)
            => _http.GetFromJsonAsync<TaskDto>($"/api/v2/tasks/{id}", _json, cancellationToken);

        public async Task<TaskDto?> GetByRequestIdAsync(string requestId, CancellationToken cancellationToken = default)
        {
            var rsp = await _http.GetAsync($"/api/v2/tasks?requestId={Uri.EscapeDataString(requestId)}", cancellationToken)
                                 .ConfigureAwait(false);

            if (!rsp.IsSuccessStatusCode) return null;

            var items = await rsp.Content.ReadFromJsonAsync<List<TaskDto>>(cancellationToken: cancellationToken)
                                         .ConfigureAwait(false);
            return items?.FirstOrDefault();
        }

        public Task<List<TaskDto>?> GetRecentAsync(
            int limit = 25,
            Guid? agentId = null,
            int? tenantId = null,
            string? taskType = null,
            string? status = null,
            ClientEnvironment? environment = null,
            CancellationToken cancellationToken = default)
        {
            var query = new List<string> { $"limit={limit}" };
            if (agentId.HasValue) query.Add($"agentId={agentId.Value}");
            if (tenantId.HasValue) query.Add($"tenantId={tenantId.Value}");
            if (!string.IsNullOrWhiteSpace(taskType)) query.Add($"taskType={Uri.EscapeDataString(taskType)}");
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (environment.HasValue) query.Add($"environment={(int)environment.Value}");

            return _http.GetFromJsonAsync<List<TaskDto>>($"/api/v2/tasks/recent?{string.Join("&", query)}", _json, cancellationToken);
        }

        public Task<TaskHistoryPageDto?> GetHistoryAsync(
            int page,
            int pageSize,
            string? search = null,
            string? status = null,
            string? taskType = null,
            int? tenantId = null,
            Guid? agentId = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            var query = new List<string>
            {
                $"page={page}",
                $"pageSize={pageSize}"
            };
            if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            if (!string.IsNullOrWhiteSpace(taskType)) query.Add($"taskType={Uri.EscapeDataString(taskType)}");
            if (tenantId.HasValue) query.Add($"tenantId={tenantId.Value}");
            if (agentId.HasValue) query.Add($"agentId={agentId.Value:D}");
            if (!string.IsNullOrWhiteSpace(requestId)) query.Add($"requestId={Uri.EscapeDataString(requestId)}");

            return _http.GetFromJsonAsync<TaskHistoryPageDto>(
                $"/api/v2/tasks/history?{string.Join("&", query)}",
                _json,
                cancellationToken);
        }

        public Task<List<TaskLogDto>?> GetLogsAsync(int id, long sinceId = 0, string stream = "all", CancellationToken cancellationToken = default)
            => _http.GetFromJsonAsync<List<TaskLogDto>>($"/api/v2/tasks/{id}/logs?sinceId={sinceId}&stream={stream}", _json, cancellationToken);

        public Task<List<TaskLogDto>?> GetLogsByRequestIdAsync(string requestId, long sinceId = 0, string stream = "all", CancellationToken cancellationToken = default)
            => _http.GetFromJsonAsync<List<TaskLogDto>>($"/api/v2/tasks/logs?requestId={Uri.EscapeDataString(requestId)}&sinceId={sinceId}&stream={stream}", _json, cancellationToken);

        private static async Task<T?> TryReadBodyAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        {
            if (resp.Content is null) return default;
            var len = resp.Content.Headers.ContentLength;
            if (len.HasValue && len.Value == 0) return default;

            try
            {
                return await resp.Content.ReadFromJsonAsync<T>(options: null, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }
}
