using System.Text.Json;
using NetRatel.API.Application.Requests.Services;

namespace NetRatel.API.Application.Requests.Services;

public sealed class ExecutionWorker(IExecutionQueue queue, IHttpClientFactory http, ILogger<ExecutionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.DequeueAsync(stoppingToken))
        {
            try
            {
                var externalService = http.CreateClient("ExternalServiceApi");
                externalService.DefaultRequestHeaders.Remove("X-Tenant");
                externalService.DefaultRequestHeaders.Remove("X-Correlation-Id");
                externalService.DefaultRequestHeaders.Add("X-Tenant", job.Tenant);
                externalService.DefaultRequestHeaders.Add("X-Correlation-Id", job.CorrelationId);
                // Send Dispatched
                await externalService.PostAsJsonAsync($"/internal/requests/{job.RequestId}/events",
                    new { job.RequestId, Status = "Dispatched", Message = "Job accepted", CreatedAt = DateTimeOffset.UtcNow.ToString("O") }, stoppingToken);

                // Simulate work + progress
                for (var p = 10; p <= 90; p += 20)
                {
                    await Task.Delay(500, stoppingToken);
                    await externalService.PostAsJsonAsync($"/internal/requests/{job.RequestId}/events",
                        new { job.RequestId, Status = "InProgress", ProgressPercent = p, Message = $"Progress {p}%", CreatedAt = DateTimeOffset.UtcNow.ToString("O") }, stoppingToken);
                }

                var payload = JsonSerializer.Serialize(new { output = "ok" });
                await externalService.PostAsJsonAsync($"/internal/requests/{job.RequestId}/events",
                    new { job.RequestId, Status = "Succeeded", PayloadJson = payload, Message = "Completed", CreatedAt = DateTimeOffset.UtcNow.ToString("O") }, stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Execution failed for {RequestId}", job.RequestId);
                var externalService = http.CreateClient("ExternalServiceApi");
                externalService.DefaultRequestHeaders.Remove("X-Tenant");
                externalService.DefaultRequestHeaders.Remove("X-Correlation-Id");
                externalService.DefaultRequestHeaders.Add("X-Tenant", job.Tenant);
                externalService.DefaultRequestHeaders.Add("X-Correlation-Id", job.CorrelationId);
                await externalService.PostAsJsonAsync($"/internal/requests/{job.RequestId}/events",
                    new { job.RequestId, Status = "Failed", Message = ex.Message, CreatedAt = DateTimeOffset.UtcNow.ToString("O") }, stoppingToken);
            }
        }
    }
}
