using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.API.Models;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;

namespace NetRatel.API.Endpoints;

/// <summary>
/// Development-only onboarding adapter for MCP. It exposes safe installer
/// metadata and target-owned, short-lived single-use enrollment-code handling.
/// A code value is returned only by the immediate confirmed create response;
/// it is never retained in ownership metadata, audit payloads, or later reads.
/// </summary>
public static class DevelopmentMcpOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapDevelopmentMcpOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/development/mcp/agents/{tenantId:int}/{agentId:guid}/onboarding")
            .WithTags("Development MCP Onboarding")
            .RequireAuthorization("Operator");
        group.MapGet("collateral/{runtime}", CollateralAsync);
        group.MapPost("enrollment-codes", CreateEnrollmentAsync);
        group.MapGet("enrollment-codes/{enrollmentCodeId:guid}", GetEnrollmentAsync);
        group.MapPost("enrollment-codes/{enrollmentCodeId:guid}/revoke", RevokeEnrollmentAsync);
        return app;
    }

    private static async Task<IResult> CollateralAsync(
        int tenantId,
        Guid agentId,
        string runtime,
        HttpContext http,
        IClientArtifactsService artifacts,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedRuntime(runtime)) return Results.BadRequest(new { code = "onboarding_runtime_invalid" });
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.OnboardingCollateralRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var artifact = await artifacts.GetLatestAsync(runtime, cancellationToken).ConfigureAwait(false);
        return artifact is null
            ? Results.NotFound(new { code = "onboarding_collateral_not_found" })
            : Results.Ok(ToCollateralDto(artifact));
    }

    private static async Task<IResult> CreateEnrollmentAsync(
        int tenantId,
        Guid agentId,
        CreateDevelopmentMcpEnrollmentCodeRequest? request,
        HttpContext http,
        IEnrollmentCodeIssueService enrollmentCodes,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeCreate(request, out var validForMinutes, out var marker)) return Results.BadRequest(new { code = "onboarding_enrollment_invalid" });
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.OnboardingMutation, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var issued = await enrollmentCodes.IssueAsync(new EnrollmentCodeIssueRequest(
            tenantId,
            validForMinutes,
            MaxUses: 1,
            CreatedBy: http.User.Identity?.Name,
            Notes: marker,
            DevelopmentMcpTargetAgentId: agentId,
            DevelopmentMcpMarker: marker), cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/v2/development/mcp/agents/{tenantId}/{agentId:D}/onboarding/enrollment-codes/{issued.EnrollmentCodeId:D}",
            new DevelopmentMcpCreatedEnrollmentCodeDto(
                issued.EnrollmentCodeId,
                tenantId,
                marker,
                issued.CreatedAtUtc,
                issued.ValidToUtc,
                MaxUses: 1,
                issued.Code));
    }

    private static async Task<IResult> GetEnrollmentAsync(
        int tenantId,
        Guid agentId,
        Guid enrollmentCodeId,
        HttpContext http,
        IEnrollmentCodeIssueService enrollmentCodes,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await enrollmentCodes.GetDevelopmentMcpOwnershipAsync(enrollmentCodeId, tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (ownership is null) return Results.NotFound(new { code = "onboarding_enrollment_not_found" });
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.OnboardingEnrollmentMetadataRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        return Results.Ok(ToMetadataDto(ownership));
    }

    private static async Task<IResult> RevokeEnrollmentAsync(
        int tenantId,
        Guid agentId,
        Guid enrollmentCodeId,
        HttpContext http,
        IEnrollmentCodeIssueService enrollmentCodes,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await enrollmentCodes.GetDevelopmentMcpOwnershipAsync(enrollmentCodeId, tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (ownership is null) return Results.NotFound(new { code = "onboarding_enrollment_not_found" });
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.OnboardingMutation, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        await enrollmentCodes.RevokeAsync(enrollmentCodeId, tenantId, http.User.Identity?.Name, "development_mcp_qa_cleanup", cancellationToken).ConfigureAwait(false);
        var revoked = await enrollmentCodes.GetDevelopmentMcpOwnershipAsync(enrollmentCodeId, tenantId, agentId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToMetadataDto(revoked!));
    }

    private static async Task<IResult?> RequireAcceptedAsync(
        HttpContext http,
        int tenantId,
        Guid agentId,
        DevelopmentOperatorOperation operation,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
        => await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, operation, correlation, targets, cancellationToken).ConfigureAwait(false);

    private static bool TryNormalizeCreate(CreateDevelopmentMcpEnrollmentCodeRequest? request, out int validForMinutes, out string marker)
    {
        validForMinutes = request?.ValidForMinutes ?? 0;
        marker = request?.Marker?.Trim() ?? string.Empty;
        return validForMinutes is >= 5 and <= 10 &&
               marker.StartsWith("MCP-QA-", StringComparison.Ordinal) &&
               marker.Length <= 128 &&
               marker.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsSupportedRuntime(string runtime) => runtime is "linux-x64" or "win-x64";

    private static DevelopmentMcpOnboardingCollateralDto ToCollateralDto(ClientArtifactSummaryDto artifact) => new(
        artifact.Rid,
        artifact.Version,
        artifact.FileName,
        artifact.Size,
        artifact.Sha256,
        artifact.UploadedAt);

    private static DevelopmentMcpEnrollmentMetadataDto ToMetadataDto(DevelopmentMcpEnrollmentOwnershipRecord ownership) => new(
        ownership.EnrollmentCodeId,
        ownership.TenantId,
        ownership.Marker,
        ownership.CreatedAtUtc,
        ownership.ValidToUtc,
        ownership.MaxUses,
        ownership.Uses,
        ownership.RevokedAtUtc,
        ownership.RevokedAtUtc is null && ownership.ValidToUtc > DateTimeOffset.UtcNow && ownership.Uses < ownership.MaxUses);
}

public sealed record CreateDevelopmentMcpEnrollmentCodeRequest(int ValidForMinutes, string Marker);
public sealed record DevelopmentMcpOnboardingCollateralDto(string Runtime, string Version, string FileName, long Size, string Sha256, DateTimeOffset UploadedAtUtc);
public sealed record DevelopmentMcpCreatedEnrollmentCodeDto(Guid EnrollmentCodeId, int TenantId, string Marker, DateTimeOffset CreatedAtUtc, DateTimeOffset ValidToUtc, int MaxUses, string EnrollmentCode);
public sealed record DevelopmentMcpEnrollmentMetadataDto(Guid EnrollmentCodeId, int TenantId, string Marker, DateTimeOffset CreatedAtUtc, DateTimeOffset ValidToUtc, int MaxUses, int Uses, DateTimeOffset? RevokedAtUtc, bool IsActive);
