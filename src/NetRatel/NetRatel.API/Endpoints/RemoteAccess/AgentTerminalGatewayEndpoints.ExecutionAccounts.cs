using Microsoft.AspNetCore.Mvc;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Secrets;
using NetRatel.Application.Presence;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Endpoints;

public static partial class AgentTerminalGatewayEndpoints
{
    private static async Task<GatewayTerminalSession> OpenForCallerAsync(IAgentTerminalSessionRegistry terminals, HttpContext http,
        ClientKey key, OpenTerminalRequest request, string shell, int columns, int rows, CancellationToken ct)
    {
        var accounts = http.RequestServices.GetService<ExecutionAccountService>();
        var client = accounts is null ? null : await accounts.GetClientAsync(key.TenantId, key.AgentId, ct);
        var requested = request.RunAs;
        if (requested is { IsValid: false }) throw new ArgumentException("Choose a valid terminal account.");
        if (requested is null && client?.DefaultTerminalCredentialProfileId is null)
            return await terminals.OpenAsync(key, shell, request.WorkingDirectory, columns, rows, ct);
        if (terminals is not AgentTerminalSessionRegistry registry || accounts is null || client is null)
            throw new TerminalGatewayActionException("terminal_run_as_unsupported", "Saved execution accounts are unavailable on this terminal gateway.");

        var owner = ExecutionAccountService.PrincipalKey(http.User)
            ?? throw new ExecutionAccountException("run_as_denied", "An authenticated operator is required for this terminal account.", 403);
        await accounts.RequireAsync(http.User, key.TenantId, ct);
        if (requested is null)
        {
            var profile = await accounts.GetEligibleAsync(client, client.DefaultTerminalCredentialProfileId!.Value, true, ct);
            requested = new(profile.Mode, profile.Id);
        }

        var credential = await ResolveTerminalCredentialAsync(accounts, client, requested, ct);
        try
        {
            await accounts.RequireAsync(http.User, key.TenantId, ct);
            return await registry.OpenWithAccountAsync(key, shell, request.WorkingDirectory, columns, rows, requested, credential, owner, ct);
        }
        finally { ExecutionAccountServiceCapability.Clear(credential); }
    }

    private static async Task<IResult> SwitchAccountAsync(string sessionId, SwitchTerminalAccountRequest request, HttpContext http,
        [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken ct)
    {
        if (request.SwitchId == Guid.Empty || request.RunAs is not { IsValid: true })
            return Results.BadRequest(new TerminalActionResponse(NewTrackingId(), "Choose a valid terminal account.", sessionId));
        if (terminals.Get(sessionId) is not { } original) return Results.NotFound();
        var attachments = BrowserAttachments(http.RequestServices);
        if (original.Generation != request.Generation || attachments?.HasCurrentLease(original, request.AttachmentLeaseId) != true)
            return GatewayFailure(new TerminalGatewayActionException("terminal_browser_attachment_stale", "This terminal attachment is no longer current."), sessionId);
        if (terminals is not AgentTerminalSessionRegistry registry)
            return GatewayFailure(new TerminalGatewayActionException("terminal_run_as_unsupported", "Terminal account switching is unavailable."), sessionId);

        var accounts = http.RequestServices.GetRequiredService<ExecutionAccountService>();
        if (ExecutionAccountService.PrincipalKey(http.User) is not { } owner) return Results.Forbid();
        try
        {
            await accounts.RequireAsync(http.User, original.TenantId, ct);
            var replacement = await registry.BeginAccountSwitchAsync(sessionId, request.Generation, request.SwitchId,
                request.RunAs, owner, async (key, token) =>
                {
                    var client = await accounts.GetClientAsync(key.TenantId, key.AgentId, token);
                    var credential = await ResolveTerminalCredentialAsync(accounts, client, request.RunAs, token);
                    try
                    {
                        await accounts.RequireAsync(http.User, key.TenantId, token);
                        return credential;
                    }
                    catch
                    {
                        ExecutionAccountServiceCapability.Clear(credential);
                        throw;
                    }
                }, ct);
            var lease = attachments!.GetAttachmentLeaseId(replacement) ?? attachments.Track(replacement);
            return Results.Accepted($"/api/v2/gateway-terminal/{replacement.SessionId}", ToDto(replacement, lease));
        }
        catch (ExecutionAccountException error) { return AccountFailure(error); }
        catch (SecretProviderException error) { return Results.Conflict(new TerminalActionResponse(NewTrackingId(), error.Message, sessionId, "terminal_run_as_secret_unavailable")); }
        catch (TerminalGatewayActionException error) { return GatewayFailure(error, sessionId); }
    }

    private static async Task<RunAsCredential?> ResolveTerminalCredentialAsync(ExecutionAccountService accounts, Agent client,
        ExecutionIdentitySetting requested, CancellationToken ct)
    {
        if (requested.CredentialProfileId is not { } id) return null;
        var profile = await accounts.GetEligibleAsync(client, id, true, ct);
        if (profile.Mode != requested.Mode) throw new ExecutionAccountException("run_as_mode_mismatch", "Choose the execution mode of the saved credential.");
        var binding = await accounts.BindAsync(client, new(profile.Id, profile.Mode, profile.MappingRevision), true, ct);
        return await accounts.ResolveAsync(client, binding, true, ct);
    }

    private static IResult AccountFailure(ExecutionAccountException error) => Results.Json(
        new TerminalActionResponse(NewTrackingId(), error.Message, Code: error.Code), statusCode: error.StatusCode);
}
