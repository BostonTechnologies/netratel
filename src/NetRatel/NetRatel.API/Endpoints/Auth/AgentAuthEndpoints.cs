using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Enrollment;
using System.IdentityModel.Tokens.Jwt;

namespace NetRatel.API.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAgentAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agents").WithTags("Agent Auth");

        group.MapPost("/enroll", async (
            AgentEnrollRequest request,
            HttpContext http,
            IEnrollmentService enrollment,
            CancellationToken ct) =>
        {
            try
            {
                var signature = http.Request.Headers["X-NetRatel-Signature"].FirstOrDefault();
                var nonce = http.Request.Headers["X-NetRatel-Nonce"].FirstOrDefault();
                var timestampRaw = http.Request.Headers["X-NetRatel-Timestamp"].FirstOrDefault();
                var timestamp = DateTimeOffset.TryParse(timestampRaw, out var parsedTimestamp)
                    ? parsedTimestamp
                    : (DateTimeOffset?)null;
                var effectiveRequest = request with
                {
                    ProofSignature = request.ProofSignature ?? signature,
                    ProofNonce = request.ProofNonce ?? nonce,
                    ProofTimestampUtc = request.ProofTimestampUtc ?? timestamp
                };
                var result = await enrollment.EnrollAsync(effectiveRequest, ct);
                return Results.Ok(result);
            }
            catch (AgentAuthException ex)
            {
                return ProblemFrom(ex);
            }
        })
        .AllowAnonymous()
        .Produces<AgentEnrollResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/token", async (
            AgentTokenRequest request,
            HttpContext http,
            IAgentTokenService tokens,
            OrchestratorDbContext db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            try
            {
                var proofToken = TryReadProofToken(http.Request);
                var signature = http.Request.Headers["X-NetRatel-Signature"].FirstOrDefault();
                var nonce = http.Request.Headers["X-NetRatel-Nonce"].FirstOrDefault();
                var timestampRaw = http.Request.Headers["X-NetRatel-Timestamp"].FirstOrDefault();
                DateTimeOffset? timestamp = null;
                if (DateTimeOffset.TryParse(timestampRaw, out var parsedTs))
                {
                    timestamp = parsedTs;
                }

                var cert = await http.Connection.GetClientCertificateAsync(ct);
                var effectiveRequest = request with
                {
                    ProofJwt = request.ProofJwt ?? proofToken,
                    ProofSignature = request.ProofSignature ?? signature,
                    ProofNonce = request.ProofNonce ?? nonce,
                    ProofTimestampUtc = request.ProofTimestampUtc ?? timestamp,
                    ClientCertificateThumbprint = request.ClientCertificateThumbprint ?? cert?.Thumbprint
                };
                var result = await tokens.ExchangeRefreshTokenAsync(effectiveRequest, ct);
                return Results.Ok(result);
            }
            catch (AgentAuthException ex)
            {
                return ProblemFrom(ex);
            }
            catch (Exception ex)
            {
                var correlationId = http.Items.TryGetValue(CorrelationConstants.HttpContextItemKey, out var corrObj)
                    ? corrObj?.ToString()
                    : http.Request.Headers[CorrelationConstants.HeaderName].FirstOrDefault();

                int? tenantId = null;
                try
                {
                    tenantId = await db.Agents
                        .Where(x => x.Id == request.AgentId)
                        .Select(x => (int?)x.TenantId)
                        .FirstOrDefaultAsync(ct);
                }
                catch (Exception diagnosticsException)
                {
                    loggerFactory.CreateLogger("AgentTokenEndpoint").LogWarning(
                        diagnosticsException,
                        "Unable to resolve tenant diagnostics for failed Agent token request. agentId={AgentId}",
                        request.AgentId);
                }

                var logger = loggerFactory.CreateLogger("AgentTokenEndpoint");
                if (ex is DbUpdateException dbEx && dbEx.InnerException is PostgresException pgEx)
                {
                    logger.LogError(
                        ex,
                        "Agent token issuance failed. corr={CorrelationId} agentId={AgentId} tenantId={TenantId} method={Method} path={Path} sqlState={SqlState} constraint={Constraint} column={Column} detail={Detail}",
                        correlationId,
                        request.AgentId,
                        tenantId,
                        http.Request.Method,
                        http.Request.Path,
                        pgEx.SqlState,
                        pgEx.ConstraintName,
                        pgEx.ColumnName,
                        pgEx.Detail);
                }
                else
                {
                    logger.LogError(
                        ex,
                        "Agent token issuance failed. corr={CorrelationId} agentId={AgentId} tenantId={TenantId} method={Method} path={Path} inner={Inner}",
                        correlationId,
                        request.AgentId,
                        tenantId,
                        http.Request.Method,
                        http.Request.Path,
                        ex.InnerException?.ToString());
                }

                var detail = http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()
                    ? $"{ex.Message}{(ex.InnerException is null ? string.Empty : $" | {ex.InnerException.Message}")}"
                    : "Agent token issuance failed.";
                var extensions = new Dictionary<string, object?>
                {
                    ["correlationId"] = correlationId,
                    ["agentId"] = request.AgentId,
                    ["tenantId"] = tenantId,
                    ["errorCode"] = "agent_token_issue_failed"
                };
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Agent token issuance failed",
                    detail: detail,
                    extensions: extensions);
            }
        })
        .AllowAnonymous()
        .Produces<AgentTokenResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/disable", async (
            Guid id,
            IAgentTokenService tokens,
            CancellationToken ct) =>
        {
            try
            {
                await tokens.DisableAgentAsync(id, ct);
                return Results.NoContent();
            }
            catch (AgentAuthException ex)
            {
                return ProblemFrom(ex);
            }
        })
        .RequireAuthorization("Operator")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        var adminGroup = app.MapGroup("/api/v1/tenants/{tenantId:int}/agents")
            .WithTags("Agent Admin")
            .RequireAuthorization("Operator");

        adminGroup.MapGet(string.Empty, async (
            int tenantId,
            [FromQuery] string? search,
            [FromQuery] bool? enabled,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            IAgentManagementService agents,
            CancellationToken ct) =>
        {
            var result = await agents.ListAsync(
                tenantId,
                new AgentListQuery(search, enabled, page ?? 1, pageSize ?? 50),
                ct);
            return Results.Ok(result);
        })
        .Produces<AgentListResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        adminGroup.MapGet("{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            IAgentManagementService agents,
            CancellationToken ct) =>
        {
            var result = await agents.GetAsync(tenantId, agentId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .Produces<AgentDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        adminGroup.MapPost("{agentId:guid}/disable", async (
            int tenantId,
            Guid agentId,
            DisableAgentRequest request,
            HttpContext http,
            IAgentManagementService agents,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { message = "Reason is required." });
            }

            var actor = http.User?.Identity?.Name ?? "unknown";
            await agents.DisableAsync(tenantId, agentId, request.Reason.Trim(), actor, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        adminGroup.MapPost("{agentId:guid}/enable", async (
            int tenantId,
            Guid agentId,
            HttpContext http,
            IAgentManagementService agents,
            CancellationToken ct) =>
        {
            var actor = http.User?.Identity?.Name ?? "unknown";
            await agents.EnableAsync(tenantId, agentId, actor, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        var enrollmentGroup = app.MapGroup("/api/v1/tenants/{tenantId:int}/enrollment-codes")
            .WithTags("Enrollment Codes")
            .RequireAuthorization("Operator");

        enrollmentGroup.MapPost(string.Empty, async (
            int tenantId,
            IssueEnrollmentCodeRequest request,
            HttpContext http,
            [FromServices]
            IEnrollmentCodeIssueService enrollmentCodes,
            CancellationToken ct) =>
        {
            try
            {
                var issued = await enrollmentCodes.IssueAsync(
                    new EnrollmentCodeIssueRequest(
                        tenantId,
                        request.ValidForMinutes,
                        request.MaxUses,
                        CreatedBy: http.User?.Identity?.Name,
                        Notes: request.Note),
                    ct);

                return Results.Ok(new IssuedEnrollmentCodeDto(
                    issued.EnrollmentCodeId,
                    issued.Code,
                    issued.CreatedAtUtc,
                    issued.ValidToUtc,
                    request.MaxUses,
                    request.Note));
            }
            catch (AgentAuthException ex)
            {
                return ProblemFrom(ex);
            }
        })
        .Produces<IssuedEnrollmentCodeDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        enrollmentGroup.MapGet(string.Empty, async (
            int tenantId,
            [FromQuery] string? status,
            [FromQuery] string? search,
            [FromServices]
            IEnrollmentCodeIssueService enrollmentCodes,
            CancellationToken ct) =>
        {
            try
            {
                var items = await enrollmentCodes.ListAsync(tenantId, status, search, ct);
                var response = new EnrollmentCodeListResponse(items.Select(MapEnrollmentCodeDto).ToArray());
                return Results.Ok(response);
            }
            catch (AgentAuthException ex)
            {
                return ProblemFrom(ex);
            }
        })
        .Produces<EnrollmentCodeListResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        enrollmentGroup.MapPost("{id:guid}/revoke", async (
            int tenantId,
            Guid id,
            RevokeEnrollmentCodeRequest request,
            HttpContext http,
            [FromServices]
            IEnrollmentCodeIssueService enrollmentCodes,
            CancellationToken ct) =>
        {
            try
            {
                await enrollmentCodes.RevokeAsync(id, tenantId, http.User?.Identity?.Name, request.Reason, ct);
                return Results.Ok();
            }
            catch (AgentAuthException ex)
            {
                return ProblemFrom(ex);
            }
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/v1/agent-auth/ping", (HttpContext http) =>
        {
            var user = http.User;
            var subject = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? user.FindFirst("sub")?.Value;
            var agentId = user.FindFirst("agent_id")?.Value;
            var tenantId = user.FindFirst("tenant_id")?.Value;
            var scope = user.FindFirst("scope")?.Value;
            var issuedAtRaw = user.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;
            var expiresAtRaw = user.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;

            DateTimeOffset? issuedAt = long.TryParse(issuedAtRaw, out var iatUnix)
                ? DateTimeOffset.FromUnixTimeSeconds(iatUnix)
                : null;
            DateTimeOffset? expiresAt = long.TryParse(expiresAtRaw, out var expUnix)
                ? DateTimeOffset.FromUnixTimeSeconds(expUnix)
                : null;

            return Results.Ok(new
            {
                agentId,
                subject,
                tenantId,
                scopes = scope,
                issuedAtUtc = issuedAt,
                expiresAtUtc = expiresAt
            });
        })
        .WithTags("Agent Auth")
        .RequireAuthorization("AgentAccess")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapGet("/.well-known/openid-configuration", async (
            IConfiguration cfg,
            IAgentTokenService tokens,
            CancellationToken ct) =>
        {
            var configuredAuthority = cfg["M2M:Authority"]?.Trim();
            if (string.IsNullOrWhiteSpace(configuredAuthority))
            {
                var fallback = await tokens.GetOpenIdConfigurationAsync(ct);
                return Results.Ok(fallback);
            }

            var authority = configuredAuthority.TrimEnd('/');
            var result = new OpenIdConfigurationDto(
                authority,
                $"{authority}/connect/token",
                $"{authority}/.well-known/jwks.json",
                ["ES256"]);
            return Results.Ok(result);
        })
        .AllowAnonymous()
        .WithTags("OIDC")
        .Produces<OpenIdConfigurationDto>(StatusCodes.Status200OK);

        app.MapGet("/.well-known/jwks.json", async (
            IAgentTokenService tokens,
            CancellationToken ct) =>
        {
            var result = await tokens.GetJwksAsync(ct);
            return Results.Ok(result);
        })
        .AllowAnonymous()
        .WithTags("OIDC")
        .Produces<JsonWebKeySetDto>(StatusCodes.Status200OK);

        return app;
    }

    private static IResult ProblemFrom(AgentAuthException ex)
        => Results.Problem(
            statusCode: ex.StatusCode,
            detail: ex.Message,
            title: "Agent auth error",
            type: ex.Code is null ? null : $"https://netratel/errors/{ex.Code}",
            extensions: ex.Code is null ? null : new Dictionary<string, object?> { ["code"] = ex.Code });

    private static string? TryReadProofToken(HttpRequest request)
    {
        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return null;
        }

        const string prefix = "PoP ";
        return authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authHeader[prefix.Length..].Trim()
            : null;
    }

    private static EnrollmentCodeDto MapEnrollmentCodeDto(EnrollmentCodeListItem item)
    {
        var status = item.RevokedAtUtc.HasValue
            ? "Revoked"
            : item.IsActive
                ? "Active"
                : "Expired";

        var showCode = item.IsActive;
        var displayCode = showCode
            ? item.Code
            : MaskCode(item.Code);

        return new EnrollmentCodeDto(
            item.EnrollmentCodeId,
            showCode ? item.Code : null,
            displayCode,
            status,
            item.CreatedAtUtc,
            item.ValidFromUtc,
            item.ValidToUtc,
            item.Uses,
            item.MaxUses,
            item.RevokedAtUtc.HasValue,
            item.RevokedAtUtc,
            item.Notes,
            item.LastUsedUtc,
            item.IsActive);
    }

    private static string MaskCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length < 4)
        {
            return "****";
        }

        return $"******{code[^4..]}";
    }

    public sealed record DisableAgentRequest(string Reason);
}
