using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Abstractions;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Services;

public class M2MConnectivityService(
    IHttpClientFactory http,
    OrchestratorDbContext db) : IM2MConnectivityService
{
    private const string DefaultRemoteAudience = "external-service.api";
    private const string DefaultRemoteSystemName = "ExternalService";

    public async Task<M2MConnectivitySettingsDto> GetAsync(CancellationToken ct)
    {
        var row = await db.Set<M2MConnectivitySettings>().AsNoTracking().FirstOrDefaultAsync(ct)
                  ?? new M2MConnectivitySettings();
        return ToDto(row);
    }

    public async Task SaveAsync(M2MConnectivitySettingsDto dto, CancellationToken ct)
    {
        var row = await db.Set<M2MConnectivitySettings>().FirstOrDefaultAsync(ct)
                  ?? new M2MConnectivitySettings();
        row.Enabled = dto.Enabled;
        row.RemoteBaseUrl = dto.RemoteBaseUrl?.Trim().TrimEnd('/');
        row.RemoteAudience = dto.RemoteAudience?.Trim();
        row.RemoteSystemName = dto.RemoteSystemName?.Trim();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;

        if (db.Entry(row).State == EntityState.Detached) db.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<M2MConnectivityTestResultDto> TestAsync(M2MConnectivityTestRequestDto req, CancellationToken ct)
    {
        var settings = req.SettingsOverride ?? await GetAsync(ct);
        var results = new List<M2MConnectivityProbeResultDto>();

        if (req.IncludeTokenAcquire)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var ok = true;
                string msg = "Token handler configured";
                sw.Stop();
                results.Add(new("AcquireToken", "-", ok ? TrafficLight.Green : TrafficLight.Amber,
                    null, sw.ElapsedMilliseconds, msg, null, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                results.Add(new("AcquireToken", "-", TrafficLight.Red, null, null, ex.Message, null, DateTimeOffset.UtcNow));
            }
        }

        if (req.IncludeRemoteHealth)
        {
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RemoteBaseUrl))
            {
                results.Add(new("RemoteHealth", "(disabled)", TrafficLight.Amber, null, null,
                    "Connectivity is disabled or the ExternalService API base URL is missing.", null, DateTimeOffset.UtcNow));
            }
            else
            {
                try
                {
                    var client = http.CreateClient();
                    var paths = new[] { "/health/ready", "/health/live" };
                    M2MConnectivityProbeResultDto? probe = null;

                    foreach (var path in paths)
                    {
                        var url = $"{settings.RemoteBaseUrl!.TrimEnd('/')}{path}";
                        var sw = Stopwatch.StartNew();
                        var resp = await client.GetAsync(url, ct);
                        sw.Stop();

                        string? remoteName = settings.RemoteSystemName;
                        var message = $"HTTP {((int)resp.StatusCode)}";

                        if (resp.IsSuccessStatusCode)
                        {
                            var content = await resp.Content.ReadAsStringAsync(ct);
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                try
                                {
                                    var json = JsonSerializer.Deserialize<JsonElement>(content);
                                    if (json.ValueKind == JsonValueKind.Object
                                        && json.TryGetProperty("service", out var svc)
                                        && svc.ValueKind == JsonValueKind.String)
                                    {
                                        remoteName = svc.GetString() ?? remoteName;
                                    }
                                }
                                catch
                                {
                                }
                            }

                            probe = new("RemoteHealth", url, TrafficLight.Green, (int)resp.StatusCode, sw.ElapsedMilliseconds, message, remoteName, DateTimeOffset.UtcNow);
                            break;
                        }

                        if ((int)resp.StatusCode != 404)
                        {
                            probe = new(
                                "RemoteHealth",
                                url,
                                (int)resp.StatusCode is 401 or 403 ? TrafficLight.Amber : TrafficLight.Red,
                                (int)resp.StatusCode,
                                sw.ElapsedMilliseconds,
                                message,
                                remoteName,
                                DateTimeOffset.UtcNow);
                            break;
                        }
                    }

                    results.Add(probe ?? new(
                        "RemoteHealth",
                        settings.RemoteBaseUrl!,
                        TrafficLight.Amber,
                        404,
                        null,
                        "ExternalService health endpoint was not found. Use the ExternalService API URL.",
                        settings.RemoteSystemName,
                        DateTimeOffset.UtcNow));
                }
                catch (Exception ex)
                {
                    results.Add(new("RemoteHealth", settings.RemoteBaseUrl!, TrafficLight.Red, null, null, ex.Message, null, DateTimeOffset.UtcNow));
                }
            }
        }

        return new(results);
    }

    private static M2MConnectivitySettingsDto ToDto(M2MConnectivitySettings row)
    {
        return new(
            row.Enabled,
            Normalize(row.RemoteBaseUrl),
            Normalize(row.RemoteAudience) ?? DefaultRemoteAudience,
            Normalize(row.RemoteSystemName) ?? DefaultRemoteSystemName);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
