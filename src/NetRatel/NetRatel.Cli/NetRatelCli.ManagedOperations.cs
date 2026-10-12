using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Operations;

internal static partial class NetRatelCli
{
    private static Command BuildManagedOperationsCommand(CliRuntime runtime, GlobalOptions globals,
        string tool, string name, string description)
    {
        var command = new Command(name, description);
        AddManagedOperations(command, runtime, globals, tool);
        if (tool == "netratel_services")
        {
            var control = ManagedOperationCatalog.Find(tool, "control")!;
            foreach (var action in Enum.GetValues<ClientServiceControlAction>())
                command.AddCommand(BuildManagedOperationCommand(runtime, globals, control,
                    action.ToString().ToLowerInvariant(), action));
        }
        return command;
    }

    private static void AddManagedOperations(Command parent, CliRuntime runtime, GlobalOptions globals, string tool)
    {
        foreach (var descriptor in ManagedOperationCatalog.Operations.Where(item => item.Tool == tool))
            parent.AddCommand(BuildManagedOperationCommand(runtime, globals, descriptor, descriptor.Operation.Replace('_', '-')));
    }

    private static Command BuildManagedOperationCommand(CliRuntime runtime, GlobalOptions globals,
        ManagedOperationDescriptor descriptor, string name, ClientServiceControlAction? serviceAction = null)
    {
        var description = serviceAction is { } action
            ? $"{action} one exact service. Reuse --operation-id when checking or retrying the same request. Requires {descriptor.Permission}."
            : ManagedOperationDescription(descriptor);
        var command = new Command(name, description);
        var tenant = RequiredTenantIdOption();
        command.AddOption(tenant);
        Option<Guid>? agent = null;
        Option<Guid>? id = null;
        Option<string>? resource = null;
        Option<string>? callId = null;
        if (descriptor.AgentTarget)
        {
            agent = new Option<Guid>("--agent-id") { Required = true, Description = "Exact registered client UUID in this tenant." };
            command.AddOption(agent);
        }
        if (descriptor.IdProperty is { } idProperty)
        {
            id = new Option<Guid>("--" + ManagedOptionName(idProperty)) { Required = true, Description = $"The {idProperty} UUID in this tenant." };
            command.AddOption(id);
        }
        if (descriptor.ResourceKey)
        {
            resource = RequiredOption("--resource-key", "Exact resourceKey returned by the alert occurrence.");
            command.AddOption(resource);
        }
        if (descriptor.ApprovalTarget)
        {
            callId = RequiredOption("--call-id", "Exact pending approval callId returned by the investigation transcript.");
            command.AddOption(callId);
        }

        Option<int?>? limit = null;
        Option<string?>? cursor = null;
        Option<string?>? search = null;
        if (descriptor.Paged)
        {
            limit = new Option<int?>("--limit") { Description = "Maximum entries, from 1 through 100; defaults to 50." };
            cursor = new Option<string?>("--cursor") { Description = "Opaque cursor returned by the previous page." };
            command.AddOption(limit); command.AddOption(cursor);
        }
        if (descriptor.Searchable)
        {
            search = new Option<string?>("--search") { Description = "Optional client text filter, at most 256 characters." };
            command.AddOption(search);
        }
        Option<int?>? skip = null;
        if (descriptor.OffsetPaged)
        {
            skip = new Option<int?>("--skip") { Description = "Number of investigation rows to skip, from 0 through 100000. Pages scan 100 rows before policy filtering." };
            command.AddOption(skip);
        }
        Option<long?>? after = null;
        if (descriptor.SequencePaged)
        {
            after = new Option<long?>("--after") { Description = "Read transcript entries after the last returned event's nonnegative sequence; a page contains at most 200 events." };
            command.AddOption(after);
        }

        Option<string?>? body = null;
        Option<string?>? bodyFile = null;
        if (descriptor.DataType is { } dataType)
        {
            body = new Option<string?>("--body") { Description = $"Typed {dataType.Name} JSON data only; tenant and target IDs are supplied with flags. Choose this or --body-file." };
            bodyFile = new Option<string?>("--body-file") { Description = $"Path to a UTF-8 JSON file containing {dataType.Name} data, at most {ManagedOperationCatalog.MaximumRequestBytes} bytes." };
            command.AddOption(body); command.AddOption(bodyFile);
        }

        Option<string?>? service = null;
        Option<ClientServicePlatform?>? platform = null;
        Option<Guid?>? operationId = null;
        Option<string?>? reason = null;
        if (serviceAction is not null)
        {
            service = new Option<string?>("--service-name") { Description = "Canonical service name from inventory, for example Spooler or sshd.service." };
            platform = new Option<ClientServicePlatform?>("--platform") { Description = "Platform reported by inventory: Windows or LinuxSystemd." };
            operationId = new Option<Guid?>("--operation-id") { Description = "Caller-generated UUID for this operation. Reuse it for the same request; choose a new UUID for a new action." };
            reason = new Option<string?>("--reason") { Description = "Optional reason recorded with the service operation." };
            command.AddOption(service); command.AddOption(platform); command.AddOption(operationId); command.AddOption(reason);
        }

        Option<bool>? confirm = null;
        if (descriptor.Mutation)
        {
            confirm = new Option<bool>("--confirm") { Description = "Execute the reviewed change. Without this flag the API returns a preview." };
            command.AddOption(confirm);
        }

        command.SetHandler(async context =>
        {
            var request = new JsonObject { ["tenantId"] = context.ParseResult.GetValueForOption(tenant) };
            if (agent is not null) request["agentId"] = context.ParseResult.GetValueForOption(agent).ToString("D");
            if (id is not null) request[descriptor.IdProperty!] = context.ParseResult.GetValueForOption(id).ToString("D");
            if (resource is not null) request["resourceKey"] = context.ParseResult.GetValueForOption(resource);
            if (callId is not null) request["callId"] = context.ParseResult.GetValueForOption(callId);
            if (limit is not null && context.ParseResult.GetValueForOption(limit) is { } pageSize) request["limit"] = pageSize;
            if (cursor is not null && context.ParseResult.GetValueForOption(cursor) is { } pageCursor) request["cursor"] = pageCursor;
            if (search is not null && context.ParseResult.GetValueForOption(search) is { } query) request["search"] = query;
            if (skip is not null && context.ParseResult.GetValueForOption(skip) is { } offset) request["skip"] = offset;
            if (after is not null && context.ParseResult.GetValueForOption(after) is { } sequence) request["after"] = sequence;

            if (descriptor.DataType is not null)
            {
                var inline = context.ParseResult.GetValueForOption(body!);
                var file = context.ParseResult.GetValueForOption(bodyFile!);
                if (serviceAction is { } selectedAction)
                {
                    request["data"] = ServiceActionData(context, selectedAction, inline, file,
                        service!, platform!, operationId!, reason!);
                }
                else
                {
                    request["data"] = ReadManagedData(inline, file);
                }
            }

            using var document = JsonDocument.Parse(request.ToJsonString(ManagedOperationCatalog.JsonOptions));
            if (!ManagedOperationCatalog.TryValidate(descriptor, document.RootElement, out var validationError))
                throw new CliValidationException(validationError ?? "The selected operation request is invalid.");
            var envelope = new JsonObject
            {
                ["request"] = request,
                ["confirm"] = confirm is not null && context.ParseResult.GetValueForOption(confirm)
            };
            var payload = envelope.ToJsonString(ManagedOperationCatalog.JsonOptions);
            if (Encoding.UTF8.GetByteCount(payload) > ManagedOperationCatalog.MaximumEnvelopeBytes)
                throw new CliValidationException("The complete operation request exceeds the request size limit.");
            await SendAsync(runtime, globals, context, HttpMethod.Post,
                $"/api/v2/mcp/operator/application/{descriptor.Tool}/{descriptor.Operation}",
                payload).ConfigureAwait(false);
        });
        return command;
    }

    private static JsonNode ServiceActionData(CliInvocationContext context, ClientServiceControlAction action,
        string? inline, string? file, Option<string?> service, Option<ClientServicePlatform?> platform,
        Option<Guid?> operationId, Option<string?> reason)
    {
        var name = context.ParseResult.GetValueForOption(service);
        var selectedPlatform = context.ParseResult.GetValueForOption(platform);
        var selectedId = context.ParseResult.GetValueForOption(operationId);
        var selectedReason = context.ParseResult.GetValueForOption(reason);
        ClientServiceControlRequest data;
        if (inline is not null || file is not null)
        {
            if (name is not null || selectedPlatform is not null || selectedId is not null || selectedReason is not null)
                throw new CliValidationException("Use either typed --body/--body-file or the service selection flags for this action.");
            try
            {
                var body = ReadManagedData(inline, file).AsObject();
                if (!body.Any(property => string.Equals(property.Key, "action", StringComparison.OrdinalIgnoreCase)))
                    body["action"] = action.ToString();
                data = body.Deserialize<ClientServiceControlRequest>(ManagedOperationCatalog.JsonOptions)
                    ?? throw new CliValidationException("The service body must contain an operationId, serviceName and platform.");
            }
            catch (JsonException exception)
            {
                throw new CliValidationException($"The service body does not match ClientServiceControlRequest: {exception.Message}");
            }
            if (data.Action != 0 && data.Action != action)
                throw new CliValidationException("The body action must match the selected start, stop or restart command.");
            data = data with { Action = action };
        }
        else
        {
            if (name is null || selectedPlatform is null || selectedId is null)
                throw new CliValidationException("Supply --service-name, --platform and --operation-id, or use --body/--body-file.");
            data = new(selectedId.Value, name, selectedPlatform.Value, action, selectedReason);
        }

        if (data.OperationId == Guid.Empty || !ClientServiceControlRules.IsValidSelection(data.ServiceName, data.Platform, data.Action))
            throw new CliValidationException("Use a nonempty operation UUID and the exact service name and supported platform returned by inventory.");
        return JsonSerializer.SerializeToNode(data, ManagedOperationCatalog.JsonOptions)!;
    }

    private static JsonNode ReadManagedData(string? inline, string? file)
    {
        if ((inline is null) == (file is null))
            throw new CliValidationException("Supply exactly one of --body or --body-file with the typed operation data.");
        try
        {
            if (file is not null && new FileInfo(file).Length > ManagedOperationCatalog.MaximumRequestBytes)
                throw new CliValidationException("The JSON body file exceeds the operation request size limit.");
            var text = file is null ? inline! : File.ReadAllText(file, Encoding.UTF8);
            if (Encoding.UTF8.GetByteCount(text) > ManagedOperationCatalog.MaximumRequestBytes)
                throw new CliValidationException("The JSON body exceeds the operation request size limit.");
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 48 }) as JsonObject
                ?? throw new CliValidationException("The typed operation data must be a JSON object.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliValidationException($"Could not read the typed JSON operation data: {exception.Message}");
        }
    }

    private static string ManagedOptionName(string property) => string.Concat(property.Select((character, index) =>
        char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static string ManagedOperationDescription(ManagedOperationDescriptor descriptor)
    {
        var description = (descriptor.Tool, descriptor.Operation) switch
        {
            ("netratel_clients", "metadata") => "Read the hostname, friendly name and tags of an exact client.",
            ("netratel_clients", "set_metadata") => "Set the client friendly name and tags with {friendlyName,tags} data.",
            ("netratel_services", "inventory") => "Read available service observations, last complete inventory and connection status.",
            ("netratel_services", "refresh") => "Request a fresh service inventory. Read inventory again to observe the result.",
            ("netratel_services", "control") => "Submit typed {operationId,serviceName,platform,action,reason} service control data.",
            ("netratel_services", "operation") => "Read the status and result of a service operation using its operationId.",
            ("netratel_monitoring", "permissions") => "Read monitoring permissions for this tenant.",
            ("netratel_monitoring", "configuration") => "Read current rules, static target groups, silences and configuration revision before editing.",
            ("netratel_monitoring", "summary") => "Read the tenant monitoring summary and active alert counts.",
            ("netratel_monitoring", "series") => "Page through current monitoring series and alert states.",
            ("netratel_monitoring", "events") => "Page through monitoring events and occurrence history.",
            ("netratel_monitoring", "clients") => "Find client targets visible to the caller for monitoring.",
            ("netratel_monitoring", "client_series") => "Read monitoring series and alert state for one exact client.",
            ("netratel_monitoring", "published_flows") => "Read the published flow versions available for rule bindings.",
            ("netratel_monitoring", "preview_targets") => "Resolve a proposed target selection before saving a rule or group.",
            ("netratel_monitoring", "save_rule") => "Create or update a rule using {rule,expectedConfigurationRevision,reason} data.",
            ("netratel_monitoring", "save_group") => "Create or update a static group using {group,expectedConfigurationRevision,reason} data.",
            ("netratel_monitoring", "delete_rule") => "Delete a rule using the current configuration revision and an audited reason.",
            ("netratel_monitoring", "delete_group") => "Delete a static target group using the current configuration revision and an audited reason.",
            ("netratel_monitoring", "save_silence") => "Create or update a scoped monitoring silence with revision, reason, scope and expiry.",
            ("netratel_monitoring", "delete_silence") => "Revoke a monitoring silence with the current configuration revision and a reason.",
            ("netratel_monitoring", "acknowledge") => "Acknowledge the exact alert occurrence using occurrenceId and an audited reason.",
            ("netratel_monitoring", "clear") => "Clear the exact alert occurrence using occurrenceId and a reason; a continuing breach must satisfy a new breach window.",
            ("netratel_monitoring", "acknowledge_offline") => "Acknowledge the tenant rule's aggregate clients:offline occurrence. Use its ruleId and occurrenceId; no agent ID or resource override is accepted.",
            ("netratel_monitoring", "clear_offline") => "Clear the tenant rule's aggregate clients:offline occurrence. Use its ruleId and occurrenceId; requires a tenant-level operation policy.",
            ("netratel_flows", "list") => "List tenant flow drafts and their publication state.",
            ("netratel_flows", "get") => "Read one flow draft, graph and current revision.",
            ("netratel_flows", "template") => "Read a valid starting graph for a new monitoring flow.",
            ("netratel_flows", "connectors") => "List configured action connectors and their execution availability.",
            ("netratel_flows", "validate") => "Validate a FlowGraphDto before saving or publishing it.",
            ("netratel_flows", "dry_run") => "Evaluate {graph,input} sample event data without executing flow actions.",
            ("netratel_flows", "create") => "Create an inert flow draft with {name,useTemplate} data.",
            ("netratel_flows", "save_draft") => "Save {expectedRevision,name,graph} to an existing flow draft.",
            ("netratel_flows", "clone") => "Clone an existing draft using {expectedRevision,name} data.",
            ("netratel_flows", "set_enabled") => "Change flow availability with {expectedRevision,enabled} data.",
            ("netratel_flows", "publish") => "Publish an immutable version of the reviewed draft using {expectedRevision} data.",
            ("netratel_flows", "versions") => "List immutable published versions for a flow.",
            ("netratel_flows", "version") => "Read one immutable flow version and its graph.",
            ("netratel_flows", "runs") => "Read recent execution history for a flow.",
            ("netratel_flows", "run") => "Read one flow run, input and action receipts.",
            ("netratel_ai", "connection") => "Read the tenant Netclaw connection status without exposing credentials.",
            ("netratel_ai", "pair") => "Pair the tenant with a Netclaw daemon using the protected typed pairing request.",
            ("netratel_ai", "test_connection") => "Verify the authenticated Netclaw chat connection for this tenant.",
            ("netratel_ai", "set_enabled") => "Enable or disable the paired tenant Netclaw connection.",
            ("netratel_ai", "clients") => "List client targets available as AI investigation context.",
            ("netratel_ai", "list") => "Read recent AI investigations and alert-linked sessions in this tenant.",
            ("netratel_ai", "get") => "Read an investigation and its transcript; use --after for incremental transcript updates.",
            ("netratel_ai", "create") => "Create a new investigation with an explicit title and selected client context.",
            ("netratel_ai", "send") => "Send an operator message in the selected Netclaw investigation. Inspect its transcript for the response.",
            ("netratel_ai", "respond") => "Respond to the exact pending approval call returned by the investigation transcript.",
            ("netratel_ai", "reconcile") => "Reconcile an uncertain Netclaw turn before sending another message.",
            ("netratel_ai", "archive") => "Archive an investigation using its last message ID and explicit acknowledgement for uncertain delivery.",
            _ => descriptor.Operation.Replace('_', ' ') + "."
        };
        return description + $" Requires {string.Join(", ", new[] { descriptor.Permission }.Concat(descriptor.AdditionalPermissions))}.";
    }
}
