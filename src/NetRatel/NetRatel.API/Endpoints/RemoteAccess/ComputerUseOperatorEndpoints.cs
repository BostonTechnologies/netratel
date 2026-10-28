using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services.ComputerUse;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.API.Endpoints;

/// <summary>Human observation and takeover controls within the existing Remote Support authorization boundary.</summary>
public static class ComputerUseOperatorEndpoints
{
    public static IEndpointRouteBuilder MapComputerUseOperatorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/computer-use")
            .WithTags("Remote Support Computer Use").RequireAuthorization("RemoteSupportOperator")
            .WithMetadata(new RequestSizeLimitAttribute(ComputerUseContract.MaximumRequestBytes));
        group.MapGet("/session", GetAsync);
        group.MapPost("/session/{sessionId:guid}/pause", (int tenantId, Guid agentId, Guid sessionId,
            ComputerUseControlRequest request, HttpContext http, IEffectiveAccessService access, IComputerUseSessionStore sessions,
            IComputerUseGateway gateway, TimeProvider time, CancellationToken ct) =>
            ControlAsync(tenantId, agentId, sessionId, "pause", request, http, access, sessions, gateway, time, ct));
        group.MapPost("/session/{sessionId:guid}/terminate", (int tenantId, Guid agentId, Guid sessionId,
            ComputerUseControlRequest request, HttpContext http, IEffectiveAccessService access, IComputerUseSessionStore sessions,
            IComputerUseGateway gateway, TimeProvider time, CancellationToken ct) =>
            ControlAsync(tenantId, agentId, sessionId, "close", request, http, access, sessions, gateway, time, ct));
        return app;
    }

    private static async Task<IResult> GetAsync(int tenantId, Guid agentId, HttpContext http, IEffectiveAccessService access,
        IComputerUseSessionStore sessions, IOptionsMonitor<ComputerUseOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.ComputerUseView, tenantId, ct)) return Results.Forbid();
        var lease = await sessions.GetActiveAsync(tenantId, agentId, time.GetUtcNow(), ct);
        var canControl = await access.AuthorizeAsync(http.User, NetRatelPermissions.ComputerUseControl, tenantId, ct);
        return Results.Ok(new { enabled = options.CurrentValue.Enabled, session = lease is null ? null : ComputerUseOperatorService.PublicSession(lease), canControl });
    }

    private static async Task<IResult> ControlAsync(int tenantId, Guid agentId, Guid sessionId, string operation,
        ComputerUseControlRequest request, HttpContext http, IEffectiveAccessService access, IComputerUseSessionStore sessions,
        IComputerUseGateway gateway, TimeProvider time, CancellationToken ct)
    {
        if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.ComputerUseControl, tenantId, ct)) return Results.Forbid();
        var subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject)) return Results.Forbid();
        if (request.IdempotencyKey is not { Length: >= 32 and <= 128 } ||
            !request.IdempotencyKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return Results.BadRequest(new { code = "idempotency_key_invalid" });
        var owner = new ComputerUseOwner(subject, "browser", "netratel-web", "browser", "operator");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { tenantId, agentId, sessionId, operation }, ComputerUseContract.JsonOptions)));
        var operationId = new Guid(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { owner, tenantId, agentId, sessionId, operation, request.IdempotencyKey }, ComputerUseContract.JsonOptions)).AsSpan(0, 16));
        var started = await sessions.BeginAsync(new(sessionId, operationId, tenantId, agentId, owner, operation, hash, null,
            http.TraceIdentifier, time.GetUtcNow(), HumanTakeover: true), ct);
        if (started.FailureCode is { } failure)
        {
            await sessions.RecordAuditAsync(new(Guid.NewGuid(), tenantId, agentId, sessionId, operationId, owner,
                operation, "refused", failure, http.TraceIdentifier, null, hash, time.GetUtcNow()), ct);
            return failure == "computer_use_session_not_found" ? Results.NotFound() : Results.Conflict(new { code = failure });
        }
        if (!started.Dispatch && started.PreviousResult is { } replay)
            return ComputerUseOperatorService.Result(replay, started.Session, http.TraceIdentifier, null, true);
        var lease = started.Session!;
        var command = ComputerUseOperatorService.Command(lease, operationId, operation);
        ComputerUseResult result;
        try { result = await gateway.ExecuteAsync(new(tenantId, agentId), lease.ConnectionId, lease.ConnectionEpoch, command, ct); }
        catch (OperationCanceledException)
        {
            result = new(ComputerUseContract.Version, operationId, sessionId, ComputerUseStatuses.Unknown,
                "execution_interrupted", Provider: lease.Provider);
        }
        await sessions.CompleteAsync(operationId, result, time.GetUtcNow(), CancellationToken.None);
        var updated = await sessions.GetActiveAsync(tenantId, agentId, time.GetUtcNow(), CancellationToken.None);
        return ComputerUseOperatorService.Result(result, updated, http.TraceIdentifier, null);
    }
}
