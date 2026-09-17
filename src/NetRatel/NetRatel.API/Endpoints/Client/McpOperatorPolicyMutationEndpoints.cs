using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Delegated, two-stage creation of an operator policy. The caller must first
/// obtain a plan for the exact target-bound draft, then submit the same draft
/// with the server-issued plan and idempotency credentials. This route never
/// accepts a forwarded user bearer token.
/// </summary>
public static class McpOperatorPolicyMutationEndpoints
{
    private const string Tool = "netratel_policy";
    private const string CreateOperation = "create";
    private const string ReplaceOperation = "replace";
    private const string DisableOperation = "disable";
    private const string RevokeOperation = "revoke";
    private const string UpsertTargetProfileOperation = "upsert_target_profile";
    private const string RequiredScope = "netratel.mcp.admin";
    private const string PolicyAdministratorRole = "PolicyAdministrator";

    public static IEndpointRouteBuilder MapMcpOperatorPolicyMutationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/policy")
            .WithTags("MCP Operator Policy Mutation")
            .RequireAuthorization("M2MOnly");

        group.MapPost("/create/preview", async (
            PreviewMcpOperatorPolicyCreateRequest request,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorAuthorization authorization,
            IMcpOperatorPolicyAdministration administration,
            IMcpOperatorConfirmationService confirmations,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "preview_create", out var context, out var failure))
                return Failure(failure!, null, null, http.TraceIdentifier);
            if (!TryBindTarget(context!, request.Policy, out var target, out failure))
                return Failure(failure!, null, null, context!.CorrelationId);
            if (request.Policy.Environment != context!.Environment)
                return Failure("environment_mismatch", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest);

            var payloadHash = PayloadHash(request.Policy);
            var decision = await EvaluateAsync(context, target, payloadHash, CreateOperation, authorization, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId);
            if (WouldEscalateCaller(request.Policy, context.Principal))
                return Failure("operator_policy_self_escalation", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);

            try
            {
                await administration.ValidateDraftAsync(request.Policy, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return Failure("target_not_found", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status404NotFound);
            }
            catch (ArgumentException)
            {
                return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
            }

            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(decision, payloadHash), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorPolicyCreatePreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                target.TenantId,
                target.AgentId,
                context.CorrelationId));
        })
        .Produces<McpOperatorPolicyCreatePreview>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/create/confirm", async (
            ConfirmMcpOperatorPolicyCreateRequest request,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorAuthorization authorization,
            IMcpOperatorPolicyAdministration administration,
            IMcpOperatorConfirmationService confirmations,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "confirm_create", out var context, out var failure))
                return Failure(failure!, null, null, http.TraceIdentifier);
            if (!TryBindTarget(context!, request.Policy, out var target, out failure))
                return Failure(failure!, null, null, context!.CorrelationId);
            if (request.Policy.Environment != context!.Environment)
                return Failure("environment_mismatch", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest);

            var payloadHash = PayloadHash(request.Policy);
            var decision = await EvaluateAsync(context, target, payloadHash, CreateOperation, authorization, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId);
            if (WouldEscalateCaller(request.Policy, context.Principal))
                return Failure("operator_policy_self_escalation", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);

            try
            {
                await administration.ValidateDraftAsync(request.Policy, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return Failure("target_not_found", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status404NotFound);
            }
            catch (ArgumentException)
            {
                return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
            }

            var admission = await confirmations.ConfirmAsync(
                new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, payloadHash, decision),
                cancellationToken).ConfigureAwait(false);
            if (admission.FailureCode is { } confirmationFailure)
                return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId);
            if (admission.IsReplay)
                return await ReplayAsync(admission, administration, target, context, cancellationToken).ConfigureAwait(false);
            if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
                return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId);

            try
            {
                await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            }
            catch (McpOperatorAdmissionRejectedException rejection)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
                return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId);
            }

            try
            {
                var created = await administration.CreateAsync(request.Policy, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, created.PolicyId.ToString("D"), cancellationToken).ConfigureAwait(false);
                return Results.Created(
                    $"/api/v2/mcp/operator/policy/policies/{created.PolicyId:D}",
                    new McpOperatorPolicyCreateResult(created, false, context.CorrelationId));
            }
            catch (KeyNotFoundException)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "target_not_found", cancellationToken).ConfigureAwait(false);
                return Failure("target_not_found", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status404NotFound);
            }
            catch (ArgumentException)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "validation_error", cancellationToken).ConfigureAwait(false);
                return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_rejected", cancellationToken).ConfigureAwait(false);
                return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
            }
        })
        .Produces<McpOperatorPolicyCreateResult>(StatusCodes.Status200OK)
        .Produces<McpOperatorPolicyCreateResult>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/replace/preview", PreviewReplaceAsync)
            .Produces<McpOperatorPolicyMutationPreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/replace/confirm", ConfirmReplaceAsync)
            .Produces<McpOperatorPolicyMutationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/disable/preview", PreviewDisableAsync)
            .Produces<McpOperatorPolicyMutationPreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/disable/confirm", ConfirmDisableAsync)
            .Produces<McpOperatorPolicyMutationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/revoke/preview", PreviewRevokeAsync)
            .Produces<McpOperatorPolicyMutationPreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/revoke/confirm", ConfirmRevokeAsync)
            .Produces<McpOperatorPolicyMutationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/target-profile/preview", PreviewTargetProfileAsync)
            .Produces<McpOperatorPolicyMutationPreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/target-profile/confirm", ConfirmTargetProfileAsync)
            .Produces<McpOperatorTargetProfileMutationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> PreviewTargetProfileAsync(
        PreviewMcpOperatorTargetProfileUpsertRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "preview_target_profile", out var context, out var failure) || context is null)
            return Failure(failure ?? "delegated_identity_required", request.TenantId, request.AgentId, http.TraceIdentifier, mutationOperation: UpsertTargetProfileOperation);
        if (!TryBindTargetProfile(context, request.TenantId, request.AgentId, out var target, out failure) ||
            !IsValidTargetProfileRequest(request.Classification, request.Tags, request.ExpectedVersion))
            return Failure(failure ?? "validation_error", request.TenantId, request.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, UpsertTargetProfileOperation);
        var agentId = target.AgentId!.Value;

        var payload = new TargetProfileUpsertPayload(request.TenantId, request.AgentId, request.Classification, request.Tags, request.ExpectedVersion);
        var payloadHash = PayloadHash(UpsertTargetProfileOperation, payload);
        var decision = await EvaluateAsync(context, target, payloadHash, UpsertTargetProfileOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: UpsertTargetProfileOperation);

        var current = await administration.GetTargetProfileAsync(target.TenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!MatchesExpectedVersion(current, request.ExpectedVersion))
            return Failure("target_profile_version_conflict", target.TenantId, target.AgentId, context!.CorrelationId, StatusCodes.Status409Conflict, UpsertTargetProfileOperation);

        return Results.Ok(await CreatePreviewAsync(decision, payloadHash, target, context, confirmations, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ConfirmTargetProfileAsync(
        ConfirmMcpOperatorTargetProfileUpsertRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "confirm_target_profile", out var context, out var failure) || context is null)
            return Failure(failure ?? "delegated_identity_required", request.TenantId, request.AgentId, http.TraceIdentifier, mutationOperation: UpsertTargetProfileOperation);
        if (!TryBindTargetProfile(context, request.TenantId, request.AgentId, out var target, out failure) ||
            !IsValidTargetProfileRequest(request.Classification, request.Tags, request.ExpectedVersion) ||
            !HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return Failure(failure ?? "validation_error", request.TenantId, request.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, UpsertTargetProfileOperation);
        var agentId = target.AgentId!.Value;

        var payload = new TargetProfileUpsertPayload(request.TenantId, request.AgentId, request.Classification, request.Tags, request.ExpectedVersion);
        var payloadHash = PayloadHash(UpsertTargetProfileOperation, payload);
        var decision = await EvaluateAsync(context, target, payloadHash, UpsertTargetProfileOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: UpsertTargetProfileOperation);

        var current = await administration.GetTargetProfileAsync(target.TenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!MatchesExpectedVersion(current, request.ExpectedVersion))
            return Failure("target_profile_version_conflict", target.TenantId, target.AgentId, context!.CorrelationId, StatusCodes.Status409Conflict, UpsertTargetProfileOperation);

        var admission = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, payloadHash, decision), cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);
        if (admission.IsReplay)
            return await ReplayTargetProfileAsync(admission, administration, target, context, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);

        try
        {
            await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var profile = await administration.UpsertTargetProfileAsync(
                target.TenantId,
                agentId,
                request.Classification,
                request.Tags,
                request.ExpectedVersion,
                context.Principal.Subject,
                cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, profile.AgentId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTargetProfileMutationResult(profile, false, context.CorrelationId));
        }
        catch (KeyNotFoundException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "target_not_found", cancellationToken).ConfigureAwait(false);
            return Failure("target_not_found", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status404NotFound, UpsertTargetProfileOperation);
        }
        catch (DbUpdateConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "target_profile_version_conflict", cancellationToken).ConfigureAwait(false);
            return Failure("target_profile_version_conflict", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, UpsertTargetProfileOperation);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "validation_error", cancellationToken).ConfigureAwait(false);
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, UpsertTargetProfileOperation);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_target_profile_rejected", cancellationToken).ConfigureAwait(false);
            return Failure("operator_target_profile_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, UpsertTargetProfileOperation);
        }
    }

    private static async Task<IResult> PreviewReplaceAsync(
        PreviewMcpOperatorPolicyReplaceRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "preview_replace", out var context, out var failure))
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: ReplaceOperation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion, request.Policy))
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: ReplaceOperation);

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: ReplaceOperation);
        var policy = existing.Policy!;
        var target = existing.Target!;

        if (!CanReplace(policy, request.ExpectedVersion, request.Policy!, context!.Principal, out failure))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        var payloadHash = PayloadHash(ReplaceOperation, new PolicyReplacePayload(request.PolicyId, request.ExpectedVersion, request.Policy!));
        var decision = await EvaluateAsync(context, target, payloadHash, ReplaceOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        try
        {
            await administration.ValidateDraftAsync(request.Policy!, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Failure("target_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        }
        catch (ArgumentException)
        {
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        }
        catch (InvalidOperationException)
        {
            return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, ReplaceOperation);
        }

        return Results.Ok(await CreatePreviewAsync(decision, payloadHash, target, context, confirmations, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ConfirmReplaceAsync(
        ConfirmMcpOperatorPolicyReplaceRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "confirm_replace", out var context, out var failure))
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: ReplaceOperation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion, request.Policy) ||
            !HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
        {
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: ReplaceOperation);
        }

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: ReplaceOperation);
        var policy = existing.Policy!;
        var target = existing.Target!;

        if (!CanReplace(policy, request.ExpectedVersion, request.Policy!, context!.Principal, out failure))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        var payloadHash = PayloadHash(ReplaceOperation, new PolicyReplacePayload(request.PolicyId, request.ExpectedVersion, request.Policy!));
        var decision = await EvaluateAsync(context, target, payloadHash, ReplaceOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        try
        {
            await administration.ValidateDraftAsync(request.Policy!, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Failure("target_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        }
        catch (ArgumentException)
        {
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        }
        catch (InvalidOperationException)
        {
            return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, ReplaceOperation);
        }

        var admission = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, payloadHash, decision), cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        if (admission.IsReplay)
            return await ReplayMutationAsync(admission, administration, target, context, ReplaceOperation, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        try
        {
            await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var replaced = await administration.ReplaceAsync(
                request.PolicyId,
                request.ExpectedVersion,
                request.Policy!,
                context.Principal.Subject,
                cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, replaced.PolicyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorPolicyMutationResult(replaced, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        }
        catch (KeyNotFoundException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_not_found", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        }
        catch (DbUpdateConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_version_conflict", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_version_conflict", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, ReplaceOperation);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "validation_error", cancellationToken).ConfigureAwait(false);
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, ReplaceOperation);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_rejected", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, ReplaceOperation);
        }
    }

    private static async Task<IResult> PreviewDisableAsync(
        PreviewMcpOperatorPolicyDisableRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "preview_disable", out var context, out var failure))
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: DisableOperation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion) || request.Target is null)
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: DisableOperation);

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: DisableOperation);
        var policy = existing.Policy!;
        var target = existing.Target!;
        if (!SameTarget(policy.TargetSelector, request.Target))
            return Failure("policy_target_immutable", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: DisableOperation);
        if (!CanDisable(policy, request.ExpectedVersion, context!.Principal, out failure))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        var payloadHash = PayloadHash(DisableOperation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context, target, payloadHash, DisableOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        return Results.Ok(await CreatePreviewAsync(decision, payloadHash, target, context, confirmations, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ConfirmDisableAsync(
        ConfirmMcpOperatorPolicyDisableRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "confirm_disable", out var context, out var failure))
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: DisableOperation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion) || request.Target is null ||
            !HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
        {
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: DisableOperation);
        }

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: DisableOperation);
        var policy = existing.Policy!;
        var target = existing.Target!;
        if (!SameTarget(policy.TargetSelector, request.Target))
            return Failure("policy_target_immutable", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: DisableOperation);
        if (!CanDisable(policy, request.ExpectedVersion, context!.Principal, out failure))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        var payloadHash = PayloadHash(DisableOperation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context, target, payloadHash, DisableOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        var admission = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, payloadHash, decision), cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);
        if (admission.IsReplay)
            return await ReplayMutationAsync(admission, administration, target, context, DisableOperation, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        try
        {
            await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var disabled = await administration.DisableAsync(request.PolicyId, request.ExpectedVersion, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
            if (disabled is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_not_found", cancellationToken).ConfigureAwait(false);
                return Failure("operator_policy_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);
            }

            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, disabled.PolicyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorPolicyMutationResult(disabled, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);
        }
        catch (DbUpdateConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_version_conflict", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_version_conflict", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, DisableOperation);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "validation_error", cancellationToken).ConfigureAwait(false);
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, DisableOperation);
        }
    }

    private static async Task<IResult> PreviewRevokeAsync(
        PreviewMcpOperatorPolicyDisableRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "preview_revoke", out var context, out var failure))
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: RevokeOperation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion) || request.Target is null)
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: RevokeOperation);

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: RevokeOperation);
        var policy = existing.Policy!;
        var target = existing.Target!;
        if (!SameTarget(policy.TargetSelector, request.Target))
            return Failure("policy_target_immutable", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: RevokeOperation);
        if (!CanRevoke(policy, request.ExpectedVersion, context.Principal, out failure))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);

        var payloadHash = PayloadHash(RevokeOperation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context, target, payloadHash, RevokeOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);

        return Results.Ok(await CreatePreviewAsync(decision, payloadHash, target, context, confirmations, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ConfirmRevokeAsync(
        ConfirmMcpOperatorPolicyDisableRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorPolicyRevocation revocation,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!TryContext(http, hostEnvironment, "confirm_revoke", out var context, out var failure))
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: RevokeOperation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion) || request.Target is null ||
            !HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
        {
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: RevokeOperation);
        }

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: RevokeOperation);
        var policy = existing.Policy!;
        var target = existing.Target!;
        if (!SameTarget(policy.TargetSelector, request.Target))
            return Failure("policy_target_immutable", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: RevokeOperation);
        if (!CanRevoke(policy, request.ExpectedVersion, context.Principal, out failure))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);

        var payloadHash = PayloadHash(RevokeOperation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context, target, payloadHash, RevokeOperation, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);

        var admission = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, payloadHash, decision), cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);
        if (admission.IsReplay)
            return await ReplayMutationAsync(admission, administration, target, context, RevokeOperation, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);

        try
        {
            await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var revoked = await revocation.RevokeAsync(request.PolicyId, request.ExpectedVersion, context.Principal.Subject, cancellationToken).ConfigureAwait(false);
            if (revoked is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_not_found", cancellationToken).ConfigureAwait(false);
                return Failure("operator_policy_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);
            }

            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, revoked.PolicyId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorPolicyMutationResult(revoked, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: RevokeOperation);
        }
        catch (DbUpdateConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_version_conflict", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_version_conflict", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, RevokeOperation);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "validation_error", cancellationToken).ConfigureAwait(false);
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, RevokeOperation);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_rejected", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, RevokeOperation);
        }
    }

    private static async Task<IResult> ReplayAsync(
        McpOperatorConfirmationAdmission admission,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !Guid.TryParse(admission.ResultReference, out var policyId))
        {
            return Failure("idempotency_failed", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
        }

        var created = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
        return created is null
            ? Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict)
            : Results.Ok(new McpOperatorPolicyCreateResult(created, true, context.CorrelationId));
    }

    private static async Task<IResult> ReplayMutationAsync(
        McpOperatorConfirmationAdmission admission,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        string mutationOperation,
        CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !Guid.TryParse(admission.ResultReference, out var policyId))
        {
            return Failure("idempotency_failed", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation);
        }

        var policy = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
        return policy is null
            ? Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation)
            : Results.Ok(new McpOperatorPolicyMutationResult(policy, true, context.CorrelationId));
    }

    private static async Task<IResult> ReplayTargetProfileAsync(
        McpOperatorConfirmationAdmission admission,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        CancellationToken cancellationToken)
    {
        var agentId = target.AgentId!.Value;
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !Guid.TryParse(admission.ResultReference, out var replayAgentId) || replayAgentId != agentId)
        {
            return Failure("idempotency_failed", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);
        }

        var profile = await administration.GetTargetProfileAsync(target.TenantId, agentId, cancellationToken).ConfigureAwait(false);
        return profile is null
            ? Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation)
            : Results.Ok(new McpOperatorTargetProfileMutationResult(profile, true, context.CorrelationId));
    }

    private static async Task<McpOperatorPolicyMutationPreview> CreatePreviewAsync(
        McpOperatorDecision decision,
        string payloadHash,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var plan = await confirmations.CreatePlanAsync(
            new McpOperatorConfirmationPlanRequest(decision, payloadHash), cancellationToken).ConfigureAwait(false);
        return new McpOperatorPolicyMutationPreview(
            plan.PlanToken,
            plan.IdempotencyKey,
            plan.ExpiresAtUtc,
            plan.ConfirmationClass,
            target.TenantId,
            target.AgentId,
            context.CorrelationId);
    }

    private static async Task<ExistingPolicyBinding> BindExistingPolicyAsync(
        McpOperatorPolicyMutationContext context,
        Guid policyId,
        IMcpOperatorPolicyAdministration administration,
        CancellationToken cancellationToken)
    {
        var policy = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
        if (policy is null)
            return new(null, default, "operator_policy_not_found", null, null);
        if (!TryBindTarget(context, policy.TargetSelector, out var target, out var failure))
        {
            return new(null, default, failure, policy.TargetSelector.TenantId,
                policy.TargetSelector.Kind == McpOperatorTargetSelectorKind.ExactAgent ? policy.TargetSelector.AgentId : null);
        }

        return policy.Environment != context.Environment
            ? new(null, default, "environment_mismatch", target.TenantId, target.AgentId)
            : new(policy, target, null, target.TenantId, target.AgentId);
    }

    private static bool IsValidVersionedPolicyRequest(Guid policyId, long expectedVersion, McpOperatorPolicyDraft? policy = null) =>
        policyId != Guid.Empty && expectedVersion > 0 && (policy is null || policy.TargetSelector is not null);

    private static bool CanReplace(
        McpOperatorPolicy existing,
        long expectedVersion,
        McpOperatorPolicyDraft proposed,
        McpOperatorPrincipal principal,
        out string? failure)
    {
        failure = null;
        if (existing.LifecycleState != McpOperatorPolicyLifecycleState.Active || existing.DisabledAtUtc is not null)
        {
            failure = "operator_policy_rejected";
            return false;
        }
        if (existing.Version != expectedVersion)
        {
            failure = "operator_policy_version_conflict";
            return false;
        }
        if (existing.Environment != proposed.Environment)
        {
            failure = "policy_environment_immutable";
            return false;
        }
        if (!SameTarget(existing.TargetSelector, proposed.TargetSelector))
        {
            failure = "policy_target_immutable";
            return false;
        }
        if (SelectsCaller(existing.PrincipalSelector, principal) || WouldEscalateCaller(proposed, principal))
        {
            failure = "operator_policy_self_escalation";
            return false;
        }
        return true;
    }

    private static bool TryBindTargetProfile(
        McpOperatorPolicyMutationContext context,
        int tenantId,
        Guid agentId,
        out McpOperatorPolicyMutationTarget target,
        out string? failure)
    {
        target = default;
        failure = null;
        if (tenantId <= 0 || agentId == Guid.Empty)
        {
            failure = "validation_error";
            return false;
        }

        if (context.DelegatedTenantId != tenantId || context.DelegatedAgentId != agentId)
        {
            failure = "delegated_identity_invalid";
            return false;
        }

        target = new McpOperatorPolicyMutationTarget(tenantId, agentId, McpOperatorTargetSelectorKind.ExactAgent, null);
        return true;
    }

    private static bool IsValidTargetProfileRequest(
        McpOperatorTargetClassification classification,
        IReadOnlyCollection<string>? tags,
        long? expectedVersion) =>
        tags is { Count: <= 32 } &&
        classification is not McpOperatorTargetClassification.Unknown &&
        Enum.IsDefined(classification) &&
        tags.All(tag => IsSafeTag(tag)) &&
        tags.Distinct(StringComparer.Ordinal).Count() == tags.Count &&
        expectedVersion is null or > 0;

    private static bool IsSafeTag(string? tag) =>
        !string.IsNullOrWhiteSpace(tag) && tag.Length <= 128 &&
        tag.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/' or '@' or '#');

    private static bool MatchesExpectedVersion(McpOperatorTargetProfile? profile, long? expectedVersion) =>
        profile is null ? expectedVersion is null : expectedVersion == profile.Version;

    private static bool CanRevoke(
        McpOperatorPolicy existing,
        long expectedVersion,
        McpOperatorPrincipal principal,
        out string? failure)
    {
        failure = null;
        if (existing.Version != expectedVersion)
        {
            failure = "operator_policy_version_conflict";
            return false;
        }
        if (existing.LifecycleState == McpOperatorPolicyLifecycleState.Revoked)
        {
            failure = "operator_policy_rejected";
            return false;
        }
        if (SelectsCaller(existing.PrincipalSelector, principal))
        {
            failure = "operator_policy_self_escalation";
            return false;
        }
        return true;
    }

    private static bool CanDisable(
        McpOperatorPolicy existing,
        long expectedVersion,
        McpOperatorPrincipal principal,
        out string? failure)
    {
        failure = null;
        if (existing.Version != expectedVersion)
        {
            failure = "operator_policy_version_conflict";
            return false;
        }
        if (existing.DisabledAtUtc is not null)
        {
            failure = "operator_policy_rejected";
            return false;
        }
        if (SelectsCaller(existing.PrincipalSelector, principal))
        {
            failure = "operator_policy_self_escalation";
            return false;
        }
        return true;
    }

    private static bool SameTarget(McpOperatorTargetSelector left, McpOperatorTargetSelector right) =>
        left.Kind == right.Kind && left.TenantId == right.TenantId && left.AgentId == right.AgentId &&
        string.Equals(left.ClientTag, right.ClientTag, StringComparison.Ordinal);

    private static bool HasPlanCredentials(string? planToken, string? idempotencyKey) =>
        IsOpaqueCredential(planToken) && IsOpaqueCredential(idempotencyKey);

    private static bool IsOpaqueCredential(string? value) =>
        value is { Length: >= 32 and <= 128 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static async Task<McpOperatorDecision> EvaluateAsync(
        McpOperatorPolicyMutationContext context,
        McpOperatorPolicyMutationTarget target,
        string payloadHash,
        string mutationOperation,
        IMcpOperatorAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var tenantVisible = await authorization.HasTenantVisibilityAsync(
            context.Environment, context.Principal, target.TenantId, cancellationToken).ConfigureAwait(false);
        var access = new McpOperatorAccessRequest(
            context.Environment,
            context.Principal,
            target.TenantId,
            target.AgentId,
            null,
            McpOperatorOperationFamily.PolicyAdministration,
            $"{Tool}/{mutationOperation}",
            new HashSet<string>([RequiredScope], StringComparer.Ordinal),
            McpOperatorConfirmationClass.PolicyAdministration,
            context.CorrelationId,
            context.RequestId,
            TargetSetDigest(mutationOperation, target, payloadHash),
            null,
            context.Resource,
            context.Instance,
            Tool,
            tenantVisible,
            TargetResolved: true,
            TargetEnabled: true,
            TargetOnline: true,
            CapabilityAvailable: true);
        return await authorization.EvaluateAsync(access, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryContext(
        HttpContext http,
        IHostEnvironment environment,
        string delegationOperation,
        out McpOperatorPolicyMutationContext? context,
        out string? failure)
    {
        context = null;
        failure = null;
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
        {
            failure = "delegated_identity_required";
            return false;
        }

        var operatorEnvironment = environment.IsDevelopment()
            ? McpOperatorEnvironment.Development
            : environment.IsProduction()
                ? McpOperatorEnvironment.Production
                : (McpOperatorEnvironment?)null;
        var expectedInstance = operatorEnvironment switch
        {
            McpOperatorEnvironment.Development => "dev",
            McpOperatorEnvironment.Production => "prod",
            _ => null
        };
        if (operatorEnvironment is null || expectedInstance is null ||
            !string.Equals(delegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(delegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(delegation.Operation, delegationOperation, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.CorrelationId))
        {
            failure = "delegated_identity_invalid";
            return false;
        }

        if (!delegation.Identity.Scopes.Contains(RequiredScope, StringComparer.Ordinal))
        {
            failure = "oauth_scope_missing";
            return false;
        }
        if (!delegation.Identity.Roles.Contains(PolicyAdministratorRole, StringComparer.Ordinal))
        {
            failure = "oauth_role_missing";
            return false;
        }

        context = new McpOperatorPolicyMutationContext(
            operatorEnvironment.Value,
            new McpOperatorPrincipal(
                delegation.Identity.Subject,
                delegation.Identity.ClientId,
                delegation.Identity.AuthorizedParty,
                delegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal),
                delegation.ServicePrincipal),
            delegation.ServicePrincipal,
            delegation.Resource!,
            delegation.Instance!,
            delegation.CorrelationId!,
            delegation.RequestId,
            delegation.TenantId,
            delegation.AgentId);
        return true;
    }

    private static bool TryBindTarget(
        McpOperatorPolicyMutationContext context,
        McpOperatorPolicyDraft? draft,
        out McpOperatorPolicyMutationTarget target,
        out string? failure) =>
        TryBindTarget(context, draft?.TargetSelector, out target, out failure);

    private static bool TryBindTarget(
        McpOperatorPolicyMutationContext context,
        McpOperatorTargetSelector? selector,
        out McpOperatorPolicyMutationTarget target,
        out string? failure)
    {
        target = default;
        failure = null;
        if (selector is null || !Enum.IsDefined(selector.Kind))
        {
            failure = "validation_error";
            return false;
        }

        var isControlPlane = selector.Kind is McpOperatorTargetSelectorKind.ControlPlane or McpOperatorTargetSelectorKind.DevelopmentEnvironment;
        var agentId = selector.Kind == McpOperatorTargetSelectorKind.ExactAgent ? selector.AgentId : null;
        var selectorIsValid = isControlPlane
            ? selector.TenantId == 0 && selector.AgentId is null && selector.ClientTag is null
            : selector.TenantId > 0 && selector.Kind switch
            {
                McpOperatorTargetSelectorKind.ExactAgent => agentId is { } exactAgent && exactAgent != Guid.Empty && selector.ClientTag is null,
                McpOperatorTargetSelectorKind.Tenant => selector.AgentId is null && selector.ClientTag is null,
                McpOperatorTargetSelectorKind.ClientTag => selector.AgentId is null && !string.IsNullOrWhiteSpace(selector.ClientTag),
                _ => false
            };
        if (!selectorIsValid)
        {
            failure = "validation_error";
            return false;
        }

        var delegatedTenantId = isControlPlane ? (int?)null : selector.TenantId;
        if (context.DelegatedTenantId != delegatedTenantId || context.DelegatedAgentId != agentId)
        {
            failure = "delegated_identity_invalid";
            return false;
        }

        target = new McpOperatorPolicyMutationTarget(selector.TenantId, agentId, selector.Kind, selector.ClientTag);
        return true;
    }

    private static bool WouldEscalateCaller(McpOperatorPolicyDraft draft, McpOperatorPrincipal principal)
    {
        if (draft.Effect != McpOperatorPolicyEffect.Allow)
            return false;

        return SelectsCaller(draft.PrincipalSelector, principal);
    }

    private static bool SelectsCaller(McpOperatorPrincipalSelector selector, McpOperatorPrincipal principal) =>
        selector.Kind switch
        {
            McpOperatorPrincipalSelectorKind.OAuthSubject => Same(selector.Value, principal.Subject),
            McpOperatorPrincipalSelectorKind.OAuthClientId => Same(selector.Value, principal.ClientId) || Same(selector.Value, principal.AuthorizedParty),
            McpOperatorPrincipalSelectorKind.OidcGroup => principal.Groups.Contains(selector.Value),
            McpOperatorPrincipalSelectorKind.MappedRole => principal.Roles.Contains(selector.Value),
            McpOperatorPrincipalSelectorKind.ServicePrincipal => Same(selector.Value, principal.ServicePrincipal),
            _ => true
        };

    private static IResult Failure(
        string code,
        int? tenantId,
        Guid? agentId,
        string correlationId,
        int? statusOverride = null,
        string mutationOperation = CreateOperation)
    {
        var status = statusOverride ?? code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_not_found" or "operator_policy_not_found" => StatusCodes.Status404NotFound,
            "validation_error" or "environment_mismatch" => StatusCodes.Status400BadRequest,
            "operator_policy_self_escalation" or "operator_policy_version_conflict" or "policy_environment_immutable" or "policy_target_immutable" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        return Results.Problem(
            statusCode: status,
            title: "MCP operator policy mutation was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP operator policy mutation was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer = Layer(code),
                    retryable = code is "idempotency_pending",
                    requiredScopes = new[] { RequiredScope },
                    requiredOperation = $"{Tool}/{mutationOperation}",
                    target = tenantId.HasValue && code is not "tenant_not_authorized" ? new { tenantId, agentId } : null,
                    safeDetails = SafeDetails(code),
                    remediation = Remediation(code)
                },
                ["correlationId"] = correlationId
            });
    }

    private static string PayloadHash(McpOperatorPolicyDraft draft)
        => PayloadHash(CreateOperation, draft);

    private static string PayloadHash(string mutationOperation, object payload)
    {
        var canonical = JsonSerializer.Serialize($"{mutationOperation}:{JsonSerializer.Serialize(payload)}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string TargetSetDigest(string mutationOperation, McpOperatorPolicyMutationTarget target, string payloadHash)
    {
        var source = $"policy-{mutationOperation}:{target.TenantId}:{target.AgentId:D}:{(short)target.Kind}:{target.ClientTag}:{payloadHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static bool Same(string left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string Layer(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
        "oauth_scope_missing" => "oauth_scope",
        "oauth_role_missing" => "oauth_role",
        "tenant_not_authorized" => "tenant",
        "target_not_found" => "target",
        "operator_policy_not_found" => "policy",
        "validation_error" or "environment_mismatch" => "input",
        "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
        "idempotency_conflict" or "idempotency_pending" or "idempotency_failed" or "idempotency_replay_unavailable" => "idempotency",
        _ => "policy"
    };

    private static string SafeDetails(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "A valid signed operator delegation was not available for this exact policy mutation operation and target.",
        "oauth_scope_missing" => "The signed delegation does not include the required administrator scope.",
        "oauth_role_missing" => "The signed delegation does not include the PolicyAdministrator role.",
        "tenant_not_authorized" => "The caller has no active policy-administration authorization for this tenant.",
        "target_not_found" => "No persisted exact target matched the requested policy selector.",
        "operator_policy_not_found" => "No current policy record matched the supplied policy identifier.",
        "validation_error" => "The policy draft did not meet the published bounded contract.",
        "environment_mismatch" => "A delegated policy mutation can apply policy only for the selected MCP environment.",
        "policy_environment_immutable" => "Replacement cannot move a policy between MCP environments.",
        "policy_target_immutable" => "Replacement cannot change the policy target selector.",
        "operator_policy_version_conflict" => "The policy changed after the version supplied for this request.",
        "operator_policy_self_escalation" => "The requested lifecycle change would grant the delegated caller additional operator authority or alter a policy that selects that caller.",
        "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "The supplied confirmation plan is not valid for this exact policy draft and current authorization decision.",
        "idempotency_conflict" => "The supplied idempotency key is already bound to a different policy mutation payload or target.",
        "idempotency_pending" => "The same confirmed policy mutation is still completing; retry with the same plan credentials later.",
        "idempotency_failed" or "idempotency_replay_unavailable" => "The prior confirmed policy mutation did not produce a replayable policy result.",
        _ => "The policy mutation request did not satisfy the current administrator policy."
    };

    private static string Remediation(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "Invoke through the authenticated MCP service so it can issue a fresh signed delegation bound to this policy target.",
        "oauth_scope_missing" => "Request the netratel.mcp.admin OAuth scope, then obtain a fresh delegation assertion.",
        "oauth_role_missing" => "Ask an identity administrator to assign the dedicated PolicyAdministrator role, then reauthorize.",
        "tenant_not_authorized" => "Ask a policy administrator to grant a reviewed PolicyAdministration policy for this tenant.",
        "target_not_found" => "Verify the persisted V2 target when using an exact-agent policy selector.",
        "operator_policy_not_found" => "Refresh the bounded policy list and use a current policy identifier.",
        "validation_error" or "environment_mismatch" => "Use the documented bounded policy draft shape and selected environment.",
        "policy_environment_immutable" or "policy_target_immutable" => "Create a new reviewed policy for the requested environment or target, then disable the old policy through an independent administrator.",
        "operator_policy_version_conflict" => "Refresh the policy and create a new preview with its current version.",
        "operator_policy_self_escalation" => "Submit policy changes through an independently authorized administrator who is not selected by either the existing or proposed policy.",
        "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "Create a fresh preview for the unchanged policy draft, then confirm it before the plan expires.",
        "idempotency_conflict" => "Do not reuse the server-issued idempotency key with a different policy draft or target.",
        "idempotency_pending" => "Retry the same confirmation request later; do not create another plan while this admission is pending.",
        _ => "Review the delegated administrator policy, policy draft, and confirmation state."
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewMcpOperatorPolicyCreateRequest(McpOperatorPolicyDraft Policy);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmMcpOperatorPolicyCreateRequest(
    McpOperatorPolicyDraft Policy,
    string PlanToken,
    string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewMcpOperatorPolicyReplaceRequest(
    Guid PolicyId,
    long ExpectedVersion,
    McpOperatorPolicyDraft Policy);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmMcpOperatorPolicyReplaceRequest(
    Guid PolicyId,
    long ExpectedVersion,
    McpOperatorPolicyDraft Policy,
    string PlanToken,
    string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewMcpOperatorPolicyDisableRequest(
    Guid PolicyId,
    long ExpectedVersion,
    McpOperatorTargetSelector Target);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmMcpOperatorPolicyDisableRequest(
    Guid PolicyId,
    long ExpectedVersion,
    McpOperatorTargetSelector Target,
    string PlanToken,
    string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreviewMcpOperatorTargetProfileUpsertRequest(
    int TenantId,
    Guid AgentId,
    McpOperatorTargetClassification Classification,
    IReadOnlyCollection<string> Tags,
    long? ExpectedVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmMcpOperatorTargetProfileUpsertRequest(
    int TenantId,
    Guid AgentId,
    McpOperatorTargetClassification Classification,
    IReadOnlyCollection<string> Tags,
    long? ExpectedVersion,
    string PlanToken,
    string IdempotencyKey);

public sealed record McpOperatorPolicyCreatePreview(
    string PlanToken,
    string IdempotencyKey,
    DateTimeOffset ExpiresAtUtc,
    McpOperatorConfirmationClass ConfirmationClass,
    int TenantId,
    Guid? AgentId,
    string CorrelationId);

public sealed record McpOperatorPolicyCreateResult(
    McpOperatorPolicy Policy,
    bool Replay,
    string CorrelationId);

public sealed record McpOperatorPolicyMutationPreview(
    string PlanToken,
    string IdempotencyKey,
    DateTimeOffset ExpiresAtUtc,
    McpOperatorConfirmationClass ConfirmationClass,
    int TenantId,
    Guid? AgentId,
    string CorrelationId);

public sealed record McpOperatorPolicyMutationResult(
    McpOperatorPolicy Policy,
    bool Replay,
    string CorrelationId);

public sealed record McpOperatorTargetProfileMutationResult(
    McpOperatorTargetProfile Profile,
    bool Replay,
    string CorrelationId);

internal sealed record McpOperatorPolicyMutationContext(
    McpOperatorEnvironment Environment,
    McpOperatorPrincipal Principal,
    string ServicePrincipal,
    string Resource,
    string Instance,
    string CorrelationId,
    string RequestId,
    int? DelegatedTenantId,
    Guid? DelegatedAgentId);

internal readonly record struct McpOperatorPolicyMutationTarget(
    int TenantId,
    Guid? AgentId,
    McpOperatorTargetSelectorKind Kind,
    string? ClientTag);

internal sealed record ExistingPolicyBinding(
    McpOperatorPolicy? Policy,
    McpOperatorPolicyMutationTarget Target,
    string? FailureCode,
    int? TenantId,
    Guid? AgentId);

internal sealed record PolicyReplacePayload(
    Guid PolicyId,
    long ExpectedVersion,
    McpOperatorPolicyDraft Policy);

internal sealed record PolicyDisablePayload(
    Guid PolicyId,
    long ExpectedVersion,
    McpOperatorTargetSelector Target);

internal sealed record TargetProfileUpsertPayload(
    int TenantId,
    Guid AgentId,
    McpOperatorTargetClassification Classification,
    IReadOnlyCollection<string> Tags,
    long? ExpectedVersion);
