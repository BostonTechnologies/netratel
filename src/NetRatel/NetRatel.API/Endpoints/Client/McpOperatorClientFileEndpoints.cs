using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Policy-admitted, root-constrained V2 file reads. The server validates the
/// declared canonical path and carries its matching roots to the agent; the
/// agent then resolves links and reparse points on the filesystem it owns.
/// </summary>
public static class McpOperatorClientFileEndpoints
{
    private const string Tool = "netratel_files";
    private const int MaximumPageSize = 100;
    private const int MaximumReadBytes = 64 * 1024;
    private const int MaximumArtifactBytes = 512 * 1024;
    private const int MaximumWriteTextBytes = 16 * 1024;
    private const int MaximumUploadBytes = 64 * 1024;
    private const int MaximumUploadBase64Characters = ((MaximumUploadBytes + 2) / 3) * 4;
    private static readonly TimeSpan ArtifactLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapMcpOperatorClientFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/files")
            .WithTags("MCP Operator Files")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/browse", BrowseAsync);
        group.MapGet("/stat", StatAsync);
        group.MapGet("/read", ReadAsync);
        group.MapPost("/artifacts/collect/preview", CollectArtifactPreviewAsync);
        group.MapPost("/artifacts/collect/confirm", CollectArtifactConfirmAsync);
        group.MapGet("/artifacts/{artifactId:guid}", ArtifactStatusAsync);
        group.MapGet("/artifacts/{artifactId:guid}/download", DownloadArtifactAsync);
        group.MapPost("/artifacts/{artifactId:guid}/cleanup/preview", CleanupArtifactPreviewAsync);
        group.MapPost("/artifacts/{artifactId:guid}/cleanup/confirm", CleanupArtifactConfirmAsync);
        group.MapPost("/write-text/preview", WriteTextPreviewAsync);
        group.MapPost("/write-text/confirm", WriteTextConfirmAsync);
        group.MapPost("/upload/preview", UploadPreviewAsync);
        group.MapPost("/upload/confirm", UploadConfirmAsync);
        group.MapPost("/create-directory/preview", CreateDirectoryPreviewAsync);
        group.MapPost("/create-directory/confirm", CreateDirectoryConfirmAsync);
        group.MapPost("/delete/preview", DeletePreviewAsync);
        group.MapPost("/delete/confirm", DeleteConfirmAsync);
        group.MapPost("/copy/preview", CopyPreviewAsync);
        group.MapPost("/copy/confirm", CopyConfirmAsync);
        group.MapPost("/move/preview", MovePreviewAsync);
        group.MapPost("/move/confirm", MoveConfirmAsync);
        return app;
    }

    private static async Task<IResult> BrowseAsync(
        int tenantId,
        Guid agentId,
        string path,
        int? pageSize,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "browse", http.TraceIdentifier);
        if (pageSize is <= 0 or > MaximumPageSize)
            return GatewayFailure("invalid_file_query", fallback);

        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "browse", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateReadPolicy(context.Decision, path, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;
        if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } beforeDispatch)
            return Failure(beforeDispatch, context);

        try
        {
            var entries = await sessions.ListAsync(
                new ClientKey(tenantId, agentId),
                path,
                pageSize ?? MaximumPageSize,
                accessPolicy,
                cancellationToken).ConfigureAwait(false);
            if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } afterDispatch)
                return Failure(afterDispatch, context);

            var safeEntries = entries
                .Where(entry => entry.Name.Length <= 512 &&
                    TryCreateReadPolicy(context.Decision, entry.FullPath, out _))
                .Select(entry => new McpOperatorFileEntry(entry.Name, entry.FullPath, entry.IsDirectory, entry.SizeBytes))
                .Take(pageSize ?? MaximumPageSize)
                .ToArray();
            return Results.Ok(new McpOperatorFileBrowseResult(
                tenantId,
                agentId,
                path,
                safeEntries,
                "mcp-operator-policy",
                context.CorrelationId));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            return GatewayFailure("session_unavailable", context);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> ReadAsync(
        int tenantId,
        Guid agentId,
        string path,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "read", http.TraceIdentifier);
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "read", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateReadPolicy(context.Decision, path, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;
        if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } beforeDispatch)
            return Failure(beforeDispatch, context);

        GatewayFileReadOperation? operation = null;
        var completed = false;
        try
        {
            operation = await sessions.ReadAsync(
                new ClientKey(tenantId, agentId), path, accessPolicy, cancellationToken).ConfigureAwait(false);
            await using var content = new MemoryStream(MaximumReadBytes);
            await foreach (var chunk in operation.Chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Length > MaximumReadBytes - content.Length)
                {
                    await operation.CancelAsync("operator_read_limit", CancellationToken.None).ConfigureAwait(false);
                    return GatewayFailure("file_too_large", context);
                }

                await content.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } revoked)
                {
                    await operation.CancelAsync("operator_policy_revoked", CancellationToken.None).ConfigureAwait(false);
                    return Failure(revoked, context);
                }
            }

            await operation.Completion.ConfigureAwait(false);
            completed = true;
            if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } afterDispatch)
                return Failure(afterDispatch, context);

            var bytes = content.ToArray();
            return Results.Ok(new McpOperatorFileReadResult(
                tenantId,
                agentId,
                path,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                ContentType(bytes),
                Convert.ToBase64String(bytes),
                "mcp-operator-policy",
                context.CorrelationId));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            return GatewayFailure("session_unavailable", context);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operation is not null)
                await operation.CancelAsync("operator_read_cancelled", CancellationToken.None).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        finally
        {
            if (operation is not null && !completed && !cancellationToken.IsCancellationRequested)
                await operation.CancelAsync("operator_read_ended", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> CollectArtifactPreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileArtifactCollectPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "collect", http.TraceIdentifier);
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "collect", cancellationToken, "preview_collect").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateReadPolicy(context.Decision, request.Path, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);
        if (!TryGetReadRootFingerprint(context.Decision, request.Path, out var readRootFingerprint))
            return Failure("file_policy_roots_missing", context);
        var maximumBytes = ArtifactLimit(context.Decision);
        if (maximumBytes <= 0)
            return Failure("file_write_limit_exceeded", context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, ArtifactCollectPayloadHash(request.Path)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorFileArtifactCollectPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                request.Path,
                maximumBytes,
                readRootFingerprint,
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> CollectArtifactConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileArtifactCollectConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorFileArtifactStore artifacts,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "collect", http.TraceIdentifier);
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return GatewayFailure("invalid_file_query", fallback);

        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "collect", cancellationToken, "confirm_collect").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateReadPolicy(context.Decision, request.Path, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);
        if (!TryGetReadRootFingerprint(context.Decision, request.Path, out var readRootFingerprint))
            return Failure("file_policy_roots_missing", context);
        var maximumBytes = ArtifactLimit(context.Decision);
        if (maximumBytes <= 0)
            return Failure("file_write_limit_exceeded", context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, ArtifactCollectPayloadHash(request.Path), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context);
        if (confirmation.IsReplay)
            return await ReplayArtifactCollectionAsync(confirmation, artifacts, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context);

        McpOperatorAcceptedAudit acceptedAudit;
        try
        {
            acceptedAudit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        GatewayFileReadOperation? operation = null;
        var completed = false;
        var operationEnded = false;
        try
        {
            var beforeDispatch = await RecheckArtifactCollectionAsync(
                admission, presence, options, new ClientKey(tenantId, agentId), context.Request, request.Path, cancellationToken).ConfigureAwait(false);
            if (beforeDispatch.FailureCode is not null ||
                !string.Equals(beforeDispatch.ReadRootFingerprint, readRootFingerprint, StringComparison.Ordinal))
            {
                var code = beforeDispatch.FailureCode ?? "artifact_policy_changed";
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, code, cancellationToken).ConfigureAwait(false);
                return Failure(code, context);
            }

            var metadata = await sessions.StatAsync(new ClientKey(tenantId, agentId), request.Path, accessPolicy, cancellationToken).ConfigureAwait(false);
            if (metadata.IsDirectory || !TryGetArtifactFileName(metadata.FullPath, out var fileName))
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "source_not_file", cancellationToken).ConfigureAwait(false);
                return GatewayFailure("source_not_file", context);
            }

            operation = await sessions.ReadAsync(new ClientKey(tenantId, agentId), request.Path, accessPolicy, cancellationToken).ConfigureAwait(false);
            await using var content = new MemoryStream(maximumBytes);
            await foreach (var chunk in operation.Chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Length > maximumBytes - content.Length)
                {
                    await operation.CancelAsync("operator_artifact_limit", CancellationToken.None).ConfigureAwait(false);
                    operationEnded = true;
                    await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "artifact_too_large", cancellationToken).ConfigureAwait(false);
                    return GatewayFailure("artifact_too_large", context);
                }

                await content.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                var duringDispatch = await RecheckArtifactCollectionAsync(
                    admission, presence, options, new ClientKey(tenantId, agentId), context.Request, request.Path, cancellationToken).ConfigureAwait(false);
                if (duringDispatch.FailureCode is not null ||
                    !string.Equals(duringDispatch.ReadRootFingerprint, readRootFingerprint, StringComparison.Ordinal) ||
                    content.Length > duringDispatch.MaximumBytes)
                {
                    await operation.CancelAsync("operator_artifact_policy_changed", CancellationToken.None).ConfigureAwait(false);
                    operationEnded = true;
                    var code = duringDispatch.FailureCode ?? "artifact_policy_changed";
                    await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, code, cancellationToken).ConfigureAwait(false);
                    return Failure(code, context);
                }
            }

            await operation.Completion.ConfigureAwait(false);
            completed = true;
            var afterDispatch = await RecheckArtifactCollectionAsync(
                admission, presence, options, new ClientKey(tenantId, agentId), context.Request, request.Path, cancellationToken).ConfigureAwait(false);
            if (afterDispatch.FailureCode is not null ||
                !string.Equals(afterDispatch.ReadRootFingerprint, readRootFingerprint, StringComparison.Ordinal) ||
                content.Length > afterDispatch.MaximumBytes)
            {
                var code = afterDispatch.FailureCode ?? "artifact_policy_changed";
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, code, cancellationToken).ConfigureAwait(false);
                return Failure(code, context);
            }

            var bytes = content.ToArray();
            var now = DateTimeOffset.UtcNow;
            var artifact = await artifacts.CreateOrGetAsync(new McpOperatorFileArtifactCreateRequest(
                context.Request,
                acceptedAudit.AuditId,
                idempotencyId,
                afterDispatch.ReadRootFingerprint!,
                fileName,
                NormalizeArtifactMimeType(metadata.MimeType, bytes),
                bytes,
                now,
                now.Add(ArtifactLifetime)), cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, artifact.ArtifactId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/files/artifacts/{artifact.ArtifactId:D}", ToArtifactMetadata(artifact, replayed: false, context.CorrelationId));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "session_unavailable", cancellationToken).ConfigureAwait(false);
            return GatewayFailure("session_unavailable", context);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException)
        {
            if (operation is not null && !operationEnded)
                await operation.CancelAsync("operator_artifact_collect_cancelled", CancellationToken.None).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (operation is not null && !completed && !operationEnded && !cancellationToken.IsCancellationRequested)
                await operation.CancelAsync("operator_artifact_collect_ended", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> ArtifactStatusAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorFileArtifactStore artifacts,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "artifact_status", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        var artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: false, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
            return Results.NotFound();
        if (!IsArtifactAvailable(artifact, DateTimeOffset.UtcNow))
            return ArtifactUnavailable(artifact, context);
        if (!HasCurrentArtifactReadRoots(context.Decision, artifact))
            return Failure("artifact_policy_changed", context);
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;
        if (await RecheckArtifactAccessAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, artifact, cancellationToken).ConfigureAwait(false) is { } afterAdmission)
            return Failure(afterAdmission, context);

        return Results.Ok(ToArtifactMetadata(artifact, replayed: false, context.CorrelationId));
    }

    private static async Task<IResult> DownloadArtifactAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorFileArtifactStore artifacts,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "download", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        var artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: false, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
            return Results.NotFound();
        if (!IsArtifactAvailable(artifact, DateTimeOffset.UtcNow))
            return ArtifactUnavailable(artifact, context);
        if (!HasCurrentArtifactReadRoots(context.Decision, artifact))
            return Failure("artifact_policy_changed", context);
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;

        artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: true, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
            return Results.NotFound();
        if (!IsArtifactAvailable(artifact, DateTimeOffset.UtcNow) || artifact.Content is null || artifact.Content.Length != artifact.SizeBytes)
            return ArtifactUnavailable(artifact, context);
        if (await RecheckArtifactAccessAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, artifact, cancellationToken).ConfigureAwait(false) is { } afterDownload)
            return Failure(afterDownload, context);

        return Results.Ok(new McpOperatorFileArtifactDownload(
            artifact.ArtifactId,
            artifact.FileName,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.MimeType,
            Convert.ToBase64String(artifact.Content),
            artifact.ExpiresAtUtc,
            context.CorrelationId));
    }

    private static async Task<IResult> CleanupArtifactPreviewAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorFileArtifactStore artifacts,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "artifact_cleanup", cancellationToken, "preview_artifact_cleanup").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        var artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: false, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
            return Results.NotFound();
        if (artifact.DeletedAtUtc is not null)
            return ArtifactAlreadyCleaned(context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, ArtifactCleanupPayloadHash(artifactId)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorFileArtifactCleanupPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                artifact.ArtifactId,
                artifact.FileName,
                artifact.ExpiresAtUtc,
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> CleanupArtifactConfirmAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        McpOperatorFileArtifactCleanupConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorFileArtifactStore artifacts,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "artifact_cleanup", http.TraceIdentifier);
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return GatewayFailure("invalid_file_query", fallback);

        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "artifact_cleanup", cancellationToken, "confirm_artifact_cleanup").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        var artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: false, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
            return Results.NotFound();
        if (artifact.DeletedAtUtc is not null)
            return ArtifactAlreadyCleaned(context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, ArtifactCleanupPayloadHash(artifactId), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context);
        if (confirmation.IsReplay)
            return await ReplayArtifactCleanupAsync(confirmation, artifacts, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context);

        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        if (await RecheckArtifactCleanupAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, cancellationToken).ConfigureAwait(false) is { } beforeCleanup)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, beforeCleanup, cancellationToken).ConfigureAwait(false);
            return Failure(beforeCleanup, context);
        }

        artifact = await artifacts.CleanupOwnedAsync(
            artifactId,
            tenantId,
            agentId,
            context.Request.Principal,
            context.Request.McpResource!,
            context.Request.McpInstance!,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "artifact_unavailable", cancellationToken).ConfigureAwait(false);
            return Results.NotFound();
        }

        if (await RecheckArtifactCleanupAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, cancellationToken).ConfigureAwait(false) is { } afterCleanup)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, artifactId.ToString("D"), cancellationToken).ConfigureAwait(false);
            return Failure(afterCleanup, context);
        }

        await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, artifactId.ToString("D"), cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToArtifactMetadata(artifact, replayed: false, context.CorrelationId));
    }

    private static async Task<IResult> StatAsync(
        int tenantId,
        Guid agentId,
        string path,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "stat", http.TraceIdentifier);
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, "stat", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateReadPolicy(context.Decision, path, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;
        if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } beforeDispatch)
            return Failure(beforeDispatch, context);

        try
        {
            var metadata = await sessions.StatAsync(
                new ClientKey(tenantId, agentId), path, accessPolicy, cancellationToken).ConfigureAwait(false);
            if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, path, cancellationToken).ConfigureAwait(false) is { } afterDispatch)
                return Failure(afterDispatch, context);

            return Results.Ok(new McpOperatorFileStatResult(
                tenantId,
                agentId,
                metadata.FullPath,
                metadata.IsDirectory,
                metadata.SizeBytes,
                metadata.LastModifiedUtc,
                metadata.MimeType,
                "mcp-operator-policy",
                context.CorrelationId));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            return GatewayFailure("session_unavailable", context);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static Task<IResult> WriteTextPreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileWriteTextPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "write_text", http.TraceIdentifier);
        if (!TryEncodeText(request.Text, MaximumWriteTextBytes, out var bytes))
            return Task.FromResult(GatewayFailure("invalid_file_query", fallback));

        return WritePreviewAsync(
            new McpOperatorFileWriteCommand("write_text", request.Path, bytes, MaximumWriteTextBytes),
            tenantId, agentId, http, environment, presence, admission, confirmations, options, cancellationToken);
    }

    private static Task<IResult> UploadPreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileUploadPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "upload", http.TraceIdentifier);
        if (!TryDecodeBase64(request.ContentBase64, MaximumUploadBytes, out var bytes))
            return Task.FromResult(GatewayFailure("invalid_file_query", fallback));

        return WritePreviewAsync(
            new McpOperatorFileWriteCommand("upload", request.Path, bytes, MaximumUploadBytes),
            tenantId, agentId, http, environment, presence, admission, confirmations, options, cancellationToken);
    }

    private static Task<IResult> WriteTextConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileWriteTextConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "write_text", http.TraceIdentifier);
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey) ||
            !TryEncodeText(request.Text, MaximumWriteTextBytes, out var bytes))
        {
            return Task.FromResult(GatewayFailure("invalid_file_query", fallback));
        }

        return WriteConfirmAsync(
            new McpOperatorFileWriteCommand("write_text", request.Path, bytes, MaximumWriteTextBytes),
            request.PlanToken, request.IdempotencyKey,
            tenantId, agentId, http, environment, presence, admission, confirmations, options, sessions, cancellationToken);
    }

    private static Task<IResult> UploadConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileUploadConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "upload", http.TraceIdentifier);
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey) ||
            !TryDecodeBase64(request.ContentBase64, MaximumUploadBytes, out var bytes))
        {
            return Task.FromResult(GatewayFailure("invalid_file_query", fallback));
        }

        return WriteConfirmAsync(
            new McpOperatorFileWriteCommand("upload", request.Path, bytes, MaximumUploadBytes),
            request.PlanToken, request.IdempotencyKey,
            tenantId, agentId, http, environment, presence, admission, confirmations, options, sessions, cancellationToken);
    }

    private static Task<IResult> CreateDirectoryPreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileCreateDirectoryPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken) =>
        WritePreviewAsync(
            new McpOperatorFileWriteCommand("create_directory", request.Path, [], 0),
            tenantId, agentId, http, environment, presence, admission, confirmations, options, cancellationToken);

    private static Task<IResult> CreateDirectoryConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileCreateDirectoryConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "create_directory", http.TraceIdentifier);
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return Task.FromResult(GatewayFailure("invalid_file_query", fallback));

        return WriteConfirmAsync(
            new McpOperatorFileWriteCommand("create_directory", request.Path, [], 0),
            request.PlanToken, request.IdempotencyKey,
            tenantId, agentId, http, environment, presence, admission, confirmations, options, sessions, cancellationToken);
    }

    private static Task<IResult> DeletePreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileDeletePreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken) =>
        WritePreviewAsync(
            new McpOperatorFileWriteCommand("delete", request.Path, [], 0),
            tenantId, agentId, http, environment, presence, admission, confirmations, options, cancellationToken);

    private static Task<IResult> DeleteConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileDeleteConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, "delete", http.TraceIdentifier);
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return Task.FromResult(GatewayFailure("invalid_file_query", fallback));

        return WriteConfirmAsync(
            new McpOperatorFileWriteCommand("delete", request.Path, [], 0),
            request.PlanToken, request.IdempotencyKey,
            tenantId, agentId, http, environment, presence, admission, confirmations, options, sessions, cancellationToken);
    }

    private static Task<IResult> CopyPreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileCopyPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken) =>
        RelocationPreviewAsync(
            new McpOperatorFileRelocationCommand("copy", request.SourcePath, request.DestinationPath),
            tenantId, agentId, http, environment, presence, admission, confirmations, options, cancellationToken);

    private static Task<IResult> CopyConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileCopyConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken) =>
        RelocationConfirmAsync(
            new McpOperatorFileRelocationCommand("copy", request.SourcePath, request.DestinationPath),
            request.PlanToken, request.IdempotencyKey,
            tenantId, agentId, http, environment, presence, admission, confirmations, options, sessions, cancellationToken);

    private static Task<IResult> MovePreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileMovePreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken) =>
        RelocationPreviewAsync(
            new McpOperatorFileRelocationCommand("move", request.SourcePath, request.DestinationPath),
            tenantId, agentId, http, environment, presence, admission, confirmations, options, cancellationToken);

    private static Task<IResult> MoveConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorFileMoveConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken) =>
        RelocationConfirmAsync(
            new McpOperatorFileRelocationCommand("move", request.SourcePath, request.DestinationPath),
            request.PlanToken, request.IdempotencyKey,
            tenantId, agentId, http, environment, presence, admission, confirmations, options, sessions, cancellationToken);

    private static async Task<IResult> WritePreviewAsync(
        McpOperatorFileWriteCommand write,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, write.Operation, http.TraceIdentifier);
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, write.Operation, cancellationToken, $"preview_{write.Operation}").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateFileWritePolicy(context.Decision, write, out _))
            return Failure("file_policy_roots_missing", context);
        if (write.Bytes.Length > WriteLimit(context.Decision, write.MaximumBytes))
            return Failure("file_write_limit_exceeded", context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, WritePayloadHash(write)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorFileWritePreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                write.Path,
                write.Bytes.Length,
                Convert.ToHexString(SHA256.HashData(write.Bytes)).ToLowerInvariant(),
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> RelocationPreviewAsync(
        McpOperatorFileRelocationCommand relocation,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, relocation.Operation, cancellationToken, $"preview_{relocation.Operation}").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateRelocationPolicy(context.Decision, relocation, out _))
            return Failure("file_policy_roots_missing", context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, RelocationPayloadHash(relocation)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorFileRelocationPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                relocation.SourcePath,
                relocation.DestinationPath,
                RelocationPayloadHash(relocation),
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> WriteConfirmAsync(
        McpOperatorFileWriteCommand write,
        string planToken,
        string idempotencyKey,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, write.Operation, cancellationToken, $"confirm_{write.Operation}").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateFileWritePolicy(context.Decision, write, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);
        if (write.Bytes.Length > WriteLimit(context.Decision, write.MaximumBytes))
            return Failure("file_write_limit_exceeded", context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(planToken, idempotencyKey, WritePayloadHash(write), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context);
        if (confirmation.IsReplay)
            return ReplayFileWrite(confirmation, context, write);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context);

        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        try
        {
            if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, write.Path, cancellationToken, isWrite: true).ConfigureAwait(false) is { } beforeDispatch)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, beforeDispatch, cancellationToken).ConfigureAwait(false);
                return Failure(beforeDispatch, context);
            }

            if (string.Equals(write.Operation, "create_directory", StringComparison.Ordinal))
            {
                await sessions.CreateDirectoryAsync(new ClientKey(tenantId, agentId), write.Path, accessPolicy, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(write.Operation, "delete", StringComparison.Ordinal))
            {
                await sessions.DeleteAsync(new ClientKey(tenantId, agentId), write.Path, accessPolicy, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var content = new MemoryStream(write.Bytes, writable: false);
                await sessions.WriteAsync(new ClientKey(tenantId, agentId), write.Path, content, accessPolicy, cancellationToken).ConfigureAwait(false);
            }
            if (await RecheckAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, write.Path, cancellationToken, isWrite: true).ConfigureAwait(false) is { } afterDispatch)
            {
                var completedAfterRevocation = ToFileWriteResult(tenantId, agentId, write, replayed: false, context.CorrelationId);
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, WriteResultReference(write, completedAfterRevocation), cancellationToken).ConfigureAwait(false);
                return Failure(afterDispatch, context);
            }

            var response = ToFileWriteResult(tenantId, agentId, write, replayed: false, context.CorrelationId);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, WriteResultReference(write, response), cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "session_unavailable", cancellationToken).ConfigureAwait(false);
            return GatewayFailure("session_unavailable", context);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IResult> RelocationConfirmAsync(
        McpOperatorFileRelocationCommand relocation,
        string planToken,
        string idempotencyKey,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentFileGatewaySessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, relocation.Operation, http.TraceIdentifier);
        if (!HasPlanCredentials(planToken, idempotencyKey))
            return GatewayFailure("invalid_file_query", fallback);

        var admitted = await TryCreateContextAsync(
            http, environment, presence, admission, options, tenantId, agentId, relocation.Operation, cancellationToken, $"confirm_{relocation.Operation}").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!TryCreateRelocationPolicy(context.Decision, relocation, out var accessPolicy))
            return Failure("file_policy_roots_missing", context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(planToken, idempotencyKey, RelocationPayloadHash(relocation), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context);
        if (confirmation.IsReplay)
            return ReplayRelocation(confirmation, context, relocation);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context);

        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        try
        {
            if (await RecheckRelocationAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, relocation, cancellationToken).ConfigureAwait(false) is { } beforeDispatch)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, beforeDispatch, cancellationToken).ConfigureAwait(false);
                return Failure(beforeDispatch, context);
            }

            if (string.Equals(relocation.Operation, "copy", StringComparison.Ordinal))
            {
                await sessions.CopyAsync(new ClientKey(tenantId, agentId), relocation.SourcePath, relocation.DestinationPath, accessPolicy, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await sessions.MoveAsync(new ClientKey(tenantId, agentId), relocation.SourcePath, relocation.DestinationPath, accessPolicy, cancellationToken).ConfigureAwait(false);
            }

            if (await RecheckRelocationAdmissionAsync(admission, presence, options, new ClientKey(tenantId, agentId), context.Request, relocation, cancellationToken).ConfigureAwait(false) is { } afterDispatch)
            {
                var completedAfterRevocation = ToRelocationResult(tenantId, agentId, relocation, replayed: false, context.CorrelationId);
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, RelocationResultReference(relocation, completedAfterRevocation), cancellationToken).ConfigureAwait(false);
                return Failure(afterDispatch, context);
            }

            var response = ToRelocationResult(tenantId, agentId, relocation, replayed: false, context.CorrelationId);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, RelocationResultReference(relocation, response), cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "session_unavailable", cancellationToken).ConfigureAwait(false);
            return GatewayFailure("session_unavailable", context);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IResult> ReplayArtifactCollectionAsync(
        McpOperatorConfirmationAdmission confirmation,
        IMcpOperatorFileArtifactStore artifacts,
        McpOperatorFileRouteContext context,
        CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !TryReadArtifactReference(confirmation.ResultReference, out var artifactId))
        {
            return Failure("idempotency_replay_unavailable", context);
        }

        var artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: false, cancellationToken).ConfigureAwait(false);
        return artifact is null || !HasCurrentArtifactReadRoots(context.Decision, artifact)
            ? Failure("idempotency_replay_unavailable", context)
            : Results.Ok(ToArtifactMetadata(artifact, replayed: true, context.CorrelationId));
    }

    private static async Task<IResult> ReplayArtifactCleanupAsync(
        McpOperatorConfirmationAdmission confirmation,
        IMcpOperatorFileArtifactStore artifacts,
        McpOperatorFileRouteContext context,
        CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !TryReadArtifactReference(confirmation.ResultReference, out var artifactId))
        {
            return Failure("idempotency_replay_unavailable", context);
        }

        var artifact = await GetOwnedArtifactAsync(artifacts, artifactId, context, includeContent: false, cancellationToken).ConfigureAwait(false);
        return artifact is null
            ? Failure("idempotency_replay_unavailable", context)
            : Results.Ok(ToArtifactMetadata(artifact, replayed: true, context.CorrelationId));
    }

    private static Task<McpOperatorFileArtifact?> GetOwnedArtifactAsync(
        IMcpOperatorFileArtifactStore artifacts,
        Guid artifactId,
        McpOperatorFileRouteContext context,
        bool includeContent,
        CancellationToken cancellationToken) =>
        artifacts.GetOwnedAsync(
            artifactId,
            context.TenantId,
            context.AgentId,
            context.Request.Principal,
            context.Request.McpResource!,
            context.Request.McpInstance!,
            includeContent,
            cancellationToken);

    private static async Task<McpOperatorFileArtifactCollectionAdmission> RecheckArtifactCollectionAsync(
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        NetRatelAkkaMigrationOptions options,
        ClientKey client,
        McpOperatorRouteAccessRequest request,
        string path,
        CancellationToken cancellationToken)
    {
        var targetOnline = (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var evaluated = await admission.EvaluateAsync(request with
        {
            TargetOnline = targetOnline,
            CapabilityAvailable = options.IsFileBrowseAuthorityActive
        }, cancellationToken).ConfigureAwait(false);
        if (!evaluated.Decision.IsAllowed)
            return new(null, null, 0, evaluated.Decision.FailureCode ?? "target_policy_missing");
        if (!TryCreateReadPolicy(evaluated.Decision, path, out var accessPolicy) ||
            !TryGetReadRootFingerprint(evaluated.Decision, path, out var readRootFingerprint))
        {
            return new(null, null, 0, "file_policy_roots_missing");
        }

        var maximumBytes = ArtifactLimit(evaluated.Decision);
        return maximumBytes > 0
            ? new(accessPolicy, readRootFingerprint, maximumBytes, null)
            : new(null, null, 0, "file_write_limit_exceeded");
    }

    private static async Task<string?> RecheckArtifactAccessAsync(
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        NetRatelAkkaMigrationOptions options,
        ClientKey client,
        McpOperatorRouteAccessRequest request,
        McpOperatorFileArtifact artifact,
        CancellationToken cancellationToken)
    {
        var targetOnline = (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var evaluated = await admission.EvaluateAsync(request with
        {
            TargetOnline = targetOnline,
            CapabilityAvailable = options.IsFileBrowseAuthorityActive
        }, cancellationToken).ConfigureAwait(false);
        return !evaluated.Decision.IsAllowed
            ? evaluated.Decision.FailureCode ?? "target_policy_missing"
            : HasCurrentArtifactReadRoots(evaluated.Decision, artifact)
                ? null
                : "artifact_policy_changed";
    }

    private static async Task<string?> RecheckArtifactCleanupAsync(
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        NetRatelAkkaMigrationOptions options,
        ClientKey client,
        McpOperatorRouteAccessRequest request,
        CancellationToken cancellationToken)
    {
        var targetOnline = (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var evaluated = await admission.EvaluateAsync(request with
        {
            TargetOnline = targetOnline,
            CapabilityAvailable = options.IsFileBrowseAuthorityActive
        }, cancellationToken).ConfigureAwait(false);
        return evaluated.Decision.IsAllowed ? null : evaluated.Decision.FailureCode ?? "target_policy_missing";
    }

    private static int ArtifactLimit(McpOperatorDecision decision) =>
        Math.Min(MaximumArtifactBytes, decision.EffectiveConstraints?.MaxArtifactBytes ?? MaximumArtifactBytes);

    private static bool TryGetReadRootFingerprint(McpOperatorDecision decision, string? path, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (!TryNormalizeCanonicalPath(path, out _, out var pathKind))
            return false;

        var roots = NormalizeReadRoots(decision.EffectiveConstraints?.ReadRoots, pathKind);
        if (roots.Count == 0)
            return false;

        fingerprint = ReadRootFingerprint(pathKind, roots);
        return true;
    }

    private static bool HasCurrentArtifactReadRoots(McpOperatorDecision decision, McpOperatorFileArtifact artifact)
    {
        foreach (var kind in Enum.GetValues<McpOperatorFilePathKind>())
        {
            var roots = NormalizeReadRoots(decision.EffectiveConstraints?.ReadRoots, kind);
            if (roots.Count > 0 && string.Equals(ReadRootFingerprint(kind, roots), artifact.ReadRootFingerprint, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> NormalizeReadRoots(IReadOnlyList<string>? roots, McpOperatorFilePathKind expectedKind) =>
        roots is null
            ? []
            : roots.Select(root => TryNormalizePolicyRoot(root, out var normalized, out var kind) && kind == expectedKind && !IsSensitivePath(normalized, kind)
                    ? normalized
                    : null)
                .Where(root => root is not null)
                .Cast<string>()
                .Distinct(expectedKind == McpOperatorFilePathKind.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .OrderBy(root => root, StringComparer.Ordinal)
                .ToArray();

    private static string ReadRootFingerprint(McpOperatorFilePathKind kind, IReadOnlyList<string> roots) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{string.Join('\n', roots)}"))).ToLowerInvariant();

    private static string ArtifactCollectPayloadHash(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"collect:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()}")));

    private static string ArtifactCleanupPayloadHash(Guid artifactId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"artifact_cleanup:{artifactId:D}")));

    private static bool TryReadArtifactReference(string? value, out Guid artifactId) =>
        Guid.TryParseExact(value, "D", out artifactId) && artifactId != Guid.Empty;

    private static bool TryGetArtifactFileName(string? path, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        var candidate = separator >= 0 ? path[(separator + 1)..] : path;
        if (candidate is { Length: > 0 and <= 512 } && !candidate.Any(char.IsControl))
        {
            fileName = candidate;
            return true;
        }

        return false;
    }

    private static string NormalizeArtifactMimeType(string? mimeType, byte[] content) =>
        mimeType is { Length: > 0 and <= 256 } && !mimeType.Any(char.IsControl)
            ? mimeType
            : ContentType(content);

    private static bool IsArtifactAvailable(McpOperatorFileArtifact artifact, DateTimeOffset now) =>
        artifact.DeletedAtUtc is null && artifact.ExpiresAtUtc > now;

    private static McpOperatorFileArtifactMetadata ToArtifactMetadata(
        McpOperatorFileArtifact artifact,
        bool replayed,
        string correlationId) =>
        new(
            artifact.ArtifactId,
            artifact.TenantId,
            artifact.AgentId,
            artifact.FileName,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.MimeType,
            artifact.CreatedAtUtc,
            artifact.ExpiresAtUtc,
            artifact.DeletedAtUtc,
            IsArtifactAvailable(artifact, DateTimeOffset.UtcNow),
            replayed,
            correlationId);

    private static IResult ArtifactUnavailable(McpOperatorFileArtifact artifact, McpOperatorFileRouteContext context) =>
        Results.Problem(
            statusCode: StatusCodes.Status410Gone,
            title: "The requested file artifact is no longer available.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = artifact.DeletedAtUtc is null ? "artifact_expired" : "artifact_cleaned",
                ["correlationId"] = context.CorrelationId
            });

    private static IResult ArtifactAlreadyCleaned(McpOperatorFileRouteContext context) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The requested file artifact was already cleaned.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "artifact_already_cleaned",
                ["correlationId"] = context.CorrelationId
            });

    private static async Task<McpOperatorFileRouteContextResult> TryCreateContextAsync(
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        int tenantId,
        Guid agentId,
        string operation,
        CancellationToken cancellationToken,
        string? delegatedOperation = null)
    {
        var fallback = MinimalContext(tenantId, agentId, operation, http.TraceIdentifier);
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
            return Rejected("delegated_identity_required", fallback);

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
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (operatorEnvironment is null || expectedInstance is null || access is null ||
            !string.Equals(delegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(delegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(delegation.Operation, delegatedOperation ?? operation, StringComparison.Ordinal) ||
            delegation.TenantId != tenantId || delegation.AgentId != agentId ||
            string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.CorrelationId))
        {
            return Rejected("delegated_identity_invalid", fallback);
        }

        var targetOnline = (await presence.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var request = new McpOperatorRouteAccessRequest(
            operatorEnvironment.Value,
            new McpOperatorPrincipal(
                delegation.Identity.Subject,
                delegation.Identity.ClientId,
                delegation.Identity.AuthorizedParty,
                delegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            delegation.ServicePrincipal,
            delegation.Resource!,
            delegation.Instance!,
            Tool,
            operation,
            tenantId,
            agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            delegation.CorrelationId!,
            delegation.RequestId,
            targetOnline,
            CapabilityAvailable: options.IsFileBrowseAuthorityActive);
        var evaluated = await admission.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var failureContext = new McpOperatorFileRouteContext(request, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope));
        return evaluated.Decision.IsAllowed
            ? new(failureContext, null)
            : Rejected(evaluated.Decision.FailureCode ?? "target_policy_missing", failureContext);
    }

    private static async Task<IResult?> RecordAcceptedAsync(
        IMcpOperatorRouteAdmission admission,
        McpOperatorFileRouteContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, context);
        }
    }

    private static async Task<string?> RecheckAdmissionAsync(
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        NetRatelAkkaMigrationOptions options,
        ClientKey client,
        McpOperatorRouteAccessRequest request,
        string path,
        CancellationToken cancellationToken,
        bool isWrite = false)
    {
        var targetOnline = (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var evaluated = await admission.EvaluateAsync(request with
        {
            TargetOnline = targetOnline,
            CapabilityAvailable = options.IsFileBrowseAuthorityActive
        }, cancellationToken).ConfigureAwait(false);
        return !evaluated.Decision.IsAllowed
            ? evaluated.Decision.FailureCode ?? "target_policy_missing"
            : (isWrite
                ? request.Operation == "delete"
                    ? TryCreateDeletePolicy(evaluated.Decision, path, out _)
                    : TryCreateWritePolicy(evaluated.Decision, path, out _)
                : TryCreateReadPolicy(evaluated.Decision, path, out _))
                ? null
                : "file_policy_roots_missing";
    }

    private static async Task<string?> RecheckRelocationAdmissionAsync(
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        NetRatelAkkaMigrationOptions options,
        ClientKey client,
        McpOperatorRouteAccessRequest request,
        McpOperatorFileRelocationCommand relocation,
        CancellationToken cancellationToken)
    {
        var targetOnline = (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var evaluated = await admission.EvaluateAsync(request with
        {
            TargetOnline = targetOnline,
            CapabilityAvailable = options.IsFileBrowseAuthorityActive
        }, cancellationToken).ConfigureAwait(false);
        return !evaluated.Decision.IsAllowed
            ? evaluated.Decision.FailureCode ?? "target_policy_missing"
            : TryCreateRelocationPolicy(evaluated.Decision, relocation, out _)
                ? null
                : "file_policy_roots_missing";
    }

    private static bool TryCreateReadPolicy(
        McpOperatorDecision decision,
        string? path,
        out GatewayFileAccessPolicy accessPolicy)
        => TryCreateRootPolicy(decision.EffectiveConstraints?.ReadRoots, path, out accessPolicy);

    private static bool TryCreateWritePolicy(
        McpOperatorDecision decision,
        string? path,
        out GatewayFileAccessPolicy accessPolicy)
        => TryCreateRootPolicy(decision.EffectiveConstraints?.WriteRoots, path, out accessPolicy);

    private static bool TryCreateFileWritePolicy(
        McpOperatorDecision decision,
        McpOperatorFileWriteCommand write,
        out GatewayFileAccessPolicy accessPolicy) =>
        string.Equals(write.Operation, "delete", StringComparison.Ordinal)
            ? TryCreateDeletePolicy(decision, write.Path, out accessPolicy)
            : TryCreateWritePolicy(decision, write.Path, out accessPolicy);

    private static bool TryCreateDeletePolicy(
        McpOperatorDecision decision,
        string? path,
        out GatewayFileAccessPolicy accessPolicy) =>
        TryCreateRootPolicy(decision.EffectiveConstraints?.WriteRoots, path, out accessPolicy, allowRootTarget: false);

    private static bool TryCreateRelocationPolicy(
        McpOperatorDecision decision,
        McpOperatorFileRelocationCommand relocation,
        out GatewayFileMoveCopyAccessPolicy accessPolicy)
    {
        accessPolicy = default!;
        if (!TryNormalizeCanonicalPath(relocation.SourcePath, out var sourcePath, out var sourceKind) ||
            !TryNormalizeCanonicalPath(relocation.DestinationPath, out var destinationPath, out var destinationKind) ||
            sourceKind != destinationKind || PathEquals(sourcePath, destinationPath, sourceKind))
        {
            return false;
        }

        var sourceRoots = string.Equals(relocation.Operation, "copy", StringComparison.Ordinal)
            ? decision.EffectiveConstraints?.ReadRoots
            : decision.EffectiveConstraints?.WriteRoots;
        var sourceMustNotBeRoot = string.Equals(relocation.Operation, "move", StringComparison.Ordinal);
        if (!TryCreateRootPolicy(sourceRoots, relocation.SourcePath, out var sourcePolicy, allowRootTarget: !sourceMustNotBeRoot) ||
            !TryCreateRootPolicy(decision.EffectiveConstraints?.WriteRoots, relocation.DestinationPath, out var destinationPolicy, allowRootTarget: false))
        {
            return false;
        }

        accessPolicy = new GatewayFileMoveCopyAccessPolicy(sourcePolicy.AllowedRoots, destinationPolicy.AllowedRoots);
        return true;
    }

    private static bool TryCreateRootPolicy(
        IReadOnlyList<string>? roots,
        string? path,
        out GatewayFileAccessPolicy accessPolicy,
        bool allowRootTarget = true)
    {
        accessPolicy = default!;
        if (!TryNormalizeCanonicalPath(path, out var candidate, out var pathKind) || IsSensitivePath(candidate, pathKind) ||
            roots is not { Count: > 0 })
        {
            return false;
        }

        var normalizedRoots = roots
            .Select(root => TryNormalizePolicyRoot(root, out var normalized, out var rootKind) && rootKind == pathKind
                ? normalized
                : null)
            .Where(root => root is not null)
            .Cast<string>()
            .Distinct(pathKind == McpOperatorFilePathKind.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        if (!allowRootTarget && normalizedRoots.Any(root => PathEquals(root, candidate, pathKind)))
            return false;

        var matchingRoots = normalizedRoots
            .Where(root => IsAncestor(root, candidate, pathKind))
            .ToArray();
        if (matchingRoots.Length == 0)
            return false;

        accessPolicy = new GatewayFileAccessPolicy(matchingRoots);
        return true;
    }

    private static bool TryEncodeText(string? text, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (text is null || maximumBytes <= 0 || text.Length > maximumBytes)
            return false;

        try
        {
            bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(text);
            return bytes.Length <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64(string? contentBase64, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (contentBase64 is null || maximumBytes <= 0 || contentBase64.Length > MaximumUploadBase64Characters ||
            contentBase64.Length % 4 != 0)
        {
            return false;
        }

        var buffer = GC.AllocateUninitializedArray<byte>((contentBase64.Length / 4) * 3);
        if (!Convert.TryFromBase64String(contentBase64, buffer, out var bytesWritten) || bytesWritten > maximumBytes)
            return false;

        bytes = buffer[..bytesWritten].ToArray();
        return string.Equals(contentBase64, Convert.ToBase64String(bytes), StringComparison.Ordinal);
    }

    private static int WriteLimit(McpOperatorDecision decision, int maximumBytes) =>
        Math.Min(maximumBytes, decision.EffectiveConstraints?.MaxArtifactBytes ?? maximumBytes);

    private static string WritePayloadHash(McpOperatorFileWriteCommand write)
    {
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(write.Path))).ToLowerInvariant();
        var contentHash = Convert.ToHexString(SHA256.HashData(write.Bytes)).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{write.Operation}:{pathHash}:{contentHash}")));
    }

    private static string RelocationPayloadHash(McpOperatorFileRelocationCommand relocation)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relocation.SourcePath))).ToLowerInvariant();
        var destinationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relocation.DestinationPath))).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{relocation.Operation}:{sourceHash}:{destinationHash}")));
    }

    private static bool HasPlanCredentials(string? planToken, string? idempotencyKey) =>
        IsOpaqueCredential(planToken) && IsOpaqueCredential(idempotencyKey);

    private static bool IsOpaqueCredential(string? value) =>
        value is { Length: >= 32 and <= 128 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static IResult ReplayFileWrite(
        McpOperatorConfirmationAdmission confirmation,
        McpOperatorFileRouteContext context,
        McpOperatorFileWriteCommand write)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !TryReadFileWriteReceipt(confirmation.ResultReference, out var receipt) ||
            !string.Equals(receipt.Operation, write.Operation, StringComparison.Ordinal) ||
            !string.Equals(receipt.Path, write.Path, StringComparison.Ordinal) ||
            receipt.ByteCount != write.Bytes.Length ||
            !string.Equals(receipt.Sha256, Convert.ToHexString(SHA256.HashData(write.Bytes)).ToLowerInvariant(), StringComparison.Ordinal))
        {
            return Failure("idempotency_replay_unavailable", context);
        }

        return Results.Ok(new McpOperatorFileWriteResult(
            context.TenantId,
            context.AgentId,
            receipt.Path,
            receipt.ByteCount,
            receipt.Sha256,
            true,
            context.CorrelationId));
    }

    private static IResult ReplayRelocation(
        McpOperatorConfirmationAdmission confirmation,
        McpOperatorFileRouteContext context,
        McpOperatorFileRelocationCommand relocation)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !TryReadRelocationReceipt(confirmation.ResultReference, out var receipt) ||
            !string.Equals(receipt.Operation, relocation.Operation, StringComparison.Ordinal) ||
            !string.Equals(receipt.SourcePath, relocation.SourcePath, StringComparison.Ordinal) ||
            !string.Equals(receipt.DestinationPath, relocation.DestinationPath, StringComparison.Ordinal))
        {
            return Failure("idempotency_replay_unavailable", context);
        }

        return Results.Ok(ToRelocationResult(context.TenantId, context.AgentId, relocation, replayed: true, context.CorrelationId));
    }

    private static McpOperatorFileWriteResult ToFileWriteResult(
        int tenantId,
        Guid agentId,
        McpOperatorFileWriteCommand write,
        bool replayed,
        string correlationId) =>
        new(
            tenantId,
            agentId,
            write.Path,
            write.Bytes.Length,
            Convert.ToHexString(SHA256.HashData(write.Bytes)).ToLowerInvariant(),
            replayed,
            correlationId);

    private static string WriteResultReference(McpOperatorFileWriteCommand write, McpOperatorFileWriteResult response) =>
        JsonSerializer.Serialize(new McpOperatorFileWriteReceipt(write.Operation, response.Path, response.ByteCount, response.Sha256));

    private static McpOperatorFileRelocationResult ToRelocationResult(
        int tenantId,
        Guid agentId,
        McpOperatorFileRelocationCommand relocation,
        bool replayed,
        string correlationId) =>
        new(tenantId, agentId, relocation.SourcePath, relocation.DestinationPath, replayed, correlationId);

    private static string RelocationResultReference(
        McpOperatorFileRelocationCommand relocation,
        McpOperatorFileRelocationResult response) =>
        JsonSerializer.Serialize(new McpOperatorFileRelocationReceipt(relocation.Operation, response.SourcePath, response.DestinationPath));

    private static bool TryReadFileWriteReceipt(string? resultReference, out McpOperatorFileWriteReceipt receipt)
    {
        receipt = default!;
        if (string.IsNullOrWhiteSpace(resultReference) || resultReference.Length > 512)
            return false;

        try
        {
            receipt = JsonSerializer.Deserialize<McpOperatorFileWriteReceipt>(resultReference)!;
            return receipt is { Operation: "write_text" or "upload" or "create_directory" or "delete", Path.Length: > 0 and <= 4096, ByteCount: >= 0 and <= MaximumUploadBytes, Sha256.Length: 64 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadRelocationReceipt(string? resultReference, out McpOperatorFileRelocationReceipt receipt)
    {
        receipt = default!;
        if (string.IsNullOrWhiteSpace(resultReference) || resultReference.Length > 10_000)
            return false;

        try
        {
            receipt = JsonSerializer.Deserialize<McpOperatorFileRelocationReceipt>(resultReference)!;
            return receipt is { Operation: "copy" or "move", SourcePath.Length: > 0 and <= 4096, DestinationPath.Length: > 0 and <= 4096 } &&
                TryNormalizeCanonicalPath(receipt.SourcePath, out _, out var sourceKind) &&
                TryNormalizeCanonicalPath(receipt.DestinationPath, out _, out var destinationKind) &&
                sourceKind == destinationKind;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNormalizePolicyRoot(string? value, out string normalized, out McpOperatorFilePathKind kind)
    {
        // Persisted policies use forward slashes for drive roots. Convert only
        // that policy representation; requests still require canonical paths.
        if (value is { Length: >= 3 } && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/')
            value = value.Replace('/', '\\');

        return TryNormalizeCanonicalPath(value, out normalized, out kind);
    }

    private static bool TryNormalizeCanonicalPath(string? value, out string normalized, out McpOperatorFilePathKind kind)
    {
        normalized = string.Empty;
        kind = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            return false;

        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') ||
                !TryNormalizeComponents(value[1..].Split('/'), "/", out normalized))
            {
                return false;
            }

            kind = McpOperatorFilePathKind.Posix;
            return true;
        }

        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\' || value.Contains('/') ||
            !TryNormalizeComponents(value[3..].Split('\\'), value[..3], out normalized))
        {
            return false;
        }

        kind = McpOperatorFilePathKind.Windows;
        return true;
    }

    private static bool TryNormalizeComponents(string[] components, string root, out string normalized)
    {
        normalized = root;
        if (components.Any(component => string.IsNullOrEmpty(component) || component is "." or ".."))
        {
            if (components.Length == 1 && components[0].Length == 0)
                return true;
            return false;
        }

        if (components.Length == 0)
            return true;

        var separator = root == "/" ? "/" : "\\";
        normalized = root + string.Join(separator, components);
        return true;
    }

    private static bool IsAncestor(string root, string path, McpOperatorFilePathKind kind)
    {
        if (PathEquals(root, path, kind))
            return true;

        var separator = kind == McpOperatorFilePathKind.Windows ? '\\' : '/';
        var comparison = kind == McpOperatorFilePathKind.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return root.EndsWith(separator) && path.StartsWith(root, comparison) ||
            path.StartsWith(root + separator, comparison);
    }

    private static bool PathEquals(string left, string right, McpOperatorFilePathKind kind) =>
        string.Equals(left, right, kind == McpOperatorFilePathKind.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSensitivePath(string path, McpOperatorFilePathKind kind)
    {
        var separator = kind == McpOperatorFilePathKind.Windows ? '\\' : '/';
        return path.Split(separator, StringSplitOptions.RemoveEmptyEntries).Any(component =>
            component.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            component.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("secrets", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
            component.Equals("known_hosts", StringComparison.OrdinalIgnoreCase));
    }

    private static string ContentType(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
                ? "application/octet-stream"
                : "text/plain; charset=utf-8";
        }
        catch (DecoderFallbackException)
        {
            return "application/octet-stream";
        }
    }

    private static McpOperatorFileRouteContextResult Rejected(string code, McpOperatorFileRouteContext context) =>
        new(null, Failure(code, context));

    private static McpOperatorFileRouteContext MinimalContext(int tenantId, Guid agentId, string operation, string correlationId)
    {
        var access = McpOperationAccessCatalog.Find(Tool, operation)!;
        return new(null!, null!, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tenantId, agentId, operation, correlationId);
    }

    private static IResult GatewayFailure(string code, McpOperatorFileRouteContext context) => code switch
    {
        "session_unavailable" => Failure("target_offline", context),
        "capability_unavailable" => Failure("capability_unavailable", context),
        "access_denied" => Failure("file_access_denied", context),
        "file_too_large" or "artifact_too_large" => Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "The file exceeds the policy-admitted MCP transfer limit.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.CorrelationId }),
        "file_not_found" or "directory_not_found" or "path_not_found" => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "The requested policy-admitted path is not available.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.CorrelationId }),
        "directory_not_empty" => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The requested directory must be empty before it can be deleted.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.CorrelationId }),
        "source_not_file" => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The requested source must be one existing regular file.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.CorrelationId }),
        "destination_already_exists" or "invalid_destination" => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The requested copy or move destination must be a distinct new file path.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.CorrelationId }),
        _ => Results.BadRequest(new { success = false, code, correlationId = context.CorrelationId })
    };

    private static IResult Failure(string code, McpOperatorFileRouteContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_not_found" => StatusCodes.Status404NotFound,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "target_offline" or "capability_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "file_write_limit_exceeded" => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" => "capability",
            "file_policy_roots_missing" or "file_write_limit_exceeded" or "file_access_denied" or "artifact_policy_changed" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(
            statusCode: status,
            title: "MCP operator file access was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP operator file access was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer,
                    retryable = code is "target_offline" or "capability_unavailable",
                    requiredScopes = new[] { context.RequiredScope },
                    requiredOperation = $"{Tool}/{context.Operation}",
                    target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId },
                    safeDetails = "The exact delegated file request did not satisfy the current policy root and gateway safeguards."
                },
                ["correlationId"] = context.CorrelationId
            });
    }

    private enum McpOperatorFilePathKind
    {
        Posix,
        Windows
    }

    private sealed record McpOperatorFileRouteContext(
        McpOperatorRouteAccessRequest Request,
        McpOperatorDecision Decision,
        string RequiredScope,
        int TenantId,
        Guid AgentId,
        string Operation,
        string CorrelationId)
    {
        public McpOperatorFileRouteContext(McpOperatorRouteAccessRequest request, McpOperatorDecision decision, string requiredScope)
            : this(request, decision, requiredScope, request.TenantId, request.AgentId, request.Operation, request.CorrelationId)
        {
        }
    }

    private sealed record McpOperatorFileRouteContextResult(McpOperatorFileRouteContext? Context, IResult? Failure);

    private sealed record McpOperatorFileEntry(string Name, string Path, bool IsDirectory, long SizeBytes);

    private sealed record McpOperatorFileBrowseResult(
        int TenantId,
        Guid AgentId,
        string Path,
        IReadOnlyList<McpOperatorFileEntry> Entries,
        string Authority,
        string CorrelationId);

    private sealed record McpOperatorFileStatResult(
        int TenantId,
        Guid AgentId,
        string Path,
        bool IsDirectory,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc,
        string MimeType,
        string Authority,
        string CorrelationId);

    private sealed record McpOperatorFileReadResult(
        int TenantId,
        Guid AgentId,
        string Path,
        int ByteCount,
        string Sha256,
        string MimeType,
        string ContentBase64,
        string Authority,
        string CorrelationId);

    private sealed record McpOperatorFileArtifactCollectPreviewRequest(string Path);

    private sealed record McpOperatorFileArtifactCollectConfirmRequest(
        string Path,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileArtifactCleanupConfirmRequest(
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileArtifactCollectPreview(
        string PlanToken,
        string IdempotencyKey,
        DateTimeOffset ExpiresAtUtc,
        McpOperatorConfirmationClass ConfirmationClass,
        int TenantId,
        Guid AgentId,
        string Path,
        int MaximumBytes,
        string ReadRootFingerprint,
        string CorrelationId);

    private sealed record McpOperatorFileArtifactCleanupPreview(
        string PlanToken,
        string IdempotencyKey,
        DateTimeOffset ExpiresAtUtc,
        McpOperatorConfirmationClass ConfirmationClass,
        Guid ArtifactId,
        string FileName,
        DateTimeOffset ArtifactExpiresAtUtc,
        string CorrelationId);

    private sealed record McpOperatorFileArtifactMetadata(
        Guid ArtifactId,
        int TenantId,
        Guid AgentId,
        string FileName,
        long SizeBytes,
        string Sha256,
        string MimeType,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset? DeletedAtUtc,
        bool IsAvailable,
        bool Replayed,
        string CorrelationId);

    private sealed record McpOperatorFileArtifactDownload(
        Guid ArtifactId,
        string FileName,
        long SizeBytes,
        string Sha256,
        string MimeType,
        string ContentBase64,
        DateTimeOffset ExpiresAtUtc,
        string CorrelationId);

    private sealed record McpOperatorFileArtifactCollectionAdmission(
        GatewayFileAccessPolicy? AccessPolicy,
        string? ReadRootFingerprint,
        int MaximumBytes,
        string? FailureCode);

    private sealed record McpOperatorFileWriteTextPreviewRequest(string Path, string? Text);

    private sealed record McpOperatorFileWriteTextConfirmRequest(
        string Path,
        string? Text,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileUploadPreviewRequest(string Path, string? ContentBase64);

    private sealed record McpOperatorFileUploadConfirmRequest(
        string Path,
        string? ContentBase64,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileCreateDirectoryPreviewRequest(string Path);

    private sealed record McpOperatorFileCreateDirectoryConfirmRequest(
        string Path,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileDeletePreviewRequest(string Path);

    private sealed record McpOperatorFileDeleteConfirmRequest(
        string Path,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileCopyPreviewRequest(string SourcePath, string DestinationPath);

    private sealed record McpOperatorFileCopyConfirmRequest(
        string SourcePath,
        string DestinationPath,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileMovePreviewRequest(string SourcePath, string DestinationPath);

    private sealed record McpOperatorFileMoveConfirmRequest(
        string SourcePath,
        string DestinationPath,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorFileWriteCommand(string Operation, string Path, byte[] Bytes, int MaximumBytes);

    private sealed record McpOperatorFileRelocationCommand(string Operation, string SourcePath, string DestinationPath);

    private sealed record McpOperatorFileWritePreview(
        string PlanToken,
        string IdempotencyKey,
        DateTimeOffset ExpiresAtUtc,
        McpOperatorConfirmationClass ConfirmationClass,
        int TenantId,
        Guid AgentId,
        string Path,
        int ByteCount,
        string Sha256,
        string CorrelationId);

    private sealed record McpOperatorFileWriteResult(
        int TenantId,
        Guid AgentId,
        string Path,
        int ByteCount,
        string Sha256,
        bool Replayed,
        string CorrelationId);

    private sealed record McpOperatorFileWriteReceipt(string Operation, string Path, int ByteCount, string Sha256);

    private sealed record McpOperatorFileRelocationPreview(
        string PlanToken,
        string IdempotencyKey,
        DateTimeOffset ExpiresAtUtc,
        McpOperatorConfirmationClass ConfirmationClass,
        int TenantId,
        Guid AgentId,
        string SourcePath,
        string DestinationPath,
        string PayloadSha256,
        string CorrelationId);

    private sealed record McpOperatorFileRelocationResult(
        int TenantId,
        Guid AgentId,
        string SourcePath,
        string DestinationPath,
        bool Replayed,
        string CorrelationId);

    private sealed record McpOperatorFileRelocationReceipt(string Operation, string SourcePath, string DestinationPath);
}
