using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class NetRatelCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task<int> RunAsync(string[] args) => RunAsync(args, new CliRuntime());

    internal static async Task<int> RunAsync(string[] args, CliRuntime runtime)
    {
        try
        {
            var invocation = new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                Output = runtime.Out,
                Error = runtime.Error
            };
            return await BuildRoot(runtime).Parse(args).InvokeAsync(invocation).ConfigureAwait(false);
        }
        catch (CliValidationException ex)
        {
            await WriteErrorAsync(runtime, "validation_error", ex.Message, CliExitCodes.ValidationError).ConfigureAwait(false);
            return CliExitCodes.ValidationError;
        }
        catch (CliRemoteException ex)
        {
            await WriteErrorAsync(runtime, ex.Code, ex.Message, ex.StatusCode, ex.ResponseBody).ConfigureAwait(false);
            return ex.StatusCode is 401 or 403 ? CliExitCodes.AuthError : CliExitCodes.RemoteError;
        }
        catch (HttpRequestException ex)
        {
            await WriteErrorAsync(runtime, "remote_request_failed", ex.Message, CliExitCodes.RemoteError).ConfigureAwait(false);
            return CliExitCodes.RemoteError;
        }
        catch (TaskCanceledException)
        {
            await WriteErrorAsync(runtime, "remote_timeout", "Remote request timed out.", CliExitCodes.RemoteError).ConfigureAwait(false);
            return CliExitCodes.RemoteError;
        }
        catch (Exception ex)
        {
            var (code, message, exitCode, responseBody) = MapException(ex);
            await WriteErrorAsync(runtime, code, message, exitCode, responseBody).ConfigureAwait(false);
            return exitCode;
        }
    }

    internal static RootCommand BuildRoot(CliRuntime runtime)
    {
        var globals = new GlobalOptions();
        var root = new RootCommand("NetRatel orchestrator CLI for AI agents and operators.")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        globals.AddTo(root);
        root.AddCommand(BuildAuthCommand(runtime, globals));
        root.AddCommand(BuildConfigCommand(runtime, globals));
        root.AddCommand(BuildHealthCommand(runtime, globals));
        root.AddCommand(BuildLogsCommand(runtime, globals));
        root.AddCommand(BuildTenantsCommand(runtime, globals));
        root.AddCommand(BuildScriptsCommand(runtime, globals));
        root.AddCommand(BuildJobsCommand(runtime, globals));
        root.AddCommand(BuildJobRunsCommand(runtime, globals));
        root.AddCommand(BuildTasksCommand(runtime, globals));
        root.AddCommand(BuildClientsCommand(runtime, globals));
        root.AddCommand(BuildClientFilesCommand(runtime, globals));
        root.AddCommand(BuildOperatorFilesCommand(runtime, globals));
        root.AddCommand(BuildCommandsCommand(runtime, globals));
        root.AddCommand(BuildOperatorScriptsCommand(runtime, globals));
        root.AddCommand(BuildOperatorTasksCommand(runtime, globals));
        root.AddCommand(BuildOperatorRequestsCommand(runtime, globals));
        root.AddCommand(BuildOperatorTenantsCommand(runtime, globals));
        root.AddCommand(BuildOperatorOnboardingCommand(runtime, globals));
        root.AddCommand(BuildOperatorClientsCommand(runtime, globals));
        root.AddCommand(BuildOperatorNotificationsCommand(runtime, globals));
        root.AddCommand(BuildOperatorEventsCommand(runtime, globals));
        root.AddCommand(BuildOperatorConnectivityCommand(runtime, globals));
        root.AddCommand(BuildOperatorTerminalCommand(runtime, globals));
        root.AddCommand(BuildTerminalCommand(runtime, globals));
        root.AddCommand(BuildRequestsCommand(runtime, globals));
        root.AddCommand(BuildSecretsCommand(runtime, globals));
        root.AddCommand(BuildSearchCommand(runtime, globals));
        root.AddCommand(BuildTelemetryCommand(runtime, globals));
        root.AddCommand(BuildConnectivityCommand(runtime, globals));
        root.AddCommand(BuildNotificationsCommand(runtime, globals));
        root.AddCommand(BuildEventsCommand(runtime, globals));
        root.AddCommand(BuildSystemCommand(runtime, globals));
        root.AddCommand(BuildRawCommand(runtime, globals));
        return root;
    }

    private static Command BuildCommandsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var commands = new Command("commands", "Run a policy-bounded V2 one-shot command through preview and explicit confirmation.");

        var availability = new Command("availability", "Check command-gateway availability for one exact tenant and agent target.");
        var (availabilityTenant, availabilityAgent) = AddCommandTargetOptions(availability);
        availability.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            CommandPath(ctx.ParseResult.GetValueForOption(availabilityTenant), ctx.ParseResult.GetValueForOption(availabilityAgent), "availability")));
        commands.AddCommand(availability);

        var preview = new Command("preview", "Preview one bounded command. The response contains the plan token and idempotency key needed by execute.");
        var previewOptions = AddCommandExecutionOptions(preview);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            CommandPath(ctx.ParseResult.GetValueForOption(previewOptions.TenantId), ctx.ParseResult.GetValueForOption(previewOptions.AgentId), "preview"),
            CommandExecutionBody(ctx, previewOptions)));
        commands.AddCommand(preview);

        var execute = new Command("execute", "Dispatch a previously previewed command only when --confirm is supplied.");
        var executeOptions = AddCommandExecutionOptions(execute);
        var planToken = RequiredOption("--plan-token", "Opaque plan token returned by commands preview.");
        var idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by commands preview.");
        var confirm = new Option<bool>("--confirm") { Description = "Confirm the destructive command dispatch." };
        execute.AddOption(planToken);
        execute.AddOption(idempotencyKey);
        execute.AddOption(confirm);
        execute.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(confirm))
            {
                await WriteJsonAsync(runtime, new
                {
                    confirmationRequired = true,
                    nextAction = "Review the preview and rerun with --confirm using its plan token and idempotency key."
                }, ctx, globals).ConfigureAwait(false);
                return;
            }

            var body = JsonSerializer.Serialize(new
            {
                shell = ctx.ParseResult.GetValueForOption(executeOptions.Shell),
                command = ctx.ParseResult.GetValueForOption(executeOptions.Command),
                workingDirectory = ctx.ParseResult.GetValueForOption(executeOptions.WorkingDirectory),
                environmentReferences = ctx.ParseResult.GetValueForOption(executeOptions.EnvironmentReferences),
                timeoutSeconds = ctx.ParseResult.GetValueForOption(executeOptions.TimeoutSeconds),
                maximumOutputBytes = ctx.ParseResult.GetValueForOption(executeOptions.MaximumOutputBytes),
                planToken = ctx.ParseResult.GetValueForOption(planToken),
                idempotencyKey = ctx.ParseResult.GetValueForOption(idempotencyKey)
            });
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                CommandPath(ctx.ParseResult.GetValueForOption(executeOptions.TenantId), ctx.ParseResult.GetValueForOption(executeOptions.AgentId), "confirm"),
                body).ConfigureAwait(false);
        });
        commands.AddCommand(execute);

        var get = new Command("get", "Get the content-free lifecycle status of one owned command.");
        var (getTenant, getAgent) = AddCommandTargetOptions(get);
        var getCommandId = new Argument<string>("command-id");
        get.AddArgument(getCommandId);
        get.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            CommandPath(ctx.ParseResult.GetValueForOption(getTenant), ctx.ParseResult.GetValueForOption(getAgent), Escape(ctx.ParseResult.GetValueForArgument(getCommandId)))));
        commands.AddCommand(get);

        var cancel = new Command("cancel", "Request cancellation of one owned command only when --confirm is supplied.");
        var (cancelTenant, cancelAgent) = AddCommandTargetOptions(cancel);
        var cancelCommandId = new Argument<string>("command-id");
        var cancelConfirm = new Option<bool>("--confirm") { Description = "Confirm the cancellation request." };
        cancel.AddArgument(cancelCommandId);
        cancel.AddOption(cancelConfirm);
        cancel.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(cancelConfirm))
            {
                await WriteJsonAsync(runtime, new
                {
                    confirmationRequired = true,
                    nextAction = "Rerun commands cancel with --confirm to request cancellation."
                }, ctx, globals).ConfigureAwait(false);
                return;
            }

            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                CommandPath(ctx.ParseResult.GetValueForOption(cancelTenant), ctx.ParseResult.GetValueForOption(cancelAgent),
                    $"{Escape(ctx.ParseResult.GetValueForArgument(cancelCommandId))}/cancel")).ConfigureAwait(false);
        });
        commands.AddCommand(cancel);

        return commands;
    }

    private static (Option<int> TenantId, Option<Guid> AgentId) AddCommandTargetOptions(Command command)
    {
        var tenantId = new Option<int>("--tenant-id") { Description = "Exact tenant ID.",  Required = true };
        var agentId = new Option<Guid>("--agent-id") { Description = "Exact V2 agent ID.",  Required = true };
        command.AddOption(tenantId);
        command.AddOption(agentId);
        return (tenantId, agentId);
    }

    private static Command BuildOperatorScriptsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var scripts = new Command("scripts-v2", "Manage caller-owned, policy-bounded Production script definitions through hash previews, confirmation, idempotency, and ETags.");

        var list = new Command("list", "List only script definitions owned by this authenticated operator identity.");
        var (listTenant, listAgent) = AddCommandTargetOptions(list);
        list.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, ScriptV2Path(ctx.ParseResult.GetValueForOption(listTenant), ctx.ParseResult.GetValueForOption(listAgent), string.Empty)));
        scripts.AddCommand(list);

        foreach (var (name, suffix, description) in new[]
        {
            ("get", "", "Get one owned script, including its content, at the current ETag revision."),
            ("params", "/params", "Get the typed parameter schema for one owned script.")
        })
        {
            var command = new Command(name, description);
            var (tenantId, agentId) = AddCommandTargetOptions(command);
            var scriptId = new Argument<long>("script-id");
            command.AddArgument(scriptId);
            command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
                ScriptV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), $"/{ctx.ParseResult.GetValueForArgument(scriptId)}{suffix}")));
            scripts.AddCommand(command);
        }

        var validate = new Command("validate", "Validate a typed script draft and current target policy without creating a script or plan.");
        var validateDraft = AddScriptDraftOptions(validate);
        validate.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            ScriptV2Path(ctx.ParseResult.GetValueForOption(validateDraft.TenantId), ctx.ParseResult.GetValueForOption(validateDraft.AgentId), "/validate"), ScriptDraftBody(ctx, validateDraft).ToJsonString(JsonOptions)));
        scripts.AddCommand(validate);

        AddScriptMutationCommands(scripts, runtime, globals, "create", requiresExistingRevision: false, hasManifestOnlyBody: false);
        AddScriptMutationCommands(scripts, runtime, globals, "update", requiresExistingRevision: true, hasManifestOnlyBody: false);
        AddScriptMutationCommands(scripts, runtime, globals, "parse_manifest", requiresExistingRevision: true, hasManifestOnlyBody: true);
        AddScriptMutationCommands(scripts, runtime, globals, "delete", requiresExistingRevision: true, hasManifestOnlyBody: false);
        AddScriptRunCommands(scripts, runtime, globals);
        return scripts;
    }

    private static void AddScriptMutationCommands(Command scripts, CliRuntime runtime, GlobalOptions globals, string action, bool requiresExistingRevision, bool hasManifestOnlyBody)
    {
        var preview = new Command($"preview-{action.Replace('_', '-')}", $"Preview the exact Production script {action} operation and obtain plan credentials.");
        var previewOptions = AddScriptMutationOptions(preview, requiresExistingRevision, hasManifestOnlyBody, includePlanCredentials: false, includeConfirm: false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            ScriptV2Path(ctx.ParseResult.GetValueForOption(previewOptions.TenantId), ctx.ParseResult.GetValueForOption(previewOptions.AgentId), $"/preview/{action}"),
            ScriptMutationBody(ctx, previewOptions).ToJsonString(JsonOptions)));
        scripts.AddCommand(preview);

        var confirm = new Command(action.Replace('_', '-'), $"Perform the previously previewed {action} only with --confirm and unchanged opaque plan credentials.");
        var confirmOptions = AddScriptMutationOptions(confirm, requiresExistingRevision, hasManifestOnlyBody, includePlanCredentials: true, includeConfirm: true);
        confirm.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(confirmOptions.Confirm!))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = $"Run scripts-v2 preview-{action.Replace('_', '-')} first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                ScriptV2Path(ctx.ParseResult.GetValueForOption(confirmOptions.TenantId), ctx.ParseResult.GetValueForOption(confirmOptions.AgentId), $"/confirm/{action}"),
                ScriptMutationBody(ctx, confirmOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        scripts.AddCommand(confirm);
    }

    private static void AddScriptRunCommands(Command scripts, CliRuntime runtime, GlobalOptions globals)
    {
        var preview = new Command("preview-run", "Preview one exact script revision and typed parameters for a single target run.");
        var previewOptions = AddScriptRunOptions(preview, includePlanCredentials: false, includeConfirm: false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            ScriptV2Path(ctx.ParseResult.GetValueForOption(previewOptions.TenantId), ctx.ParseResult.GetValueForOption(previewOptions.AgentId), $"/{ctx.ParseResult.GetValueForOption(previewOptions.ScriptId)}/runs/preview"),
            ScriptRunBody(ctx, previewOptions).ToJsonString(JsonOptions)));
        scripts.AddCommand(preview);

        var run = new Command("run", "Dispatch one previewed script revision only with --confirm and unchanged opaque plan credentials.");
        var runOptions = AddScriptRunOptions(run, includePlanCredentials: true, includeConfirm: true);
        run.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(runOptions.Confirm!))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = "Run scripts-v2 preview-run first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                ScriptV2Path(ctx.ParseResult.GetValueForOption(runOptions.TenantId), ctx.ParseResult.GetValueForOption(runOptions.AgentId), $"/{ctx.ParseResult.GetValueForOption(runOptions.ScriptId)}/runs/confirm"),
                ScriptRunBody(ctx, runOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        scripts.AddCommand(run);
    }

    private static ScriptDraftOptions AddScriptDraftOptions(Command command)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var name = RequiredOption("--name", "Bounded script name.");
        var description = RequiredOption("--description", "Bounded reviewed script description.");
        var shell = RequiredOption("--shell", "Policy-allowlisted runtime: sh, bash, powershell, or pwsh.");
        var content = RequiredOption("--content", "UTF-8 script content. It is sent to the API but never printed by this CLI.");
        var contentHash = new Option<string?>("--content-hash") { Description = "Optional SHA-256 hash of --content. If omitted the CLI calculates it." };
        var parametersJson = new Option<string>("--parameters-json") { Description = "JSON array of typed parameter definitions; secret_reference parameters name references only.", DefaultValueFactory = _ => "[]" };
        var timeout = new Option<int>("--timeout-seconds") { Description = "Policy-bounded timeout in seconds.",  Required = true };
        var workingDirectory = RequiredOption("--working-directory", "Exact policy-allowlisted working directory.");
        var sideEffects = new Option<int[]>("--side-effect") { Description = "Reviewed side effect: 1 read_only, 2 filesystem_write, 3 service_control, 4 network_access, 5 process_execution.",  Required = true, Arity = ArgumentArity.OneOrMore };
        var manifestJson = new Option<string?>("--manifest-json") { Description = "Optional JSON object manifest." };
        command.AddOption(name); command.AddOption(description); command.AddOption(shell); command.AddOption(content); command.AddOption(contentHash);
        command.AddOption(parametersJson); command.AddOption(timeout); command.AddOption(workingDirectory); command.AddOption(sideEffects); command.AddOption(manifestJson);
        return new ScriptDraftOptions(tenantId, agentId, name, description, shell, content, contentHash, parametersJson, timeout, workingDirectory, sideEffects, manifestJson);
    }

    private static ScriptMutationOptions AddScriptMutationOptions(Command command, bool requiresExistingRevision, bool hasManifestOnlyBody, bool includePlanCredentials, bool includeConfirm)
    {
        ScriptDraftOptions? draft = null;
        Option<int> tenantId;
        Option<Guid> agentId;
        if (hasManifestOnlyBody || command.Name.EndsWith("delete", StringComparison.Ordinal))
        {
            (tenantId, agentId) = AddCommandTargetOptions(command);
        }
        else
        {
            draft = AddScriptDraftOptions(command);
            tenantId = draft.TenantId;
            agentId = draft.AgentId;
        }
        Option<long>? scriptId = null;
        Option<long>? expectedVersion = null;
        if (requiresExistingRevision)
        {
            scriptId = new Option<long>("--script-id") { Description = "Owned script ID.",  Required = true };
            expectedVersion = new Option<long>("--expected-version") { Description = "Current ETag version; stale writes are rejected.",  Required = true };
            command.AddOption(scriptId); command.AddOption(expectedVersion);
        }
        Option<string>? manifest = null;
        if (hasManifestOnlyBody)
        {
            manifest = RequiredOption("--manifest-json", "Replacement JSON object manifest.");
            command.AddOption(manifest);
        }
        Option<string>? planToken = null;
        Option<string>? idempotencyKey = null;
        if (includePlanCredentials)
        {
            planToken = RequiredOption("--plan-token", "Opaque plan token returned by the matching preview.");
            idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by the matching preview.");
            command.AddOption(planToken); command.AddOption(idempotencyKey);
        }
        Option<bool>? confirm = null;
        if (includeConfirm)
        {
            confirm = new Option<bool>("--confirm") { Description = "Confirm the script mutation." };
            command.AddOption(confirm);
        }
        return new ScriptMutationOptions(tenantId, agentId, draft, scriptId, expectedVersion, manifest, planToken, idempotencyKey, confirm);
    }

    private static ScriptRunOptions AddScriptRunOptions(Command command, bool includePlanCredentials, bool includeConfirm)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var scriptId = new Option<long>("--script-id") { Description = "Owned script ID.",  Required = true };
        var version = new Option<long>("--version") { Description = "Exact current script version.",  Required = true };
        var contentHash = RequiredOption("--content-hash", "Exact current script content SHA-256 hash.");
        var parametersJson = new Option<string>("--parameters-json") { Description = "JSON object of typed parameter values. Secret parameters accept named references only.", DefaultValueFactory = _ => "{}" };
        command.AddOption(scriptId); command.AddOption(version); command.AddOption(contentHash); command.AddOption(parametersJson);
        Option<string>? planToken = null; Option<string>? idempotencyKey = null; Option<bool>? confirm = null;
        if (includePlanCredentials)
        {
            planToken = RequiredOption("--plan-token", "Opaque plan token returned by preview-run.");
            idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by preview-run.");
            command.AddOption(planToken); command.AddOption(idempotencyKey);
        }
        if (includeConfirm) { confirm = new Option<bool>("--confirm") { Description = "Confirm the script run." }; command.AddOption(confirm); }
        return new ScriptRunOptions(tenantId, agentId, scriptId, version, contentHash, parametersJson, planToken, idempotencyKey, confirm);
    }

    private static JsonObject ScriptDraftBody(CliInvocationContext context, ScriptDraftOptions options)
    {
        var content = context.ParseResult.GetValueForOption(options.Content) ?? throw new CliValidationException("--content is required.");
        var suppliedHash = context.ParseResult.GetValueForOption(options.ContentHash);
        var hash = string.IsNullOrWhiteSpace(suppliedHash) ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))) : suppliedHash.Trim();
        var body = new JsonObject
        {
            ["name"] = context.ParseResult.GetValueForOption(options.Name),
            ["description"] = context.ParseResult.GetValueForOption(options.Description),
            ["shellType"] = context.ParseResult.GetValueForOption(options.Shell),
            ["content"] = content,
            ["contentHash"] = hash,
            ["parameters"] = RequiredJsonNode(context.ParseResult.GetValueForOption(options.ParametersJson) ?? "", JsonValueKind.Array, "--parameters-json"),
            ["timeoutSeconds"] = context.ParseResult.GetValueForOption(options.TimeoutSeconds),
            ["workingDirectory"] = context.ParseResult.GetValueForOption(options.WorkingDirectory),
            ["declaredSideEffects"] = new JsonArray((context.ParseResult.GetValueForOption(options.SideEffects) ?? []).Select(value => JsonValue.Create(value)).ToArray())
        };
        var manifest = context.ParseResult.GetValueForOption(options.ManifestJson);
        if (!string.IsNullOrWhiteSpace(manifest)) body["manifestJson"] = manifest;
        return body;
    }

    private static JsonObject ScriptMutationBody(CliInvocationContext context, ScriptMutationOptions options)
    {
        var body = new JsonObject();
        if (options.Draft is { } draft) body["script"] = ScriptDraftBody(context, draft);
        if (options.ScriptId is not null) body["scriptId"] = context.ParseResult.GetValueForOption(options.ScriptId);
        if (options.ExpectedVersion is not null) body["expectedVersion"] = context.ParseResult.GetValueForOption(options.ExpectedVersion);
        if (options.ManifestJson is not null) body["manifestJson"] = context.ParseResult.GetValueForOption(options.ManifestJson);
        if (options.PlanToken is not null) body["planToken"] = context.ParseResult.GetValueForOption(options.PlanToken);
        if (options.IdempotencyKey is not null) body["idempotencyKey"] = context.ParseResult.GetValueForOption(options.IdempotencyKey);
        return body;
    }

    private static JsonObject ScriptRunBody(CliInvocationContext context, ScriptRunOptions options)
    {
        var body = new JsonObject
        {
            ["version"] = context.ParseResult.GetValueForOption(options.Version),
            ["contentHash"] = context.ParseResult.GetValueForOption(options.ContentHash),
            ["parameters"] = RequiredJsonNode(context.ParseResult.GetValueForOption(options.ParametersJson) ?? "", JsonValueKind.Object, "--parameters-json")
        };
        if (options.PlanToken is not null) body["planToken"] = context.ParseResult.GetValueForOption(options.PlanToken);
        if (options.IdempotencyKey is not null) body["idempotencyKey"] = context.ParseResult.GetValueForOption(options.IdempotencyKey);
        return body;
    }

    private static JsonNode RequiredJsonNode(string value, JsonValueKind expectedKind, string option)
    {
        try
        {
            var node = JsonNode.Parse(value) ?? throw new CliValidationException($"{option} must contain JSON.");
            if ((expectedKind == JsonValueKind.Array && node is not JsonArray) || (expectedKind == JsonValueKind.Object && node is not JsonObject))
                throw new CliValidationException($"{option} must be a JSON {expectedKind.ToString().ToLowerInvariant()}.");
            return node;
        }
        catch (JsonException exception)
        {
            throw new CliValidationException($"{option} must contain valid JSON: {exception.Message}");
        }
    }

    private static string ScriptV2Path(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/scripts{suffix}";

    private sealed record ScriptDraftOptions(Option<int> TenantId, Option<Guid> AgentId, Option<string> Name, Option<string> Description, Option<string> Shell, Option<string> Content, Option<string?> ContentHash, Option<string> ParametersJson, Option<int> TimeoutSeconds, Option<string> WorkingDirectory, Option<int[]> SideEffects, Option<string?> ManifestJson);
    private sealed record ScriptMutationOptions(Option<int> TenantId, Option<Guid> AgentId, ScriptDraftOptions? Draft, Option<long>? ScriptId, Option<long>? ExpectedVersion, Option<string>? ManifestJson, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);
    private sealed record ScriptRunOptions(Option<int> TenantId, Option<Guid> AgentId, Option<long> ScriptId, Option<long> Version, Option<string> ContentHash, Option<string> ParametersJson, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);

    private static CommandExecutionOptions AddCommandExecutionOptions(Command command)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var shell = RequiredOption("--shell", "Policy-allowed shell, for example bash or pwsh.");
        var commandText = RequiredOption("--command", "Command text. It is sent only after confirmation and is never included in CLI output.");
        var workingDirectory = RequiredOption("--working-directory", "Policy-allowed working directory.");
        var environmentReferences = new Option<string[]>("--environment-reference") { Description = "Name of an environment value already present on the target; values are never accepted.",            Arity = ArgumentArity.ZeroOrMore
        };
        var timeoutSeconds = new Option<int?>("--timeout-seconds") { Description = "Optional timeout constrained by policy." };
        var maximumOutputBytes = new Option<int?>("--maximum-output-bytes") { Description = "Optional output bound constrained by policy." };
        command.AddOption(shell);
        command.AddOption(commandText);
        command.AddOption(workingDirectory);
        command.AddOption(environmentReferences);
        command.AddOption(timeoutSeconds);
        command.AddOption(maximumOutputBytes);
        return new CommandExecutionOptions(tenantId, agentId, shell, commandText, workingDirectory, environmentReferences, timeoutSeconds, maximumOutputBytes);
    }

    private static Option<string> RequiredOption(string name, string description) => new(name) { Description = description, Required = true };

    private static string CommandExecutionBody(CliInvocationContext context, CommandExecutionOptions options) => JsonSerializer.Serialize(new
    {
        shell = context.ParseResult.GetValueForOption(options.Shell),
        command = context.ParseResult.GetValueForOption(options.Command),
        workingDirectory = context.ParseResult.GetValueForOption(options.WorkingDirectory),
        environmentReferences = context.ParseResult.GetValueForOption(options.EnvironmentReferences),
        timeoutSeconds = context.ParseResult.GetValueForOption(options.TimeoutSeconds),
        maximumOutputBytes = context.ParseResult.GetValueForOption(options.MaximumOutputBytes)
    });

    private static string CommandPath(int tenantId, Guid agentId, string suffix) =>
        $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/commands/{suffix}";

    private sealed record CommandExecutionOptions(
        Option<int> TenantId,
        Option<Guid> AgentId,
        Option<string> Shell,
        Option<string> Command,
        Option<string> WorkingDirectory,
        Option<string[]> EnvironmentReferences,
        Option<int?> TimeoutSeconds,
        Option<int?> MaximumOutputBytes);

    private static Command BuildOperatorTasksCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var tasks = new Command("tasks-v2", "Operate caller-owned, policy-bounded Production tasks through preview, confirmation, idempotency, and owned-result routes.");
        tasks.AddCommand(BuildOperatorTaskListCommand(runtime, globals, "list", "List only tasks owned by the authenticated operator identity.", string.Empty));
        tasks.AddCommand(BuildOperatorTaskListCommand(runtime, globals, "recent", "List recent tasks owned by the authenticated operator identity.", "/recent"));

        var get = new Command("get", "Get one owned task with its redacted bounded result summary.");
        var (getTenant, getAgent) = AddCommandTargetOptions(get);
        var getTask = new Argument<long>("task-id") { Description = "Positive owned task ID." };
        get.AddArgument(getTask);
        get.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            TaskV2Path(ctx.ParseResult.GetValueForOption(getTenant), ctx.ParseResult.GetValueForOption(getAgent), $"/{ctx.ParseResult.GetValueForArgument(getTask)}")));
        tasks.AddCommand(get);

        tasks.AddCommand(BuildOperatorTaskLogsCommand(runtime, globals, "logs", "Read bounded, redacted logs for one owned task.", false));
        tasks.AddCommand(BuildOperatorTaskLogsCommand(runtime, globals, "logs-by-request", "Read bounded, redacted logs using one owned request identifier.", true));
        AddOperatorTaskCommandMutations(tasks, runtime, globals);
        AddOperatorTaskScriptMutations(tasks, runtime, globals);
        AddOperatorTaskCancelMutations(tasks, runtime, globals);
        return tasks;
    }

    private static Command BuildOperatorTaskListCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string suffix)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var state = new Option<string?>("--state") { Description = "Optional exact state: Pending, Processing, CancelRequested, Completed, Failed, or Cancelled." };
        var since = new Option<DateTimeOffset?>("--since-utc") { Description = "Optional inclusive ISO-8601 lower timestamp bound." };
        var limit = new Option<int?>("--limit") { Description = "Optional result limit from 1 through 100." };
        command.AddOption(state); command.AddOption(since); command.AddOption(limit);
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            TaskV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), suffix) +
            Query(ctx, (state, "state"), (since, "sinceUtc"), (limit, "limit"))));
        return command;
    }

    private static Command BuildOperatorTaskLogsCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, bool byRequest)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        Argument<long>? taskId = null;
        Option<string>? requestId = null;
        if (byRequest)
        {
            requestId = RequiredOption("--request-id", "Exact owned task request identifier.");
            command.AddOption(requestId);
        }
        else
        {
            taskId = new Argument<long>("task-id") { Description = "Positive owned task ID." };
            command.AddArgument(taskId);
        }
        var sinceId = new Option<long?>("--since-id") { Description = "Optional exclusive log sequence cursor." };
        var stream = new Option<string?>("--stream") { Description = "Optional stream: all, stdout, or stderr." };
        var limit = new Option<int?>("--limit") { Description = "Optional log limit from 1 through 100." };
        command.AddOption(sinceId); command.AddOption(stream); command.AddOption(limit);
        command.SetHandler(ctx =>
        {
            var suffix = byRequest ? "/logs" : $"/{ctx.ParseResult.GetValueForArgument(taskId!)}/logs";
            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                TaskV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), suffix) +
                Query(ctx, (requestId!, "requestId"), (sinceId, "sinceId"), (stream, "stream"), (limit, "limit")));
        });
        return command;
    }

    private static void AddOperatorTaskCommandMutations(Command tasks, CliRuntime runtime, GlobalOptions globals)
    {
        AddOperatorTaskMutationPair(tasks, runtime, globals, "create-command", "create_command", "Preview a bounded one-shot command task.", "Create a previewed command task only with --confirm.", AddTaskCommandOptions, TaskCommandBody);
    }

    private static void AddOperatorTaskScriptMutations(Command tasks, CliRuntime runtime, GlobalOptions globals)
    {
        AddOperatorTaskMutationPair(tasks, runtime, globals, "run-library-script", "run_library_script", "Preview one exact owned script revision as a task.", "Run a previewed script task only with --confirm.", AddTaskScriptOptions, TaskScriptBody);
    }

    private static void AddOperatorTaskCancelMutations(Command tasks, CliRuntime runtime, GlobalOptions globals)
    {
        AddOperatorTaskMutationPair(tasks, runtime, globals, "cancel", "cancel", "Preview cancellation of one owned non-terminal task.", "Request cancellation only with --confirm.", AddTaskCancelOptions, TaskCancelBody);
    }

    private static void AddOperatorTaskMutationPair<TOptions>(Command tasks, CliRuntime runtime, GlobalOptions globals, string commandName, string action, string previewDescription, string confirmDescription, Func<Command, bool, TOptions> addOptions, Func<CliInvocationContext, TOptions, JsonObject> body)
    {
        var preview = new Command($"preview-{commandName}", previewDescription);
        var previewOptions = addOptions(preview, false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            TaskV2Path(TaskTenant(ctx, previewOptions), TaskAgent(ctx, previewOptions), $"/preview/{action}"), body(ctx, previewOptions).ToJsonString(JsonOptions)));
        tasks.AddCommand(preview);

        var confirm = new Command(commandName, confirmDescription);
        var confirmOptions = addOptions(confirm, true);
        confirm.SetHandler(async ctx =>
        {
            if (!TaskConfirmed(ctx, confirmOptions))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = $"Run tasks-v2 preview-{commandName} first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                TaskV2Path(TaskTenant(ctx, confirmOptions), TaskAgent(ctx, confirmOptions), $"/confirm/{action}"), body(ctx, confirmOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        tasks.AddCommand(confirm);
    }

    private static TaskCommandOptions AddTaskCommandOptions(Command command, bool includeConfirmation)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var shell = RequiredOption("--shell", "Policy-allowed shell, for example bash or pwsh.");
        var commandText = RequiredOption("--command", "Command text. It is transmitted but never printed by this CLI.");
        var workingDirectory = RequiredOption("--working-directory", "Exact policy-allowed working directory.");
        var timeoutSeconds = new Option<int>("--timeout-seconds") { Description = "Positive timeout constrained by policy.",  Required = true };
        var maximumOutputBytes = new Option<int>("--maximum-output-bytes") { Description = "Positive output bound constrained by policy.",  Required = true };
        var environmentReferences = new Option<string[]>("--environment-reference") { Description = "Name of a target-local environment reference; values are never accepted.",  Arity = ArgumentArity.ZeroOrMore };
        command.AddOption(shell); command.AddOption(commandText); command.AddOption(workingDirectory); command.AddOption(timeoutSeconds); command.AddOption(maximumOutputBytes); command.AddOption(environmentReferences);
        var confirmation = AddTaskConfirmationOptions(command, includeConfirmation);
        return new(tenantId, agentId, shell, commandText, workingDirectory, timeoutSeconds, maximumOutputBytes, environmentReferences, confirmation.PlanToken, confirmation.IdempotencyKey, confirmation.Confirm);
    }

    private static TaskScriptOptions AddTaskScriptOptions(Command command, bool includeConfirmation)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var scriptId = new Option<long>("--script-id") { Description = "Owned script ID.",  Required = true };
        var version = new Option<long>("--version") { Description = "Exact current script version.",  Required = true };
        var contentHash = RequiredOption("--content-hash", "Exact current script content SHA-256 hash.");
        var parameters = new Option<string>("--parameters-json") { Description = "JSON object of typed script parameter values; secret values are references only.", DefaultValueFactory = _ => "{}" };
        command.AddOption(scriptId); command.AddOption(version); command.AddOption(contentHash); command.AddOption(parameters);
        var confirmation = AddTaskConfirmationOptions(command, includeConfirmation);
        return new(tenantId, agentId, scriptId, version, contentHash, parameters, confirmation.PlanToken, confirmation.IdempotencyKey, confirmation.Confirm);
    }

    private static TaskCancelOptions AddTaskCancelOptions(Command command, bool includeConfirmation)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var taskId = new Option<long>("--task-id") { Description = "Owned non-terminal task ID.",  Required = true };
        command.AddOption(taskId);
        var confirmation = AddTaskConfirmationOptions(command, includeConfirmation);
        return new(tenantId, agentId, taskId, confirmation.PlanToken, confirmation.IdempotencyKey, confirmation.Confirm);
    }

    private static (Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm) AddTaskConfirmationOptions(Command command, bool includeConfirmation)
    {
        if (!includeConfirmation) return (null, null, null);
        var planToken = RequiredOption("--plan-token", "Opaque plan token returned by the matching preview.");
        var idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by the matching preview.");
        var confirm = new Option<bool>("--confirm") { Description = "Confirm this task mutation." };
        command.AddOption(planToken); command.AddOption(idempotencyKey); command.AddOption(confirm);
        return (planToken, idempotencyKey, confirm);
    }

    private static JsonObject TaskCommandBody(CliInvocationContext context, TaskCommandOptions options) => TaskMutationBody(context, options, new JsonObject
    {
        ["command"] = new JsonObject
        {
            ["shell"] = context.ParseResult.GetValueForOption(options.Shell),
            ["command"] = context.ParseResult.GetValueForOption(options.Command),
            ["workingDirectory"] = context.ParseResult.GetValueForOption(options.WorkingDirectory),
            ["timeoutSeconds"] = context.ParseResult.GetValueForOption(options.TimeoutSeconds),
            ["maximumOutputBytes"] = context.ParseResult.GetValueForOption(options.MaximumOutputBytes),
            ["environmentReferences"] = new JsonArray((context.ParseResult.GetValueForOption(options.EnvironmentReferences) ?? []).Select(value => JsonValue.Create(value)).ToArray())
        }
    });

    private static JsonObject TaskScriptBody(CliInvocationContext context, TaskScriptOptions options) => TaskMutationBody(context, options, new JsonObject
    {
        ["script"] = new JsonObject
        {
            ["scriptId"] = context.ParseResult.GetValueForOption(options.ScriptId),
            ["version"] = context.ParseResult.GetValueForOption(options.Version),
            ["contentHash"] = context.ParseResult.GetValueForOption(options.ContentHash),
            ["parameters"] = RequiredJsonNode(context.ParseResult.GetValueForOption(options.ParametersJson) ?? "", JsonValueKind.Object, "--parameters-json")
        }
    });

    private static JsonObject TaskCancelBody(CliInvocationContext context, TaskCancelOptions options) => TaskMutationBody(context, options, new JsonObject { ["taskId"] = context.ParseResult.GetValueForOption(options.TaskId) });

    private static JsonObject TaskMutationBody<TOptions>(CliInvocationContext context, TOptions options, JsonObject body)
    {
        if (TaskPlanToken(options) is { } planToken) body["planToken"] = context.ParseResult.GetValueForOption(planToken);
        if (TaskIdempotencyKey(options) is { } idempotencyKey) body["idempotencyKey"] = context.ParseResult.GetValueForOption(idempotencyKey);
        return body;
    }

    private static int TaskTenant<TOptions>(CliInvocationContext context, TOptions options) => context.ParseResult.GetValueForOption(TaskTarget(options).TenantId);
    private static Guid TaskAgent<TOptions>(CliInvocationContext context, TOptions options) => context.ParseResult.GetValueForOption(TaskTarget(options).AgentId);
    private static bool TaskConfirmed<TOptions>(CliInvocationContext context, TOptions options) => TaskConfirm(options) is { } confirm && context.ParseResult.GetValueForOption(confirm);
    private static (Option<int> TenantId, Option<Guid> AgentId) TaskTarget<TOptions>(TOptions options) => options switch
    {
        TaskCommandOptions command => (command.TenantId, command.AgentId),
        TaskScriptOptions script => (script.TenantId, script.AgentId),
        TaskCancelOptions cancel => (cancel.TenantId, cancel.AgentId),
        _ => throw new ArgumentOutOfRangeException(nameof(options))
    };
    private static Option<string>? TaskPlanToken<TOptions>(TOptions options) => options switch { TaskCommandOptions command => command.PlanToken, TaskScriptOptions script => script.PlanToken, TaskCancelOptions cancel => cancel.PlanToken, _ => null };
    private static Option<string>? TaskIdempotencyKey<TOptions>(TOptions options) => options switch { TaskCommandOptions command => command.IdempotencyKey, TaskScriptOptions script => script.IdempotencyKey, TaskCancelOptions cancel => cancel.IdempotencyKey, _ => null };
    private static Option<bool>? TaskConfirm<TOptions>(TOptions options) => options switch { TaskCommandOptions command => command.Confirm, TaskScriptOptions script => script.Confirm, TaskCancelOptions cancel => cancel.Confirm, _ => null };
    private static string TaskV2Path(int tenantId, Guid agentId, string suffix) => $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/tasks{suffix}";

    private sealed record TaskCommandOptions(Option<int> TenantId, Option<Guid> AgentId, Option<string> Shell, Option<string> Command, Option<string> WorkingDirectory, Option<int> TimeoutSeconds, Option<int> MaximumOutputBytes, Option<string[]> EnvironmentReferences, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);
    private sealed record TaskScriptOptions(Option<int> TenantId, Option<Guid> AgentId, Option<long> ScriptId, Option<long> Version, Option<string> ContentHash, Option<string> ParametersJson, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);
    private sealed record TaskCancelOptions(Option<int> TenantId, Option<Guid> AgentId, Option<long> TaskId, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);

    private static Command BuildOperatorRequestsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var requests = new Command("requests-v2", "Operate caller-owned, job-linked Production requests through preview, confirmation, ETags, idempotency, and bounded owned-result routes.");
        var list = new Command("list", "List only bounded requests owned by the authenticated operator identity.");
        var (listTenant, listAgent) = AddCommandTargetOptions(list);
        var state = new Option<string?>("--state") { Description = "Optional exact state: Pending, Claimed, Completed, Failed, or Cancelled." };
        var jobId = new Option<long?>("--job-id") { Description = "Optional owned job identifier filter." };
        var since = new Option<DateTimeOffset?>("--since-utc") { Description = "Optional inclusive ISO-8601 lower timestamp bound." };
        var limit = new Option<int?>("--limit") { Description = "Optional result limit from 1 through 100." };
        list.AddOption(state); list.AddOption(jobId); list.AddOption(since); list.AddOption(limit);
        list.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            RequestV2Path(ctx.ParseResult.GetValueForOption(listTenant), ctx.ParseResult.GetValueForOption(listAgent), string.Empty) +
            Query(ctx, (state, "state"), (jobId, "jobId"), (since, "sinceUtc"), (limit, "limit"))));
        requests.AddCommand(list);

        var get = new Command("get", "Get one caller-owned request with its bounded redacted result summary.");
        var (getTenant, getAgent) = AddCommandTargetOptions(get);
        var requestId = new Argument<int>("request-id") { Description = "Positive owned request identifier." };
        get.AddArgument(requestId);
        get.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            RequestV2Path(ctx.ParseResult.GetValueForOption(getTenant), ctx.ParseResult.GetValueForOption(getAgent), $"/{ctx.ParseResult.GetValueForArgument(requestId)}")));
        requests.AddCommand(get);

        foreach (var action in new[] { "create", "update", "claim", "complete", "fail", "cancel" })
            AddOperatorRequestMutationPair(requests, runtime, globals, action);
        return requests;
    }

    private static void AddOperatorRequestMutationPair(Command requests, CliRuntime runtime, GlobalOptions globals, string action)
    {
        var preview = new Command($"preview-{action}", $"Preview the exact owned Production request {action} operation.");
        var previewOptions = AddRequestMutationOptions(preview, action, false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            RequestV2Path(ctx.ParseResult.GetValueForOption(previewOptions.TenantId), ctx.ParseResult.GetValueForOption(previewOptions.AgentId), $"/preview/{action}"), RequestMutationBody(ctx, previewOptions).ToJsonString(JsonOptions)));
        requests.AddCommand(preview);

        var confirm = new Command(action, $"Perform the previewed request {action} only with --confirm and unchanged plan credentials.");
        var confirmOptions = AddRequestMutationOptions(confirm, action, true);
        confirm.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(confirmOptions.Confirm!))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = $"Run requests-v2 preview-{action} first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                RequestV2Path(ctx.ParseResult.GetValueForOption(confirmOptions.TenantId), ctx.ParseResult.GetValueForOption(confirmOptions.AgentId), $"/confirm/{action}"), RequestMutationBody(ctx, confirmOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        requests.AddCommand(confirm);
    }

    private static RequestMutationOptions AddRequestMutationOptions(Command command, string action, bool includeConfirmation)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        Option<long>? requestId = null; Option<long>? expectedVersion = null; Option<long>? jobId = null;
        Option<string>? summary = null; Option<string?>? resultSummary = null; Option<string>? claimReference = null;
        if (action == "create")
        {
            jobId = new Option<long>("--job-id") { Description = "Exact caller-owned job identifier.",  Required = true };
            summary = RequiredOption("--summary", "Bounded request summary. Secret values are never accepted.");
            command.AddOption(jobId); command.AddOption(summary);
        }
        else
        {
            requestId = new Option<long>("--request-id") { Description = "Positive caller-owned request identifier.",  Required = true };
            expectedVersion = new Option<long>("--expected-version") { Description = "Current ETag revision required for optimistic concurrency.",  Required = true };
            command.AddOption(requestId); command.AddOption(expectedVersion);
            if (action == "update")
            {
                summary = RequiredOption("--summary", "Bounded replacement request summary.");
                command.AddOption(summary);
            }
            else if (action == "claim")
            {
                claimReference = RequiredOption("--claim-reference", "Bounded worker or workflow reference; it is stored only as a hash.");
                command.AddOption(claimReference);
            }
            else
            {
                resultSummary = action == "cancel"
                    ? new Option<string?>("--result-summary") { Description = "Optional bounded cancellation summary; it is redacted before persistence." }
                    : new Option<string?>("--result-summary") { Description = "Bounded result summary; it is redacted before persistence.",  Required = true };
                command.AddOption(resultSummary);
            }
        }
        Option<string>? planToken = null; Option<string>? idempotencyKey = null; Option<bool>? confirm = null;
        if (includeConfirmation)
        {
            planToken = RequiredOption("--plan-token", "Opaque plan token returned by the matching preview.");
            idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by the matching preview.");
            confirm = new Option<bool>("--confirm") { Description = "Confirm this request lifecycle mutation." };
            command.AddOption(planToken); command.AddOption(idempotencyKey); command.AddOption(confirm);
        }
        return new(tenantId, agentId, requestId, expectedVersion, jobId, summary, resultSummary, claimReference, planToken, idempotencyKey, confirm);
    }

    private static JsonObject RequestMutationBody(CliInvocationContext context, RequestMutationOptions options)
    {
        var body = new JsonObject();
        if (options.RequestId is not null) body["requestId"] = context.ParseResult.GetValueForOption(options.RequestId);
        if (options.ExpectedVersion is not null) body["expectedVersion"] = context.ParseResult.GetValueForOption(options.ExpectedVersion);
        if (options.JobId is not null) body["jobId"] = context.ParseResult.GetValueForOption(options.JobId);
        if (options.Summary is not null) body["summary"] = context.ParseResult.GetValueForOption(options.Summary);
        if (options.ResultSummary is not null) body["resultSummary"] = context.ParseResult.GetValueForOption(options.ResultSummary);
        if (options.ClaimReference is not null) body["claimReference"] = context.ParseResult.GetValueForOption(options.ClaimReference);
        if (options.PlanToken is not null) body["planToken"] = context.ParseResult.GetValueForOption(options.PlanToken);
        if (options.IdempotencyKey is not null) body["idempotencyKey"] = context.ParseResult.GetValueForOption(options.IdempotencyKey);
        return body;
    }

    private static string RequestV2Path(int tenantId, Guid agentId, string suffix) => $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/requests{suffix}";

    private sealed record RequestMutationOptions(Option<int> TenantId, Option<Guid> AgentId, Option<long>? RequestId, Option<long>? ExpectedVersion, Option<long>? JobId, Option<string>? Summary, Option<string?>? ResultSummary, Option<string>? ClaimReference, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);

    private static Command BuildOperatorTenantsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var tenants = new Command("tenants-v2", "Manage Production tenants through the global control plane, explicit ControlPlane policy, ETags, impact preview, confirmation, idempotency, and audit.");

        var list = new Command("list", "List tenants through the authorized control plane.");
        var cursor = new Option<int?>("--cursor") { Description = "Optional non-negative page cursor." };
        var limit = new Option<int?>("--limit") { Description = "Optional result limit from 1 through 100." };
        list.AddOption(cursor); list.AddOption(limit);
        list.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            TenantV2Path + Query(ctx, (cursor, "cursor"), (limit, "limit"))));
        tenants.AddCommand(list);

        var get = new Command("get", "Get one tenant and its current ETag revision.");
        var tenantId = new Argument<int>("tenant-id") { Description = "Positive tenant identifier." };
        get.AddArgument(tenantId);
        get.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            $"{TenantV2Path}/{ctx.ParseResult.GetValueForArgument(tenantId)}"));
        tenants.AddCommand(get);

        foreach (var action in new[] { "create", "update", "delete" })
            AddOperatorTenantMutationPair(tenants, runtime, globals, action);
        return tenants;
    }

    private static void AddOperatorTenantMutationPair(Command tenants, CliRuntime runtime, GlobalOptions globals, string action)
    {
        var preview = new Command($"preview-{action}", $"Preview the exact global tenant {action} operation; no tenant is changed.");
        var previewOptions = AddTenantMutationOptions(preview, action, includeConfirmation: false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            $"{TenantV2Path}/preview/{action}", TenantMutationBody(ctx, previewOptions).ToJsonString(JsonOptions)));
        tenants.AddCommand(preview);

        var confirm = new Command(action, $"Perform the previewed global tenant {action} only with --confirm and unchanged plan credentials.");
        var confirmOptions = AddTenantMutationOptions(confirm, action, includeConfirmation: true);
        confirm.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(confirmOptions.Confirm!))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = $"Run tenants-v2 preview-{action} first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                $"{TenantV2Path}/confirm/{action}", TenantMutationBody(ctx, confirmOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        tenants.AddCommand(confirm);
    }

    private static TenantMutationOptions AddTenantMutationOptions(Command command, string action, bool includeConfirmation)
    {
        Option<int>? tenantId = null; Option<long>? expectedVersion = null;
        Option<string>? name = null; Option<string[]>? domains = null; Option<string>? autoUpdate = null;
        Option<string?>? description = null; Option<string?>? location = null; Option<string?>? contactPerson = null; Option<string?>? contactEmail = null;
        Option<string?>? autoUpdateChannel = null; Option<string?>? autoUpdateTargetVersion = null; Option<string>? cascade = null;
        if (action is "create" or "update")
        {
            if (action == "update")
            {
                tenantId = new Option<int>("--tenant-id") { Description = "Tenant identifier to replace.",  Required = true };
                expectedVersion = new Option<long>("--expected-version") { Description = "Current tenant ETag version; stale writes are rejected.",  Required = true };
                command.AddOption(tenantId); command.AddOption(expectedVersion);
            }
            name = RequiredOption("--name", "Tenant display name.");
            domains = new Option<string[]>("--domain") { Description = "Tenant domain; repeat this option for more domains.",  Arity = ArgumentArity.ZeroOrMore };
            autoUpdate = RequiredOption("--auto-update", "Explicit true or false automatic-update setting.");
            description = new Option<string?>("--description") { Description = "Optional bounded tenant description." };
            location = new Option<string?>("--location") { Description = "Optional bounded tenant location." };
            contactPerson = new Option<string?>("--contact-person") { Description = "Optional bounded tenant contact name." };
            contactEmail = new Option<string?>("--contact-email") { Description = "Optional bounded tenant contact email." };
            autoUpdateChannel = new Option<string?>("--auto-update-channel") { Description = "Optional stable or prerelease update channel." };
            autoUpdateTargetVersion = new Option<string?>("--auto-update-target-version") { Description = "Optional target semantic version." };
            command.AddOption(name); command.AddOption(domains); command.AddOption(autoUpdate); command.AddOption(description); command.AddOption(location);
            command.AddOption(contactPerson); command.AddOption(contactEmail); command.AddOption(autoUpdateChannel); command.AddOption(autoUpdateTargetVersion);
        }
        else
        {
            tenantId = new Option<int>("--tenant-id") { Description = "Tenant identifier to delete.",  Required = true };
            expectedVersion = new Option<long>("--expected-version") { Description = "Current tenant ETag version; stale deletes are rejected.",  Required = true };
            cascade = RequiredOption("--cascade", "Explicit true or false cascade selection. Only false is currently supported.");
            command.AddOption(tenantId); command.AddOption(expectedVersion); command.AddOption(cascade);
        }

        Option<string>? planToken = null; Option<string>? idempotencyKey = null; Option<bool>? confirm = null;
        if (includeConfirmation)
        {
            planToken = RequiredOption("--plan-token", "Opaque plan token returned by the matching preview.");
            idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by the matching preview.");
            confirm = new Option<bool>("--confirm") { Description = "Confirm the tenant mutation." };
            command.AddOption(planToken); command.AddOption(idempotencyKey); command.AddOption(confirm);
        }
        return new(tenantId, expectedVersion, name, domains, autoUpdate, description, location, contactPerson, contactEmail, autoUpdateChannel, autoUpdateTargetVersion, cascade, planToken, idempotencyKey, confirm);
    }

    private static JsonObject TenantMutationBody(CliInvocationContext context, TenantMutationOptions options)
    {
        var body = new JsonObject();
        if (options.TenantId is not null) body["tenantId"] = context.ParseResult.GetValueForOption(options.TenantId);
        if (options.ExpectedVersion is not null) body["expectedVersion"] = context.ParseResult.GetValueForOption(options.ExpectedVersion);
        if (options.Name is not null)
        {
            body["name"] = context.ParseResult.GetValueForOption(options.Name);
            body["domains"] = new JsonArray((context.ParseResult.GetValueForOption(options.Domains!) ?? []).Select(domain => JsonValue.Create(domain)).ToArray());
            body["autoUpdate"] = RequiredBoolean(context.ParseResult.GetValueForOption(options.AutoUpdate!), "--auto-update");
            if (options.Description is not null) body["description"] = context.ParseResult.GetValueForOption(options.Description);
            if (options.Location is not null) body["location"] = context.ParseResult.GetValueForOption(options.Location);
            if (options.ContactPerson is not null) body["contactPerson"] = context.ParseResult.GetValueForOption(options.ContactPerson);
            if (options.ContactEmail is not null) body["contactEmail"] = context.ParseResult.GetValueForOption(options.ContactEmail);
            if (options.AutoUpdateChannel is not null) body["autoUpdateChannel"] = context.ParseResult.GetValueForOption(options.AutoUpdateChannel);
            if (options.AutoUpdateTargetVersion is not null) body["autoUpdateTargetVersion"] = context.ParseResult.GetValueForOption(options.AutoUpdateTargetVersion);
        }
        if (options.Cascade is not null) body["cascade"] = RequiredBoolean(context.ParseResult.GetValueForOption(options.Cascade), "--cascade");
        if (options.PlanToken is not null) body["planToken"] = context.ParseResult.GetValueForOption(options.PlanToken);
        if (options.IdempotencyKey is not null) body["idempotencyKey"] = context.ParseResult.GetValueForOption(options.IdempotencyKey);
        return body;
    }

    private static bool RequiredBoolean(string? value, string option) => bool.TryParse(value, out var parsed)
        ? parsed
        : throw new CliValidationException($"{option} must be true or false.");

    private const string TenantV2Path = "/api/v2/mcp/operator/tenants";
    private sealed record TenantMutationOptions(Option<int>? TenantId, Option<long>? ExpectedVersion, Option<string>? Name, Option<string[]>? Domains, Option<string>? AutoUpdate, Option<string?>? Description, Option<string?>? Location, Option<string?>? ContactPerson, Option<string?>? ContactEmail, Option<string?>? AutoUpdateChannel, Option<string?>? AutoUpdateTargetVersion, Option<string>? Cascade, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);

    private static Command BuildOperatorOnboardingCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var onboarding = new Command("onboarding-v2", "Manage tenant-scoped Production enrollment codes before a client has an agent ID. Raw enrollment codes are returned only by the first confirmed create response.");

        var collateral = new Command("collateral", "Get the current installer collateral metadata for one tenant and runtime.");
        var collateralTenant = RequiredTenantIdOption();
        var collateralRuntime = RequiredOption("--runtime", "Installer runtime: linux-x64 or win-x64.");
        collateral.AddOption(collateralTenant); collateral.AddOption(collateralRuntime);
        collateral.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(collateralTenant))}/collateral/{Escape(RequiredRuntime(ctx.ParseResult.GetValueForOption(collateralRuntime)))}"));
        onboarding.AddCommand(collateral);

        var download = new Command("download", "Download bounded collateral as base64 JSON; the tenant policy and 64 KiB MCP transfer cap both apply.");
        var downloadTenant = RequiredTenantIdOption();
        var downloadRuntime = RequiredOption("--runtime", "Installer runtime: linux-x64 or win-x64.");
        download.AddOption(downloadTenant); download.AddOption(downloadRuntime);
        download.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(downloadTenant))}/collateral/{Escape(RequiredRuntime(ctx.ParseResult.GetValueForOption(downloadRuntime)))}/download"));
        onboarding.AddCommand(download);

        var list = new Command("list", "List only tenant-scoped Production enrollment metadata; no raw code is returned.");
        var listTenant = RequiredTenantIdOption();
        var status = new Option<string?>("--status") { Description = "Optional active, expired, revoked, or all filter." };
        var cursor = new Option<long?>("--cursor") { Description = "Optional non-negative enrollment page cursor." };
        var limit = new Option<int?>("--limit") { Description = "Optional result limit from 1 through 100." };
        list.AddOption(listTenant); list.AddOption(status); list.AddOption(cursor); list.AddOption(limit);
        list.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            OnboardingV2Path(ctx.ParseResult.GetValueForOption(listTenant)) + "/enrollments" + Query(ctx, (status, "status"), (cursor, "cursor"), (limit, "limit"))));
        onboarding.AddCommand(list);

        var get = new Command("get", "Get tenant-scoped enrollment metadata; no raw code is returned.");
        var getTenant = RequiredTenantIdOption();
        var enrollmentCodeId = new Argument<Guid>("enrollment-code-id") { Description = "Enrollment code UUID." };
        get.AddOption(getTenant); get.AddArgument(enrollmentCodeId);
        get.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(getTenant))}/enrollments/{ctx.ParseResult.GetValueForArgument(enrollmentCodeId):D}"));
        onboarding.AddCommand(get);

        AddOnboardingCreateCommands(onboarding, runtime, globals);
        AddOnboardingRevokeCommands(onboarding, runtime, globals);
        return onboarding;
    }

    private static void AddOnboardingCreateCommands(Command onboarding, CliRuntime runtime, GlobalOptions globals)
    {
        var preview = new Command("preview-create", "Preview one policy-bounded enrollment-code issuance and obtain opaque plan credentials.");
        var previewOptions = AddOnboardingCreateOptions(preview, includeConfirmation: false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(previewOptions.TenantId))}/preview/create-enrollment",
            OnboardingCreateBody(ctx, previewOptions).ToJsonString(JsonOptions)));
        onboarding.AddCommand(preview);

        var confirm = new Command("create", "Create one short-lived enrollment code only with --confirm and unchanged preview credentials.");
        var confirmOptions = AddOnboardingCreateOptions(confirm, includeConfirmation: true);
        confirm.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(confirmOptions.Confirm!))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = "Run onboarding-v2 preview-create first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(confirmOptions.TenantId))}/confirm/create-enrollment",
                OnboardingCreateBody(ctx, confirmOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        onboarding.AddCommand(confirm);
    }

    private static void AddOnboardingRevokeCommands(Command onboarding, CliRuntime runtime, GlobalOptions globals)
    {
        var preview = new Command("preview-revoke", "Preview revocation of one tenant-scoped enrollment code and obtain opaque plan credentials.");
        var previewOptions = AddOnboardingRevokeOptions(preview, includeConfirmation: false);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(previewOptions.TenantId))}/preview/revoke-enrollment",
            OnboardingRevokeBody(ctx, previewOptions).ToJsonString(JsonOptions)));
        onboarding.AddCommand(preview);

        var confirm = new Command("revoke", "Revoke one enrollment code only with --confirm and unchanged preview credentials.");
        var confirmOptions = AddOnboardingRevokeOptions(confirm, includeConfirmation: true);
        confirm.SetHandler(async ctx =>
        {
            if (!ctx.ParseResult.GetValueForOption(confirmOptions.Confirm!))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = "Run onboarding-v2 preview-revoke first, then rerun with --confirm and its plan credentials." }, ctx, globals).ConfigureAwait(false);
                return;
            }
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                $"{OnboardingV2Path(ctx.ParseResult.GetValueForOption(confirmOptions.TenantId))}/confirm/revoke-enrollment",
                OnboardingRevokeBody(ctx, confirmOptions).ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        onboarding.AddCommand(confirm);
    }

    private static OnboardingCreateOptions AddOnboardingCreateOptions(Command command, bool includeConfirmation)
    {
        var tenantId = RequiredTenantIdOption();
        var runtime = RequiredOption("--runtime", "Installer runtime: linux-x64 or win-x64.");
        var validForMinutes = new Option<int>("--valid-for-minutes") { Description = "Lifetime from 5 through 10 minutes, further bounded by policy.",  Required = true };
        var maxUses = new Option<int>("--max-uses") { Description = "Maximum uses from 1 through 10, further bounded by policy.",  Required = true };
        command.AddOption(tenantId); command.AddOption(runtime); command.AddOption(validForMinutes); command.AddOption(maxUses);
        Option<string>? planToken = null; Option<string>? idempotencyKey = null; Option<bool>? confirm = null;
        if (includeConfirmation)
        {
            planToken = RequiredOption("--plan-token", "Opaque plan token returned by onboarding-v2 preview-create.");
            idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by onboarding-v2 preview-create.");
            confirm = new Option<bool>("--confirm") { Description = "Confirm issuance of the enrollment code." };
            command.AddOption(planToken); command.AddOption(idempotencyKey); command.AddOption(confirm);
        }
        return new(tenantId, runtime, validForMinutes, maxUses, planToken, idempotencyKey, confirm);
    }

    private static OnboardingRevokeOptions AddOnboardingRevokeOptions(Command command, bool includeConfirmation)
    {
        var tenantId = RequiredTenantIdOption();
        var enrollmentCodeId = new Option<Guid>("--enrollment-code-id") { Description = "Enrollment code UUID to revoke.",  Required = true };
        command.AddOption(tenantId); command.AddOption(enrollmentCodeId);
        Option<string>? planToken = null; Option<string>? idempotencyKey = null; Option<bool>? confirm = null;
        if (includeConfirmation)
        {
            planToken = RequiredOption("--plan-token", "Opaque plan token returned by onboarding-v2 preview-revoke.");
            idempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by onboarding-v2 preview-revoke.");
            confirm = new Option<bool>("--confirm") { Description = "Confirm revocation of the enrollment code." };
            command.AddOption(planToken); command.AddOption(idempotencyKey); command.AddOption(confirm);
        }
        return new(tenantId, enrollmentCodeId, planToken, idempotencyKey, confirm);
    }

    private static JsonObject OnboardingCreateBody(CliInvocationContext context, OnboardingCreateOptions options)
    {
        var validForMinutes = context.ParseResult.GetValueForOption(options.ValidForMinutes);
        var maxUses = context.ParseResult.GetValueForOption(options.MaxUses);
        if (validForMinutes is < 5 or > 10 || maxUses is < 1 or > 10)
            throw new CliValidationException("--valid-for-minutes must be 5 through 10 and --max-uses must be 1 through 10.");
        var body = new JsonObject
        {
            ["runtime"] = RequiredRuntime(context.ParseResult.GetValueForOption(options.Runtime)),
            ["validForMinutes"] = validForMinutes,
            ["maxUses"] = maxUses
        };
        if (options.PlanToken is not null) body["planToken"] = context.ParseResult.GetValueForOption(options.PlanToken);
        if (options.IdempotencyKey is not null) body["idempotencyKey"] = context.ParseResult.GetValueForOption(options.IdempotencyKey);
        return body;
    }

    private static JsonObject OnboardingRevokeBody(CliInvocationContext context, OnboardingRevokeOptions options)
    {
        var enrollmentCodeId = context.ParseResult.GetValueForOption(options.EnrollmentCodeId);
        if (enrollmentCodeId == Guid.Empty) throw new CliValidationException("--enrollment-code-id must be a non-empty UUID.");
        var body = new JsonObject { ["enrollmentCodeId"] = enrollmentCodeId.ToString("D") };
        if (options.PlanToken is not null) body["planToken"] = context.ParseResult.GetValueForOption(options.PlanToken);
        if (options.IdempotencyKey is not null) body["idempotencyKey"] = context.ParseResult.GetValueForOption(options.IdempotencyKey);
        return body;
    }

    private static Option<int> RequiredTenantIdOption() => new("--tenant-id") { Description = "Positive tenant identifier.", Required = true };
    private static string RequiredRuntime(string? runtime) => runtime is "linux-x64" or "win-x64"
        ? runtime
        : throw new CliValidationException("--runtime must be linux-x64 or win-x64.");
    private static string OnboardingV2Path(int tenantId) => tenantId > 0
        ? $"{TenantV2Path}/{tenantId.ToString(CultureInfo.InvariantCulture)}/onboarding"
        : throw new CliValidationException("--tenant-id must be positive.");
    private sealed record OnboardingCreateOptions(Option<int> TenantId, Option<string> Runtime, Option<int> ValidForMinutes, Option<int> MaxUses, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);
    private sealed record OnboardingRevokeOptions(Option<int> TenantId, Option<Guid> EnrollmentCodeId, Option<string>? PlanToken, Option<string>? IdempotencyKey, Option<bool>? Confirm);

    private static Command BuildOperatorClientsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var clients = new Command("clients-v2", "Inspect one exact policy-admitted Production V2 client. These reads never invoke browser or legacy client-identity routes.");
        AddOperatorClientRead(clients, runtime, globals, "get", "Get durable administrative metadata for one persisted client.", string.Empty);
        AddOperatorClientRead(clients, runtime, globals, "presence", "Get bounded current gateway presence for one persisted client.", "/presence");
        AddOperatorClientRead(clients, runtime, globals, "capabilities", "Get bounded current gateway capabilities for one persisted client.", "/capabilities");
        AddOperatorClientRead(clients, runtime, globals, "binding", "Get the authoritative primary-client binding for one persisted client.", "/binding");
        AddOperatorClientRead(clients, runtime, globals, "telemetry", "Get one bounded current V2 gateway telemetry snapshot.", "/telemetry");
        AddOperatorClientRead(clients, runtime, globals, "update-attempts", "Get bounded redacted update lifecycle facts for one persisted client.", "/update-attempts");
        AddOperatorClientRead(clients, runtime, globals, "update-metadata", "Get durable tenant and client update-policy metadata for one persisted client.", "/update-metadata");
        clients.AddCommand(BuildOperatorClientPingCommand(runtime, globals, "preview-ping", "Preview one execute-scoped client ping and receive opaque credentials.", confirmed: false));
        clients.AddCommand(BuildOperatorClientPingCommand(runtime, globals, "confirm-ping", "Confirm the unchanged execute-scoped client ping.", confirmed: true));
        clients.AddCommand(BuildOperatorClientSoftwareUpdateCommand(runtime, globals, "preview-software-update", "Preview resuming a server-suspended automatic update and receive opaque credentials.", confirmed: false));
        clients.AddCommand(BuildOperatorClientSoftwareUpdateCommand(runtime, globals, "confirm-software-update", "Confirm resuming the unchanged server-suspended automatic update.", confirmed: true));
        clients.AddCommand(BuildOperatorClientLifecycleCommand(runtime, globals, "preview-disable", "disable", "Preview one destructive client disable and receive opaque credentials.", confirmed: false));
        clients.AddCommand(BuildOperatorClientLifecycleCommand(runtime, globals, "confirm-disable", "disable", "Confirm the unchanged destructive client disable.", confirmed: true));
        clients.AddCommand(BuildOperatorClientLifecycleCommand(runtime, globals, "preview-enable", "enable", "Preview one client enable and receive opaque credentials.", confirmed: false));
        clients.AddCommand(BuildOperatorClientLifecycleCommand(runtime, globals, "confirm-enable", "enable", "Confirm the unchanged client enable.", confirmed: true));
        clients.AddCommand(BuildOperatorClientLifecycleCommand(runtime, globals, "preview-delete", "delete", "Preview one irreversible client decommission and receive opaque credentials.", confirmed: false));
        clients.AddCommand(BuildOperatorClientLifecycleCommand(runtime, globals, "confirm-delete", "delete", "Confirm the unchanged irreversible client decommission.", confirmed: true));
        return clients;
    }

    private static void AddOperatorClientRead(Command parent, CliRuntime runtime, GlobalOptions globals, string name, string description, string suffix)
    {
        var command = new Command(name, description);
        var tenantId = RequiredTenantIdOption();
        var agentId = new Option<Guid>("--agent-id") { Description = "Persisted V2 agent UUID.",  Required = true };
        command.AddOption(tenantId);
        command.AddOption(agentId);
        command.SetHandler(ctx =>
        {
            var agent = ctx.ParseResult.GetValueForOption(agentId);
            if (agent == Guid.Empty) throw new CliValidationException("--agent-id must be a non-empty UUID.");
            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                ClientV2Path(ctx.ParseResult.GetValueForOption(tenantId), agent, suffix));
        });
        parent.AddCommand(command);
    }

    private static Command BuildOperatorClientPingCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, bool confirmed)
    {
        var command = new Command(name, description);
        var tenantId = RequiredTenantIdOption();
        var agentId = new Option<Guid>("--agent-id") { Description = "Persisted V2 agent UUID.",  Required = true };
        command.AddOption(tenantId);
        command.AddOption(agentId);
        Option<string>? planToken = null;
        Option<string>? idempotencyKey = null;
        if (confirmed)
        {
            planToken = new Option<string>("--plan-token") { Description = "Opaque credential returned by preview-ping.",  Required = true };
            idempotencyKey = new Option<string>("--idempotency-key") { Description = "Opaque credential returned by preview-ping.",  Required = true };
            command.AddOption(planToken);
            command.AddOption(idempotencyKey);
        }

        command.SetHandler(ctx =>
        {
            var tenant = ctx.ParseResult.GetValueForOption(tenantId);
            var agent = ctx.ParseResult.GetValueForOption(agentId);
            if (agent == Guid.Empty) throw new CliValidationException("--agent-id must be a non-empty UUID.");
            var body = default(string);
            if (confirmed)
            {
                var plan = ctx.ParseResult.GetValueForOption(planToken!);
                var key = ctx.ParseResult.GetValueForOption(idempotencyKey!);
                if (!IsOpaqueCredential(plan) || !IsOpaqueCredential(key)) throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
                body = JsonSerializer.Serialize(new { planToken = plan, idempotencyKey = key });
            }
            return SendAsync(runtime, globals, ctx, HttpMethod.Post, ClientV2Path(tenant, agent, $"/ping/{(confirmed ? "confirm" : "preview")}"), body);
        });
        return command;
    }

    private static Command BuildOperatorClientSoftwareUpdateCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, bool confirmed)
    {
        var command = new Command(name, description);
        var tenantId = RequiredTenantIdOption();
        var agentId = new Option<Guid>("--agent-id") { Description = "Persisted V2 agent UUID.",  Required = true };
        command.AddOption(tenantId);
        command.AddOption(agentId);
        Option<string>? planToken = null;
        Option<string>? idempotencyKey = null;
        if (confirmed)
        {
            planToken = new Option<string>("--plan-token") { Description = "Opaque credential returned by preview-software-update.",  Required = true };
            idempotencyKey = new Option<string>("--idempotency-key") { Description = "Opaque credential returned by preview-software-update.",  Required = true };
            command.AddOption(planToken);
            command.AddOption(idempotencyKey);
        }

        command.SetHandler(ctx =>
        {
            var tenant = ctx.ParseResult.GetValueForOption(tenantId);
            var agent = ctx.ParseResult.GetValueForOption(agentId);
            if (agent == Guid.Empty) throw new CliValidationException("--agent-id must be a non-empty UUID.");
            string? body = null;
            if (confirmed)
            {
                var plan = ctx.ParseResult.GetValueForOption(planToken!);
                var key = ctx.ParseResult.GetValueForOption(idempotencyKey!);
                if (!IsOpaqueCredential(plan) || !IsOpaqueCredential(key)) throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
                body = JsonSerializer.Serialize(new { planToken = plan, idempotencyKey = key });
            }
            return SendAsync(runtime, globals, ctx, HttpMethod.Post, ClientV2Path(tenant, agent, $"/software-update/{(confirmed ? "confirm" : "preview")}"), body);
        });
        return command;
    }

    private static Command BuildOperatorClientLifecycleCommand(CliRuntime runtime, GlobalOptions globals, string name, string action, string description, bool confirmed)
    {
        var command = new Command(name, description);
        var tenantId = RequiredTenantIdOption();
        var agentId = new Option<Guid>("--agent-id") { Description = "Persisted V2 agent UUID.",  Required = true };
        command.AddOption(tenantId);
        command.AddOption(agentId);
        Option<string>? reason = null;
        if (action is "disable" or "delete")
        {
            reason = new Option<string>("--reason") { Description = "Bounded non-secret lifecycle reason.",  Required = true };
            command.AddOption(reason);
        }

        Option<string>? planToken = null;
        Option<string>? idempotencyKey = null;
        if (confirmed)
        {
            planToken = new Option<string>("--plan-token") { Description = $"Opaque credential returned by preview-{action}.", Required = true };
            idempotencyKey = new Option<string>("--idempotency-key") { Description = $"Opaque credential returned by preview-{action}.", Required = true };
            command.AddOption(planToken);
            command.AddOption(idempotencyKey);
        }

        command.SetHandler(ctx =>
        {
            var tenant = ctx.ParseResult.GetValueForOption(tenantId);
            var agent = ctx.ParseResult.GetValueForOption(agentId);
            if (agent == Guid.Empty) throw new CliValidationException("--agent-id must be a non-empty UUID.");
            var disableReason = reason is null ? null : ctx.ParseResult.GetValueForOption(reason);
            if (action is "disable" or "delete" && (string.IsNullOrWhiteSpace(disableReason) || disableReason.Length > 256 || disableReason.Any(char.IsControl)))
                throw new CliValidationException("--reason must be a non-empty non-control value of at most 256 characters.");

            string? body = action is "disable" or "delete"
                ? JsonSerializer.Serialize(new { reason = disableReason!.Trim() })
                : null;
            if (confirmed)
            {
                var plan = ctx.ParseResult.GetValueForOption(planToken!);
                var key = ctx.ParseResult.GetValueForOption(idempotencyKey!);
                if (!IsOpaqueCredential(plan) || !IsOpaqueCredential(key)) throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
                body = action is "disable" or "delete"
                    ? JsonSerializer.Serialize(new { reason = disableReason!.Trim(), planToken = plan, idempotencyKey = key })
                    : JsonSerializer.Serialize(new { planToken = plan, idempotencyKey = key });
            }
            return SendAsync(runtime, globals, ctx, HttpMethod.Post, ClientV2Path(tenant, agent, $"/{action}/{(confirmed ? "confirm" : "preview")}"), body);
        });
        return command;
    }

    private static string ClientV2Path(int tenantId, Guid agentId, string suffix)
    {
        if (tenantId <= 0 || agentId == Guid.Empty) throw new CliValidationException("--tenant-id must be positive and --agent-id must be a non-empty UUID.");
        return $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/clients{suffix}";
    }

    private static Command BuildOperatorNotificationsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        const string path = "/api/v2/mcp/operator/notifications";
        var notifications = new Command("notifications-v2", "Inspect notifications owned by the signed delegated Production operator, or acknowledge bounded notification UUIDs through preview and confirmation.");
        notifications.AddCommand(GetListCommand(runtime, globals, "list", path));
        notifications.AddCommand(GetListCommand(runtime, globals, "summary", path + "/summary"));
        notifications.AddCommand(GetListCommand(runtime, globals, "unread-errors", path + "/unread-errors"));

        var get = new Command("get", "Get one notification only when it belongs to the signed delegated operator.");
        var notificationId = new Option<Guid>("--id") { Description = "Notification UUID.",  Required = true };
        get.AddOption(notificationId);
        get.SetHandler(ctx =>
        {
            var id = ctx.ParseResult.GetValueForOption(notificationId);
            if (id == Guid.Empty) throw new CliValidationException("--id must be a non-empty UUID.");
            return SendAsync(runtime, globals, ctx, HttpMethod.Get, $"{path}/{id:D}");
        });
        notifications.AddCommand(get);

        notifications.AddCommand(BuildOperatorNotificationMarkReadCommand(runtime, globals, "preview-mark-read", "Preview acknowledgement and receive opaque plan credentials.", false));
        notifications.AddCommand(BuildOperatorNotificationMarkReadCommand(runtime, globals, "confirm-mark-read", "Confirm the unchanged acknowledgement with preview credentials.", true));
        return notifications;
    }

    private static Command BuildOperatorNotificationMarkReadCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, bool confirmed)
    {
        const string path = "/api/v2/mcp/operator/notifications";
        var command = new Command(name, description);
        var ids = new Option<string>("--ids") { Description = "Comma-separated notification UUIDs (1 through 200).",  Required = true };
        command.AddOption(ids);
        Option<string>? planToken = null;
        Option<string>? idempotencyKey = null;
        if (confirmed)
        {
            planToken = new Option<string>("--plan-token") { Description = "Opaque plan token returned by preview-mark-read.",  Required = true };
            idempotencyKey = new Option<string>("--idempotency-key") { Description = "Opaque idempotency key returned by preview-mark-read.",  Required = true };
            command.AddOption(planToken);
            command.AddOption(idempotencyKey);
        }

        command.SetHandler(ctx =>
        {
            var parsed = ParseNotificationIds(ctx.ParseResult.GetValueForOption(ids));
            var body = new Dictionary<string, object?> { ["ids"] = parsed.Select(id => id.ToString("D")).ToArray() };
            if (confirmed)
            {
                var plan = ctx.ParseResult.GetValueForOption(planToken!);
                var key = ctx.ParseResult.GetValueForOption(idempotencyKey!);
                if (!IsOpaqueCredential(plan) || !IsOpaqueCredential(key)) throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
                body["planToken"] = plan;
                body["idempotencyKey"] = key;
            }
            return SendAsync(runtime, globals, ctx, HttpMethod.Post, $"{path}/{(confirmed ? "confirm" : "preview")}/mark-read", JsonSerializer.Serialize(body));
        });
        return command;
    }

    private static Command BuildOperatorEventsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        const string path = "/api/v2/mcp/operator/events";
        var events = new Command("events-v2", "Inspect bounded redacted Production event summaries and control one event through preview and confirmation.");
        events.AddCommand(GetListCommand(runtime, globals, "list", path));

        var get = new Command("get", "Get one bounded redacted event summary.");
        var eventId = new Option<Guid>("--event-id") { Description = "Persisted event UUID.",  Required = true };
        get.AddOption(eventId);
        get.SetHandler(ctx =>
        {
            var id = ctx.ParseResult.GetValueForOption(eventId);
            if (id == Guid.Empty) throw new CliValidationException("--event-id must be a non-empty UUID.");
            return SendAsync(runtime, globals, ctx, HttpMethod.Get, $"{path}/{id:D}");
        });
        events.AddCommand(get);
        events.AddCommand(BuildOperatorEventControlCommand(runtime, globals, "preview-retry", "retry", "Preview one event retry and receive opaque credentials.", false));
        events.AddCommand(BuildOperatorEventControlCommand(runtime, globals, "confirm-retry", "retry", "Confirm the unchanged event retry.", true));
        events.AddCommand(BuildOperatorEventControlCommand(runtime, globals, "preview-disable", "disable", "Preview one event disable and receive opaque credentials.", false));
        events.AddCommand(BuildOperatorEventControlCommand(runtime, globals, "confirm-disable", "disable", "Confirm the unchanged event disable.", true));
        return events;
    }

    private static Command BuildOperatorEventControlCommand(CliRuntime runtime, GlobalOptions globals, string name, string action, string description, bool confirmed)
    {
        const string path = "/api/v2/mcp/operator/events";
        var command = new Command(name, description);
        var eventId = new Option<Guid>("--event-id") { Description = "Persisted event UUID.",  Required = true };
        command.AddOption(eventId);
        Option<string>? planToken = null;
        Option<string>? idempotencyKey = null;
        if (confirmed)
        {
            planToken = new Option<string>("--plan-token") { Description = $"Opaque credential returned by preview-{action}.", Required = true };
            idempotencyKey = new Option<string>("--idempotency-key") { Description = $"Opaque credential returned by preview-{action}.", Required = true };
            command.AddOption(planToken);
            command.AddOption(idempotencyKey);
        }
        command.SetHandler(ctx =>
        {
            var id = ctx.ParseResult.GetValueForOption(eventId);
            if (id == Guid.Empty) throw new CliValidationException("--event-id must be a non-empty UUID.");
            string? body = null;
            if (confirmed)
            {
                var plan = ctx.ParseResult.GetValueForOption(planToken!);
                var key = ctx.ParseResult.GetValueForOption(idempotencyKey!);
                if (!IsOpaqueCredential(plan) || !IsOpaqueCredential(key)) throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
                body = JsonSerializer.Serialize(new { planToken = plan, idempotencyKey = key });
            }
            return SendAsync(runtime, globals, ctx, HttpMethod.Post, $"{path}/{id:D}/{action}/{(confirmed ? "confirm" : "preview")}", body);
        });
        return command;
    }

    private static Command BuildOperatorConnectivityCommand(CliRuntime runtime, GlobalOptions globals)
    {
        const string path = "/api/v2/mcp/operator/connectivity";
        var connectivity = new Command("connectivity-v2", "Inspect redacted server-owned Production connectivity state or run the bounded ExternalService M2M probe through preview and confirmation.");
        connectivity.AddCommand(GetListCommand(runtime, globals, "settings", path + "/settings"));
        connectivity.AddCommand(GetListCommand(runtime, globals, "netratel", path + "/netratel"));

        var preview = new Command("preview-test", "Preview the server-owned bounded ExternalService M2M probe and receive opaque credentials.");
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post, path + "/test/preview"));
        connectivity.AddCommand(preview);

        var confirm = new Command("confirm-test", "Confirm the unchanged server-owned bounded ExternalService M2M probe.");
        var planToken = new Option<string>("--plan-token") { Description = "Opaque credential returned by preview-test.",  Required = true };
        var idempotencyKey = new Option<string>("--idempotency-key") { Description = "Opaque credential returned by preview-test.",  Required = true };
        confirm.AddOption(planToken);
        confirm.AddOption(idempotencyKey);
        confirm.SetHandler(ctx =>
        {
            var plan = ctx.ParseResult.GetValueForOption(planToken);
            var key = ctx.ParseResult.GetValueForOption(idempotencyKey);
            if (!IsOpaqueCredential(plan) || !IsOpaqueCredential(key)) throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
            return SendAsync(runtime, globals, ctx, HttpMethod.Post, path + "/test/confirm", JsonSerializer.Serialize(new { planToken = plan, idempotencyKey = key }));
        });
        connectivity.AddCommand(confirm);
        return connectivity;
    }

    private static IReadOnlyList<Guid> ParseNotificationIds(string? raw)
    {
        var ids = (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .ToArray();
        if (ids.Length is < 1 or > 200 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new CliValidationException("--ids must contain 1 through 200 unique non-empty UUIDs separated by commas.");
        return ids;
    }

    private static bool IsOpaqueCredential(string? value) =>
        value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static Command BuildOperatorTerminalCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var terminal = new Command("terminal-v2", "Operate a policy-bounded V2 terminal; this is not a fallback to retired V1 terminal routes.");

        var availability = new Command("availability", "Check terminal gateway availability for one exact tenant and agent target.");
        var (availabilityTenant, availabilityAgent) = AddCommandTargetOptions(availability);
        availability.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            TerminalPath(ctx.ParseResult.GetValueForOption(availabilityTenant), ctx.ParseResult.GetValueForOption(availabilityAgent), "availability")));
        terminal.AddCommand(availability);

        var preview = new Command("preview-open", "Preview an owned terminal session and obtain plan credentials.");
        var previewOptions = AddTerminalOpenOptions(preview);
        preview.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            TerminalPath(ctx.ParseResult.GetValueForOption(previewOptions.TenantId), ctx.ParseResult.GetValueForOption(previewOptions.AgentId), "sessions/preview"),
            TerminalOpenBody(ctx, previewOptions)));
        terminal.AddCommand(preview);

        var open = new Command("open", "Open a terminal only when --confirm is supplied with preview credentials.");
        var openOptions = AddTerminalOpenOptions(open);
        var openPlanToken = RequiredOption("--plan-token", "Opaque plan token returned by terminal-v2 preview-open.");
        var openIdempotencyKey = RequiredOption("--idempotency-key", "Opaque idempotency key returned by terminal-v2 preview-open.");
        var openConfirm = new Option<bool>("--confirm") { Description = "Confirm the terminal open." };
        open.AddOption(openPlanToken);
        open.AddOption(openIdempotencyKey);
        open.AddOption(openConfirm);
        open.SetHandler(ctx => ConfirmTerminalOpenAsync(runtime, globals, ctx, openOptions, openPlanToken, openIdempotencyKey, openConfirm));
        terminal.AddCommand(open);

        terminal.AddCommand(BuildTerminalV2GetCommand(runtime, globals, "get", "Get the state of one owned terminal session.", HttpMethod.Get, ""));
        terminal.AddCommand(BuildTerminalV2GetCommand(runtime, globals, "diagnostics", "Read content-free diagnostics for one owned terminal session.", HttpMethod.Get, "diagnostics"));

        var input = new Command("input", "Send one bounded UTF-8 input frame to an owned terminal session.");
        var (inputTenant, inputAgent) = AddCommandTargetOptions(input);
        var inputSession = new Argument<string>("session-id");
        var inputValue = RequiredOption("--input", "Input frame; it is never printed by this CLI command.");
        input.AddArgument(inputSession);
        input.AddOption(inputValue);
        input.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Post,
            TerminalPath(ctx.ParseResult.GetValueForOption(inputTenant), ctx.ParseResult.GetValueForOption(inputAgent),
                $"sessions/{Escape(ctx.ParseResult.GetValueForArgument(inputSession))}/input"),
            JsonSerializer.Serialize(new { input = ctx.ParseResult.GetValueForOption(inputValue) })));
        terminal.AddCommand(input);

        var stream = new Command("stream-window", "Read one bounded terminal output window.");
        var (streamTenant, streamAgent) = AddCommandTargetOptions(stream);
        var streamSession = new Argument<string>("session-id");
        var windowSeconds = new Option<int?>("--window-seconds") { Description = "Optional 1–15 second output window." };
        var maxRecords = new Option<int?>("--max-records") { Description = "Optional 1–100 record bound." };
        stream.AddArgument(streamSession);
        stream.AddOption(windowSeconds);
        stream.AddOption(maxRecords);
        stream.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            TerminalPath(ctx.ParseResult.GetValueForOption(streamTenant), ctx.ParseResult.GetValueForOption(streamAgent),
                $"sessions/{Escape(ctx.ParseResult.GetValueForArgument(streamSession))}/stream-window") +
            Query(("windowSeconds", ctx.ParseResult.GetValueForOption(windowSeconds)?.ToString(CultureInfo.InvariantCulture)),
                ("maxRecords", ctx.ParseResult.GetValueForOption(maxRecords)?.ToString(CultureInfo.InvariantCulture)))));
        terminal.AddCommand(stream);

        terminal.AddCommand(BuildTerminalV2ResizeCommand(runtime, globals));
        terminal.AddCommand(BuildTerminalV2ConfirmedAction(runtime, globals, "close", "Confirm idempotent close of an owned terminal session.", "close", []));
        return terminal;
    }

    private static TerminalOpenOptions AddTerminalOpenOptions(Command command)
    {
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var shell = RequiredOption("--shell", "Policy-allowed terminal shell.");
        var workingDirectory = RequiredOption("--working-directory", "Policy-allowed terminal working directory.");
        var columns = new Option<int?>("--columns") { Description = "Optional terminal columns." };
        var rows = new Option<int?>("--rows") { Description = "Optional terminal rows." };
        command.AddOption(shell);
        command.AddOption(workingDirectory);
        command.AddOption(columns);
        command.AddOption(rows);
        return new TerminalOpenOptions(tenantId, agentId, shell, workingDirectory, columns, rows);
    }

    private static Task ConfirmTerminalOpenAsync(
        CliRuntime runtime,
        GlobalOptions globals,
        CliInvocationContext context,
        TerminalOpenOptions options,
        Option<string> planToken,
        Option<string> idempotencyKey,
        Option<bool> confirm)
    {
        if (!context.ParseResult.GetValueForOption(confirm))
            return WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = "Review terminal-v2 preview-open and rerun with --confirm." }, context, globals);

        var body = JsonSerializer.Serialize(new
        {
            shell = context.ParseResult.GetValueForOption(options.Shell),
            workingDirectory = context.ParseResult.GetValueForOption(options.WorkingDirectory),
            columns = context.ParseResult.GetValueForOption(options.Columns),
            rows = context.ParseResult.GetValueForOption(options.Rows),
            planToken = context.ParseResult.GetValueForOption(planToken),
            idempotencyKey = context.ParseResult.GetValueForOption(idempotencyKey)
        });
        return SendAsync(runtime, globals, context, HttpMethod.Post,
            TerminalPath(context.ParseResult.GetValueForOption(options.TenantId), context.ParseResult.GetValueForOption(options.AgentId), "sessions/confirm"), body);
    }

    private static Command BuildTerminalV2GetCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, HttpMethod method, string suffix)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var sessionId = new Argument<string>("session-id");
        command.AddArgument(sessionId);
        command.SetHandler(context => SendAsync(runtime, globals, context, method,
            TerminalPath(context.ParseResult.GetValueForOption(tenantId), context.ParseResult.GetValueForOption(agentId),
                $"sessions/{Escape(context.ParseResult.GetValueForArgument(sessionId))}{(suffix.Length == 0 ? string.Empty : $"/{suffix}")}")));
        return command;
    }

    private static Command BuildTerminalV2ConfirmedAction(
        CliRuntime runtime,
        GlobalOptions globals,
        string name,
        string description,
        string suffix,
        IReadOnlyList<(string OptionName, string JsonName)> fields)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var sessionId = new Argument<string>("session-id");
        var confirm = new Option<bool>("--confirm") { Description = "Confirm the terminal action." };
        command.AddArgument(sessionId);
        command.AddOption(confirm);
        var options = fields.Select(field => (Option: RequiredOption(field.OptionName, $"Required terminal action field '{field.JsonName}'."), field.JsonName)).ToArray();
        foreach (var (option, _) in options)
            command.AddOption(option);
        command.SetHandler(async context =>
        {
            if (!context.ParseResult.GetValueForOption(confirm))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = $"Rerun terminal-v2 {name} with --confirm." }, context, globals).ConfigureAwait(false);
                return;
            }

            var body = new JsonObject();
            foreach (var (option, jsonName) in options)
                body[jsonName] = context.ParseResult.GetValueForOption(option);
            await SendAsync(runtime, globals, context, HttpMethod.Post,
                TerminalPath(context.ParseResult.GetValueForOption(tenantId), context.ParseResult.GetValueForOption(agentId),
                    $"sessions/{Escape(context.ParseResult.GetValueForArgument(sessionId))}/{suffix}"),
                body.ToJsonString()).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildTerminalV2ResizeCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("resize", "Confirm a terminal resize.");
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var sessionId = new Argument<string>("session-id");
        var columns = new Option<int>("--columns") { Description = "Required terminal columns.",  Required = true };
        var rows = new Option<int>("--rows") { Description = "Required terminal rows.",  Required = true };
        var confirm = new Option<bool>("--confirm") { Description = "Confirm the terminal resize." };
        command.AddArgument(sessionId);
        command.AddOption(columns);
        command.AddOption(rows);
        command.AddOption(confirm);
        command.SetHandler(async context =>
        {
            if (!context.ParseResult.GetValueForOption(confirm))
            {
                await WriteJsonAsync(runtime, new { confirmationRequired = true, nextAction = "Rerun terminal-v2 resize with --confirm." }, context, globals).ConfigureAwait(false);
                return;
            }

            await SendAsync(runtime, globals, context, HttpMethod.Post,
                TerminalPath(context.ParseResult.GetValueForOption(tenantId), context.ParseResult.GetValueForOption(agentId),
                    $"sessions/{Escape(context.ParseResult.GetValueForArgument(sessionId))}/resize"),
                JsonSerializer.Serialize(new
                {
                    columns = context.ParseResult.GetValueForOption(columns),
                    rows = context.ParseResult.GetValueForOption(rows)
                })).ConfigureAwait(false);
        });
        return command;
    }

    private static string TerminalOpenBody(CliInvocationContext context, TerminalOpenOptions options) => JsonSerializer.Serialize(new
    {
        shell = context.ParseResult.GetValueForOption(options.Shell),
        workingDirectory = context.ParseResult.GetValueForOption(options.WorkingDirectory),
        columns = context.ParseResult.GetValueForOption(options.Columns),
        rows = context.ParseResult.GetValueForOption(options.Rows)
    });

    private static string TerminalPath(int tenantId, Guid agentId, string suffix) =>
        suffix == "availability"
            ? $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/terminal/availability"
            : $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/terminal/{suffix}";

    private sealed record TerminalOpenOptions(
        Option<int> TenantId,
        Option<Guid> AgentId,
        Option<string> Shell,
        Option<string> WorkingDirectory,
        Option<int?> Columns,
        Option<int?> Rows);

    private static Command BuildAuthCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var auth = new Command("auth", "Configure and verify Oidc AI-agent authentication.");

        var configure = new Command("configure", "Persist local CLI authentication settings.");
        configure.SetHandler(async ctx =>
        {
            var config = LoadConfig(ctx, globals, requireAuth: false);
            var store = new CliConfigStore(ctx.ParseResult.GetValueForOption(globals.ConfigPath));
            store.Save(config.File.Merge(config.Overrides));
            await WriteJsonAsync(runtime, new { saved = true, path = store.Path }, ctx, globals).ConfigureAwait(false);
        });
        auth.AddCommand(configure);

        var status = new Command("status", "Call the NetRatel API AI-agent status endpoint.");
        status.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/auth/ai-agent/status"));
        auth.AddCommand(status);

        var token = new Command("token", "Mint and print an Oidc AI-agent bearer token.");
        token.SetHandler(async ctx =>
        {
            var resolved = LoadConfig(ctx, globals).Resolved;
            var accessToken = await runtime.GetAccessTokenAsync(resolved!, ctx.GetCancellationToken()).ConfigureAwait(false);
            if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                await runtime.Out.WriteLineAsync(accessToken).ConfigureAwait(false);
            }
        });
        auth.AddCommand(token);

        return auth;
    }

    private static Command BuildConfigCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var config = new Command("config", "Read and edit local NetRatel CLI config.");
        var keyArg = new Argument<string>("key");
        var valueArg = new Argument<string>("value");

        var show = new Command("show", "Show merged CLI config with secrets redacted.");
        show.SetHandler(ctx =>
        {
            var loaded = LoadConfig(ctx, globals, requireAuth: false);
            return WriteJsonAsync(runtime, Redact(loaded.File.Merge(loaded.Overrides)), ctx, globals);
        });
        config.AddCommand(show);

        var get = new Command("get", "Read one persisted config value.") { keyArg };
        get.SetHandler(async ctx =>
        {
            var store = new CliConfigStore(ctx.ParseResult.GetValueForOption(globals.ConfigPath));
            var value = GetConfigValue(store.Load(), ctx.ParseResult.GetValueForArgument(keyArg), redact: true);
            await WriteJsonAsync(runtime, new { key = ctx.ParseResult.GetValueForArgument(keyArg), value }, ctx, globals).ConfigureAwait(false);
        });
        config.AddCommand(get);

        var set = new Command("set", "Set one persisted config value.") { keyArg, valueArg };
        set.SetHandler(async ctx =>
        {
            var store = new CliConfigStore(ctx.ParseResult.GetValueForOption(globals.ConfigPath));
            var current = store.Load();
            var updated = SetConfigValue(current, ctx.ParseResult.GetValueForArgument(keyArg), ctx.ParseResult.GetValueForArgument(valueArg));
            store.Save(updated);
            await WriteJsonAsync(runtime, new { saved = true, path = store.Path }, ctx, globals).ConfigureAwait(false);
        });
        config.AddCommand(set);

        var unset = new Command("unset", "Unset one persisted config value.") { keyArg };
        unset.SetHandler(async ctx =>
        {
            var store = new CliConfigStore(ctx.ParseResult.GetValueForOption(globals.ConfigPath));
            var updated = SetConfigValue(store.Load(), ctx.ParseResult.GetValueForArgument(keyArg), null);
            store.Save(updated);
            await WriteJsonAsync(runtime, new { saved = true, path = store.Path }, ctx, globals).ConfigureAwait(false);
        });
        config.AddCommand(unset);

        return config;
    }

    private static Command BuildHealthCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("health", "Check live, ready, and authenticated AI-agent status.");
        command.SetHandler(async ctx =>
        {
            var loaded = LoadConfig(ctx, globals);
            var live = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/health/live", authenticated: true, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var ready = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/health/ready", authenticated: true, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var auth = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/api/v1/auth/ai-agent/status", authenticated: true, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            await WriteJsonAsync(runtime, new[] { live.ToHealth("live"), ready.ToHealth("ready"), auth.ToHealth("auth") }, ctx, globals).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildLogsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var logs = new Command("logs", "Search NetRatel API AI-agent operation logs.");
        var search = new Command("search", "Search log entries.");
        var tail = new Command("tail", "Poll log entries repeatedly.");
        var since = new Option<long?>("--since") { Description = "Return logs after this sequence." };
        var level = new Option<string?>("--level") { Description = "Filter by log level." };
        var contains = new Option<string?>("--contains") { Description = "Filter by message text." };
        var correlationId = new Option<string?>("--correlation-id") { Description = "Filter by correlation id." };
        var limit = new Option<int?>("--limit") { Description = "Maximum entries." };
        foreach (var option in new Option[] { since, level, contains, correlationId, limit }) search.AddOption(option);
        var raw = new Option<bool>("--raw") { Description = "Print the unshaped API response." };
        search.AddOption(raw);
        search.SetHandler(async ctx =>
        {
            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/ops/ai-agent/logs" + Query(ctx, (since, "since"), (level, "level"), (contains, "contains"), (correlationId, "correlationId"), (limit, "limit")), null).ConfigureAwait(false);
            if (ctx.ParseResult.GetValueForOption(raw))
            {
                await WriteResponseBodyAsync(runtime, response.Body, response.StatusCode, ctx, globals).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(runtime, ShapeAgentResponse(response.Body, AgentShape.Logs), ctx, globals).ConfigureAwait(false);
        });
        logs.AddCommand(search);

        var iterations = new Option<int>("--iterations") { Description = "Poll count.", DefaultValueFactory = _ => 20 };
        var delay = new Option<int>("--delay-seconds") { Description = "Delay between polls.", DefaultValueFactory = _ => 3 };
        foreach (var option in new Option[] { since, iterations, delay }) tail.AddOption(option);
        tail.SetHandler(async ctx =>
        {
            long? cursor = ctx.ParseResult.GetValueForOption(since);
            var count = Math.Max(1, ctx.ParseResult.GetValueForOption(iterations));
            var pause = Math.Max(1, ctx.ParseResult.GetValueForOption(delay));
            for (var i = 0; i < count; i++)
            {
                var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/ops/ai-agent/logs" + Query(("since", cursor?.ToString())), null).ConfigureAwait(false);
                if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
                {
                    await runtime.Out.WriteLineAsync(response.Body).ConfigureAwait(false);
                }
                cursor = ExtractNextSince(response.Body) ?? cursor;
                if (i + 1 < count)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pause), ctx.GetCancellationToken()).ConfigureAwait(false);
                }
            }
        });
        logs.AddCommand(tail);

        return logs;
    }

    private static Command BuildTenantsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var tenants = new Command("tenants", "Inspect source-backed tenants.");
        tenants.AddCommand(GetListCommand(runtime, globals, "list", "/api/v1/tenants/"));
        tenants.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v1/tenants/{id}", intId: true));
        return tenants;
    }

    private static Command BuildScriptsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var scripts = new Command("scripts", "Inspect source-backed script library entries.");
        scripts.AddCommand(GetAgentListCommand(runtime, globals, "list", "/api/v1/script-library/", AgentShape.Scripts));
        scripts.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v1/script-library/{id}"));
        scripts.AddCommand(GetByIdCommand(runtime, globals, "params", "/api/v1/script-library/{id}/params"));
        return scripts;
    }

    private static Command BuildJobsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var jobs = new Command("jobs", "Inspect job definitions.");
        var folder = new Option<string?>("--folder") { Description = "Filter by folder." };
        var search = new Option<string?>("--search") { Description = "Search jobs." };
        jobs.AddCommand(GetAgentListCommand(runtime, globals, "list", "/api/v1/jobs/", AgentShape.Jobs, (folder, "folder"), (search, "search")));
        jobs.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v1/jobs/{id}"));
        jobs.AddCommand(GetByIdCommand(runtime, globals, "details", "/api/v1/jobs/{id}/details"));
        jobs.AddCommand(GetByIdCommand(runtime, globals, "params", "/api/v1/jobs/{id}/params"));
        jobs.AddCommand(GetByIdCommand(runtime, globals, "steps", "/api/v1/jobs/{id}/steps"));
        return jobs;
    }

    private static Command BuildJobRunsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var runs = new Command("job-runs", "Start and inspect job runs.");
        var status = new Option<string?>("--status") { Description = "Filter by status." };
        var jobId = new Option<long?>("--job-id") { Description = "Filter by job id." };
        var tenantId = new Option<int?>("--tenant-id") { Description = "Filter by tenant id." };
        var take = new Option<int?>("--take") { Description = "Maximum run count." };
        runs.AddCommand(GetAgentListCommand(runtime, globals, "list", "/api/v1/jobruns/", AgentShape.JobRuns, (status, "status"), (jobId, "jobId"), (tenantId, "tenantId"), (take, "take")));
        runs.AddCommand(GetListCommand(runtime, globals, "query", "/api/v1/jobruns/query", (status, "status"), (jobId, "jobId"), (tenantId, "tenantId")));
        runs.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v1/jobruns/{id}"));
        runs.AddCommand(GetByIdCommand(runtime, globals, "steps", "/api/v1/jobruns/{id}/steps"));
        runs.AddCommand(BodyCommand(runtime, globals, "start", HttpMethod.Post, "/api/v1/jobruns/start/{id}", ("--started-by", "startedBy"), ("--client-identity-override", "clientIdentityOverride"), ("--inputs-json", "inputsJson")));
        runs.AddCommand(BodyCommand(runtime, globals, "cancel", HttpMethod.Post, "/api/v1/jobruns/{id}/cancel"));
        runs.AddCommand(DeleteByIdCommand(runtime, globals, "delete", "/api/v1/jobruns/{id}"));
        var runId = new Argument<long>("id");
        var ordinal = new Option<int>("--ordinal") { Description = "Step ordinal.", DefaultValueFactory = _ => 1 };
        var logs = new Command("logs", "GET /api/v1/jobruns/{id}/steps/{ordinal}/logs") { runId, ordinal };
        logs.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, $"/api/v1/jobruns/{ctx.ParseResult.GetValueForArgument(runId)}/steps/{ctx.ParseResult.GetValueForOption(ordinal)}/logs"));
        runs.AddCommand(logs);
        return runs;
    }

    private static Command BuildTasksCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var tasks = new Command("tasks", "Inspect source-backed V2 agent tasks.");
        var requestId = new Option<string?>("--request-id") { Description = "Request id." };
        var recentLimit = new Option<int?>("--limit") { Description = "Maximum task count." };
        var agent = new Option<string?>("--agent-id") { Description = "Persisted V2 agent identifier." };
        var tenant = new Option<int?>("--tenant-id") { Description = "Tenant id." };
        var type = new Option<string?>("--task-type") { Description = "Task type." };
        var status = new Option<string?>("--status") { Description = "Task status." };
        tasks.AddCommand(BuildTasksListCommand(runtime, globals, requestId, recentLimit, agent, tenant, type, status));
        tasks.AddCommand(BuildTasksResultCommand(runtime, globals));
        tasks.AddCommand(GetAgentListCommand(runtime, globals, "recent", "/api/v2/tasks/recent", AgentShape.Tasks, (recentLimit, "limit"), (agent, "agentId"), (tenant, "tenantId"), (type, "taskType"), (status, "status")));
        tasks.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v2/tasks/{id}", intId: true));
        tasks.AddCommand(BuildTaskLogsCommand(runtime, globals));
        tasks.AddCommand(BuildTaskLogsByRequestCommand(runtime, globals));
        return tasks;
    }

    private static Command BuildTasksListCommand(
        CliRuntime runtime,
        GlobalOptions globals,
        Option<string?> requestId,
        Option<int?> limit,
        Option<string?> agent,
        Option<int?> tenant,
        Option<string?> type,
        Option<string?> status)
    {
        var raw = new Option<bool>("--raw") { Description = "Print the unshaped API response." };
        var command = new Command("list", "List recent client tasks, or inspect one request with --request-id.")
        {
            requestId,
            limit,
            agent,
            tenant,
            type,
            status,
            raw
        };

        command.SetHandler(async ctx =>
        {
            var request = ctx.ParseResult.GetValueForOption(requestId);
            var path = string.IsNullOrWhiteSpace(request)
                ? "/api/v2/tasks/recent" + Query(ctx, (limit, "limit"), (agent, "agentId"), (tenant, "tenantId"), (type, "taskType"), (status, "status"))
                : "/api/v2/tasks" + Query(("requestId", request));
            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, path, null).ConfigureAwait(false);

            if (ctx.ParseResult.GetValueForOption(raw))
            {
                await WriteResponseBodyAsync(runtime, response.Body, response.StatusCode, ctx, globals).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(runtime, ShapeTaskList(response.Body), ctx, globals).ConfigureAwait(false);
        });

        return command;
    }

    private static Command BuildTasksResultCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var requestId = new Argument<string>("request-id") { Description = "Task request id." };
        var rawLogs = new Option<bool>("--raw-logs") { Description = "Include raw task log rows in the result." };
        var command = new Command("result", "Fetch task status plus output by request id.") { requestId, rawLogs };

        command.SetHandler(async ctx =>
        {
            var request = ctx.ParseResult.GetValueForArgument(requestId);
            var lookup = await TryFetchTaskByRequestIdAsync(runtime, globals, ctx, request).ConfigureAwait(false);

            if (lookup.Task is null)
            {
                throw new CliValidationException($"Task request '{request}' was not found. Lookup error: {lookup.Error ?? "not found"}");
            }

            var logs = await TryFetchTaskLogsAsync(runtime, globals, ctx, request).ConfigureAwait(false);
            await WriteJsonAsync(runtime, BuildTaskResult(lookup.Task, logs.Logs, ctx.ParseResult.GetValueForOption(rawLogs), logs.Error ?? lookup.Error), ctx, globals).ConfigureAwait(false);
        });

        return command;
    }

    private static Command BuildTaskLogsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var id = new Argument<string>("id") { Description = "Positive V2 task identifier." };
        var sinceId = new Option<long?>("--since-id") { Description = "Optional exclusive log sequence." };
        var stream = new Option<string?>("--stream") { Description = "Optional log stream: all, stdout, or stderr." };
        var command = new Command("logs", "Read bounded logs for a V2 task.") { id, sinceId, stream };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            "/api/v2/tasks/" + Escape(ctx.ParseResult.GetValueForArgument(id)) + "/logs" + Query(ctx, (sinceId, "sinceId"), (stream, "stream"))));
        return command;
    }

    private static Command BuildTaskLogsByRequestCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var requestId = new Argument<string>("request-id") { Description = "Task request identifier." };
        var sinceId = new Option<long?>("--since-id") { Description = "Optional exclusive log sequence." };
        var stream = new Option<string?>("--stream") { Description = "Optional log stream: all, stdout, or stderr." };
        var command = new Command("logs-by-request", "Read bounded logs for a task request.") { requestId, sinceId, stream };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get,
            "/api/v2/tasks/logs" + Query(("requestId", ctx.ParseResult.GetValueForArgument(requestId)), ("sinceId", ctx.ParseResult.GetValueForOption(sinceId)?.ToString(CultureInfo.InvariantCulture)), ("stream", ctx.ParseResult.GetValueForOption(stream)))));
        return command;
    }

    private static Command BuildClientsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var clients = new Command("clients", "Inspect source-backed client telemetry and update attempts.");
        clients.AddCommand(GetByIdentityCommand(runtime, globals, "telemetry", "/api/v1/clients/{identity}/telemetry"));
        clients.AddCommand(BuildDevelopmentTelemetryCommand(runtime, globals));
        clients.AddCommand(BuildDevelopmentTelemetryWindowCommand(runtime, globals));
        clients.AddCommand(BuildDevelopmentClientLogsCommand(runtime, globals));
        var clientIdentity = new Option<string?>("--client-identity") { Description = "Optional agent UUID accepted by the source API's legacy-named query parameter." };
        var releaseId = new Option<int?>("--release-id") { Description = "Optional positive update-release identifier." };
        var status = new Option<string?>("--status") { Description = "Optional update-attempt state." };
        clients.AddCommand(GetListCommand(runtime, globals, "update-attempts", "/api/v1/client-updates/attempts", (clientIdentity, "clientIdentity"), (releaseId, "releaseId"), (status, "status")));
        return clients;
    }

    private static Command BuildDevelopmentTelemetryCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var (tenantId, agentId) = DevelopmentTargetOptions();
        var command = new Command("telemetry-target", "Read one persisted Development target's telemetry snapshot.") { tenantId, agentId };
        command.SetHandler(ctx =>
        {
            var (tenant, parsedAgent) = ParseDevelopmentTarget(ctx, tenantId, agentId);
            return SendAsync(
                runtime,
                globals,
                ctx,
                HttpMethod.Get,
                $"/api/v2/development/mcp/agents/{tenant.ToString(CultureInfo.InvariantCulture)}/{parsedAgent:D}/telemetry/snapshot");
        });
        return command;
    }

    private static Command BuildDevelopmentTelemetryWindowCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var (tenantId, agentId) = DevelopmentTargetOptions();
        var windowSeconds = new Option<int?>("--window-seconds") { Description = "Bounded telemetry observation window from 1 through 15 seconds." };
        var maxSamples = new Option<int?>("--max-samples") { Description = "Maximum accepted telemetry samples from 1 through 20." };
        var command = new Command("telemetry-window", "Read a bounded persisted Development target telemetry window.") { tenantId, agentId, windowSeconds, maxSamples };
        command.SetHandler(ctx =>
        {
            var (tenant, parsedAgent) = ParseDevelopmentTarget(ctx, tenantId, agentId);
            var window = ctx.ParseResult.GetValueForOption(windowSeconds);
            var samples = ctx.ParseResult.GetValueForOption(maxSamples);
            if (window is < 1 or > 15 || samples is < 1 or > 20)
            {
                throw new CliValidationException("--window-seconds must be 1 through 15 and --max-samples must be 1 through 20.");
            }

            return SendAsync(
                runtime,
                globals,
                ctx,
                HttpMethod.Get,
                $"/api/v2/development/mcp/agents/{tenant.ToString(CultureInfo.InvariantCulture)}/{parsedAgent:D}/telemetry/stream-window" +
                Query(("windowSeconds", window?.ToString(CultureInfo.InvariantCulture)), ("maxSamples", samples?.ToString(CultureInfo.InvariantCulture))));
        });
        return command;
    }

    private static Command BuildDevelopmentClientLogsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var logs = new Command("client-logs", "Inspect target-gated Development client log sources, bounded history, a bounded live tail, and confirmed resync.");

        var (sourcesTenantId, sourcesAgentId) = DevelopmentTargetOptions();
        var sources = new Command("sources", "List the persisted Development target's advertised log sources.") { sourcesTenantId, sourcesAgentId };
        sources.SetHandler(ctx =>
        {
            var (tenant, agent) = ParseDevelopmentTarget(ctx, sourcesTenantId, sourcesAgentId);
            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                $"/api/v2/development/mcp/agents/{tenant.ToString(CultureInfo.InvariantCulture)}/{agent:D}/logs/sources");
        });
        logs.AddCommand(sources);

        var (historyTenantId, historyAgentId) = DevelopmentTargetOptions();
        var sourceId = new Option<string>("--source-id") { Description = "Advertised client log source identifier.",  Required = true };
        var cursor = new Option<string?>("--cursor") { Description = "Optional opaque exclusive history cursor." };
        var pageSize = new Option<int?>("--page-size") { Description = "Bounded history page size from 1 through 100." };
        var severity = new Option<string?>("--severity") { Description = "Optional exact severity filter advertised by the selected source." };
        var prefix = new Option<string?>("--prefix") { Description = "Optional exact prefix filter advertised by the selected source." };
        var category = new Option<string?>("--category") { Description = "Optional exact category filter advertised by the selected source." };
        var text = new Option<string?>("--text") { Description = "Optional bounded log text filter." };
        var history = new Command("history", "Read a bounded, cursor-paged Development client log history.") { historyTenantId, historyAgentId, sourceId, cursor, pageSize, severity, prefix, category, text };
        history.SetHandler(ctx =>
        {
            var (tenant, agent) = ParseDevelopmentTarget(ctx, historyTenantId, historyAgentId);
            var source = ctx.ParseResult.GetValueForOption(sourceId);
            var limit = ctx.ParseResult.GetValueForOption(pageSize);
            if (string.IsNullOrWhiteSpace(source) || source.Length > 128 || limit is < 1 or > 100)
            {
                throw new CliValidationException("--source-id is required and --page-size must be 1 through 100.");
            }

            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                $"/api/v2/development/mcp/agents/{tenant.ToString(CultureInfo.InvariantCulture)}/{agent:D}/logs/history" +
                Query(("sourceId", source), ("cursor", ctx.ParseResult.GetValueForOption(cursor)), ("pageSize", limit?.ToString(CultureInfo.InvariantCulture)), ("severity", ctx.ParseResult.GetValueForOption(severity)), ("prefix", ctx.ParseResult.GetValueForOption(prefix)), ("category", ctx.ParseResult.GetValueForOption(category)), ("text", ctx.ParseResult.GetValueForOption(text))));
        });
        logs.AddCommand(history);

        var (tailTenantId, tailAgentId) = DevelopmentTargetOptions();
        var tailSourceId = new Option<string>("--source-id") { Description = "Advertised client log source identifier.",  Required = true };
        var tailWindowSeconds = new Option<int?>("--window-seconds") { Description = "Bounded log observation window from 1 through 15 seconds." };
        var maxRecords = new Option<int?>("--max-records") { Description = "Maximum log records from 1 through 100." };
        var tail = new Command("tail", "Read a bounded live Development client log tail window.") { tailTenantId, tailAgentId, tailSourceId, tailWindowSeconds, maxRecords };
        tail.SetHandler(ctx =>
        {
            var (tenant, agent) = ParseDevelopmentTarget(ctx, tailTenantId, tailAgentId);
            var source = ctx.ParseResult.GetValueForOption(tailSourceId);
            var window = ctx.ParseResult.GetValueForOption(tailWindowSeconds);
            var records = ctx.ParseResult.GetValueForOption(maxRecords);
            if (string.IsNullOrWhiteSpace(source) || source.Length > 128 || window is < 1 or > 15 || records is < 1 or > 100)
            {
                throw new CliValidationException("--source-id is required; --window-seconds must be 1 through 15 and --max-records must be 1 through 100.");
            }

            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                $"/api/v2/development/mcp/agents/{tenant.ToString(CultureInfo.InvariantCulture)}/{agent:D}/logs/tail" +
                Query(("sourceId", source), ("windowSeconds", window?.ToString(CultureInfo.InvariantCulture)), ("maxRecords", records?.ToString(CultureInfo.InvariantCulture))));
        });
        logs.AddCommand(tail);

        var (resyncTenantId, resyncAgentId) = DevelopmentTargetOptions();
        var resyncSourceId = new Option<string>("--source-id") { Description = "Advertised client log source identifier to refresh through the current gateway.",  Required = true };
        var confirmResync = new Option<bool>("--confirm") { Description = "Confirm the bounded history refresh and gap-clear action." };
        var resync = new Command("resync", "Confirm a bounded current-history refresh and clear a previously reported log gap only after it succeeds.")
        {
            resyncTenantId,
            resyncAgentId,
            resyncSourceId,
            confirmResync
        };
        resync.SetHandler(ctx =>
        {
            var (tenant, agent) = ParseDevelopmentTarget(ctx, resyncTenantId, resyncAgentId);
            var source = ctx.ParseResult.GetValueForOption(resyncSourceId);
            if (string.IsNullOrWhiteSpace(source) || source.Length > 128)
            {
                throw new CliValidationException("--source-id is required and must be at most 128 characters.");
            }
            if (!ctx.ParseResult.GetValueForOption(confirmResync))
            {
                throw new CliValidationException("--confirm is required to resync client logs.");
            }

            return SendAsync(runtime, globals, ctx, HttpMethod.Post,
                $"/api/v2/development/mcp/agents/{tenant.ToString(CultureInfo.InvariantCulture)}/{agent:D}/logs/resync",
                JsonSerializer.Serialize(new { sourceId = source }));
        });
        logs.AddCommand(resync);

        return logs;
    }

    private static (Option<int> TenantId, Option<string> AgentId) DevelopmentTargetOptions() =>
    (
        new Option<int>("--tenant-id") { Description = "Positive tenant identifier for the persisted Development target.",  Required = true },
        new Option<string>("--agent-id") { Description = "Persisted Development target agent UUID.",  Required = true }
    );

    private static (int TenantId, Guid AgentId) ParseDevelopmentTarget(
        CliInvocationContext context,
        Option<int> tenantId,
        Option<string> agentId)
    {
        var tenant = context.ParseResult.GetValueForOption(tenantId);
        var agent = context.ParseResult.GetValueForOption(agentId);
        if (tenant <= 0 || !Guid.TryParse(agent, out var parsedAgent))
        {
            throw new CliValidationException("--tenant-id must be positive and --agent-id must be a UUID.");
        }

        return (tenant, parsedAgent);
    }

    private static Command BuildClientFilesCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var files = new Command("client-files", "Browse and mutate client files.");
        var identity = new Argument<string>("identity");
        var path = new Option<string>("--path") { Description = "Remote path.",  Required = true };
        var browse = new Command("browse", "List a remote directory.") { identity, path };
        browse.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, $"/api/v1/clients/{Escape(ctx.ParseResult.GetValueForArgument(identity))}/filesystem?path={Escape(ctx.ParseResult.GetValueForOption(path)!)}"));
        files.AddCommand(browse);
        var readIdentity = new Argument<string>("identity");
        var read = new Command("read", "Read a remote file.") { readIdentity, path };
        read.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, $"/api/v1/clients/{Escape(ctx.ParseResult.GetValueForArgument(readIdentity))}/filesystem/file?path={Escape(ctx.ParseResult.GetValueForOption(path)!)}"));
        files.AddCommand(read);
        files.AddCommand(BodyCommand(runtime, globals, "write", HttpMethod.Post, "/api/v1/clients/{identity}/filesystem/file", ("--path", "path"), ("--content", "content"), ("--encoding", "encoding")));
        return files;
    }

    private static Command BuildOperatorFilesCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var files = new Command("files-v2", "Operate policy-admitted V2 files for one exact tenant and agent through bounded reads, artifact custody, preview, confirmation, and idempotency. This command has no legacy file-route fallback.");
        files.AddCommand(BuildOperatorFileReadCommand(runtime, globals, "browse", "List one policy-admitted directory with an optional bounded page size.", "/browse", includePageSize: true));
        files.AddCommand(BuildOperatorFileReadCommand(runtime, globals, "stat", "Read bounded metadata for one policy-admitted path.", "/stat", includePageSize: false));
        files.AddCommand(BuildOperatorFileReadCommand(runtime, globals, "read", "Read one policy-admitted file within the server byte bound.", "/read", includePageSize: false));
        files.AddCommand(BuildOperatorFilePathMutationCommand(runtime, globals, "preview-collect-artifact", "Preview collecting one bounded file artifact and receive opaque credentials.", "/artifacts/collect/preview", confirmed: false));
        files.AddCommand(BuildOperatorFilePathMutationCommand(runtime, globals, "confirm-collect-artifact", "Collect one previewed file artifact only with unchanged credentials and --confirm.", "/artifacts/collect/confirm", confirmed: true));
        files.AddCommand(BuildOperatorFileArtifactReadCommand(runtime, globals, "artifact-status", "Read the caller-owned metadata for one bounded artifact.", string.Empty));
        files.AddCommand(BuildOperatorFileArtifactReadCommand(runtime, globals, "download-artifact", "Download the caller-owned bounded artifact content before expiry.", "/download"));
        files.AddCommand(BuildOperatorFileArtifactMutationCommand(runtime, globals, "preview-cleanup-artifact", "Preview caller-owned artifact cleanup and receive opaque credentials.", "/cleanup/preview", confirmed: false));
        files.AddCommand(BuildOperatorFileArtifactMutationCommand(runtime, globals, "confirm-cleanup-artifact", "Clean up a previewed caller-owned artifact only with --confirm.", "/cleanup/confirm", confirmed: true));
        files.AddCommand(BuildOperatorFileTextMutationCommand(runtime, globals, "preview-write-text", "Preview a bounded UTF-8 file write and receive opaque credentials.", confirmed: false));
        files.AddCommand(BuildOperatorFileTextMutationCommand(runtime, globals, "confirm-write-text", "Write the unchanged bounded UTF-8 content only with --confirm.", confirmed: true));
        files.AddCommand(BuildOperatorFileUploadMutationCommand(runtime, globals, "preview-upload", "Preview a bounded canonical-base64 file upload and receive opaque credentials.", confirmed: false));
        files.AddCommand(BuildOperatorFileUploadMutationCommand(runtime, globals, "confirm-upload", "Upload the unchanged bounded canonical-base64 content only with --confirm.", confirmed: true));
        files.AddCommand(BuildOperatorFilePathMutationCommand(runtime, globals, "preview-create-directory", "Preview creation of one policy-admitted directory.", "/create-directory/preview", confirmed: false));
        files.AddCommand(BuildOperatorFilePathMutationCommand(runtime, globals, "confirm-create-directory", "Create the previewed directory only with --confirm.", "/create-directory/confirm", confirmed: true));
        files.AddCommand(BuildOperatorFilePathMutationCommand(runtime, globals, "preview-delete", "Preview deletion of one policy-admitted path.", "/delete/preview", confirmed: false));
        files.AddCommand(BuildOperatorFilePathMutationCommand(runtime, globals, "confirm-delete", "Delete the previewed path only with --confirm.", "/delete/confirm", confirmed: true));
        files.AddCommand(BuildOperatorFileRelocationMutationCommand(runtime, globals, "preview-copy", "Preview a policy-admitted file copy.", "copy", confirmed: false));
        files.AddCommand(BuildOperatorFileRelocationMutationCommand(runtime, globals, "confirm-copy", "Copy the previewed file only with --confirm.", "copy", confirmed: true));
        files.AddCommand(BuildOperatorFileRelocationMutationCommand(runtime, globals, "preview-move", "Preview a policy-admitted file move.", "move", confirmed: false));
        files.AddCommand(BuildOperatorFileRelocationMutationCommand(runtime, globals, "confirm-move", "Move the previewed file only with --confirm.", "move", confirmed: true));
        return files;
    }

    private static Command BuildOperatorFileReadCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string suffix, bool includePageSize)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var path = RequiredOption("--path", "Exact policy-admitted remote path.");
        command.AddOption(path);
        Option<int?>? pageSize = null;
        if (includePageSize)
        {
            pageSize = new Option<int?>("--page-size") { Description = "Optional page size from 1 through 100." };
            command.AddOption(pageSize);
        }

        command.SetHandler(ctx =>
        {
            var remotePath = ctx.ParseResult.GetValueForOption(path);
            ValidateOperatorFilePath(remotePath, "--path");
            var requestedPageSize = pageSize is null ? null : ctx.ParseResult.GetValueForOption(pageSize);
            if (requestedPageSize is <= 0 or > 100) throw new CliValidationException("--page-size must be from 1 through 100.");
            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), suffix) +
                Query(("path", remotePath), ("pageSize", requestedPageSize?.ToString(CultureInfo.InvariantCulture))));
        });
        return command;
    }

    private static Command BuildOperatorFilePathMutationCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string suffix, bool confirmed)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var path = RequiredOption("--path", "Exact policy-admitted remote path.");
        command.AddOption(path);
        var confirmation = confirmed ? AddOperatorFileConfirmationOptions(command, name) : null;
        command.SetHandler(async ctx =>
        {
            var remotePath = ctx.ParseResult.GetValueForOption(path);
            ValidateOperatorFilePath(remotePath, "--path");
            if (!await RequireOperatorFileConfirmationAsync(runtime, ctx, globals, confirmation, name).ConfigureAwait(false)) return;
            var body = new JsonObject { ["path"] = remotePath };
            AddOperatorFileConfirmation(body, ctx, confirmation);
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), suffix),
                body.ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildOperatorFileArtifactReadCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string suffix)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var artifactId = new Option<Guid>("--artifact-id") { Description = "Caller-owned artifact UUID.",  Required = true };
        command.AddOption(artifactId);
        command.SetHandler(ctx =>
        {
            var artifact = RequiredArtifactId(ctx.ParseResult.GetValueForOption(artifactId));
            return SendAsync(runtime, globals, ctx, HttpMethod.Get,
                $"{FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), "/artifacts")}/{artifact:D}{suffix}");
        });
        return command;
    }

    private static Command BuildOperatorFileArtifactMutationCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string suffix, bool confirmed)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var artifactId = new Option<Guid>("--artifact-id") { Description = "Caller-owned artifact UUID.",  Required = true };
        command.AddOption(artifactId);
        var confirmation = confirmed ? AddOperatorFileConfirmationOptions(command, name) : null;
        command.SetHandler(async ctx =>
        {
            var artifact = RequiredArtifactId(ctx.ParseResult.GetValueForOption(artifactId));
            if (!await RequireOperatorFileConfirmationAsync(runtime, ctx, globals, confirmation, name).ConfigureAwait(false)) return;
            var body = new JsonObject();
            AddOperatorFileConfirmation(body, ctx, confirmation);
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                $"{FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), "/artifacts")}/{artifact:D}{suffix}",
                body.Count == 0 ? null : body.ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildOperatorFileTextMutationCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, bool confirmed)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var path = RequiredOption("--path", "Exact policy-admitted remote path.");
        var text = RequiredOption("--text", "UTF-8 file text within the 16 KiB server bound; empty text truncates a file.");
        command.AddOption(path);
        command.AddOption(text);
        var confirmation = confirmed ? AddOperatorFileConfirmationOptions(command, name) : null;
        command.SetHandler(async ctx =>
        {
            var remotePath = ctx.ParseResult.GetValueForOption(path);
            var content = ctx.ParseResult.GetValueForOption(text);
            ValidateOperatorFilePath(remotePath, "--path");
            ValidateOperatorFileText(content);
            if (!await RequireOperatorFileConfirmationAsync(runtime, ctx, globals, confirmation, name).ConfigureAwait(false)) return;
            var body = new JsonObject { ["path"] = remotePath, ["text"] = content };
            AddOperatorFileConfirmation(body, ctx, confirmation);
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), $"/write-text/{(confirmed ? "confirm" : "preview")}"),
                body.ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildOperatorFileUploadMutationCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, bool confirmed)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var path = RequiredOption("--path", "Exact policy-admitted remote path.");
        var contentBase64 = RequiredOption("--content-base64", "Canonical base64 content within the 64 KiB decoded server bound.");
        command.AddOption(path);
        command.AddOption(contentBase64);
        var confirmation = confirmed ? AddOperatorFileConfirmationOptions(command, name) : null;
        command.SetHandler(async ctx =>
        {
            var remotePath = ctx.ParseResult.GetValueForOption(path);
            var content = ctx.ParseResult.GetValueForOption(contentBase64);
            ValidateOperatorFilePath(remotePath, "--path");
            ValidateOperatorFileBase64(content);
            if (!await RequireOperatorFileConfirmationAsync(runtime, ctx, globals, confirmation, name).ConfigureAwait(false)) return;
            var body = new JsonObject { ["path"] = remotePath, ["contentBase64"] = content };
            AddOperatorFileConfirmation(body, ctx, confirmation);
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), $"/upload/{(confirmed ? "confirm" : "preview")}"),
                body.ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildOperatorFileRelocationMutationCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string action, bool confirmed)
    {
        var command = new Command(name, description);
        var (tenantId, agentId) = AddCommandTargetOptions(command);
        var sourcePath = RequiredOption("--source-path", "Exact policy-admitted source path.");
        var destinationPath = RequiredOption("--destination-path", "Exact policy-admitted destination path.");
        command.AddOption(sourcePath);
        command.AddOption(destinationPath);
        var confirmation = confirmed ? AddOperatorFileConfirmationOptions(command, name) : null;
        command.SetHandler(async ctx =>
        {
            var source = ctx.ParseResult.GetValueForOption(sourcePath);
            var destination = ctx.ParseResult.GetValueForOption(destinationPath);
            ValidateOperatorFilePath(source, "--source-path");
            ValidateOperatorFilePath(destination, "--destination-path");
            if (!await RequireOperatorFileConfirmationAsync(runtime, ctx, globals, confirmation, name).ConfigureAwait(false)) return;
            var body = new JsonObject { ["sourcePath"] = source, ["destinationPath"] = destination };
            AddOperatorFileConfirmation(body, ctx, confirmation);
            await SendAsync(runtime, globals, ctx, HttpMethod.Post,
                FileV2Path(ctx.ParseResult.GetValueForOption(tenantId), ctx.ParseResult.GetValueForOption(agentId), $"/{action}/{(confirmed ? "confirm" : "preview")}"),
                body.ToJsonString(JsonOptions)).ConfigureAwait(false);
        });
        return command;
    }

    private static FileConfirmationOptions AddOperatorFileConfirmationOptions(Command command, string operation)
    {
        var planToken = RequiredOption("--plan-token", $"Opaque credential returned by preview-{operation.Replace("confirm-", string.Empty, StringComparison.Ordinal)}.");
        var idempotencyKey = RequiredOption("--idempotency-key", $"Opaque credential returned by preview-{operation.Replace("confirm-", string.Empty, StringComparison.Ordinal)}.");
        var confirm = new Option<bool>("--confirm") { Description = "Confirm this unchanged policy-admitted file mutation." };
        command.AddOption(planToken);
        command.AddOption(idempotencyKey);
        command.AddOption(confirm);
        return new FileConfirmationOptions(planToken, idempotencyKey, confirm);
    }

    private static async Task<bool> RequireOperatorFileConfirmationAsync(CliRuntime runtime, CliInvocationContext context, GlobalOptions globals, FileConfirmationOptions? confirmation, string operation)
    {
        if (confirmation is null) return true;
        if (!context.ParseResult.GetValueForOption(confirmation.Confirm))
        {
            await WriteJsonAsync(runtime, new
            {
                confirmationRequired = true,
                nextAction = $"Review preview-{operation.Replace("confirm-", string.Empty, StringComparison.Ordinal)} and rerun with --confirm plus its unchanged plan token and idempotency key."
            }, context, globals).ConfigureAwait(false);
            return false;
        }

        var planToken = context.ParseResult.GetValueForOption(confirmation.PlanToken);
        var idempotencyKey = context.ParseResult.GetValueForOption(confirmation.IdempotencyKey);
        if (!IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
            throw new CliValidationException("--plan-token and --idempotency-key must be opaque 32-128 character preview credentials.");
        return true;
    }

    private static void AddOperatorFileConfirmation(JsonObject body, CliInvocationContext context, FileConfirmationOptions? confirmation)
    {
        if (confirmation is null) return;
        body["planToken"] = context.ParseResult.GetValueForOption(confirmation.PlanToken);
        body["idempotencyKey"] = context.ParseResult.GetValueForOption(confirmation.IdempotencyKey);
    }

    private static Guid RequiredArtifactId(Guid artifactId) => artifactId != Guid.Empty
        ? artifactId
        : throw new CliValidationException("--artifact-id must be a non-empty UUID.");

    private static void ValidateOperatorFilePath(string? path, string optionName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0'))
            throw new CliValidationException($"{optionName} must be a non-empty non-NUL path of at most 4096 characters.");
    }

    private static void ValidateOperatorFileText(string? text)
    {
        if (text is null || text.Length > 16 * 1024 || text.Contains('\0'))
            throw new CliValidationException("--text must be non-NUL and at most 16 KiB UTF-8.");
        try
        {
            if (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetByteCount(text) > 16 * 1024)
                throw new CliValidationException("--text must be non-NUL and at most 16 KiB UTF-8.");
        }
        catch (EncoderFallbackException)
        {
            throw new CliValidationException("--text must contain valid UTF-8 text.");
        }
    }

    private static void ValidateOperatorFileBase64(string? contentBase64)
    {
        const int maximumBytes = 64 * 1024;
        const int maximumCharacters = ((maximumBytes + 2) / 3) * 4;
        if (contentBase64 is null || contentBase64.Length > maximumCharacters || contentBase64.Length % 4 != 0)
            throw new CliValidationException("--content-base64 must be canonical base64 within the 64 KiB decoded bound.");
        var buffer = new byte[(contentBase64.Length / 4) * 3];
        if (!Convert.TryFromBase64String(contentBase64, buffer, out var bytesWritten) ||
            bytesWritten > maximumBytes ||
            !string.Equals(contentBase64, Convert.ToBase64String(buffer, 0, bytesWritten), StringComparison.Ordinal))
        {
            throw new CliValidationException("--content-base64 must be canonical base64 within the 64 KiB decoded bound.");
        }
    }

    private static string FileV2Path(int tenantId, Guid agentId, string suffix)
    {
        if (tenantId <= 0 || agentId == Guid.Empty) throw new CliValidationException("--tenant-id must be positive and --agent-id must be a non-empty UUID.");
        return $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/files{suffix}";
    }

    private sealed record FileConfirmationOptions(Option<string> PlanToken, Option<string> IdempotencyKey, Option<bool> Confirm);

    private static Command BuildTerminalCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var terminal = new Command("terminal", "Open and control API-backed terminal sessions.");
        terminal.AddCommand(BuildTerminalListHostsCommand(runtime, globals));
        terminal.AddCommand(BuildTerminalCommandCommand(runtime, globals));
        terminal.AddCommand(BodyCommand(runtime, globals, "open", HttpMethod.Post, "/api/v1/clients/{identity}/terminal/open", ("--shell-type", "shellType"), ("--cols", "cols"), ("--rows", "rows"), ("--working-directory", "workingDirectory"), ("--transport", "transport")));
        terminal.AddCommand(BodyCommand(runtime, globals, "self-test", HttpMethod.Post, "/api/v1/clients/{identity}/terminal/self-test", ("--shell-type", "shellType"), ("--timeout-ms", "timeoutMs"), ("--transport", "transport")));
        terminal.AddCommand(GetByStringIdCommand(runtime, globals, "get", "/api/v1/terminal/{id}", "session-id"));
        terminal.AddCommand(GetByIdentityCommand(runtime, globals, "sessions", "/api/v1/clients/{identity}/terminal/sessions"));
        terminal.AddCommand(BodyCommand(runtime, globals, "stdin", HttpMethod.Post, "/api/v1/terminal/{id}/stdin", ("--input", "data")));
        terminal.AddCommand(BodyCommand(runtime, globals, "resize", HttpMethod.Post, "/api/v1/terminal/{id}/resize", ("--cols", "cols"), ("--rows", "rows")));
        terminal.AddCommand(GetByStringIdCommand(runtime, globals, "stream", "/api/v1/terminal/{id}/stream", "session-id"));
        terminal.AddCommand(GetByStringIdCommand(runtime, globals, "diagnostics", "/api/v1/terminal/{id}/diagnostics", "session-id"));
        terminal.AddCommand(BodyCommand(runtime, globals, "close", HttpMethod.Post, "/api/v1/terminal/{id}/close"));
        return terminal;
    }

    private static Command BuildTerminalListHostsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("listhosts", "List clients with terminal-ready host names and shell hints.");
        var onlineOnly = new Option<bool>("--online-only") { Description = "Only include online and enabled clients." };
        var search = new Option<string?>("--search") { Description = "Filter by host, display name, client name, identity, or short id." };
        command.AddOption(onlineOnly);
        command.AddOption(search);
        command.SetHandler(async ctx =>
        {
            var clients = await FetchTerminalHostsAsync(runtime, globals, ctx).ConfigureAwait(false);
            var query = ctx.ParseResult.GetValueForOption(search);
            if (ctx.ParseResult.GetValueForOption(onlineOnly))
            {
                clients = clients.Where(client => client.Online && client.Enabled).ToList();
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                clients = clients.Where(client => client.Matches(query!)).ToList();
            }

            var payload = clients.Select(client => new
            {
                host = client.HostName ?? client.DisplayName ?? client.ClientName ?? client.ShortId,
                client.DisplayName,
                client.ClientName,
                client.ClientIdentity,
                client.ShortId,
                client.Online,
                client.Enabled,
                client.AvailableShells,
                client.DetectedOs,
                client.EffectiveTerminalTransport,
                client.TerminalTunnelConnected,
                client.LastHeartbeat
            }).ToArray();
            await WriteJsonAsync(runtime, payload, ctx, globals).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildTerminalCommandCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var commandText = new Argument<string>("command") { Description = "Command text to run." };
        var host = new Argument<string>("host") { Description = "Host name, display name, client name, short id, or full client identity." };
        var shell = new Option<string?>("--shell") { Description = "Preferred shell: auto, powershell, pwsh, cmd, bash, sh, or zsh." };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { Description = "Maximum command runtime and CLI wait time.", DefaultValueFactory = _ => 120 };
        var workingDirectory = new Option<string?>("--working-directory") { Description = "Remote working directory." };
        var noWait = new Option<bool>("--no-wait") { Description = "Submit the command and return request ids without polling." };
        var pollSeconds = new Option<int>("--poll-seconds") { Description = "Seconds between status polls while waiting.", DefaultValueFactory = _ => 2 };
        var rawLogs = new Option<bool>("--raw-logs") { Description = "Include raw task log rows in the result." };

        var command = new Command("command", "Run a one-shot shell command on a client by host name.") { commandText, host, shell, timeoutSeconds, workingDirectory, noWait, pollSeconds, rawLogs };
        command.SetHandler(async ctx =>
        {
            var clients = await FetchTerminalHostsAsync(runtime, globals, ctx).ConfigureAwait(false);
            var target = ResolveTerminalHost(clients, ctx.ParseResult.GetValueForArgument(host));
            if (!target.Enabled)
            {
                throw new CliValidationException($"Client '{target.BestName}' is disabled. Run `netratel terminal listhosts --search {target.BestName}` to inspect it.");
            }

            if (!target.Online)
            {
                throw new CliValidationException($"Client '{target.BestName}' is offline. Run `netratel terminal listhosts --online-only` to find available hosts.");
            }

            var requestedShell = ctx.ParseResult.GetValueForOption(shell);
            var resolvedShell = ResolveShell(target, requestedShell);
            var timeout = Math.Max(1, ctx.ParseResult.GetValueForOption(timeoutSeconds));
            var requestBody = BuildShellTaskBody(
                target.ClientIdentity,
                ctx.ParseResult.GetValueForArgument(commandText),
                resolvedShell,
                ctx.ParseResult.GetValueForOption(workingDirectory),
                timeout);

            var created = await SendStringAsync(runtime, globals, ctx, HttpMethod.Post, "/api/v1/client-tasks/", requestBody).ConfigureAwait(false);
            var task = ParseTaskResponse(created.Body);
            if (ctx.ParseResult.GetValueForOption(noWait))
            {
                await WriteJsonAsync(runtime, BuildCommandResult(target, task, resolvedShell, Array.Empty<TerminalTaskLog>(), includeRawLogs: false, completed: false, resultLookupError: null), ctx, globals).ConfigureAwait(false);
                return;
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
            var pollDelay = TimeSpan.FromSeconds(Math.Max(1, ctx.ParseResult.GetValueForOption(pollSeconds)));
            TerminalTaskSnapshot latest = task;
            string? resultLookupError = null;
            while (!IsTerminalTaskStatus(latest.Status) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(pollDelay, ctx.GetCancellationToken()).ConfigureAwait(false);
                var lookup = await TryFetchTaskByRequestIdAsync(runtime, globals, ctx, latest.RequestId).ConfigureAwait(false);

                if (lookup.Task is null)
                {
                    resultLookupError = lookup.Error;
                    break;
                }

                latest = lookup.Task;
                resultLookupError = lookup.Error;
            }

            var logs = await TryFetchTaskLogsAsync(runtime, globals, ctx, latest.RequestId).ConfigureAwait(false);
            resultLookupError ??= logs.Error;
            var completed = IsTerminalTaskStatus(latest.Status);
            await WriteJsonAsync(runtime, BuildCommandResult(target, latest, resolvedShell, logs.Logs, ctx.ParseResult.GetValueForOption(rawLogs), completed, resultLookupError), ctx, globals).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildRequestsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var requests = new Command("requests", "Manage orchestration requests.");
        requests.AddCommand(GetAgentListCommand(runtime, globals, "list", "/api/v1/requests/", AgentShape.Requests));
        requests.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v1/requests/{id}", intId: true));
        requests.AddCommand(BodyCommand(runtime, globals, "create", HttpMethod.Post, "/api/v1/requests/", ("--source-system", "sourceSystem"), ("--target-client-identity", "targetClientIdentity"), ("--job-definition-id", "jobDefinitionId"), ("--job-inputs-json", "jobInputsJson")));
        requests.AddCommand(BodyCommand(runtime, globals, "update", HttpMethod.Put, "/api/v1/requests/{id}", ("--source-system", "sourceSystem"), ("--target-client-identity", "targetClientIdentity"), ("--job-definition-id", "jobDefinitionId"), ("--execution-id", "executionId"), ("--status", "status"), ("--result-message", "resultMessage"), ("--result-data", "resultData"), ("--job-inputs", "jobInputs")));
        requests.AddCommand(BodyCommand(runtime, globals, "claim", HttpMethod.Put, "/api/v1/requests/{id}/claim", ("--execution-id", "executionId")));
        requests.AddCommand(BodyCommand(runtime, globals, "complete", HttpMethod.Put, "/api/v1/requests/{id}/complete", ("--result-message", "resultMessage"), ("--result-data", "resultData")));
        requests.AddCommand(BodyCommand(runtime, globals, "fail", HttpMethod.Put, "/api/v1/requests/{id}/fail", ("--error-message", "errorMessage")));
        return requests;
    }

    private static Command BuildSecretsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var secrets = new Command("secrets", "Manage orchestrator secrets.");
        secrets.AddCommand(GetListCommand(runtime, globals, "list", "/api/v1/secrets/"));
        secrets.AddCommand(GetByIdCommand(runtime, globals, "get", "/api/v1/secrets/{id}", intId: true));
        secrets.AddCommand(BodyCommand(runtime, globals, "create", HttpMethod.Post, "/api/v1/secrets/", ("--name", "name"), ("--value", "value"), ("--description", "description")));
        secrets.AddCommand(BodyCommand(runtime, globals, "update", HttpMethod.Put, "/api/v1/secrets/{id}", ("--name", "name"), ("--value", "value"), ("--description", "description")));
        secrets.AddCommand(DeleteByIdCommand(runtime, globals, "delete", "/api/v1/secrets/{id}", intId: true));
        return secrets;
    }

    private static Command BuildSearchCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var search = new Command("search", "Search major NetRatel objects.");
        var q = new Option<string?>("--query") { Description = "Search query." };
        foreach (var item in new[] { "tenants", "scripts", "jobs", "requests", "clients", "tasks" })
        {
            search.AddCommand(GetAgentListCommand(runtime, globals, item, $"/api/v1/global-search/{item}", AgentShape.Search, (q, "q")));
        }
        return search;
    }

    private static Command BuildTelemetryCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var telemetry = new Command("telemetry", "Inspect client telemetry.");
        telemetry.AddCommand(GetListCommand(runtime, globals, "overview", "/api/v1/telemetry/overview"));
        telemetry.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/telemetry/overview"));
        return telemetry;
    }

    private static Command BuildConnectivityCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var connectivity = new Command("connectivity", "Inspect and test admin connectivity settings.");
        connectivity.AddCommand(GetListCommand(runtime, globals, "settings", "/api/v1/admin/connectivity/settings"));
        connectivity.AddCommand(BodyCommand(runtime, globals, "test", HttpMethod.Post, "/api/v1/admin/connectivity/tests"));
        connectivity.AddCommand(GetListCommand(runtime, globals, "netratel", "/api/v1/admin/orchestration/netratel"));
        return connectivity;
    }

    private static Command BuildNotificationsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var notifications = new Command("notifications", "Inspect notifications.");
        notifications.AddCommand(GetAgentListCommand(runtime, globals, "list", "/api/v1/notifications", AgentShape.Notifications));
        notifications.AddCommand(GetByStringIdCommand(runtime, globals, "get", "/api/v1/notifications/{id}", "id"));
        notifications.AddCommand(GetListCommand(runtime, globals, "summary", "/api/v1/notifications/summary"));
        notifications.AddCommand(GetListCommand(runtime, globals, "unread-errors", "/api/v1/notifications/unread-errors"));
        notifications.AddCommand(BodyCommand(runtime, globals, "mark-read", HttpMethod.Post, "/api/v1/notifications/mark-read", ("--ids", "ids")));
        return notifications;
    }

    private static Command BuildEventsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var events = new Command("events", "Inspect domain events.");
        events.AddCommand(GetAgentListCommand(runtime, globals, "list", "/api/v1/events", AgentShape.Events));
        events.AddCommand(GetByStringIdCommand(runtime, globals, "get", "/api/v1/events/{id}", "id"));
        events.AddCommand(BodyCommand(runtime, globals, "retry", HttpMethod.Post, "/api/v1/events/{id}/retry"));
        events.AddCommand(BodyCommand(runtime, globals, "disable", HttpMethod.Post, "/api/v1/events/{id}/disable"));
        return events;
    }

    private static Command BuildSystemCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var system = new Command("system", "Inspect safe system endpoints.");
        system.AddCommand(GetListCommand(runtime, globals, "version", "/api/v1/system/version"));
        return system;
    }

    private static Command BuildRawCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var raw = new Command("raw", "Call an operator-safe API path directly.");
        var method = new Argument<string>("method") { Description = "HTTP method: get, post, put, delete." };
        var path = new Option<string>("--path") { Description = "Operator-safe /api/v1 path.",  Required = true };
        var body = new Option<string?>("--body") { Description = "JSON request body." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Path to JSON request body file." };
        raw.AddArgument(method);
        raw.AddOption(path);
        raw.AddOption(body);
        raw.AddOption(bodyFile);
        raw.SetHandler(ctx =>
        {
            var rawPath = ctx.ParseResult.GetValueForOption(path);
            EnsureAllowedRawPath(rawPath!);
            return SendAsync(
                runtime,
                globals,
                ctx,
                ResolveMethod(ctx.ParseResult.GetValueForArgument(method)),
                rawPath!,
                ReadBody(ctx.ParseResult.GetValueForOption(body), ctx.ParseResult.GetValueForOption(bodyFile), "{}"));
        });
        return raw;
    }

    private static async Task<List<TerminalHostInfo>> FetchTerminalHostsAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx)
    {
        var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/clients/", null).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new CliValidationException("Client list response was not a JSON array.");
        }

        return doc.RootElement.EnumerateArray().Select(ParseTerminalHost).ToList();
    }

    private static TerminalHostInfo ParseTerminalHost(JsonElement item)
    {
        var identity = GetString(item, "clientIdentity") ?? GetString(item, "identity") ?? string.Empty;
        var shortId = GetString(item, "shortId") ?? (identity.Length >= 8 ? identity[..8] : identity);
        return new TerminalHostInfo(
            ClientIdentity: identity,
            ShortId: shortId,
            DisplayName: GetString(item, "displayName"),
            ClientName: GetString(item, "clientName"),
            HostName: GetString(item, "hostName"),
            Online: GetBool(item, "online"),
            Enabled: GetBool(item, "enabled"),
            AvailableShells: GetStringArray(item, "availableShells"),
            DetectedOs: GetString(item, "detectedOs"),
            EffectiveTerminalTransport: GetString(item, "effectiveTerminalTransport"),
            TerminalTunnelConnected: GetBool(item, "terminalTunnelConnected"),
            LastHeartbeat: GetString(item, "lastHeartbeat"));
    }

    private static TerminalHostInfo ResolveTerminalHost(IReadOnlyList<TerminalHostInfo> clients, string query)
    {
        var exact = clients.Where(client => client.ExactMatches(query)).ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }

        if (exact.Length > 1)
        {
            throw new CliValidationException($"Host '{query}' matched multiple clients exactly: {FormatHostCandidates(exact)}");
        }

        var partial = clients.Where(client => client.Matches(query)).ToArray();
        if (partial.Length == 1)
        {
            return partial[0];
        }

        if (partial.Length > 1)
        {
            throw new CliValidationException($"Host '{query}' is ambiguous. Candidates: {FormatHostCandidates(partial)}");
        }

        throw new CliValidationException($"Host '{query}' was not found. Try `netratel terminal listhosts --search {query}`.");
    }

    private static string ResolveShell(TerminalHostInfo target, string? requested)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? "auto" : requested.Trim();
        if (!string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant() switch
            {
                "powershell" or "pwsh" or "cmd" or "bash" or "sh" or "zsh" => value.ToLowerInvariant(),
                _ => throw new CliValidationException($"Unsupported shell '{requested}'. Use auto, powershell, pwsh, cmd, bash, sh, or zsh.")
            };
        }

        var shells = target.AvailableShells.Select(s => s.ToLowerInvariant()).ToArray();
        foreach (var candidate in PreferredShellOrder(target))
        {
            if (shells.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        if (shells.Length > 0)
        {
            return shells[0];
        }

        throw new CliValidationException($"Client '{target.BestName}' did not report available shells. Retry with `--shell powershell`, `--shell cmd`, `--shell bash`, or inspect `netratel terminal listhosts --search {target.BestName}`.");
    }

    private static IEnumerable<string> PreferredShellOrder(TerminalHostInfo target)
    {
        var os = target.DetectedOs ?? string.Empty;
        if (os.Contains("windows", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "powershell", "pwsh", "cmd", "bash", "sh", "zsh" };
        }

        return new[] { "bash", "sh", "zsh", "pwsh", "powershell", "cmd" };
    }

    private static string BuildShellTaskBody(string clientIdentity, string command, string shell, string? workingDirectory, int timeoutSeconds)
    {
        var root = new JsonObject
        {
            ["clientIdentity"] = clientIdentity,
            ["taskType"] = "exec-shell-cmd",
            ["shellCommand"] = new JsonObject
            {
                ["command"] = command,
                ["preferred"] = ShellToPreferred(shell),
                ["timeoutSeconds"] = timeoutSeconds
            },
            ["timeoutSeconds"] = timeoutSeconds
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            root["workingDirectory"] = workingDirectory;
            root["shellCommand"]!["workingDirectory"] = workingDirectory;
        }

        return root.ToJsonString(JsonOptions);
    }

    private static int ShellToPreferred(string shell)
        => shell.ToLowerInvariant() switch
        {
            "pwsh" => 1,
            "powershell" => 2,
            "bash" or "sh" or "zsh" => 3,
            "cmd" => 4,
            _ => 0
        };

    private static async Task<TerminalTaskSnapshot?> FetchTaskByRequestIdAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx, string requestId)
    {
        var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v2/tasks" + Query(("requestId", requestId)), null).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? ParseTaskResponse(first) : null;
    }

    private static async Task<TerminalTaskLookup> TryFetchTaskByRequestIdAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx, string requestId)
    {
        try
        {
            return new TerminalTaskLookup(await FetchTaskByRequestIdAsync(runtime, globals, ctx, requestId).ConfigureAwait(false), null);
        }
        catch (CliRemoteException ex) when (ex.StatusCode is 400 or 404 or 410)
        {
            return new TerminalTaskLookup(null, $"request lookup HTTP {ex.StatusCode}: {CreatePreview(ex.ResponseBody ?? ex.Message)}");
        }
    }

    private static async Task<TerminalTaskLog[]> FetchTaskLogsAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx, string requestId)
    {
        var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v2/tasks/logs" + Query(("requestId", requestId), ("sinceId", "0"), ("stream", "all")), null).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TerminalTaskLog>();
        }

        return doc.RootElement.EnumerateArray().Select(item => new TerminalTaskLog(
            Id: GetLong(item, "id"),
            Stream: GetString(item, "stream") ?? "stdout",
            Message: GetString(item, "message") ?? string.Empty,
            Seq: GetLong(item, "seq"))).ToArray();
    }

    private static async Task<TerminalTaskLogsLookup> TryFetchTaskLogsAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx, string requestId)
    {
        try
        {
            return new TerminalTaskLogsLookup(await FetchTaskLogsAsync(runtime, globals, ctx, requestId).ConfigureAwait(false), null);
        }
        catch (CliRemoteException ex) when (ex.StatusCode is 400 or 404 or 410)
        {
            return new TerminalTaskLogsLookup(Array.Empty<TerminalTaskLog>(), $"logs lookup HTTP {ex.StatusCode}: {CreatePreview(ex.ResponseBody ?? ex.Message)}");
        }
    }

    private static TerminalTaskSnapshot ParseTaskResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return ParseTaskResponse(doc.RootElement);
    }

    private static TerminalTaskSnapshot ParseTaskResponse(JsonElement item)
        => new(
            Id: GetInt(item, "id"),
            RequestId: GetString(item, "requestId") ?? string.Empty,
            ClientIdentity: GetString(item, "clientIdentity") ?? string.Empty,
            Status: GetString(item, "status") ?? "Unknown",
            StatusMessage: GetString(item, "statusMessage"),
            ReturnData: GetString(item, "returnData"),
            ExitCode: GetNullableInt(item, "exitCode"));

    private static object BuildCommandResult(TerminalHostInfo target, TerminalTaskSnapshot task, string shell, TerminalTaskLog[] logs, bool includeRawLogs, bool completed, string? resultLookupError)
    {
        var (stdout, stderr) = ComposeTaskOutput(task, logs);

        return new
        {
            submitted = true,
            host = target.BestName,
            target.ClientIdentity,
            shell,
            taskId = task.Id,
            requestId = task.RequestId,
            status = task.Status,
            statusMessage = task.StatusMessage,
            exitCode = task.ExitCode,
            completed,
            stdout,
            stderr,
            returnData = task.ReturnData,
            resultLookupError,
            logCount = logs.Length,
            logs = includeRawLogs ? logs : null
        };
    }

    private static object BuildTaskResult(TerminalTaskSnapshot task, TerminalTaskLog[] logs, bool includeRawLogs, string? resultLookupError)
    {
        var (stdout, stderr) = ComposeTaskOutput(task, logs);

        return new
        {
            taskId = task.Id,
            requestId = task.RequestId,
            clientIdentity = task.ClientIdentity,
            status = task.Status,
            statusMessage = task.StatusMessage,
            exitCode = task.ExitCode,
            completed = IsTerminalTaskStatus(task.Status),
            stdout,
            stderr,
            returnData = task.ReturnData,
            resultLookupError,
            logCount = logs.Length,
            logs = includeRawLogs ? logs : null
        };
    }

    private static (string Stdout, string Stderr) ComposeTaskOutput(TerminalTaskSnapshot task, TerminalTaskLog[] logs)
    {
        var stdout = string.Concat(logs.Where(log => IsStdout(log.Stream)).OrderBy(log => log.Seq).Select(log => log.Message));
        var stderr = string.Concat(logs.Where(log => IsStderr(log.Stream)).OrderBy(log => log.Seq).Select(log => log.Message));
        if ((!string.IsNullOrEmpty(stdout) || !string.IsNullOrEmpty(stderr)) || string.IsNullOrWhiteSpace(task.ReturnData))
        {
            return (stdout, stderr);
        }

        try
        {
            using var doc = JsonDocument.Parse(task.ReturnData);
            var root = doc.RootElement;
            return (
                ReadReturnStream(root, "stdout") ?? task.ReturnData,
                ReadReturnStream(root, "stderr") ?? string.Empty);
        }
        catch (JsonException)
        {
            return (task.ReturnData, string.Empty);
        }
    }

    private static string? ReadReturnStream(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var stream))
        {
            return null;
        }

        return stream.ValueKind switch
        {
            JsonValueKind.String => stream.GetString(),
            JsonValueKind.Array => string.Concat(stream.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())),
            _ => stream.ToString()
        };
    }

    private static object[] ShapeTaskList(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<object>();
        }

        return doc.RootElement.EnumerateArray().Select(ShapeTaskItem).ToArray();
    }

    private static object ShapeTaskItem(JsonElement task)
    {
        var created = GetString(task, "created");
        var completed = GetString(task, "completedAt");
        var preview = FirstPresent(GetString(task, "returnData"), GetString(task, "statusMessage"));
        return new
        {
            id = GetInt(task, "id"),
            requestId = GetString(task, "requestId"),
            status = GetString(task, "status"),
            taskType = GetString(task, "taskType"),
            target = FirstPresent(GetString(task, "clientHostName"), GetString(task, "clientDisplayName"), GetString(task, "clientName"), GetString(task, "clientIdentity")),
            clientIdentity = GetString(task, "clientIdentity"),
            environment = GetString(task, "environment"),
            created,
            completedAt = completed,
            runtimeMs = RuntimeMs(created, completed),
            exitCode = GetNullableInt(task, "exitCode"),
            preview = string.IsNullOrWhiteSpace(preview) ? null : CreatePreview(preview)
        };
    }

    private static bool IsTerminalTaskStatus(string status)
        => status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("TimedOut", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Timed Out", StringComparison.OrdinalIgnoreCase);

    private static bool IsStdout(string stream)
        => stream.Equals("stdout", StringComparison.OrdinalIgnoreCase)
            || stream.Equals("output", StringComparison.OrdinalIgnoreCase)
            || stream.Equals("info", StringComparison.OrdinalIgnoreCase);

    private static bool IsStderr(string stream)
        => stream.Equals("stderr", StringComparison.OrdinalIgnoreCase)
            || stream.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static string FormatHostCandidates(IEnumerable<TerminalHostInfo> clients)
        => string.Join("; ", clients.Take(10).Select(client => $"{client.BestName} ({client.ShortId}, online={client.Online}, enabled={client.Enabled})"));

    private static string? FirstPresent(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string CreatePreview(string value, int maxLength = 240)
    {
        var collapsed = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }

    private static string? PreviewProperty(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return string.IsNullOrWhiteSpace(value) ? null : CreatePreview(value);
    }

    private static long? RuntimeMs(string? created, string? completed)
    {
        if (DateTimeOffset.TryParse(created, out var start) &&
            DateTimeOffset.TryParse(completed, out var end) &&
            end >= start)
        {
            return Convert.ToInt64((end - start).TotalMilliseconds);
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static int? GetNullableInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static string[] GetStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray()
            : Array.Empty<string>();

    private sealed record TerminalHostInfo(
        string ClientIdentity,
        string ShortId,
        string? DisplayName,
        string? ClientName,
        string? HostName,
        bool Online,
        bool Enabled,
        string[] AvailableShells,
        string? DetectedOs,
        string? EffectiveTerminalTransport,
        bool TerminalTunnelConnected,
        string? LastHeartbeat)
    {
        public string BestName => FirstPresent(HostName, DisplayName, ClientName, ShortId) ?? ShortId;

        public bool ExactMatches(string query)
            => MatchValues().Any(value => value.Equals(query, StringComparison.OrdinalIgnoreCase));

        public bool Matches(string query)
            => MatchValues().Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));

        private IEnumerable<string> MatchValues()
            => new[] { HostName, DisplayName, ClientName, ClientIdentity, ShortId }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>();
    }

    private sealed record TerminalTaskSnapshot(
        int Id,
        string RequestId,
        string ClientIdentity,
        string Status,
        string? StatusMessage,
        string? ReturnData,
        int? ExitCode);

    private sealed record TerminalTaskLog(long Id, string Stream, string Message, long Seq);

    private sealed record TerminalTaskLookup(TerminalTaskSnapshot? Task, string? Error);

    private sealed record TerminalTaskLogsLookup(TerminalTaskLog[] Logs, string? Error);

    private enum AgentShape
    {
        Clients,
        Tasks,
        Jobs,
        JobRuns,
        Requests,
        Scripts,
        Events,
        Notifications,
        Logs,
        Search
    }

    private static Command GetListCommand(CliRuntime runtime, GlobalOptions globals, string name, string path, params (Option option, string queryName)[] query)
    {
        var command = new Command(name, $"GET {path}");
        foreach (var (option, _) in query) command.AddOption(option);
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, path + Query(ctx, query)));
        return command;
    }

    private static Command GetAgentListCommand(CliRuntime runtime, GlobalOptions globals, string name, string path, AgentShape shape, params (Option option, string queryName)[] query)
    {
        var command = new Command(name, $"GET {path}");
        foreach (var (option, _) in query) command.AddOption(option);
        var raw = new Option<bool>("--raw") { Description = "Print the unshaped API response." };
        command.AddOption(raw);
        command.SetHandler(async ctx =>
        {
            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, path + Query(ctx, query), null).ConfigureAwait(false);
            if (ctx.ParseResult.GetValueForOption(raw))
            {
                await WriteResponseBodyAsync(runtime, response.Body, response.StatusCode, ctx, globals).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(runtime, ShapeAgentResponse(response.Body, shape), ctx, globals).ConfigureAwait(false);
        });
        return command;
    }

    private static object ShapeAgentResponse(string body, AgentShape shape)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.EnumerateArray().Select(item => ShapeAgentItem(item, shape)).ToArray();
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "items", "results", "data", "entries", "logs" })
            {
                if (doc.RootElement.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    return new
                    {
                        nextSince = GetString(doc.RootElement, "nextSince"),
                        count = items.GetArrayLength(),
                        items = items.EnumerateArray().Select(item => ShapeAgentItem(item, shape)).ToArray()
                    };
                }
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static object ShapeAgentItem(JsonElement item, AgentShape shape)
        => shape switch
        {
            AgentShape.Clients => new
            {
                clientIdentity = GetString(item, "clientIdentity"),
                shortId = GetString(item, "shortId"),
                host = FirstPresent(GetString(item, "hostName"), GetString(item, "displayName"), GetString(item, "clientName")),
                online = GetBool(item, "online"),
                enabled = GetBool(item, "enabled"),
                environment = GetString(item, "environment"),
                tenant = FirstPresent(GetString(item, "tenantName"), GetString(item, "tenantId")),
                shells = GetStringArray(item, "availableShells"),
                agentVersion = GetString(item, "agentVersion"),
                lastHeartbeat = GetString(item, "lastHeartbeat")
            },
            AgentShape.Tasks => ShapeTaskItem(item),
            AgentShape.Jobs => new
            {
                id = FirstPresent(GetString(item, "id"), GetString(item, "jobId")),
                name = GetString(item, "name"),
                folder = FirstPresent(GetString(item, "folderPath"), GetString(item, "folder")),
                tenantId = GetString(item, "tenantId"),
                clientIdentity = GetString(item, "clientIdentity"),
                environment = GetString(item, "environment"),
                description = PreviewProperty(item, "description")
            },
            AgentShape.JobRuns => new
            {
                id = FirstPresent(GetString(item, "id"), GetString(item, "jobRunId")),
                jobId = GetString(item, "jobId"),
                job = FirstPresent(GetString(item, "jobName"), GetString(item, "name")),
                status = FirstPresent(GetString(item, "status"), GetString(item, "runStatus")),
                target = FirstPresent(GetString(item, "clientHostName"), GetString(item, "clientDisplayName"), GetString(item, "clientIdentity")),
                environment = GetString(item, "environment"),
                startedAt = FirstPresent(GetString(item, "startedAt"), GetString(item, "runStartedAt")),
                completedAt = FirstPresent(GetString(item, "completedAt"), GetString(item, "runCompletedAt")),
                error = PreviewProperty(item, "error")
            },
            AgentShape.Requests => new
            {
                id = GetString(item, "id"),
                title = GetString(item, "title"),
                status = GetString(item, "status"),
                tenantId = GetString(item, "tenantId"),
                jobId = GetString(item, "jobId"),
                createdAt = FirstPresent(GetString(item, "createdAtUtc"), GetString(item, "createdAt")),
                updatedAt = FirstPresent(GetString(item, "updatedAtUtc"), GetString(item, "updatedAt")),
                description = PreviewProperty(item, "description")
            },
            AgentShape.Scripts => new
            {
                id = GetString(item, "id"),
                name = GetString(item, "name"),
                folder = GetString(item, "folderPath"),
                scriptType = FirstPresent(GetString(item, "scriptType"), GetString(item, "type")),
                updatedAt = FirstPresent(GetString(item, "updatedAtUtc"), GetString(item, "updatedAt")),
                description = PreviewProperty(item, "description")
            },
            AgentShape.Events => new
            {
                id = GetString(item, "id"),
                type = FirstPresent(GetString(item, "eventType"), GetString(item, "type")),
                source = GetString(item, "source"),
                severity = GetString(item, "severity"),
                entityId = GetString(item, "entityId"),
                correlationId = GetString(item, "correlationId"),
                timestamp = FirstPresent(GetString(item, "timestamp"), GetString(item, "createdAt"), GetString(item, "occurredAt")),
                message = PreviewProperty(item, "message")
            },
            AgentShape.Notifications => new
            {
                id = GetString(item, "id"),
                severity = GetString(item, "severity"),
                title = GetString(item, "title"),
                message = PreviewProperty(item, "message"),
                isRead = GetBool(item, "isRead"),
                createdAt = FirstPresent(GetString(item, "createdAtUtc"), GetString(item, "createdAt"))
            },
            AgentShape.Logs => new
            {
                sequence = FirstPresent(GetString(item, "sequence"), GetString(item, "seq"), GetString(item, "id")),
                level = GetString(item, "level"),
                timestamp = FirstPresent(GetString(item, "timestamp"), GetString(item, "ts"), GetString(item, "createdAt")),
                correlationId = GetString(item, "correlationId"),
                message = PreviewProperty(item, "message")
            },
            AgentShape.Search => new
            {
                id = FirstPresent(GetString(item, "id"), GetString(item, "entityId"), GetString(item, "clientIdentity")),
                type = FirstPresent(GetString(item, "type"), GetString(item, "entityType"), GetString(item, "kind")),
                label = FirstPresent(GetString(item, "name"), GetString(item, "title"), GetString(item, "displayName"), GetString(item, "hostName")),
                status = GetString(item, "status"),
                preview = FirstPresent(PreviewProperty(item, "description"), PreviewProperty(item, "message"))
            },
            _ => JsonSerializer.Deserialize<JsonElement>(item.GetRawText())
        };

    private static Command GetByIdCommand(CliRuntime runtime, GlobalOptions globals, string name, string template, bool intId = false)
    {
        var id = new Argument<string>("id");
        var command = new Command(name, $"GET {template}") { id };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, Fill(template, ctx.ParseResult.GetValueForArgument(id))));
        return command;
    }

    private static Command GetByStringIdCommand(CliRuntime runtime, GlobalOptions globals, string name, string template, string argumentName)
    {
        var id = new Argument<string>(argumentName);
        var command = new Command(name, $"GET {template}") { id };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, Fill(template, ctx.ParseResult.GetValueForArgument(id))));
        return command;
    }

    private static Command GetByIdentityCommand(CliRuntime runtime, GlobalOptions globals, string name, string template)
    {
        var identity = new Argument<string>("identity");
        var command = new Command(name, $"GET {template}") { identity };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Get, template.Replace("{identity}", Escape(ctx.ParseResult.GetValueForArgument(identity)), StringComparison.Ordinal)));
        return command;
    }

    private static Command DeleteByIdCommand(CliRuntime runtime, GlobalOptions globals, string name, string template, bool intId = false)
    {
        var id = new Argument<string>("id");
        var command = new Command(name, $"DELETE {template}") { id };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Delete, Fill(template, ctx.ParseResult.GetValueForArgument(id))));
        return command;
    }

    private static Command DeleteByIdentityCommand(CliRuntime runtime, GlobalOptions globals, string name, string template)
    {
        var identity = new Argument<string>("identity");
        var command = new Command(name, $"DELETE {template}") { identity };
        command.SetHandler(ctx => SendAsync(runtime, globals, ctx, HttpMethod.Delete, template.Replace("{identity}", Escape(ctx.ParseResult.GetValueForArgument(identity)), StringComparison.Ordinal)));
        return command;
    }

    private static Command BodyCommand(CliRuntime runtime, GlobalOptions globals, string name, HttpMethod method, string template, params (string optionName, string jsonName)[] bodyOptions)
    {
        var command = new Command(name, $"{method.Method} {template}");
        var idArg = template.Contains("{id}", StringComparison.Ordinal) ? new Argument<string>("id") : null;
        var identityArg = template.Contains("{identity}", StringComparison.Ordinal) ? new Argument<string>("identity") : null;
        if (idArg is not null) command.AddArgument(idArg);
        if (identityArg is not null) command.AddArgument(identityArg);

        var body = new Option<string?>("--body") { Description = "JSON request body. Overrides field flags." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Path to JSON request body file. Overrides field flags." };
        command.AddOption(body);
        command.AddOption(bodyFile);
        var fields = new List<(Option<string?> option, string jsonName)>();
        foreach (var (optionName, jsonName) in bodyOptions)
        {
            var option = new Option<string?>(optionName) { Description = $"JSON field '{jsonName}'." };
            command.AddOption(option);
            fields.Add((option, jsonName));
        }

        command.SetHandler(ctx =>
        {
            var path = template;
            if (idArg is not null) path = path.Replace("{id}", Escape(ctx.ParseResult.GetValueForArgument(idArg)), StringComparison.Ordinal);
            if (identityArg is not null) path = path.Replace("{identity}", Escape(ctx.ParseResult.GetValueForArgument(identityArg)), StringComparison.Ordinal);
            var content = ReadBody(ctx.ParseResult.GetValueForOption(body), ctx.ParseResult.GetValueForOption(bodyFile), null)
                ?? BuildBodyFromOptions(ctx, fields);
            return SendAsync(runtime, globals, ctx, method, path, content);
        });

        return command;
    }

    private static async Task SendAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx, HttpMethod method, string path, string? body = null)
    {
        var response = await SendStringAsync(runtime, globals, ctx, method, path, body).ConfigureAwait(false);
        if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
        {
            await WriteResponseBodyAsync(runtime, response.Body, response.StatusCode, ctx, globals).ConfigureAwait(false);
        }
    }

    private static async Task<ApiStringResponse> SendStringAsync(CliRuntime runtime, GlobalOptions globals, CliInvocationContext ctx, HttpMethod method, string path, string? body)
    {
        var loaded = LoadConfig(ctx, globals);
        var result = await SendRawAsync(runtime, loaded.Resolved!, method, path, authenticated: true, body, ctx.GetCancellationToken()).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new CliRemoteException("api_request_failed", $"NetRatel API request failed with HTTP {result.StatusCode}.", result.StatusCode, result.Body);
        }

        return result;
    }

    private static async Task<ApiStringResponse> SendRawAsync(CliRuntime runtime, ResolvedCliConfig config, HttpMethod method, string path, bool authenticated, string? body, CancellationToken ct)
    {
        using var client = authenticated
            ? await runtime.CreateAuthenticatedClientAsync(config, ct).ConfigureAwait(false)
            : runtime.CreateHttpClient(config.ApiBaseUrl);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        return new ApiStringResponse((int)response.StatusCode, response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static LoadedConfig LoadConfig(CliInvocationContext ctx, GlobalOptions globals, bool requireAuth = true)
    {
        var store = new CliConfigStore(ctx.ParseResult.GetValueForOption(globals.ConfigPath));
        var file = store.Load();
        var env = CliConfig.FromEnvironment(Environment.GetEnvironmentVariable);
        var overrides = new CliConfig(
            ctx.ParseResult.GetValueForOption(globals.ApiBaseUrl),
            ctx.ParseResult.GetValueForOption(globals.TokenUrl),
            ctx.ParseResult.GetValueForOption(globals.ClientId),
            ctx.ParseResult.GetValueForOption(globals.Username),
            ctx.ParseResult.GetValueForOption(globals.AppPassword),
            ctx.ParseResult.GetValueForOption(globals.Scope));
        var merged = file.Merge(env).Merge(overrides);
        return new LoadedConfig(file, env.Merge(overrides), requireAuth ? merged.Resolve() : null);
    }

    private static async Task WriteResponseBodyAsync(CliRuntime runtime, string body, int statusCode, CliInvocationContext ctx, GlobalOptions globals)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteJsonAsync(runtime, new { status = statusCode }, ctx, globals).ConfigureAwait(false);
            return;
        }

        if (ctx.ParseResult.GetValueForOption(globals.Output) == "text")
        {
            await runtime.Out.WriteLineAsync(body).ConfigureAwait(false);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            await runtime.Out.WriteLineAsync(JsonSerializer.Serialize(doc.RootElement, ctx.ParseResult.GetValueForOption(globals.Pretty) ? PrettyJsonOptions : JsonOptions)).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(runtime, new { status = statusCode, body }, ctx, globals).ConfigureAwait(false);
        }
    }

    private static async Task WriteJsonAsync(CliRuntime runtime, object payload, CliInvocationContext ctx, GlobalOptions globals)
    {
        if (ctx.ParseResult.GetValueForOption(globals.Output) == "text")
        {
            await runtime.Out.WriteLineAsync(payload.ToString()).ConfigureAwait(false);
            return;
        }

        await runtime.Out.WriteLineAsync(JsonSerializer.Serialize(payload, ctx.ParseResult.GetValueForOption(globals.Pretty) ? PrettyJsonOptions : JsonOptions)).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(CliRuntime runtime, string code, string message, int exitCode, string? responseBody = null)
        => runtime.Error.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, error = new { code, message, exitCode, responseBody } }, JsonOptions));

    private static (string Code, string Message, int ExitCode, string? ResponseBody) MapException(Exception exception) => exception switch
    {
        CliValidationException ex => ("validation_error", ex.Message, CliExitCodes.ValidationError, null),
        CliRemoteException ex => (ex.Code, ex.Message, ex.StatusCode is 401 or 403 ? CliExitCodes.AuthError : CliExitCodes.RemoteError, ex.ResponseBody),
        HttpRequestException ex => ("remote_request_failed", ex.Message, CliExitCodes.RemoteError, null),
        TaskCanceledException => ("remote_timeout", "Remote request timed out.", CliExitCodes.RemoteError, null),
        _ => ("unexpected_error", exception.Message, CliExitCodes.RemoteError, null)
    };

    private static string Query(CliInvocationContext ctx, params (Option option, string queryName)[] options)
        => Query(options.Select(option => (option.queryName, ValueToString(ctx.ParseResult.GetValueForOption(option.option)))).ToArray());

    private static string Query(params (string name, string? value)[] values)
    {
        var items = values.Where(x => !string.IsNullOrWhiteSpace(x.value)).Select(x => $"{Escape(x.name)}={Escape(x.value!)}").ToArray();
        return items.Length == 0 ? string.Empty : "?" + string.Join("&", items);
    }

    private static string BuildBodyFromOptions(CliInvocationContext ctx, IEnumerable<(Option<string?> option, string jsonName)> fields)
    {
        var obj = new JsonObject();
        foreach (var (option, jsonName) in fields)
        {
            var value = ctx.ParseResult.GetValueForOption(option);
            if (value is null)
            {
                continue;
            }

            obj[jsonName] = ParseJsonValue(value);
        }

        return obj.ToJsonString(JsonOptions);
    }

    private static JsonNode? ParseJsonValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) || decimal.TryParse(trimmed, out _))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Treat malformed JSON-looking values as strings so agent commands still return validation from the API.
            }
        }

        return JsonValue.Create(value);
    }

    private static string? ReadBody(string? body, string? bodyFile, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(bodyFile))
        {
            return File.ReadAllText(bodyFile);
        }

        return body ?? fallback;
    }

    private static string Fill(string template, string? id)
        => template.Replace("{id}", Escape(id ?? throw new CliValidationException("id is required.")), StringComparison.Ordinal)
            .Replace("{ordinal}", "1", StringComparison.Ordinal);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static HttpMethod ResolveMethod(string method) => method.ToLowerInvariant() switch
    {
        "get" => HttpMethod.Get,
        "post" => HttpMethod.Post,
        "put" => HttpMethod.Put,
        "delete" => HttpMethod.Delete,
        _ => throw new CliValidationException("raw method must be get, post, put, or delete.")
    };

    private static void EnsureAllowedRawPath(string path)
    {
        var allowed = new[]
        {
            "/api/v1/auth/ai-agent/status",
            "/api/v1/ops/ai-agent/logs",
            "/api/v1/tenants",
            "/api/v1/script-library",
            "/api/v1/jobs",
            "/api/v1/jobruns",
            "/api/v2/tasks",
            "/api/v1/clients",
            "/api/v1/requests",
            "/api/v1/secrets",
            "/api/v1/global-search",
            "/api/v1/telemetry",
            "/api/v1/admin/connectivity",
            "/api/v1/admin/orchestration/netratel",
            "/api/v1/notifications",
            "/api/v1/events",
            "/api/v1/system",
            "/health"
        };

        if (!allowed.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CliValidationException($"Raw path '{path}' is not in the NetRatel CLI operator allow-list.");
        }
    }

    private static long? ExtractNextSince(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("nextSince", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ValueToString(object? value) => value switch
    {
        null => null,
        string s when string.IsNullOrWhiteSpace(s) => null,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static CliConfig Redact(CliConfig config) => config with
    {
        OidcAppPassword = string.IsNullOrWhiteSpace(config.OidcAppPassword) ? null : "***"
    };

    private static string? GetConfigValue(CliConfig config, string key, bool redact) => NormalizeConfigKey(key) switch
    {
        "apiBaseUrl" => config.ApiBaseUrl,
        "oidcTokenUrl" => config.OidcTokenUrl,
        "oidcClientId" => config.OidcClientId,
        "oidcUsername" => config.OidcUsername,
        "oidcAppPassword" => redact && !string.IsNullOrWhiteSpace(config.OidcAppPassword) ? "***" : config.OidcAppPassword,
        "oidcScope" => config.OidcScope,
        _ => throw new CliValidationException($"Unknown config key '{key}'.")
    };

    private static CliConfig SetConfigValue(CliConfig config, string key, string? value) => NormalizeConfigKey(key) switch
    {
        "apiBaseUrl" => config with { ApiBaseUrl = value },
        "oidcTokenUrl" => config with { OidcTokenUrl = value },
        "oidcClientId" => config with { OidcClientId = value },
        "oidcUsername" => config with { OidcUsername = value },
        "oidcAppPassword" => config with { OidcAppPassword = value },
        "oidcScope" => config with { OidcScope = value },
        _ => throw new CliValidationException($"Unknown config key '{key}'.")
    };

    private static string NormalizeConfigKey(string key) => key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "apibaseurl" or "btstoapibaseurl" => "apiBaseUrl",
        "oidctokenurl" or "btoidctokenurl" => "oidcTokenUrl",
        "oidcclientid" or "btoidcclientid" => "oidcClientId",
        "oidcusername" or "btoidcusername" => "oidcUsername",
        "oidcapppassword" or "btoidcapppassword" => "oidcAppPassword",
        "oidcscope" or "btoidcscope" => "oidcScope",
        _ => key
    };

    private sealed record LoadedConfig(CliConfig File, CliConfig Overrides, ResolvedCliConfig? Resolved);
    private sealed record ApiStringResponse(int StatusCode, bool IsSuccess, string Body)
    {
        public object ToHealth(string name) => new { name, status = StatusCode, ok = IsSuccess, body = Body };
    }

    private sealed class GlobalOptions
    {
        public Option<string?> ApiBaseUrl { get; } = new("--api-base-url") { Description = "NetRatel API base URL." };
        public Option<string?> TokenUrl { get; } = new("--token-url") { Description = "Oidc token URL." };
        public Option<string?> ClientId { get; } = new("--client-id") { Description = "Oidc client id." };
        public Option<string?> Username { get; } = new("--username") { Description = "Oidc service username." };
        public Option<string?> AppPassword { get; } = new("--app-password") { Description = "Oidc service-user app password." };
        public Option<string?> Scope { get; } = new("--scope") { Description = "Oidc OAuth scope." };
        public Option<string?> ConfigPath { get; } = new("--config") { Description = "CLI config file path." };
        public Option<string> Output { get; } = new("--output") { Description = "Output format: json or text.", DefaultValueFactory = _ => "json" };
        public Option<bool> Pretty { get; } = new("--pretty") { Description = "Pretty-print JSON output." };
        public Option<bool> Quiet { get; } = new("--quiet") { Description = "Suppress normal output." };

        public void AddTo(Command command)
        {
            foreach (var option in new Option[] { ApiBaseUrl, TokenUrl, ClientId, Username, AppPassword, Scope, ConfigPath, Output, Pretty, Quiet })
            {
                command.AddGlobalOption(option);
            }
        }
    }
}
