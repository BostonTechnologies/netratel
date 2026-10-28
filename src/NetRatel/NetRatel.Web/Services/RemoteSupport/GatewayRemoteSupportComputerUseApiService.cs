using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Web.Services.RemoteSupport;

public sealed partial class GatewayRemoteSupportApiService
{
    public async Task<ComputerUseOperatorStatus?> GetComputerUseStatusAsync(int tenantId, Guid agentId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(ComputerUseUri(tenantId, agentId, "session"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ComputerUseOperatorStatus>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComputerUseResult> ControlComputerUseAsync(int tenantId, Guid agentId, Guid sessionId,
        string operation, CancellationToken cancellationToken = default)
    {
        if (operation is not ("pause" or "terminate")) throw new ArgumentOutOfRangeException(nameof(operation));
        using var response = await _http.PostAsJsonAsync(
            ComputerUseUri(tenantId, agentId, $"session/{sessionId:D}/{operation}"),
            new { idempotencyKey = Guid.NewGuid().ToString("N") }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ComputerUseControlResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?.Result ?? throw new RemoteSupportApiException(response.StatusCode, "computer_use_response_empty");
    }

    private static string ComputerUseUri(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/agents/{tenantId}/{agentId:D}/computer-use/{suffix}";

    private sealed record ComputerUseControlResponse(ComputerUseResult Result);
}
