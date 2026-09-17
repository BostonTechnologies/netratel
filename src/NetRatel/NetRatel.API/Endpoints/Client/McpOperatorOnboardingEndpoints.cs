using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Middleware;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production tenant-scoped enrollment lifecycle. It is deliberately distinct
/// from the Development target-owned onboarding adapter because a prospective
/// agent cannot supply an existing agent identifier before it has enrolled.
/// </summary>
public static class McpOperatorOnboardingEndpoints
{
    private const string Tool = "netratel_onboarding";
    private const int MaximumPageSize = 100;
    private const int MaximumCollateralDownloadBytes = 64 * 1024;
    private const string ProductionMarkerPrefix = "mcp-operator-onboarding:prod:";
    private const string LinuxMarker = ProductionMarkerPrefix + "linux-x64";
    private const string WindowsMarker = ProductionMarkerPrefix + "win-x64";

    public static IEndpointRouteBuilder MapMcpOperatorOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/tenants/{tenantId:int}/onboarding")
            .WithTags("MCP Operator Onboarding")
            .RequireAuthorization("M2MOnly");
        group.MapGet("/collateral/{runtime}", CollateralAsync);
        group.MapGet("/collateral/{runtime}/download", DownloadCollateralAsync);
        group.MapGet("/enrollments", ListAsync);
        group.MapGet("/enrollments/{enrollmentCodeId:guid}", GetAsync);
        group.MapPost("/preview/create-enrollment", PreviewCreateAsync);
        group.MapPost("/confirm/create-enrollment", ConfirmCreateAsync);
        group.MapPost("/preview/revoke-enrollment", PreviewRevokeAsync);
        group.MapPost("/confirm/revoke-enrollment", ConfirmRevokeAsync);
        return app;
    }

    private static async Task<IResult> CollateralAsync(int tenantId, string runtime, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IClientArtifactsService artifacts, CancellationToken cancellationToken)
    {
        if (!IsRuntime(runtime)) return Results.BadRequest(new { code = "onboarding_runtime_invalid" });
        var admitted = await TryContextAsync("collateral", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var artifact = await artifacts.GetLatestAsync(runtime, cancellationToken).ConfigureAwait(false);
            return artifact is null ? Failure("onboarding_collateral_not_found", context) : Results.Ok(ToCollateral(artifact));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> ListAsync(int tenantId, string? status, long? cursor, int? limit, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        if (!IsStatus(status) || cursor is < 0 || limit is < 1 or > MaximumPageSize) return Results.BadRequest(new { code = "onboarding_query_invalid" });
        var admitted = await TryContextAsync("list_enrollments", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var take = limit ?? 25;
            var now = DateTimeOffset.UtcNow;
            var codes = db.EnrollmentCodes.AsNoTracking()
                .Where(code => code.TenantId == tenantId && (code.Notes == LinuxMarker || code.Notes == WindowsMarker));
            codes = status switch
            {
                "active" => codes.Where(code => code.RevokedAtUtc == null && code.ValidFromUtc <= now && code.ValidToUtc > now && (!code.MaxUses.HasValue || code.Uses < code.MaxUses.Value)),
                "expired" => codes.Where(code => code.RevokedAtUtc == null && (code.ValidFromUtc > now || code.ValidToUtc <= now || (code.MaxUses.HasValue && code.Uses >= code.MaxUses.Value))),
                "revoked" => codes.Where(code => code.RevokedAtUtc != null),
                _ => codes
            };
            var rows = await codes.Where(code => !cursor.HasValue || code.CreatedAtUtc.Ticks < cursor.Value)
                .OrderByDescending(code => code.CreatedAtUtc).ThenBy(code => code.Id).Take(take + 1)
                .Select(code => ToMetadata(code, now)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var page = rows.Take(take).ToArray();
            var nextCursor = rows.Count > take ? rows[take - 1].CreatedAtUtc.Ticks : (long?)null;
            return Results.Ok(new McpOperatorEnrollmentPage(page, nextCursor, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(int tenantId, Guid enrollmentCodeId, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        if (enrollmentCodeId == Guid.Empty) return Results.BadRequest(new { code = "onboarding_enrollment_invalid" });
        var admitted = await TryContextAsync("get_enrollment", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var metadata = await GetMetadataAsync(db, tenantId, enrollmentCodeId, cancellationToken).ConfigureAwait(false);
            return metadata is null ? Failure("onboarding_enrollment_not_found", context) : Results.Ok(metadata with { CorrelationId = context.CorrelationId });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewCreateAsync(int tenantId, McpOperatorEnrollmentCreateRequest request, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("create_enrollment", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!IsCreateValid(request, context.Decision.EffectiveConstraints)) return Failure("onboarding_enrollment_invalid", context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, CreateHash(tenantId, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorEnrollmentPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, "create_enrollment", tenantId, request.Runtime!, request.ValidForMinutes!.Value, request.MaxUses!.Value, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmCreateAsync(int tenantId, McpOperatorEnrollmentCreateRequest request, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations, IEnrollmentCodeIssueService enrollmentCodes, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("create_enrollment", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey) || !IsCreateValid(request, context.Decision.EffectiveConstraints)) return Failure("confirmation_plan_invalid", context);
        var admission = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, CreateHash(tenantId, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (admission.IsReplay) return await ReplayCreateAsync(admission, tenantId, db, context, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var issued = await enrollmentCodes.IssueAsync(new EnrollmentCodeIssueRequest(tenantId, request.ValidForMinutes!.Value, request.MaxUses!.Value,
                context.Decision.Request.Principal.Subject, ProductionMarker(request.Runtime!)), cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, issued.EnrollmentCodeId.ToString("D"), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorEnrollmentCreateResult(issued.EnrollmentCodeId, tenantId, request.Runtime!, issued.CreatedAtUtc, issued.ValidToUtc, request.MaxUses.Value, issued.Code, context.CorrelationId));
        }
        catch (AgentAuthException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "onboarding_enrollment_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure(exception.StatusCode == 409 ? "onboarding_enrollment_conflict" : "onboarding_enrollment_invalid", context);
        }
    }

    private static async Task<IResult> DownloadCollateralAsync(int tenantId, string runtime, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IClientArtifactsService artifacts, CancellationToken cancellationToken)
    {
        if (!IsRuntime(runtime)) return Results.BadRequest(new { code = "onboarding_runtime_invalid" });
        var admitted = await TryContextAsync("collateral_download", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var collateral = await artifacts.GetLatestAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (collateral is null) return Failure("onboarding_collateral_not_found", context);
            var policyMaximum = context.Decision.EffectiveConstraints?.MaxArtifactBytes ?? MaximumCollateralDownloadBytes;
            if (policyMaximum < 0 || collateral.Size > policyMaximum) return Failure("onboarding_collateral_download_too_large", context);
            if (collateral.Size > MaximumCollateralDownloadBytes)
            {
                // Installer bytes belong in an authenticated binary transfer, not an LLM context.
                // The existing enrollment credential is supplied only in a header, never a URL.
                var path = $"/api/v2/client-artifacts/{Uri.EscapeDataString(runtime)}/{Uri.EscapeDataString(collateral.Version)}/onboarding-download";
                return Results.Ok(new McpOperatorOnboardingCollateralDownload(ToCollateral(collateral), string.Empty, context.CorrelationId)
                {
                    TransferMode = "enrollment_download",
                    Download = new(path, "GET", tenantId, "X-NetRatel-Tenant-Id", "X-NetRatel-Enrollment-Code")
                });
            }
            var maximumBytes = Math.Min(MaximumCollateralDownloadBytes, policyMaximum);
            var download = await artifacts.DownloadRawAsync(runtime, collateral.Version, cancellationToken).ConfigureAwait(false);
            await using var content = download.Content;
            var bytes = await ReadBoundedAsync(content, maximumBytes, cancellationToken).ConfigureAwait(false);
            return bytes is null
                ? Failure("onboarding_collateral_download_too_large", context)
                : Results.Ok(new McpOperatorOnboardingCollateralDownload(ToCollateral(collateral), Convert.ToBase64String(bytes), context.CorrelationId));
        }
        catch (FileNotFoundException) { return Failure("onboarding_collateral_not_found", context); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewRevokeAsync(int tenantId, McpOperatorEnrollmentRevokeRequest request, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("revoke_enrollment", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        var metadata = await ResolveRevokeAsync(db, tenantId, request.EnrollmentCodeId, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return Failure("onboarding_enrollment_not_found", context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, RevokeHash(tenantId, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorEnrollmentRevokePreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, metadata, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmRevokeAsync(int tenantId, McpOperatorEnrollmentRevokeRequest request, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations, IEnrollmentCodeIssueService enrollmentCodes, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("revoke_enrollment", tenantId, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey) || request.EnrollmentCodeId is not { } enrollmentCodeId || enrollmentCodeId == Guid.Empty) return Failure("confirmation_plan_invalid", context);
        if (await ResolveRevokeAsync(db, tenantId, enrollmentCodeId, cancellationToken).ConfigureAwait(false) is null) return Failure("onboarding_enrollment_not_found", context);
        var admission = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, RevokeHash(tenantId, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (admission.IsReplay) return await ReplayRevokeAsync(admission, tenantId, db, context, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            await enrollmentCodes.RevokeAsync(enrollmentCodeId, tenantId, context.Decision.Request.Principal.Subject, "mcp_operator", cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, enrollmentCodeId.ToString("D"), CancellationToken.None).ConfigureAwait(false);
            var metadata = await GetMetadataAsync(db, tenantId, enrollmentCodeId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorEnrollmentRevokeResult(metadata!, false, context.CorrelationId));
        }
        catch (AgentAuthException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "onboarding_enrollment_not_found", CancellationToken.None).ConfigureAwait(false);
            return Failure(exception.StatusCode == 404 ? "onboarding_enrollment_not_found" : "onboarding_enrollment_invalid", context);
        }
    }

    private static async Task<IResult> ReplayCreateAsync(McpOperatorConfirmationAdmission admission, int tenantId, OrchestratorDbContext db, McpOperatorOnboardingContext context, CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !Guid.TryParse(admission.ResultReference, out var id)) return Failure("idempotency_replay_unavailable", context);
        var metadata = await GetMetadataAsync(db, tenantId, id, cancellationToken).ConfigureAwait(false);
        return metadata is null ? Failure("idempotency_replay_unavailable", context) : Results.Ok(new McpOperatorEnrollmentCreateResult(metadata.EnrollmentCodeId, tenantId, metadata.Runtime, metadata.CreatedAtUtc, metadata.ValidToUtc, metadata.MaxUses, null, context.CorrelationId, true));
    }

    private static async Task<IResult> ReplayRevokeAsync(McpOperatorConfirmationAdmission admission, int tenantId, OrchestratorDbContext db, McpOperatorOnboardingContext context, CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !Guid.TryParse(admission.ResultReference, out var id)) return Failure("idempotency_replay_unavailable", context);
        var metadata = await GetMetadataAsync(db, tenantId, id, cancellationToken).ConfigureAwait(false);
        return metadata is null ? Failure("idempotency_replay_unavailable", context) : Results.Ok(new McpOperatorEnrollmentRevokeResult(metadata, true, context.CorrelationId));
    }

    private static async Task<McpOperatorOnboardingContextResult> TryContextAsync(string operation, int tenantId, HttpContext http, IHostEnvironment environment, IMcpOperatorAuthorization authorization, CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(operation, tenantId, http.TraceIdentifier);
        if (!http.TryGetMcpOperatorDelegation(out var effective) || effective is null) return new(null, Failure("delegated_identity_required", fallback));
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        var descriptor = McpOperatorOperationCatalog.Find(Tool, operation);
        if (tenantId <= 0 || !McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || descriptor is null || !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) || !string.Equals(effective.Operation, operation, StringComparison.Ordinal) ||
            effective.TenantId != tenantId || effective.AgentId is not null || string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
            return new(null, Failure("delegated_identity_invalid", fallback));
        var principal = new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
            effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal), effective.ServicePrincipal);
        var request = new McpOperatorAccessRequest(operatorEnvironment, principal, tenantId, null, null, descriptor.OperationFamily,
            $"{Tool}/{operation}", new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal), descriptor.ConfirmationClass,
            effective.CorrelationId!, effective.RequestId, TenantDigest(tenantId), null, effective.Resource, effective.Instance, Tool, true, true, true, true, true);
        var decision = await authorization.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorOnboardingContext(decision, effective.ServicePrincipal, McpOperationAccessScopeNames.Canonical(access.RequiredScope), operation, tenantId, effective.CorrelationId!);
        return decision.IsAllowed ? new(context, null) : new(context, Failure(decision.FailureCode ?? "target_policy_missing", context));
    }

    private static async Task<McpOperatorEnrollmentMetadata?> ResolveRevokeAsync(OrchestratorDbContext db, int tenantId, Guid? enrollmentCodeId, CancellationToken cancellationToken) =>
        enrollmentCodeId is not { } id || id == Guid.Empty ? null : await GetMetadataAsync(db, tenantId, id, cancellationToken).ConfigureAwait(false);

    private static async Task<McpOperatorEnrollmentMetadata?> GetMetadataAsync(OrchestratorDbContext db, int tenantId, Guid enrollmentCodeId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.EnrollmentCodes.AsNoTracking().Where(code => code.Id == enrollmentCodeId && code.TenantId == tenantId && (code.Notes == LinuxMarker || code.Notes == WindowsMarker))
            .Select(code => ToMetadata(code, now)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static McpOperatorEnrollmentMetadata ToMetadata(EnrollmentCode code, DateTimeOffset now) => new(code.Id, code.TenantId, RuntimeFromMarker(code.Notes!), code.CreatedAtUtc, code.ValidFromUtc, code.ValidToUtc,
        code.MaxUses ?? 1, code.Uses, code.RevokedAtUtc, code.RevokedBy, code.RevokedAtUtc is null && code.ValidFromUtc <= now && code.ValidToUtc > now && (!code.MaxUses.HasValue || code.Uses < code.MaxUses.Value));
    private static McpOperatorOnboardingCollateral ToCollateral(ClientArtifactSummaryDto artifact) => new(artifact.Rid, artifact.Version, artifact.FileName, artifact.Size, artifact.Sha256, artifact.UploadedAt);
    private static bool IsCreateValid(McpOperatorEnrollmentCreateRequest request, McpOperatorConstraints? constraints) => IsRuntime(request.Runtime) && request.ValidForMinutes is >= 5 and <= 10 && request.MaxUses is >= 1 and <= 10 &&
        (constraints?.MaxOnboardingCodeLifetimeSeconds is not { } maximumLifetime || request.ValidForMinutes.Value * 60 <= maximumLifetime) &&
        (constraints?.MaxOnboardingCodeUses is not { } maximumUses || request.MaxUses.Value <= maximumUses);
    private static bool IsRuntime(string? runtime) => runtime is "linux-x64" or "win-x64";
    private static bool IsStatus(string? status) => status is null or "active" or "expired" or "revoked" or "all";
    private static string ProductionMarker(string runtime) => ProductionMarkerPrefix + runtime;
    private static string RuntimeFromMarker(string marker) => marker[ProductionMarkerPrefix.Length..];
    private static string TenantDigest(int tenantId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"netratel-mcp-onboarding-tenant:{tenantId}")));
    private static string CreateHash(int tenantId, McpOperatorEnrollmentCreateRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenantId, request.Runtime, request.ValidForMinutes, request.MaxUses }))));
    private static string RevokeHash(int tenantId, McpOperatorEnrollmentRevokeRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenantId, request.EnrollmentCodeId }))));
    private static bool HasPlan(string? plan, string? key) => IsOpaque(plan) && IsOpaque(key);
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maximumBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return read > maximumBytes ? null : buffer[..read];
    }
    private static McpOperatorOnboardingContext MinimalContext(string operation, int tenantId, string correlationId) => new(null!, string.Empty, McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(Tool, operation)!.RequiredScope), operation, tenantId, correlationId);

    private static IResult Failure(string code, McpOperatorOnboardingContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "onboarding_collateral_not_found" or "onboarding_enrollment_not_found" => StatusCodes.Status404NotFound,
            "onboarding_enrollment_invalid" or "onboarding_runtime_invalid" or "onboarding_query_invalid" => StatusCodes.Status400BadRequest,
            "onboarding_collateral_download_too_large" => StatusCodes.Status413PayloadTooLarge,
            "onboarding_enrollment_conflict" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "onboarding_enrollment_invalid" or "onboarding_runtime_invalid" or "onboarding_query_invalid" or "onboarding_collateral_download_too_large" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator onboarding access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator onboarding access was not admitted.",
            ["failure"] = new { code, layer, retryable = false, requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{Tool}/{context.Operation}", target = new { tenantId = context.TenantId }, remediation = "Use a signed delegated identity with netratel.mcp.onboarding and an active tenant Onboarding policy. For enrollment mutations, obtain a fresh preview and confirm with its unchanged opaque credentials." },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorOnboardingContext(McpOperatorDecision Decision, string ServicePrincipal, string RequiredScope, string Operation, int TenantId, string CorrelationId);
    private sealed record McpOperatorOnboardingContextResult(McpOperatorOnboardingContext? Context, IResult? Failure);
}

public sealed record McpOperatorEnrollmentCreateRequest(string? Runtime, int? ValidForMinutes, int? MaxUses, string? PlanToken = null, string? IdempotencyKey = null);
public sealed record McpOperatorEnrollmentRevokeRequest(Guid? EnrollmentCodeId, string? PlanToken = null, string? IdempotencyKey = null);
public sealed record McpOperatorOnboardingCollateral(string Runtime, string Version, string FileName, long Size, string Sha256, DateTimeOffset UploadedAtUtc);
public sealed record McpOperatorOnboardingCollateralDownload(McpOperatorOnboardingCollateral Collateral, string ContentBase64, string CorrelationId)
{
    public string TransferMode { get; init; } = "inline";
    public McpOperatorOnboardingDownloadDescriptor? Download { get; init; }
}
public sealed record McpOperatorOnboardingDownloadDescriptor(string Path, string Method, int TenantId, string TenantHeader, string EnrollmentCodeHeader);
public sealed record McpOperatorEnrollmentMetadata(Guid EnrollmentCodeId, int TenantId, string Runtime, DateTimeOffset CreatedAtUtc, DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc, int MaxUses, int Uses, DateTimeOffset? RevokedAtUtc, string? RevokedBy, bool IsActive, string? CorrelationId = null);
public sealed record McpOperatorEnrollmentPage(IReadOnlyList<McpOperatorEnrollmentMetadata> Items, long? NextCursor, string CorrelationId);
public sealed record McpOperatorEnrollmentPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, int TenantId, string Runtime, int ValidForMinutes, int MaxUses, string CorrelationId);
public sealed record McpOperatorEnrollmentRevokePreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, McpOperatorEnrollmentMetadata Enrollment, string CorrelationId);
public sealed record McpOperatorEnrollmentCreateResult(Guid EnrollmentCodeId, int TenantId, string Runtime, DateTimeOffset CreatedAtUtc, DateTimeOffset ValidToUtc, int MaxUses, string? EnrollmentCode, string CorrelationId, bool Replay = false);
public sealed record McpOperatorEnrollmentRevokeResult(McpOperatorEnrollmentMetadata Enrollment, bool Replay, string CorrelationId);
