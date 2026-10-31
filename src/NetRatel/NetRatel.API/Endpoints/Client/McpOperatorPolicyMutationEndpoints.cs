using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Reviewed, two-stage operator policy lifecycle. Native MCP requests require
/// signed delegation; the account adapter requires current application authority.
/// Both bind the exact target and payload to durable confirmation and idempotency.
/// </summary>
public static class McpOperatorPolicyMutationEndpoints
{
    private const string Tool = "netratel_policy";
    private const string CreateOperation = "create";
    private const string ReplaceOperation = "replace";
    private const string DisableOperation = "disable";
    private const string RevokeOperation = "revoke";
    private const string DeleteOperation = "delete";
    private const string UpsertTargetProfileOperation = "upsert_target_profile";
    private const string RequiredScope = "netratel.mcp.admin";
    private const string PolicyAdministratorRole = "PolicyAdministrator";

    public static IEndpointRouteBuilder MapMcpOperatorPolicyMutationEndpoints(this IEndpointRouteBuilder app)
    {
        MapMutations(app.MapGroup("/api/v2/mcp/operator/policy")
            .WithTags("MCP Operator Policy Mutation")
            .RequireAuthorization("M2MOnly"));
        return app;
    }

    /// <summary>Account requests use the same reviewed lifecycle without a fabricated MCP delegation.</summary>
    public static IEndpointRouteBuilder MapMcpOperatorAccountPolicyMutationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp-operator/policies/manage")
            .WithTags("MCP Operator Administration")
            .RequireAuthorization("McpOperatorPolicyAdmin")
            .WithMetadata(new AccountPolicyMutation())
            .AddEndpointFilter(InstancePolicyAccountAdmission.FilterAsync);
        group.MapGet("/self", (HttpContext http, IHostEnvironment environment) =>
            TryAccountContext(http, environment, out var context)
                ? Results.Ok(new { subject = context!.Principal.Subject, environment = context.Environment })
                : Results.Forbid());
        MapMutations(group, includeTargetProfileMutations: false);
        group.MapPost("/delete/preview", PreviewDeleteAsync);
        group.MapPost("/delete/confirm", ConfirmDeleteAsync);
        return app;
    }

    private static void MapMutations(RouteGroupBuilder group, bool includeTargetProfileMutations = true)
    {
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
            var decision = await EvaluateAsync(context, target, payloadHash, CreateOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId);
            if (context.InstanceAccount is null && WouldEscalateCaller(request.Policy, context.Principal))
                return Failure("operator_policy_self_escalation", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);

            try
            {
                await administration.ValidateDraftAsync(request.Policy, context.Principal.Subject, cancellationToken, decision.AccountAuthority).ConfigureAwait(false);
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
            var decision = await EvaluateAsync(context, target, payloadHash, CreateOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId);
            if (context.InstanceAccount is null && WouldEscalateCaller(request.Policy, context.Principal))
                return Failure("operator_policy_self_escalation", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);

            try
            {
                await administration.ValidateDraftAsync(request.Policy, context.Principal.Subject, cancellationToken, decision.AccountAuthority).ConfigureAwait(false);
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
                return await ReplayAsync(admission, administration, target, context, request.Policy, cancellationToken).ConfigureAwait(false);
            if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
                return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId);

            try
            {
                var created = await ExecuteMutationAsync(
                    decision, context, target, payloadHash, CreateOperation, authorization, administration,
                    mutationToken => administration.CreateAsync(request.Policy, context.Principal.Subject, mutationToken, decision.AccountAuthority),
                    cancellationToken).ConfigureAwait(false);
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, PolicyCreateResultReference(created), CancellationToken.None).ConfigureAwait(false);
                return Results.Created(
                    $"/api/v2/mcp/operator/policy/policies/{created.PolicyId:D}",
                    new McpOperatorPolicyCreateResult(created, false, context.CorrelationId));
            }
            catch (McpOperatorAdmissionRejectedException rejection)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
                return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId);
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

        if (includeTargetProfileMutations)
        {
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
        }
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
        var decision = await EvaluateAsync(context, target, payloadHash, UpsertTargetProfileOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
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
        var decision = await EvaluateAsync(context, target, payloadHash, UpsertTargetProfileOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: UpsertTargetProfileOperation);

        var confirmationRequest = new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, payloadHash, decision);
        if (await TryReplayTargetProfileAsync(confirmationRequest, confirmations, administration, target, context, request.ExpectedVersion, cancellationToken).ConfigureAwait(false) is { } replay)
            return replay;

        var current = await administration.GetTargetProfileAsync(target.TenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!MatchesExpectedVersion(current, request.ExpectedVersion))
        {
            // A concurrent first confirmation may have completed after the replay lookup.
            return await TryReplayTargetProfileAsync(confirmationRequest, confirmations, administration, target, context, request.ExpectedVersion, cancellationToken).ConfigureAwait(false)
                ?? Failure("target_profile_version_conflict", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, UpsertTargetProfileOperation);
        }

        var admission = await confirmations.ConfirmAsync(confirmationRequest, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);
        if (admission.IsReplay)
            return await ReplayTargetProfileAsync(admission, administration, target, context, request.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);

        try
        {
            var profile = await ExecuteMutationAsync(
                decision, context, target, payloadHash, UpsertTargetProfileOperation, authorization, administration,
                mutationToken => administration.UpsertTargetProfileAsync(
                    target.TenantId, agentId, request.Classification, request.Tags, request.ExpectedVersion,
                    context.Principal.Subject, mutationToken),
                cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, profile.AgentId.ToString("D"), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTargetProfileMutationResult(profile, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);
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

        if (!CanReplace(policy, request.ExpectedVersion, request.Policy!, context!.Principal, out failure, context.InstanceAccount is not null))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        var payloadHash = PayloadHash(ReplaceOperation, new PolicyReplacePayload(request.PolicyId, request.ExpectedVersion, request.Policy!));
        var decision = await EvaluateAsync(context, target, payloadHash, ReplaceOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        try
        {
            await administration.ValidateDraftAsync(request.Policy!, context.Principal.Subject, cancellationToken, decision.AccountAuthority).ConfigureAwait(false);
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

        var payloadHash = PayloadHash(ReplaceOperation, new PolicyReplacePayload(request.PolicyId, request.ExpectedVersion, request.Policy!));
        var decision = await EvaluateAsync(context!, target, payloadHash, ReplaceOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: ReplaceOperation);

        var confirmationRequest = new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, payloadHash, decision);
        if (await TryReplayMutationAsync(confirmationRequest, confirmations, administration, target, context!, ReplaceOperation,
            request.PolicyId, request.ExpectedVersion, request.Policy!, cancellationToken).ConfigureAwait(false) is { } replay)
            return replay;
        if (!CanReplace(policy, request.ExpectedVersion, request.Policy!, context!.Principal, out failure, context.InstanceAccount is not null))
        {
            // A concurrent confirmation may finish between the receipt lookup
            // and mutable version/state validation. Never consume a stale new plan.
            return await TryReplayMutationAsync(confirmationRequest, confirmations, administration, target, context!, ReplaceOperation,
                request.PolicyId, request.ExpectedVersion, request.Policy!, cancellationToken).ConfigureAwait(false)
                ?? Failure(failure!, target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: ReplaceOperation);
        }

        try
        {
            await administration.ValidateDraftAsync(request.Policy!, context.Principal.Subject, cancellationToken, decision.AccountAuthority).ConfigureAwait(false);
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

        var admission = await confirmations.ConfirmAsync(confirmationRequest, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);
        if (admission.IsReplay)
            return await ReplayMutationAsync(admission, administration, target, context!, ReplaceOperation, request.PolicyId, request.ExpectedVersion, request.Policy!, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: ReplaceOperation);

        try
        {
            var replaced = await ExecuteMutationAsync(
                decision, context, target, payloadHash, ReplaceOperation, authorization, administration,
                mutationToken => administration.ReplaceAsync(request.PolicyId, request.ExpectedVersion,
                    request.Policy!, context.Principal.Subject, mutationToken, decision.AccountAuthority),
                cancellationToken).ConfigureAwait(false);
            // The mutation is durable; preserve its receipt even if response delivery is cancelled.
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, PolicyMutationResultReference(replaced), CancellationToken.None).ConfigureAwait(false);
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
        if (!CanDisable(policy, request.ExpectedVersion, context!.Principal, out failure, context.InstanceAccount is not null))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        var payloadHash = PayloadHash(DisableOperation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context, target, payloadHash, DisableOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
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

        var payloadHash = PayloadHash(DisableOperation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context!, target, payloadHash, DisableOperation, authorization, administration, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: DisableOperation);

        var confirmationRequest = new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, payloadHash, decision);
        if (await TryReplayMutationAsync(confirmationRequest, confirmations, administration, target, context!, DisableOperation,
            request.PolicyId, request.ExpectedVersion, null, cancellationToken).ConfigureAwait(false) is { } replay)
            return replay;
        if (!CanDisable(policy, request.ExpectedVersion, context!.Principal, out failure, context.InstanceAccount is not null))
        {
            // A concurrent confirmation may finish between the receipt lookup
            // and mutable version/state validation. Never consume a stale new plan.
            return await TryReplayMutationAsync(confirmationRequest, confirmations, administration, target, context!, DisableOperation,
                request.PolicyId, request.ExpectedVersion, null, cancellationToken).ConfigureAwait(false)
                ?? Failure(failure!, target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: DisableOperation);
        }

        var admission = await confirmations.ConfirmAsync(confirmationRequest, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);
        if (admission.IsReplay)
            return await ReplayMutationAsync(admission, administration, target, context!, DisableOperation, request.PolicyId, request.ExpectedVersion, null, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);

        try
        {
            var disabled = await ExecuteMutationAsync(
                decision, context, target, payloadHash, DisableOperation, authorization, administration,
                mutationToken => administration.DisableAsync(request.PolicyId, request.ExpectedVersion, context.Principal.Subject, mutationToken),
                cancellationToken).ConfigureAwait(false);
            if (disabled is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_not_found", cancellationToken).ConfigureAwait(false);
                return Failure("operator_policy_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: DisableOperation);
            }

            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, PolicyMutationResultReference(disabled), CancellationToken.None).ConfigureAwait(false);
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

    private static Task<IResult> PreviewRevokeAsync(PreviewMcpOperatorPolicyDisableRequest request, HttpContext http, IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization, IMcpOperatorPolicyAdministration administration, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken) =>
        PreviewRetireAsync(request, http, hostEnvironment, authorization, administration, confirmations, cancellationToken, RevokeOperation);
    private static Task<IResult> ConfirmRevokeAsync(ConfirmMcpOperatorPolicyDisableRequest request, HttpContext http, IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization, IMcpOperatorPolicyAdministration administration, IMcpOperatorPolicyRevocation revocation, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken) =>
        ConfirmRetireAsync(request, http, hostEnvironment, authorization, administration, revocation, confirmations, cancellationToken, RevokeOperation);
    private static Task<IResult> PreviewDeleteAsync(PreviewMcpOperatorPolicyDisableRequest request, HttpContext http, IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization, IMcpOperatorPolicyAdministration administration, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken) =>
        PreviewRetireAsync(request, http, hostEnvironment, authorization, administration, confirmations, cancellationToken, DeleteOperation);
    private static Task<IResult> ConfirmDeleteAsync(ConfirmMcpOperatorPolicyDisableRequest request, HttpContext http, IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization, IMcpOperatorPolicyAdministration administration, IMcpOperatorPolicyRevocation revocation, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken) =>
        ConfirmRetireAsync(request, http, hostEnvironment, authorization, administration, revocation, confirmations, cancellationToken, DeleteOperation);

    private static async Task<IResult> PreviewRetireAsync(
        PreviewMcpOperatorPolicyDisableRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken, string operation)
    {
        if (!TryContext(http, hostEnvironment, "preview_" + operation, out var context, out var failure) || context is null)
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: operation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion) || request.Target is null)
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: operation);

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: operation);
        var policy = existing.Policy!;
        var target = existing.Target!;
        if (!SameTarget(policy.TargetSelector, request.Target))
            return Failure("policy_target_immutable", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: operation);
        if (!CanRetire(policy, request.ExpectedVersion, context.Principal, operation, out failure, context.InstanceAccount is not null))
            return Failure(failure!, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: operation);

        var payloadHash = PayloadHash(operation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context, target, payloadHash, operation, authorization, administration, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: operation);

        return Results.Ok(await CreatePreviewAsync(decision, payloadHash, target, context, confirmations, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ConfirmRetireAsync(
        ConfirmMcpOperatorPolicyDisableRequest request,
        HttpContext http,
        IHostEnvironment hostEnvironment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        IMcpOperatorPolicyRevocation revocation,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken, string operation)
    {
        if (!TryContext(http, hostEnvironment, "confirm_" + operation, out var context, out var failure) || context is null)
            return Failure(failure!, null, null, http.TraceIdentifier, mutationOperation: operation);
        if (!IsValidVersionedPolicyRequest(request.PolicyId, request.ExpectedVersion) || request.Target is null ||
            !HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
        {
            return Failure("validation_error", null, null, context!.CorrelationId, mutationOperation: operation);
        }

        var existing = await BindExistingPolicyAsync(context!, request.PolicyId, administration, cancellationToken).ConfigureAwait(false);
        if (existing.FailureCode is { } existingFailure)
            return Failure(existingFailure, existing.TenantId, existing.AgentId, context!.CorrelationId, mutationOperation: operation);
        var policy = existing.Policy!;
        var target = existing.Target!;
        if (!SameTarget(policy.TargetSelector, request.Target))
            return Failure("policy_target_immutable", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: operation);

        var payloadHash = PayloadHash(operation, new PolicyDisablePayload(request.PolicyId, request.ExpectedVersion, request.Target));
        var decision = await EvaluateAsync(context!, target, payloadHash, operation, authorization, administration, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            return Failure(decision.FailureCode ?? "target_policy_missing", target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: operation);

        var confirmationRequest = new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, payloadHash, decision);
        if (await TryReplayMutationAsync(confirmationRequest, confirmations, administration, target, context!, operation,
            request.PolicyId, request.ExpectedVersion, null, cancellationToken).ConfigureAwait(false) is { } replay)
            return replay;
        if (!CanRetire(policy, request.ExpectedVersion, context.Principal, operation, out failure, context.InstanceAccount is not null))
        {
            // A concurrent confirmation may finish between the receipt lookup
            // and mutable version/state validation. Never consume a stale new plan.
            return await TryReplayMutationAsync(confirmationRequest, confirmations, administration, target, context!, operation,
                request.PolicyId, request.ExpectedVersion, null, cancellationToken).ConfigureAwait(false)
                ?? Failure(failure!, target.TenantId, target.AgentId, context!.CorrelationId, mutationOperation: operation);
        }

        var admission = await confirmations.ConfirmAsync(confirmationRequest, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: operation);
        if (admission.IsReplay)
            return await ReplayMutationAsync(admission, administration, target, context!, operation, request.PolicyId, request.ExpectedVersion, null, cancellationToken).ConfigureAwait(false);
        if (!admission.IsNewDispatch || admission.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: operation);

        try
        {
            var revoked = await ExecuteMutationAsync(
                decision, context, target, payloadHash, operation, authorization, administration,
                mutationToken => operation == DeleteOperation && administration is IMcpOperatorPolicyDeletion deletion
                    ? deletion.DeleteAsync(request.PolicyId, request.ExpectedVersion, context.Principal.Subject, mutationToken)
                    : revocation.RevokeAsync(request.PolicyId, request.ExpectedVersion, context.Principal.Subject, mutationToken),
                cancellationToken).ConfigureAwait(false);
            if (revoked is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_not_found", cancellationToken).ConfigureAwait(false);
                return Failure("operator_policy_not_found", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: operation);
            }

            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, PolicyMutationResultReference(revoked), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorPolicyMutationResult(revoked, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: operation);
        }
        catch (DbUpdateConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_version_conflict", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_version_conflict", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, operation);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "validation_error", cancellationToken).ConfigureAwait(false);
            return Failure("validation_error", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status400BadRequest, operation);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "operator_policy_rejected", cancellationToken).ConfigureAwait(false);
            return Failure("operator_policy_rejected", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict, operation);
        }
    }

    private static async Task<IResult> ReplayAsync(
        McpOperatorConfirmationAdmission admission,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        McpOperatorPolicyDraft draft,
        CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded)
        {
            return Failure("idempotency_failed", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
        }

        var parts = admission.ResultReference?.Split(':');
        var legacy = Guid.TryParse(admission.ResultReference, out var policyId);
        if (!legacy && (parts is not { Length: 7 } || parts[0] != "policy-create" || !Guid.TryParse(parts[1], out policyId)))
            return Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);
        var current = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Environment != context.Environment ||
            !TryBindTarget(context, current.TargetSelector, out var currentTarget, out _) || currentTarget != target)
            return Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);

        // Identifier-only receipts predate retained creation metadata. They are
        // exact only while the original version remains unchanged; never relabel
        // a later definition as the accepted create result.
        if (legacy)
            return current.Version == 1 && current.LifecycleState == McpOperatorPolicyLifecycleState.Active && current.DisabledAtUtc is null
                ? Results.Ok(new McpOperatorPolicyCreateResult(current, true, context.CorrelationId))
                : Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict);

        return !TryReadPolicyCreateResult(parts!, current.PolicyId, draft, out var original)
            ? Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, StatusCodes.Status409Conflict)
            : Results.Ok(new McpOperatorPolicyCreateResult(original!, true, context.CorrelationId));
    }

    private static string PolicyCreateResultReference(McpOperatorPolicy policy) =>
        string.Create(CultureInfo.InvariantCulture,
            $"policy-create:{policy.PolicyId:D}:{policy.Version}:{policy.CreatedAtUtc.UtcTicks}:{policy.ExpiresAtUtc?.UtcTicks ?? 0}:{policy.ReviewByUtc?.UtcTicks ?? 0}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(policy.CreatedBy))}");

    private static bool TryReadPolicyCreateResult(string[] parts, Guid policyId, McpOperatorPolicyDraft draft,
        out McpOperatorPolicy? original)
    {
        original = null;
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version != 1 ||
            !TryUtcTicks(parts[3], out var createdAt) || createdAt == 0 ||
            !TryUtcTicks(parts[4], out var expiresAt) || !TryUtcTicks(parts[5], out var reviewBy)) return false;
        string actor;
        try { actor = Encoding.UTF8.GetString(Convert.FromBase64String(parts[6])); }
        catch (FormatException) { return false; }
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || actor.Any(char.IsControl)) return false;

        // The original normalized definition comes only from the exact draft
        // authenticated by the consumed plan. Persisted receipt metadata retains
        // creation identity and timestamp precision independently of later edits.
        original = new(policyId, draft.Name.Trim(), draft.Environment, draft.Effect, draft.Priority,
            draft.PrincipalSelector with { Value = draft.PrincipalSelector.Value.Trim() },
            draft.TargetSelector with { ClientTag = draft.TargetSelector.ClientTag?.Trim() },
            draft.TargetClassification, draft.OperationFamily, string.IsNullOrWhiteSpace(draft.Operation) ? null : draft.Operation.Trim(),
            draft.Constraints, new DateTimeOffset(createdAt, TimeSpan.Zero), actor,
            expiresAt == 0 ? null : new DateTimeOffset(expiresAt, TimeSpan.Zero),
            reviewBy == 0 ? null : new DateTimeOffset(reviewBy, TimeSpan.Zero), null, null, version,
            string.IsNullOrWhiteSpace(draft.AuditReference) ? null : draft.AuditReference.Trim()) { ParentPolicyId = draft.ParentPolicyId, IntegrationCredentialId = draft.IntegrationCredentialId, Resource = draft.Resource, Operations = draft.Operations, IsContainer = draft.IsContainer };
        return true;
    }

    private static bool TryUtcTicks(string value, out long ticks) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks >= 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks;

    private static async Task<IResult?> TryReplayMutationAsync(
        McpOperatorConfirmationRequest request,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        string mutationOperation,
        Guid policyId,
        long expectedVersion,
        McpOperatorPolicyDraft? replacement,
        CancellationToken cancellationToken)
    {
        // The current decision binds identity, target, policy revision and exact
        // payload. A replay-only lookup cannot reserve or consume a new dispatch.
        var admission = await confirmations.ConfirmAsync(request with { ReplayOnly = true }, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } failure)
            return Failure(failure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation);
        return admission.IsReplay
            ? await ReplayMutationAsync(admission, administration, target, context, mutationOperation, policyId, expectedVersion, replacement, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task<IResult> ReplayMutationAsync(
        McpOperatorConfirmationAdmission admission,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        string mutationOperation,
        Guid policyId,
        long expectedVersion,
        McpOperatorPolicyDraft? replacement,
        CancellationToken cancellationToken)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded || expectedVersion == long.MaxValue)
            return Failure("idempotency_failed", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation);

        var policy = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
        if (policy is null || policy.Environment != context.Environment ||
            !TryBindTarget(context, policy.TargetSelector, out var currentTarget, out _) || currentTarget != target ||
            !TryReadPolicyMutationResult(admission.ResultReference, policy, expectedVersion + 1, mutationOperation, out var original))
            return Failure("idempotency_replay_unavailable", target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: mutationOperation);

        // A replaced draft is bound by the consumed plan's payload hash. Rebuild
        // its normalized fields, retaining immutable creation metadata, so later
        // replacements or lifecycle changes cannot rewrite the original result.
        if (replacement is not null)
        {
            original = original with
            {
                Name = replacement.Name.Trim(),
                Environment = replacement.Environment,
                Effect = replacement.Effect,
                Priority = replacement.Priority,
                PrincipalSelector = replacement.PrincipalSelector with { Value = replacement.PrincipalSelector.Value.Trim() },
                TargetSelector = replacement.TargetSelector with { ClientTag = replacement.TargetSelector.ClientTag?.Trim() },
                TargetClassification = replacement.TargetClassification,
                Operations = replacement.Operations, ParentPolicyId = replacement.ParentPolicyId,
                IntegrationCredentialId = replacement.IntegrationCredentialId, Resource = replacement.Resource, IsContainer = replacement.IsContainer,
                OperationFamily = replacement.OperationFamily,
                Operation = string.IsNullOrWhiteSpace(replacement.Operation) ? null : replacement.Operation.Trim(),
                Constraints = replacement.Constraints,
                ExpiresAtUtc = replacement.ExpiresAtUtc,
                ReviewByUtc = replacement.ReviewByUtc,
                AuditReference = string.IsNullOrWhiteSpace(replacement.AuditReference) ? null : replacement.AuditReference.Trim()
            };
        }

        return Results.Ok(new McpOperatorPolicyMutationResult(original, true, context.CorrelationId));
    }

    private static string PolicyMutationResultReference(McpOperatorPolicy policy) =>
        string.Create(CultureInfo.InvariantCulture,
            $"policy-mutation:{policy.PolicyId:D}:{policy.Version}:{(int)policy.LifecycleState}:{policy.DisabledAtUtc?.UtcTicks ?? 0}");

    private static bool TryReadPolicyMutationResult(
        string? resultReference,
        McpOperatorPolicy current,
        long expectedResultVersion,
        string mutationOperation,
        out McpOperatorPolicy original)
    {
        original = current;
        var lifecycle = mutationOperation switch
        {
            ReplaceOperation => McpOperatorPolicyLifecycleState.Active,
            DisableOperation => McpOperatorPolicyLifecycleState.Disabled,
            DeleteOperation => McpOperatorPolicyLifecycleState.Deleted,
            _ => McpOperatorPolicyLifecycleState.Revoked
        };
        var disabledAtUtc = mutationOperation == ReplaceOperation ? null : current.DisabledAtUtc;
        if (!Guid.TryParse(resultReference, out var policyId))
        {
            var parts = resultReference?.Split(':');
            if (parts is not { Length: 5 } || parts[0] != "policy-mutation" ||
                !Guid.TryParse(parts[1], out policyId) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version != expectedResultVersion ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var state) || state != (int)lifecycle ||
                !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var disabledTicks) ||
                disabledTicks < 0 || disabledTicks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }
            disabledAtUtc = disabledTicks == 0 ? null : new DateTimeOffset(disabledTicks, TimeSpan.Zero);
        }
        // Legacy identifier-only receipts still have an exact versioned payload.
        // Disabled/revoked definitions and their disabled actor remain immutable.
        // PostgreSQL retains microseconds; compare at that precision while the
        // receipt preserves the original response's full timestamp precision.
        if (policyId != current.PolicyId || current.Version < expectedResultVersion ||
            (mutationOperation == ReplaceOperation && disabledAtUtc is not null) ||
            (mutationOperation == DisableOperation && current.LifecycleState is not (McpOperatorPolicyLifecycleState.Disabled or McpOperatorPolicyLifecycleState.Revoked or McpOperatorPolicyLifecycleState.Deleted)) ||
            (mutationOperation == RevokeOperation && current.LifecycleState is not (McpOperatorPolicyLifecycleState.Revoked or McpOperatorPolicyLifecycleState.Deleted)) ||
            (mutationOperation == DeleteOperation && current.LifecycleState != McpOperatorPolicyLifecycleState.Deleted) ||
            (mutationOperation != ReplaceOperation &&
                (disabledAtUtc is not { } originalDisabledAtUtc || current.DisabledAtUtc is not { } persistedDisabledAtUtc ||
                 persistedDisabledAtUtc.UtcTicks / TimeSpan.TicksPerMicrosecond != originalDisabledAtUtc.UtcTicks / TimeSpan.TicksPerMicrosecond ||
                 current.DisabledBy is null)))
        {
            return false;
        }

        original = current with
        {
            Version = expectedResultVersion,
            LifecycleState = lifecycle,
            DisabledAtUtc = disabledAtUtc,
            DisabledBy = mutationOperation == ReplaceOperation ? null : current.DisabledBy
        };
        return true;
    }

    private static async Task<IResult?> TryReplayTargetProfileAsync(
        McpOperatorConfirmationRequest request,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        var admission = await confirmations.ConfirmAsync(request with { ReplayOnly = true }, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } failure)
            return Failure(failure, target.TenantId, target.AgentId, context.CorrelationId, mutationOperation: UpsertTargetProfileOperation);
        return admission.IsReplay
            ? await ReplayTargetProfileAsync(admission, administration, target, context, expectedVersion, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task<IResult> ReplayTargetProfileAsync(
        McpOperatorConfirmationAdmission admission,
        IMcpOperatorPolicyAdministration administration,
        McpOperatorPolicyMutationTarget target,
        McpOperatorPolicyMutationContext context,
        long? expectedVersion,
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
        return profile is null || profile.Version - 1 != (expectedVersion ?? 0)
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
        out string? failure,
        bool instanceAdministrator = false)
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
        if (!SameScopeTarget(existing.TargetSelector, proposed.TargetSelector))
        {
            failure = "policy_target_immutable";
            return false;
        }
        if (!instanceAdministrator && (SelectsCaller(existing.PrincipalSelector, principal) || WouldEscalateCaller(proposed, principal)))
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

    private static bool CanRetire(McpOperatorPolicy existing, long version, McpOperatorPrincipal principal, string operation, out string? failure, bool instanceAdministrator = false)
    {
        if (operation != DeleteOperation) return CanRevoke(existing, version, principal, out failure, instanceAdministrator);
        failure = existing.Version != version ? "operator_policy_version_conflict" :
            existing.LifecycleState == McpOperatorPolicyLifecycleState.Deleted ? "operator_policy_rejected" :
            !instanceAdministrator && SelectsCaller(existing.PrincipalSelector, principal) ? "operator_policy_self_escalation" : null;
        return failure is null;
    }

    private static bool CanRevoke(
        McpOperatorPolicy existing,
        long expectedVersion,
        McpOperatorPrincipal principal,
        out string? failure,
        bool instanceAdministrator = false)
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
        if (!instanceAdministrator && SelectsCaller(existing.PrincipalSelector, principal))
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
        out string? failure,
        bool instanceAdministrator = false)
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
        if (!instanceAdministrator && SelectsCaller(existing.PrincipalSelector, principal))
        {
            failure = "operator_policy_self_escalation";
            return false;
        }
        return true;
    }

    private static bool SameScopeTarget(McpOperatorTargetSelector left, McpOperatorTargetSelector right) =>
        left.Kind == right.Kind && left.TenantId == right.TenantId && left.AgentId == right.AgentId &&
        string.Equals(left.ClientTag, right.ClientTag, StringComparison.Ordinal);

    private static bool SameTarget(McpOperatorTargetSelector left, McpOperatorTargetSelector right) =>
        SameScopeTarget(left, right) &&
        (left.AgentIds ?? []).SequenceEqual(right.AgentIds ?? []) && (left.ClientTags ?? []).SequenceEqual(right.ClientTags ?? []);

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
        IMcpOperatorPolicyAdministration administration,
        CancellationToken cancellationToken)
    {
        var accountAuthority = context.InstanceAccount;
        var currentInstanceAccount = accountAuthority is not null &&
            await accountAuthority.IsCurrentAsync(cancellationToken).ConfigureAwait(false);
        var tenantVisible = currentInstanceAccount || await authorization.HasTenantVisibilityAsync(
            context.Environment, context.Principal, target.TenantId, cancellationToken).ConfigureAwait(false);
        // Policy selectors describe the proposed rule, not trusted facts about
        // its target. Read the exact persisted profile for both phases and bind
        // its revision into the confirmation decision.
        var profile = tenantVisible && target.AgentId is { } agentId
            ? await administration.GetTargetProfileAsync(target.TenantId, agentId, cancellationToken).ConfigureAwait(false)
            : null;
        var access = new McpOperatorAccessRequest(
            context.Environment,
            context.Principal,
            target.TenantId,
            target.AgentId,
            profile?.Classification,
            McpOperatorOperationFamily.PolicyAdministration,
            $"{Tool}/{mutationOperation}",
            new HashSet<string>([RequiredScope], StringComparer.Ordinal),
            McpOperatorConfirmationClass.PolicyAdministration,
            context.CorrelationId,
            context.RequestId,
            TargetSetDigest(mutationOperation, target, payloadHash, profile),
            profile?.Tags.ToHashSet(StringComparer.Ordinal),
            context.Resource,
            context.Instance,
            Tool,
            tenantVisible,
            TargetResolved: true,
            TargetEnabled: true,
            TargetOnline: true,
            CapabilityAvailable: true)
        {
            RequiresDevelopmentEnvironmentPolicy = target.Kind == McpOperatorTargetSelectorKind.DevelopmentEnvironment
        };
        if (accountAuthority is not null && !currentInstanceAccount)
            return McpOperatorDecision.Denied(access, "instance_account_authority_changed", McpOperatorAuthorizationLayer.Authentication);
        // Creating an initial profile is the reviewed bootstrap operation.
        // A policy mutation for an exact agent must already have that profile.
        if (tenantVisible && target.AgentId is not null && profile is null && mutationOperation != UpsertTargetProfileOperation)
            return McpOperatorDecision.Denied(access, "target_profile_missing", McpOperatorAuthorizationLayer.Target);
        if (currentInstanceAccount)
            return new McpOperatorDecision(true, null, null, [], null, access.TargetSetDigest, access)
            {
                AccountAuthority = new(accountAuthority!.PrincipalId)
            };
        return await authorization.EvaluateAsync(access, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteMutationAsync<T>(
        McpOperatorDecision decision,
        McpOperatorPolicyMutationContext context,
        McpOperatorPolicyMutationTarget target,
        string payloadHash,
        string mutationOperation,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorPolicyAdministration administration,
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        var profile = target.AgentId is { } agentId
            ? await administration.GetTargetProfileAsync(target.TenantId, agentId, cancellationToken).ConfigureAwait(false)
            : null;
        if (TargetSetDigest(mutationOperation, target, payloadHash, profile) != decision.Request.TargetSetDigest)
            throw new McpOperatorAdmissionRejectedException("confirmation_plan_stale");

        async Task<T> PersistAsync(CancellationToken mutationToken)
        {
            if (context.InstanceAccount is { } account)
            {
                if (decision.AccountAuthority?.PrincipalId != account.PrincipalId ||
                    !await account.IsCurrentAsync(mutationToken).ConfigureAwait(false))
                    throw new McpOperatorAdmissionRejectedException("instance_account_authority_changed");
                // This is application administration, not admission under a delegated
                // MCP policy. The durable plan/receipt and immutable policy-change
                // audit retain the exact actor, target, payload and resulting version.
                // Do not fabricate an MCP parent or a policy-backed accepted audit.
            }
            else
                await authorization.RecordAcceptedAsync(decision, context.ServicePrincipal, mutationToken).ConfigureAwait(false);
            return await mutation(mutationToken).ConfigureAwait(false);
        }

        return target.AgentId is { } exactAgentId
            ? await administration.ExecuteWithTargetProfileAsync(
                target.TenantId, exactAgentId, profile, PersistAsync, cancellationToken).ConfigureAwait(false)
            : await PersistAsync(cancellationToken).ConfigureAwait(false);
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
        if (http.GetEndpoint()?.Metadata.GetMetadata<AccountPolicyMutation>() is not null)
        {
            if (TryAccountContext(http, environment, out context)) return true;
            failure = "delegated_identity_required";
            return false;
        }
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

    private static bool TryAccountContext(HttpContext http, IHostEnvironment environment,
        out McpOperatorPolicyMutationContext? context)
    {
        context = null;
        // The route's effective-access policy rechecks the current account assignment on
        // every phase. Credentials and service tokens cannot enter this account adapter.
        if (!InstancePolicyAccountAdmission.TryInteractivePrincipal(http.User, out _) ||
            !McpOperatorDelegationIdentityFactory.TryCreate(http.User, out var identity) || identity is null)
            return false;
        var selectedEnvironment = environment.IsDevelopment() ? McpOperatorEnvironment.Development
            : environment.IsProduction() ? McpOperatorEnvironment.Production : (McpOperatorEnvironment?)null;
        if (selectedEnvironment is null) return false;
        // Non-instance accounts keep the existing bounded parent-policy path.
        // A current instance account uses a distinct confirmation authority and never
        // turns these translated role/scope names into a grant for an MCP credential.
        var instanceAccount = InstancePolicyAccountAdmission.FromContext(http);
        var resource = instanceAccount is null ? "netratel-web-policy-administration"
            : new McpOperatorAccountAuthority(instanceAccount.PrincipalId).Resource;
        var roles = identity.Roles.ToHashSet(StringComparer.Ordinal);
        roles.Add(PolicyAdministratorRole);
        var scopes = identity.Scopes.ToHashSet(StringComparer.Ordinal);
        scopes.Add(RequiredScope);
        context = new McpOperatorPolicyMutationContext(selectedEnvironment.Value,
            new McpOperatorPrincipal(identity.Subject, identity.ClientId, identity.AuthorizedParty,
                identity.Groups.ToHashSet(StringComparer.Ordinal), roles, scopes, "netratel-web"),
            "netratel-web", resource,
            selectedEnvironment == McpOperatorEnvironment.Development ? "dev" : "prod",
            http.TraceIdentifier, Guid.NewGuid().ToString("N"), null, null) { SignedInAccount = true, InstanceAccount = instanceAccount };
        return true;
    }

    private sealed record AccountPolicyMutation;

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
        if (!context.SignedInAccount &&
            (context.DelegatedTenantId != delegatedTenantId || context.DelegatedAgentId != agentId))
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
            "operator_policy_self_escalation" or "operator_policy_version_conflict" or "policy_environment_immutable" or "policy_target_immutable" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" => StatusCodes.Status409Conflict,
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

    private static string TargetSetDigest(string mutationOperation, McpOperatorPolicyMutationTarget target, string payloadHash,
        McpOperatorTargetProfile? profile)
    {
        var source = JsonSerializer.Serialize(new
        {
            operation = $"{Tool}/{mutationOperation}",
            target.TenantId,
            target.AgentId,
            target.Kind,
            target.ClientTag,
            payloadHash,
            profileVersion = profile?.Version,
            classification = profile?.Classification,
            tags = profile?.Tags.Order(StringComparer.Ordinal).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static bool Same(string left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string Layer(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
        "instance_account_authority_changed" => "authentication",
        "oauth_scope_missing" => "oauth_scope",
        "oauth_role_missing" => "oauth_role",
        "tenant_not_authorized" => "tenant",
        "target_not_found" or "target_profile_missing" => "target",
        "operator_policy_not_found" => "policy",
        "validation_error" or "environment_mismatch" => "input",
        "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
        "idempotency_conflict" or "idempotency_pending" or "idempotency_failed" or "idempotency_replay_unavailable" => "idempotency",
        _ => "policy"
    };

    private static string SafeDetails(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "A valid signed operator delegation was not available for this exact policy mutation operation and target.",
        "instance_account_authority_changed" => "Current instance-administrator account authority is no longer available.",
        "oauth_scope_missing" => "The signed delegation does not include the required administrator scope.",
        "oauth_role_missing" => "The signed delegation does not include the PolicyAdministrator role.",
        "tenant_not_authorized" => "The caller has no active policy-administration authorization for this tenant.",
        "target_not_found" => "No persisted exact target matched the requested policy selector.",
        "target_profile_missing" => "The exact agent has no persisted operator target profile for policy administration.",
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
        "instance_account_authority_changed" => "Refresh account access and create a new preview before confirming the policy change.",
        "oauth_scope_missing" => "Request the netratel.mcp.admin OAuth scope, then obtain a fresh delegation assertion.",
        "oauth_role_missing" => "Ask an identity administrator to assign the dedicated PolicyAdministrator role, then reauthorize.",
        "tenant_not_authorized" => "Ask a policy administrator to grant a reviewed PolicyAdministration policy for this tenant.",
        "target_not_found" => "Verify the persisted V2 target when using an exact-agent policy selector.",
        "target_profile_missing" => "Have an independently authorized administrator create the exact agent's reviewed target profile, then create a fresh policy preview.",
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
    Guid? DelegatedAgentId)
{
    public bool SignedInAccount { get; init; }
    public InstancePolicyAccountAdmission? InstanceAccount { get; init; }
}

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
