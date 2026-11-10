using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class NetRatelCli
{
    private static Command BuildReviewedJobsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var parent = new Command("operator-jobs", "Manage caller-owned reviewed job definitions, target policies and exact library steps.");
        foreach (var operation in new[] { "list", "get", "details", "params", "steps", "create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete" })
            parent.AddCommand(ReviewedJobCommand(runtime, globals, false, operation));
        return parent;
    }

    private static Command BuildReviewedJobRunsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var parent = new Command("operator-job-runs", "Preview and confirm one complete target plan; inspect its parent, nodes and isolated output.");
        foreach (var operation in new[] { "list", "query", "get", "steps", "logs", "nodes", "node_steps", "node_logs", "start", "cancel", "delete" })
            parent.AddCommand(ReviewedJobCommand(runtime, globals, true, operation));
        return parent;
    }

    private static Command ReviewedJobCommand(CliRuntime runtime, GlobalOptions globals, bool run, string operation)
    {
        var command = new Command(operation.Replace('_', '-'), "Use the closed JSON request shared with stdio and HTTP MCP. IDs are exact decimal strings.");
        var tenant = RequiredTenantIdOption();
        var agent = new Option<Guid>("--agent-id") { Required = true, Description = "The job's persisted caller-ownership context; execution uses its separately reviewed target plan." };
        var body = new Option<string?>("--body") { Description = "JSON request fields, excluding tenantId and agentId. Use --body-file for inputs." };
        var file = new Option<string?>("--body-file") { Description = "Path to the UTF-8 closed JSON request." };
        var confirm = new Option<bool>("--confirm") { Description = "Consume the exact preview planToken and idempotencyKey in the unchanged request." };
        command.AddOption(tenant); command.AddOption(agent); command.AddOption(body); command.AddOption(file); command.AddOption(confirm);
        command.SetHandler(async context =>
        {
            var tenantId = context.ParseResult.GetValueForOption(tenant);
            var agentId = context.ParseResult.GetValueForOption(agent);
            if (tenantId <= 0 || agentId == Guid.Empty) throw new CliValidationException("Supply a positive tenant and nonempty persisted client UUID.");
            var inline = context.ParseResult.GetValueForOption(body);
            var filename = context.ParseResult.GetValueForOption(file);
            JsonObject request;
            if (inline is null && filename is null) request = new JsonObject();
            else request = ReadManagedData(inline, filename).AsObject();
            if (request.ContainsKey("tenantId") || request.ContainsKey("agentId"))
                throw new CliValidationException("Target context belongs in --tenant-id and --agent-id.");
            var mutation = run ? operation is "start" or "cancel" or "delete" : operation is not ("list" or "get" or "details" or "params" or "steps");
            if (context.ParseResult.GetValueForOption(confirm) && !mutation)
                throw new CliValidationException("Read operations do not accept --confirm.");
            var root = $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/jobs";
            string path;
            if (mutation)
            {
                var phase = context.ParseResult.GetValueForOption(confirm) ? "confirm" : "preview";
                if (run)
                {
                    var idName = operation == "start" ? "jobId" : "jobRunId";
                    var id = ReadJobId(request, idName);
                    request.Remove(idName);
                    path = operation == "start" ? $"{root}/{id}/runs/{phase}" : $"{root}/runs/{id}/{operation}/{phase}";
                }
                else path = $"{root}/{phase}/{operation}";
                await SendAsync(runtime, globals, context, HttpMethod.Post, path, request.ToJsonString()).ConfigureAwait(false);
                return;
            }
            if (!run)
            {
                if (operation == "list") path = root;
                else
                {
                    var id = ReadJobId(request, "jobId"); request.Remove("jobId");
                    path = $"{root}/{id}" + (operation == "get" ? "" : $"/{operation}");
                }
            }
            else if (operation is "list" or "query")
            {
                path = $"{root}/runs" + (operation == "query" ? "/query" : "");
                if (request.ContainsKey("jobId"))
                {
                    path += $"?jobId={ReadJobId(request, "jobId")}"; request.Remove("jobId");
                }
            }
            else
            {
                var id = ReadJobId(request, "jobRunId"); request.Remove("jobRunId");
                path = $"{root}/runs/{id}";
                if (operation is "node_steps" or "node_logs")
                {
                    if (!Guid.TryParse(request["nodeAgentId"]?.ToString(), out var node) || node == Guid.Empty)
                        throw new CliValidationException("Supply nodeAgentId from the parent node list.");
                    request.Remove("nodeAgentId"); path += $"/nodes/{node:D}";
                    if (operation == "node_steps") path += "/steps";
                    else
                    {
                        var step = ReadJobId(request, "stepRunId"); request.Remove("stepRunId");
                        var limit = ReadPage(request, "limit", 500, 1000);
                        var bytes = ReadPage(request, "maximumBytes", 65536, 65536);
                        var cursor = request["cursor"]?.GetValue<string>(); request.Remove("cursor");
                        if (cursor?.Length > 64) throw new CliValidationException("Use the exact cursor from the previous output page.");
                        path += $"/steps/{step}/logs?limit={limit}&maximumBytes={bytes}" +
                            (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                    }
                }
                else if (operation != "get") path += $"/{operation}";
                if (operation == "nodes")
                {
                    var page = ReadPage(request, "page", 1, 100000);
                    var size = ReadPage(request, "pageSize", 50, 100);
                    path += $"?page={page}&pageSize={size}";
                }
            }
            if (request.Count > 0) throw new CliValidationException("The request contains unsupported fields for this operation.");
            await SendAsync(runtime, globals, context, HttpMethod.Get, path).ConfigureAwait(false);
        });
        return command;
    }

    private static string ReadJobId(JsonObject request, string name)
    {
        var text = request[name]?.ToString();
        if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new CliValidationException($"{name} must be an exact positive decimal identifier.");
        return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ReadPage(JsonObject request, string name, int fallback, int maximum)
    {
        if (!request.TryGetPropertyValue(name, out var value)) return fallback;
        request.Remove(name);
        if (!int.TryParse(value?.ToString(), out var result) || result < 1 || result > maximum)
            throw new CliValidationException($"{name} must be between 1 and {maximum}.");
        return result;
    }
}
