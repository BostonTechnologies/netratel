using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Core.Prompts;
using NetRatel.Mcp.Tools;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class McpCoreIsolationTests
{
    [Fact]
    public void Target_normalizes_its_base_uri_and_rejects_a_query_or_fragment()
    {
        var target = new NetRatelMcpTarget(
            "dev",
            new Uri("https://dev-api.example/path/"),
            new Uri("netratel://server/status"),
            NetRatelMcpCatalog.Revision);

        target.ApiBaseUri.Should().Be(new Uri("https://dev-api.example/path/"));
        Action invalid = () => _ = new NetRatelMcpTarget(
            "dev",
            new Uri("https://dev-api.example/?unexpected=true"),
            new Uri("netratel://server/status"),
            NetRatelMcpCatalog.Revision);

        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Core_registration_exposes_the_same_immutable_host_context_and_target()
    {
        var target = CreateTarget("dev");
        var context = new NetRatelMcpHostContext(target, NetRatelMcpTransport.StreamableHttp, "NetRatel.Mcp.Http");
        var services = new ServiceCollection().AddNetRatelMcpCore(context);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NetRatelMcpHostContext>().Should().BeSameAs(context);
        provider.GetRequiredService<NetRatelMcpTarget>().Should().BeSameAs(target);
    }

    [Fact]
    public void Core_registration_rejects_multiple_host_contexts()
    {
        var services = new ServiceCollection()
            .AddNetRatelMcpCore(new NetRatelMcpHostContext(CreateTarget("dev"), NetRatelMcpTransport.StreamableHttp, "NetRatel.Mcp.Http"));

        Action duplicate = () => services.AddNetRatelMcpCore(
            new NetRatelMcpHostContext(CreateTarget("prod"), NetRatelMcpTransport.StreamableHttp, "NetRatel.Mcp.Http"));

        duplicate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Catalog_is_explicit_and_omits_retired_v1_file_adapters()
    {
        NetRatelMcpCatalog.Tools.Should().OnlyHaveUniqueItems(tool => tool.Name);
        NetRatelMcpCatalog.Tools.Select(tool => tool.Name).Should().NotContain("netratel_client_files");
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_terminal").Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_config").Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_config").AvailableOverHttp.Should().BeFalse();
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_remote_support_v2").AvailableOverHttp.Should().BeTrue();
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_notifications").Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_health").AvailableOverHttp.Should().BeTrue();
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_files").Operations
            .Where(operation => operation.Name is "browse" or "read" or "download")
            .Should().OnlyContain(operation => operation.Environments == NetRatelMcpOperationEnvironment.All);
        NetRatelMcpCatalog.FindOperation("netratel_files", "stat")!.Environments
            .Should().Be(NetRatelMcpOperationEnvironment.Production);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_files").Operations
            .Where(operation => operation.Name is "artifact_status" or "preview_collect" or "confirm_collect" or "preview_artifact_cleanup" or "confirm_artifact_cleanup" or "preview_write_text" or "confirm_write_text" or "preview_upload" or "confirm_upload" or "preview_create_directory" or "confirm_create_directory" or "preview_delete" or "confirm_delete" or "preview_copy" or "confirm_copy" or "preview_move" or "confirm_move")
            .Should().OnlyContain(operation => operation.Environments == NetRatelMcpOperationEnvironment.Production);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_files").Operations
            .Where(operation => operation.Name is not "browse" and not "stat" and not "read" and not "artifact_status" and not "download" and not "preview_collect" and not "confirm_collect" and not "preview_artifact_cleanup" and not "confirm_artifact_cleanup" and not "preview_write_text" and not "confirm_write_text" and not "preview_upload" and not "confirm_upload" and not "preview_create_directory" and not "confirm_create_directory" and not "preview_delete" and not "confirm_delete" and not "preview_copy" and not "confirm_copy" and not "preview_move" and not "confirm_move")
            .Should().OnlyContain(operation => operation.Environments == NetRatelMcpOperationEnvironment.Development);
        NetRatelMcpCatalog.FindOperation("netratel_clients", "presence")!.Environments
            .Should().Be(NetRatelMcpOperationEnvironment.All);
    }

    [Fact]
    public void Catalog_descriptions_are_operator_complete_and_match_the_published_operation_contract()
    {
        foreach (var tool in NetRatelMcpCatalog.Tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.Description.Should().Contain("Set operation and supply only the matching closed request schema.");

            foreach (var operation in tool.Operations.Where(operation => operation.AvailableOverHttp || !tool.AvailableOverHttp))
                tool.Description.Should().Contain(operation.Name);

            if (tool.Operations.Any(operation => operation.RequiresConfirmation))
                tool.Description.Should().Contain("confirm: true");

            if (tool.AvailableOverHttp)
                tool.Description.Should().Contain("HTTP");
            else
                tool.Description.Should().Contain("unavailable over HTTP");
        }
    }

    [Fact]
    public void Explicit_catalog_matches_known_tool_classes_and_marks_http_operations_consistently()
    {
        var registeredToolNames = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Select(tool => tool.ProtocolTool.Name)
            .OrderBy(name => name)
            .ToArray();

        NetRatelMcpCatalog.Tools
            .Where(tool => tool.Operations.Any(operation => operation.IsAvailableIn("dev")))
            .Select(tool => tool.Name).OrderBy(name => name).Should().Equal(registeredToolNames);
        NetRatelMcpCatalog.Tools
            .SelectMany(tool => tool.Operations.Select(operation => $"{tool.Name}/{operation.Name}"))
            .Should().OnlyHaveUniqueItems();
        NetRatelMcpCatalog.Tools
            .Where(tool => !tool.AvailableOverHttp)
            .SelectMany(tool => tool.Operations)
            .Should().OnlyContain(operation => !operation.AvailableOverHttp);
        NetRatelMcpCatalog.Resources.Should().Contain(["netratel://health", "netratel://config/redacted", "netratel://schemas/catalog"]);
        NetRatelMcpCatalog.Prompts.Should().Contain("inspect_client");
    }

    [Fact]
    public void Capability_schema_exposes_only_the_tools_and_operations_available_to_its_transport()
    {
        var target = CreateTarget("dev");
        var httpContext = new NetRatelMcpHostContext(target, NetRatelMcpTransport.StreamableHttp, "NetRatel.Mcp.Http");
        var stdioContext = new NetRatelMcpHostContext(target, NetRatelMcpTransport.Stdio, "NetRatel.Mcp");

        using var httpDocument = JsonDocument.Parse(JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(httpContext)));
        using var stdioDocument = JsonDocument.Parse(JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(stdioContext)));

        var httpTools = httpDocument.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var stdioTools = stdioDocument.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var httpToolNames = httpTools.Select(tool => tool.GetProperty("Name").GetString()).ToArray();
        var stdioToolNames = stdioTools.Select(tool => tool.GetProperty("Name").GetString()).ToArray();

        httpToolNames.Should().HaveCount(22).And.NotContain(["netratel_config", "netratel_remote_support_v2", "netratel_connectivity", "netratel_events"]);
        stdioToolNames.Should().HaveCount(24).And.Contain(["netratel_config", "netratel_remote_support_v2"]);
        httpDocument.RootElement.GetProperty("toolCount").GetInt32().Should().Be(httpTools.Length);
        stdioDocument.RootElement.GetProperty("toolCount").GetInt32().Should().Be(stdioTools.Length);

        var httpNotifications = httpTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_notifications");
        httpNotifications.GetProperty("safety").GetString().Should().Be(nameof(NetRatelMcpOperationSafety.Read));
        httpNotifications
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().NotContain("mark_read");
        var stdioNotifications = stdioTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_notifications");
        stdioNotifications.GetProperty("safety").GetString().Should().Be(nameof(NetRatelMcpOperationSafety.OperatorMutation));
        stdioNotifications
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().Contain("mark_read");

        var fileRead = httpTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "read");
        fileRead.GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Files));
        fileRead.GetProperty("minimumRole").GetString().Should().Be(nameof(McpOperationMinimumRole.Operator));

        var policyInspection = httpTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_policy")
            .GetProperty("operations").EnumerateArray().ToArray();
        policyInspection.Should().HaveCount(17);
        policyInspection.Should().OnlyContain(operation =>
            operation.GetProperty("requiredScope").GetString() == nameof(McpOperationAccessScope.Admin) &&
            operation.GetProperty("minimumRole").GetString() == nameof(McpOperationMinimumRole.Administrator));
        policyInspection.Single(operation => operation.GetProperty("Name").GetString() == "confirm_create")
            .GetProperty("RequiresConfirmation").GetBoolean().Should().BeTrue();
        policyInspection.Single(operation => operation.GetProperty("Name").GetString() == "evaluate")
            .GetProperty("RequiresConfirmation").GetBoolean().Should().BeFalse();
        policyInspection.Where(operation => operation.GetProperty("Name").GetString() is "confirm_replace" or "confirm_disable" or "confirm_revoke" or "confirm_target_profile")
            .Should().OnlyContain(operation => operation.GetProperty("RequiresConfirmation").GetBoolean());

        var stdioConfigSet = stdioTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_config")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "set");
        stdioConfigSet.GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Admin));
        stdioConfigSet.GetProperty("minimumRole").GetString().Should().Be(nameof(McpOperationMinimumRole.Administrator));
    }

    [Fact]
    public void Production_http_catalog_and_tool_definitions_publish_policy_admitted_observability_reads_and_exclude_development_only_operations()
    {
        var context = new NetRatelMcpHostContext(
            CreateTarget("prod"),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(context)));

        var cataloguedTools = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var cataloguedNames = cataloguedTools.Select(tool => tool.GetProperty("Name").GetString()).ToArray();
        cataloguedNames.Should().Contain(["netratel_clients", "netratel_files", "netratel_client_logs", "netratel_client_telemetry", "netratel_scripts", "netratel_terminal", "netratel_onboarding"])
            .And.NotContain("netratel_marker_jobs");
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_scripts")
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().BeEquivalentTo("list", "get", "params", "validate", "create", "update", "parse_manifest", "run", "delete");
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_scripts")
            .GetProperty("operations").EnumerateArray()
            .Where(operation => operation.GetProperty("Name").GetString() is not "list" and not "get" and not "params" and not "validate")
            .Should().OnlyContain(operation => operation.GetProperty("RequiresConfirmation").GetBoolean());
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().Equal("browse", "read", "download", "stat", "artifact_status", "preview_collect", "preview_artifact_cleanup", "preview_write_text", "preview_upload", "preview_create_directory", "preview_delete", "preview_copy", "preview_move", "confirm_collect", "confirm_artifact_cleanup", "confirm_write_text", "confirm_upload", "confirm_create_directory", "confirm_delete", "confirm_copy", "confirm_move");
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "preview_write_text")
            .GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Write));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "confirm_upload")
            .GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Write));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "confirm_create_directory")
            .GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Write));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "confirm_delete")
            .GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Write));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "confirm_copy")
            .GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Write));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_files")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "confirm_move")
            .GetProperty("requiredScope").GetString().Should().Be(nameof(McpOperationAccessScope.Write));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_clients")
            .GetProperty("operations").EnumerateArray()
            .Should().OnlyContain(operation => operation.GetProperty("availableIn").EnumerateArray()
                .Select(value => value.GetString()).Contains("prod"));
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_terminal")
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().Equal("availability", "get", "stream_window", "diagnostics", "open", "resize", "close", "preview_open", "send_input");
        var onboarding = cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_onboarding")
            .GetProperty("operations").EnumerateArray().ToArray();
        onboarding.Select(operation => operation.GetProperty("Name").GetString())
            .Should().Equal("collateral", "get_enrollment", "collateral_download", "list_enrollments", "create_enrollment", "revoke_enrollment");
        onboarding.Should().OnlyContain(operation => operation.GetProperty("requiredScope").GetString() == nameof(McpOperationAccessScope.Onboarding));
        onboarding.Where(operation => operation.GetProperty("Name").GetString() is "create_enrollment" or "revoke_enrollment")
            .Should().OnlyContain(operation => operation.GetProperty("RequiresConfirmation").GetBoolean());
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_client_logs")
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().Equal("sources", "history", "search", "tail", "preview_resync", "confirm_resync");
        cataloguedTools.Single(tool => tool.GetProperty("Name").GetString() == "netratel_client_telemetry")
            .GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("Name").GetString())
            .Should().Equal("snapshot", "stream_window");

        NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .Select(tool => tool.ProtocolTool.Name)
            .Should().BeEquivalentTo(cataloguedNames);
    }

    [Fact]
    public void Shared_http_tool_types_implement_every_catalogued_http_tool_and_nothing_excluded()
    {
        var sharedHttpToolNames = new[] { typeof(NetRatelMcpCatalogTools), typeof(NetRatelMcpOperationalTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        var cataloguedHttpToolNames = NetRatelMcpCatalog.Tools
            .Where(tool => tool.AvailableOverHttp)
            .Select(tool => tool.Name)
            .OrderBy(name => name)
            .ToArray();

        sharedHttpToolNames.Should().Equal(cataloguedHttpToolNames);
        sharedHttpToolNames.Should().HaveCount(27);
        sharedHttpToolNames.Should().NotContain("netratel_config");
        sharedHttpToolNames.Should().Contain("netratel_remote_support_v2");
    }

    [Fact]
    public void Http_tool_definitions_are_catalogued_closed_discriminated_read_contracts()
    {
        var definitions = NetRatelMcpToolDefinitions.CreateForHttp();
        var expectedNames = NetRatelMcpCatalog.Tools
            .Where(tool => tool.AvailableOverHttp && tool.Operations.Any(operation => operation.IsAvailableOverHttpIn("dev")))
            .Select(tool => tool.Name)
            .OrderBy(name => name)
            .ToArray();

        definitions.Select(tool => tool.ProtocolTool.Name).Should().Equal(expectedNames);
        foreach (var definition in definitions)
        {
            var schema = definition.ProtocolTool.InputSchema;
            schema.GetProperty("type").GetString().Should().Be("object");
            var alternatives = schema.GetProperty("oneOf").EnumerateArray().ToArray();
            alternatives.Should().NotBeEmpty();
            foreach (var alternative in alternatives)
            {
                alternative.GetProperty("type").GetString().Should().Be("object");
                alternative.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
                alternative.GetProperty("properties").GetProperty("operation").TryGetProperty("const", out var operation).Should().BeTrue();
                operation.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined);
            }
            definition.ProtocolTool.OutputSchema!.Value.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public void Development_operator_surface_publishes_the_v2_catalog_and_production_target_schemas()
    {
        var hostContext = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(
                "dev",
                new Uri("https://api.dev.example/"),
                new Uri("https://mcp.dev.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests",
            operatorSurfaceEnabled: true);

        var tools = NetRatelMcpToolDefinitions.CreateForHttp(hostContext)
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        var productionTools = NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

        tools.Should().HaveCount(26);
        productionTools.Should().HaveCount(26);
        tools.Should().ContainKeys("netratel_commands", "netratel_connectivity", "netratel_events", "netratel_requests");
        tools.Should().NotContainKey("netratel_marker_jobs");
        productionTools.Should().NotContainKey("netratel_marker_jobs", "marker jobs are a Development-only QA compatibility contract, not a Production operator capability");
        AlternativesByOperation(tools["netratel_commands"].ProtocolTool.InputSchema)["execute"].GetRawText()
            .Should().Be(AlternativesByOperation(productionTools["netratel_commands"].ProtocolTool.InputSchema)["execute"].GetRawText());
        AlternativesByOperation(tools["netratel_jobs"].ProtocolTool.InputSchema)["list"].GetRawText()
            .Should().Be(AlternativesByOperation(productionTools["netratel_jobs"].ProtocolTool.InputSchema)["list"].GetRawText());
        JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(hostContext)).Should().Contain("netratel_commands");
        foreach (var (name, tool) in tools)
            AlternativesByOperation(tool.ProtocolTool.InputSchema).Keys.Should()
                .BeEquivalentTo(AlternativesByOperation(productionTools[name].ProtocolTool.InputSchema).Keys);
        AlternativesByOperation(tools["netratel_files"].ProtocolTool.InputSchema).Keys.Should().NotContain(["status", "collect", "cleanup"]);
        AlternativesByOperation(tools["netratel_terminal"].ProtocolTool.InputSchema).Keys.Should().NotContain(["stream", "fixture", "self_test", "deployment-control-plane_inspect"]);
        tools["netratel_scripts"].ProtocolTool.Description.Should().Contain("taskId").And.NotContain("marker-script");
        tools["netratel_tasks"].ProtocolTool.InputSchema.GetRawText().Should().NotContain("Production tenant");
        using var catalog = JsonDocument.Parse(JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(hostContext)));
        foreach (var descriptor in catalog.RootElement.GetProperty("tools").EnumerateArray())
            descriptor.GetProperty("Description").GetString().Should().Be(tools[descriptor.GetProperty("Name").GetString()!].ProtocolTool.Description);
    }

    [Fact]
    public void Development_operator_surface_catalog_advertises_its_effective_v2_availability()
    {
        var hostContext = new NetRatelMcpHostContext(
            CreateTarget("dev"),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests",
            operatorSurfaceEnabled: true);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(NetRatelMcpCatalogTools.CatalogSchema(hostContext)));

        var commandAvailability = document.RootElement.GetProperty("tools").EnumerateArray()
            .Single(tool => tool.GetProperty("Name").GetString() == "netratel_commands")
            .GetProperty("operations").EnumerateArray()
            .Single(operation => operation.GetProperty("Name").GetString() == "availability")
            .GetProperty("availableIn").EnumerateArray()
            .Select(instance => instance.GetString())
            .ToArray();

        commandAvailability.Should().Equal("dev", "prod");
    }

    [Fact]
    public void Http_tool_definitions_require_the_exact_identifiers_for_addressed_reads()
    {
        var jobs = NetRatelMcpToolDefinitions.CreateForHttp()
            .Single(tool => tool.ProtocolTool.Name == "netratel_job_runs")
            .ProtocolTool.InputSchema;
        var logs = jobs.GetProperty("oneOf").EnumerateArray()
            .Single(alternative => alternative.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == "logs");

        logs.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("operation", "request");
        var request = logs.GetProperty("properties").GetProperty("request");
        request.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        request.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("jobRunId", "ordinal");
        request.GetProperty("properties").GetProperty("ordinal").GetProperty("oneOf").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Client_observability_contracts_are_closed_target_gated_and_keep_production_resync_plan_bound_in_both_transports()
    {
        var http = NetRatelMcpToolDefinitions.CreateForHttp().ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        var stdio = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers).ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

        foreach (var name in new[] { "netratel_client_logs", "netratel_client_telemetry" })
        {
            http[name].ProtocolTool.InputSchema.GetRawText().Should().Be(stdio[name].ProtocolTool.InputSchema.GetRawText());
            var alternatives = AlternativesByOperation(http[name].ProtocolTool.InputSchema);
            foreach (var (operation, alternative) in alternatives)
            {
                var hasConfirm = alternative.GetProperty("properties").TryGetProperty("confirm", out var confirm);
                hasConfirm.Should().Be(name == "netratel_client_logs" && (operation is "resync" or "confirm_resync"));
                if (hasConfirm) confirm.GetProperty("default").GetBoolean().Should().BeFalse();
            }
        }

        AlternativesByOperation(http["netratel_client_logs"].ProtocolTool.InputSchema).Keys.Should().BeEquivalentTo(["sources", "history", "search", "tail", "resync", "preview_resync", "confirm_resync"]);
        var history = AlternativesByOperation(http["netratel_client_logs"].ProtocolTool.InputSchema)["history"]
            .GetProperty("properties").GetProperty("request");
        history.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        history.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "sourceId");
        var search = AlternativesByOperation(http["netratel_client_logs"].ProtocolTool.InputSchema)["search"]
            .GetProperty("properties").GetProperty("request");
        search.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "sourceId", "text");
        var confirmResync = AlternativesByOperation(http["netratel_client_logs"].ProtocolTool.InputSchema)["confirm_resync"]
            .GetProperty("properties").GetProperty("request");
        confirmResync.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("tenantId", "agentId", "sourceId", "planToken", "idempotencyKey");
        AlternativesByOperation(http["netratel_client_telemetry"].ProtocolTool.InputSchema).Keys.Should().BeEquivalentTo(["snapshot", "stream_window"]);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_client_logs").Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_client_telemetry").Safety.Should().Be(NetRatelMcpOperationSafety.Read);
    }

    [Fact]
    public void Stdio_migrated_definitions_keep_every_shared_read_contract_identical_to_http()
    {
        var stdio = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers);
        var http = NetRatelMcpToolDefinitions.CreateForHttp()
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

        stdio.Should().HaveCount(NetRatelMcpCatalog.Tools.Count(tool => tool.Operations.Any(operation => operation.IsAvailableIn("dev"))));
        foreach (var definition in stdio)
        {
            if (!http.TryGetValue(definition.ProtocolTool.Name, out var httpDefinition))
            {
                definition.ProtocolTool.InputSchema.GetProperty("type").GetString().Should().Be("object");
                definition.ProtocolTool.InputSchema.GetProperty("oneOf").EnumerateArray().Should().NotBeEmpty();
                definition.ProtocolTool.OutputSchema!.Value.GetRawText().Should().Be(NetRatelMcpToolDefinitions.CreateForHttp().First().ProtocolTool.OutputSchema!.Value.GetRawText());
                continue;
            }

            definition.ProtocolTool.OutputSchema!.Value.GetRawText().Should().Be(httpDefinition.ProtocolTool.OutputSchema!.Value.GetRawText());
            if (definition.ProtocolTool.Name is not ("netratel_notifications" or "netratel_search"))
            {
                definition.ProtocolTool.InputSchema.GetRawText().Should().Be(httpDefinition.ProtocolTool.InputSchema.GetRawText());
                continue;
            }

            var stdioAlternatives = AlternativesByOperation(definition.ProtocolTool.InputSchema);
            foreach (var (operation, httpAlternative) in AlternativesByOperation(httpDefinition.ProtocolTool.InputSchema))
            {
                stdioAlternatives[operation].GetRawText().Should().Be(httpAlternative.GetRawText());
            }
        }
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("prod")]
    public void Http_search_schema_only_advertises_delegated_discovery(string instance)
    {
        var http = NetRatelMcpToolDefinitions.CreateForHttp(instance)
            .Single(tool => tool.ProtocolTool.Name == "netratel_search").ProtocolTool.InputSchema;
        var stdio = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers, instance)
            .Single(tool => tool.ProtocolTool.Name == "netratel_search").ProtocolTool.InputSchema;

        AlternativesByOperation(http).Keys.Should().BeEquivalentTo("tenants", "clients", "scripts", "jobs", "requests", "tasks");
        AlternativesByOperation(stdio).Keys.Should().BeEquivalentTo("tenants", "clients", "scripts", "jobs", "requests", "tasks");
        foreach (var (operation, alternative) in AlternativesByOperation(http))
            alternative.GetRawText().Should().Be(AlternativesByOperation(stdio)[operation].GetRawText());
    }

    [Fact]
    public void Stdio_notification_mutation_contract_is_closed_bounded_and_confirmation_gated()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_notifications")
            .ProtocolTool.InputSchema;
        var markRead = AlternativesByOperation(schema)["mark_read"];

        markRead.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        markRead.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("operation", "request");
        var properties = markRead.GetProperty("properties");
        properties.GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
        properties.GetProperty("confirm").GetProperty("default").GetBoolean().Should().BeFalse();
        var ids = properties.GetProperty("request").GetProperty("properties").GetProperty("ids");
        ids.GetProperty("type").GetString().Should().Be("array");
        ids.GetProperty("minItems").GetInt32().Should().Be(1);
        ids.GetProperty("maxItems").GetInt32().Should().Be(200);
        ids.GetProperty("uniqueItems").GetBoolean().Should().BeTrue();
        ids.GetProperty("items").GetProperty("maxLength").GetInt32().Should().Be(200);

        var response = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_notifications")
            .ProtocolTool.OutputSchema!.Value;
        response.GetProperty("properties").GetProperty("requiresConfirmation").GetProperty("type").GetString().Should().Be("boolean");
        response.GetProperty("properties").GetProperty("confirmation").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        response.GetProperty("properties").GetProperty("affectedIds").GetProperty("oneOf").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Production_control_plane_event_and_connectivity_operations_have_only_the_reviewed_contracts()
    {
        var tools = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers, "prod")
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

        AlternativesByOperation(tools["netratel_events"].ProtocolTool.InputSchema).Keys.Should().BeEquivalentTo(["list", "get", "preview_retry", "retry", "preview_disable", "disable"]);
        AlternativesByOperation(tools["netratel_connectivity"].ProtocolTool.InputSchema).Keys.Should().BeEquivalentTo(["settings", "netratel", "preview_test", "test"]);
        var developmentTools = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        var jobRuns = AlternativesByOperation(developmentTools["netratel_job_runs"].ProtocolTool.InputSchema);
        jobRuns.Keys.Should().BeEquivalentTo(["list", "query", "get", "steps", "logs"]);
        jobRuns.Should().NotContainKeys("start", "cancel", "delete", "cleanup_cancelled_tasks");
        jobRuns["list"].GetProperty("properties").GetProperty("request").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        jobRuns["query"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("pageSize").GetProperty("maximum").GetInt32().Should().Be(100);
    }

    [Fact]
    public void Stdio_job_definition_contract_has_only_the_verified_bounded_reads()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_jobs")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(schema);

        alternatives.Keys.Should().BeEquivalentTo(["list", "get", "details", "params", "steps"]);
        alternatives["list"].GetProperty("properties").GetProperty("request").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        alternatives["list"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("folder").GetProperty("maxLength").GetInt32().Should().Be(512);
        alternatives["get"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("jobId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Production_job_contract_exposes_only_owned_closed_lifecycle_operations()
    {
        var tools = NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        var jobs = AlternativesByOperation(tools["netratel_jobs"].ProtocolTool.InputSchema);
        var runs = AlternativesByOperation(tools["netratel_job_runs"].ProtocolTool.InputSchema);

        jobs.Keys.Should().BeEquivalentTo(["list", "get", "details", "params", "steps", "create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete"]);
        runs.Keys.Should().BeEquivalentTo(["list", "query", "get", "steps", "logs", "start", "cancel", "delete"]);
        var create = jobs["create"].GetProperty("properties").GetProperty("request");
        create.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        create.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "job");
        jobs["update"].GetProperty("properties").GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
        var step = jobs["step_add"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("step");
        step.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        step.GetProperty("properties").GetProperty("scriptId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        var start = runs["start"].GetProperty("properties").GetProperty("request");
        start.GetProperty("properties").GetProperty("jobId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        runs["cancel"].GetProperty("properties").GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void Production_task_contract_exposes_only_owned_closed_lifecycle_operations()
    {
        var task = NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .Single(tool => tool.ProtocolTool.Name == "netratel_tasks")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(task);

        alternatives.Keys.Should().BeEquivalentTo(["list", "recent", "get", "logs", "logs_by_request", "create_command", "run_library_script", "cancel"]);
        var command = alternatives["create_command"].GetProperty("properties").GetProperty("request");
        command.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        command.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "command");
        command.GetProperty("properties").GetProperty("command").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var script = alternatives["run_library_script"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("script");
        script.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        script.GetProperty("properties").GetProperty("scriptId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        alternatives["cancel"].GetProperty("properties").GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public void Production_request_contract_exposes_only_owned_closed_lifecycle_operations()
    {
        var request = NetRatelMcpToolDefinitions.CreateForHttp("prod")
            .Single(tool => tool.ProtocolTool.Name == "netratel_requests")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(request);

        alternatives.Keys.Should().BeEquivalentTo(["list", "get", "create", "update", "claim", "complete", "fail", "cancel"]);
        var create = alternatives["create"].GetProperty("properties").GetProperty("request");
        create.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        create.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "jobId", "summary");
        alternatives["get"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("requestId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        alternatives["update"].GetProperty("properties").GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
        alternatives["cancel"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("resultSummary").GetProperty("maxLength").GetInt32().Should().Be(48 * 1024);
    }

    [Fact]
    public void Stdio_client_contract_has_only_the_source_backed_bounded_reads()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_clients")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(schema);

        alternatives.Keys.Should().BeEquivalentTo(["presence", "binding", "telemetry", "update_attempts"]);
        var presence = alternatives["presence"].GetProperty("properties").GetProperty("request");
        presence.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        presence.GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32().Should().Be(100);
        var binding = alternatives["binding"].GetProperty("properties").GetProperty("request");
        binding.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId");
        binding.GetProperty("properties").GetProperty("agentId").GetProperty("format").GetString().Should().Be("uuid");
        var telemetry = alternatives["telemetry"].GetProperty("properties").GetProperty("request");
        var telemetryAlternatives = telemetry.GetProperty("oneOf");
        telemetryAlternatives.GetArrayLength().Should().Be(2);
        telemetryAlternatives[0].GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("clientIdentity");
        telemetryAlternatives[1].GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId");
        telemetryAlternatives[1].GetProperty("properties").GetProperty("agentId").GetProperty("format").GetString().Should().Be("uuid");
        alternatives["update_attempts"].GetProperty("properties").GetProperty("request").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        alternatives["update_attempts"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("status").GetProperty("enum").GetArrayLength().Should().Be(9);
    }

    [Fact]
    public void Stdio_task_contract_has_only_closed_source_backed_v2_reads()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_tasks")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(schema);

        alternatives.Keys.Should().BeEquivalentTo(["list", "recent", "get", "logs", "logs_by_request"]);
        alternatives["list"].GetProperty("properties").GetProperty("request").GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("requestId");
        alternatives["recent"].GetProperty("properties").GetProperty("request").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        alternatives["recent"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("agentId").GetProperty("format").GetString().Should().Be("uuid");
        alternatives["get"].GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("taskId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        foreach (var alternative in alternatives.Values)
        {
            alternative.GetProperty("properties").TryGetProperty("confirm", out var confirm).Should().BeFalse();
            confirm.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Undefined);
        }
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_tasks").Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
    }

    [Fact]
    public void Operational_log_search_contract_exposes_a_nonnegative_sequence_cursor()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForHttp()
            .Single(tool => tool.ProtocolTool.Name == "netratel_logs")
            .ProtocolTool.InputSchema;
        var search = AlternativesByOperation(schema)["search"];
        var request = search.GetProperty("properties").GetProperty("request");
        var since = request.GetProperty("properties").GetProperty("since");

        request.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        request.GetProperty("required").EnumerateArray().Should().BeEmpty();
        since.GetProperty("description").GetString().Should().Be("Optional exclusive log sequence cursor.");
        var alternatives = since.GetProperty("oneOf").EnumerateArray().ToArray();
        alternatives.Should().HaveCount(2);
        alternatives.Should().Contain(alternative =>
            alternative.GetProperty("type").GetString() == "integer" &&
            alternative.GetProperty("minimum").GetInt32() == 0);
        alternatives.Should().Contain(alternative =>
            alternative.GetProperty("type").GetString() == "string" &&
            alternative.GetProperty("pattern").GetString() == "^(0|[1-9][0-9]{0,18})$");
    }

    [Fact]
    public void Stdio_script_contract_keeps_closed_development_markers_and_production_owned_lifecycle_operations()
    {
        var developmentSchema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_scripts")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(developmentSchema);

        alternatives.Keys.Should().BeEquivalentTo(["list", "get", "params", "create_marker", "update_marker", "parse_manifest", "run", "delete"]);
        foreach (var operation in new[] { "list", "get", "params", "create_marker", "update_marker", "parse_manifest", "run", "delete" })
        {
            var request = alternatives[operation].GetProperty("properties").GetProperty("request");
            request.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        }
        alternatives["list"].GetProperty("properties").GetProperty("request").GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId");
        foreach (var operation in new[] { "get", "params", "parse_manifest", "run", "delete" })
        {
            var request = alternatives[operation].GetProperty("properties").GetProperty("request");
            request.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "scriptId");
            request.GetProperty("properties").GetProperty("scriptId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        }
        foreach (var operation in new[] { "create_marker", "update_marker", "parse_manifest", "run", "delete" })
        {
            alternatives[operation].GetProperty("properties").GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
        }
        var productionSchema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers, "prod")
            .Single(tool => tool.ProtocolTool.Name == "netratel_scripts")
            .ProtocolTool.InputSchema;
        var productionAlternatives = AlternativesByOperation(productionSchema);
        productionAlternatives.Keys.Should().BeEquivalentTo(["list", "get", "params", "validate", "create", "update", "parse_manifest", "run", "delete"]);
        var create = productionAlternatives["create"].GetProperty("properties").GetProperty("request");
        create.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal(
            "tenantId", "agentId", "name", "description", "shellType", "content", "contentHash", "parameters", "timeoutSeconds", "workingDirectory", "declaredSideEffects");
        create.GetProperty("properties").GetProperty("contentHash")
            .GetProperty("pattern").GetString().Should().Be("^[A-Fa-f0-9]{64}$");
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_scripts").Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
    }

    [Fact]
    public async Task Marker_job_contract_is_closed_confirmation_gated_and_sends_no_caller_content()
    {
        var httpSchema = NetRatelMcpToolDefinitions.CreateForHttp()
            .Single(tool => tool.ProtocolTool.Name == "netratel_marker_jobs")
            .ProtocolTool.InputSchema;
        var stdioSchema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_marker_jobs")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(httpSchema);

        alternatives.Keys.Should().BeEquivalentTo(["create", "run", "cancel", "delete"]);
        stdioSchema.GetRawText().Should().Be(httpSchema.GetRawText());
        foreach (var alternative in alternatives.Values)
        {
            alternative.GetProperty("properties").GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");
            alternative.GetProperty("properties").GetProperty("request").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        }
        alternatives["create"].GetProperty("properties").GetProperty("request").GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "scriptId");
        alternatives["run"].GetProperty("properties").GetProperty("request").GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "jobId");
        alternatives["cancel"].GetProperty("properties").GetProperty("request").GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId", "agentId", "jobId", "runId");
        NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_marker_jobs").Safety.Should().Be(NetRatelMcpOperationSafety.DevelopmentMutation);

        var client = new RecordingMcpApiClient();
        var tools = new NetRatelMcpOperationalTools(client);
        using var request = JsonDocument.Parse("{\"tenantId\":3,\"agentId\":\"d2719b66-0282-4c15-88f4-9d3ad81ab006\",\"scriptId\":42}");

        var preview = await tools.netratel_marker_jobs("create", request.RootElement, confirm: false);
        var confirmed = await tools.netratel_marker_jobs("create", request.RootElement, confirm: true);

        preview.Status.Should().Be("confirmation_required");
        preview.RequiresConfirmation.Should().BeTrue();
        client.Requests.Should().ContainSingle();
        client.Requests.Single().Method.Should().Be(HttpMethod.Post);
        client.Requests.Single().Path.Should().Be("/api/v2/development/mcp/agents/3/d2719b66-0282-4c15-88f4-9d3ad81ab006/marker-jobs/42");
        client.Requests.Single().Body.Should().BeNull();
        confirmed.Success.Should().BeTrue();
    }

    [Fact]
    public void Stdio_tenant_contract_has_only_closed_source_backed_reads()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .Single(tool => tool.ProtocolTool.Name == "netratel_tenants")
            .ProtocolTool.InputSchema;
        var alternatives = AlternativesByOperation(schema);

        alternatives.Keys.Should().BeEquivalentTo(["list", "get"]);
        var request = alternatives["get"].GetProperty("properties").GetProperty("request");
        request.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        request.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("tenantId");
        request.GetProperty("properties").GetProperty("tenantId").GetProperty("oneOf").GetArrayLength().Should().Be(2);
        foreach (var alternative in alternatives.Values)
        {
            alternative.GetProperty("properties").TryGetProperty("confirm", out var confirm).Should().BeFalse();
            confirm.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Undefined);
        }
        var tenantTool = NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_tenants");
        tenantTool.Safety.Should().Be(NetRatelMcpOperationSafety.OperatorMutation);
        tenantTool.Operations.Where(operation => operation.Name is "list" or "get")
            .Should().OnlyContain(operation => operation.Safety == NetRatelMcpOperationSafety.Read && operation.Environments == NetRatelMcpOperationEnvironment.All);
        tenantTool.Operations.Where(operation => operation.Name is "create" or "update" or "delete")
            .Should().OnlyContain(operation => operation.Safety == NetRatelMcpOperationSafety.OperatorMutation && operation.Environments == NetRatelMcpOperationEnvironment.Production && operation.RequiresConfirmation);
    }

    [Fact]
    public async Task Custom_http_definitions_construct_shared_handlers_from_request_services()
    {
        var services = new ServiceCollection()
            .AddSingleton<INetRatelMcpApiClient, TestMcpApiClient>();
        using var provider = services.BuildServiceProvider();

        var handler = NetRatelMcpToolDefinitions.CreateHandler(provider, typeof(NetRatelMcpOperationalTools));
        var result = await ((NetRatelMcpOperationalTools)handler).netratel_system("version");

        result.Success.Should().BeTrue();
        result.Status.Should().Be("completed");
    }

    [Fact]
    public async Task Stdio_host_registers_known_catalog_types_without_assembly_scanning()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "src", "NetRatel", "NetRatel.Mcp", "Program.cs"));

        source.Should().Contain(".WithTools(NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>");
        source.Should().Contain(".AddSingleton<INetRatelMcpApiClient, NetRatelMcpStdioApiClient>()");
        source.Should().NotContain(".WithTools<ReadOnlyTools>()");
        source.Should().NotContain(".WithTools<MutationTools>()");
        source.Should().Contain(".WithResources<NetRatelMcpCatalogResources>()");
        source.Should().Contain(".WithPrompts<NetRatelPrompts>()");
        source.Should().NotContain("FromAssembly");
    }

    [Fact]
    public async Task Stdio_process_publishes_the_migrated_capabilities_tool_with_the_shared_closed_schema()
    {
        await using var stdio = StdioMcpClient.Start();

        using var initialize = await stdio.CallAsync(1, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "netratel-test", version = "1.0" }
        });
        initialize.RootElement.TryGetProperty("result", out _).Should().BeTrue();
        await stdio.NotifyAsync("notifications/initialized", new { });

        using var tools = await stdio.CallAsync(2, "tools/list", new { });
        var publishedTools = tools.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .ToArray();
        var expectedTools = NetRatelMcpToolDefinitions.CreateForStdio(StdioCompatibilityHandlers)
            .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        publishedTools.Select(tool => tool.GetProperty("name").GetString())
            .Should().BeEquivalentTo(expectedTools.Keys);
        publishedTools.Select(tool => tool.GetProperty("name").GetString())
            .Should().OnlyHaveUniqueItems();
        foreach (var published in publishedTools)
        {
            var name = published.GetProperty("name").GetString();
            var expected = expectedTools[name!].ProtocolTool;
            published.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
            published.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            published.GetProperty("annotations").ValueKind.Should().Be(JsonValueKind.Object);
            published.GetProperty("inputSchema").GetRawText().Should().Be(expected.InputSchema.GetRawText());
            published.GetProperty("outputSchema").GetRawText().Should().Be(expected.OutputSchema!.Value.GetRawText());
        }

        using var capability = await stdio.CallAsync(3, "tools/call", new
        {
            name = "netratel_capabilities",
            arguments = new { operation = "get" }
        });
        capability.RootElement.GetProperty("result").GetProperty("structuredContent")
            .GetProperty("success").GetBoolean().Should().BeTrue();

        using var preview = await stdio.CallAsync(4, "tools/call", new
        {
            name = "netratel_notifications",
            arguments = new { operation = "mark_read", request = new { ids = new[] { "notification-1" } } }
        });
        preview.RootElement.TryGetProperty("result", out var previewPayload)
            .Should().BeTrue(preview.RootElement.GetRawText());
        previewPayload.TryGetProperty("structuredContent", out var previewResult)
            .Should().BeTrue(preview.RootElement.GetRawText());
        previewResult.GetProperty("status").GetString().Should().Be("confirmation_required");
        previewResult.GetProperty("requiresConfirmation").GetBoolean().Should().BeTrue();

        using var resources = await stdio.CallAsync(5, "resources/list", new { });
        var publishedResources = resources.RootElement.GetProperty("result").GetProperty("resources")
            .EnumerateArray()
            .ToArray();
        publishedResources.Select(resource => resource.GetProperty("uri").GetString())
            .Should().BeEquivalentTo(NetRatelMcpCatalog.Resources);
        foreach (var resource in publishedResources)
        {
            using var read = await stdio.CallAsync(6, "resources/read", new { uri = resource.GetProperty("uri").GetString() });
            read.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray().Should().NotBeEmpty();
        }

        using var prompts = await stdio.CallAsync(7, "prompts/list", new { });
        prompts.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray()
            .Select(prompt => prompt.GetProperty("name").GetString())
            .Should().BeEquivalentTo(NetRatelMcpCatalog.Prompts);
        using var prompt = await stdio.CallAsync(8, "prompts/get", new { name = "controlled_remote_execution", arguments = new { } });
        prompt.RootElement.GetProperty("result").GetProperty("messages").EnumerateArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Http_host_registers_the_shared_read_surface_and_prompts_without_assembly_scanning()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "src", "NetRatel", "NetRatel.Mcp.Http", "NetRatelMcpHttpApplication.cs"));

        source.Should().Contain(".WithTools(NetRatelMcpToolDefinitions.CreateForHttp(hostContext))");
        source.Should().NotContain(".WithTools<NetRatelMcpHttpReadOnlyTools>()");
        source.Should().Contain(".WithPrompts<NetRatelPrompts>()");
        source.Should().NotContain("FromAssembly");
    }

    [Fact]
    public void Http_read_prompts_recommend_only_catalogued_read_operations()
    {
        var prompts = new NetRatelPrompts(new NetRatelMcpHostContext(
            CreateTarget("dev"),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http"));

        var prompt = prompts.InspectClient("client-1");

        prompt.Should().Contain("netratel://capabilities").And.Contain("netratel_clients telemetry");
        prompt.Should().Contain("catalogued read operations");
        prompt.Should().NotContain("netratel_terminal operation=command");
    }

    [Fact]
    public void Http_workflow_prompts_describe_the_catalogued_production_boundaries()
    {
        var prompts = new NetRatelPrompts(new NetRatelMcpHostContext(
            CreateTarget("dev"),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http"));

        var execution = prompts.ControlledRemoteExecution();
        var workflow = prompts.CreateWorkflow();

        execution.Should().Contain("netratel_access evaluate")
            .And.Contain("netratel_commands")
            .And.Contain("netratel_tasks")
            .And.Contain("opaque plan credentials")
            .And.Contain("target policy")
            .And.Contain("Remote Support");
        workflow.Should().Contain("netratel_scripts")
            .And.Contain("netratel_jobs")
            .And.Contain("netratel_job_runs")
            .And.Contain("ETags")
            .And.Contain("onboarding")
            .And.Contain("immutable accepted audit");

        prompts.ManageOperatorAccess().Should().Contain("PolicyAdministrator").And.Contain("netratel.mcp.admin");
        prompts.SafeFileOperation().Should().Contain("canonical read roots").And.Contain("plaintext secrets");
        prompts.SafeTerminalOperation().Should().Contain("caller-owned session ID").And.Contain("Terminal bytes");
        prompts.OnboardClient().Should().Contain("raw enrollment code").And.Contain("immediate-use");
    }

    [Fact]
    public async Task Outbound_clients_keep_tokens_and_api_targets_isolated()
    {
        var devRequests = new List<(Uri Uri, string Token)>();
        var prodRequests = new List<(Uri Uri, string Token)>();
        var dev = CreateClient("dev", "dev-minted-token", devRequests);
        var prod = CreateClient("prod", "prod-minted-token", prodRequests);

        await Task.WhenAll(
            dev.GetAsync("/api/v1/system/version"),
            prod.GetAsync("/api/v1/system/version"));
        await dev.GetAsync("/api/v1/system/version");

        devRequests.Should().HaveCount(2).And.OnlyContain(request => request.Uri.Host == "dev-api.example" && request.Token == "dev-minted-token");
        prodRequests.Should().ContainSingle().Which.Should().Be((new Uri("https://prod-api.example/api/v1/system/version"), "prod-minted-token"));
    }

    [Fact]
    public async Task Outbound_client_never_forwards_an_inbound_caller_token()
    {
        const string inboundCallerToken = "inbound-user-token";
        var requests = new List<(Uri Uri, string Token)>();
        var client = CreateClient("dev", "isolated-app-token", requests);

        await client.GetAsync("/api/v1/system/version");

        requests.Should().ContainSingle().Which.Token.Should().Be("isolated-app-token");
        requests.Should().NotContain(request => request.Token == inboundCallerToken);
    }

    [Fact]
    public async Task Outbound_client_carries_a_scoped_delegation_assertion_alongside_its_app_token()
    {
        string? appToken = null;
        string? delegationAssertion = null;
        var options = CreateOptions("dev");
        var delegationContext = new McpOperatorDelegationContext();
        var client = new NetRatelMcpOutboundClient(
            new StaticHttpClientFactory(name => new HttpClient(new DelegateHandler(async request =>
            {
                if (name == NetRatelMcpOutboundClient.TokenHttpClientName)
                {
                    var body = await request.Content!.ReadAsStringAsync();
                    body.Should().Contain("grant_type=client_credentials");
                    return Json(HttpStatusCode.OK, "{\"access_token\":\"isolated-app-token\",\"expires_in\":300}");
                }

                appToken = request.Headers.Authorization?.Parameter;
                delegationAssertion = request.Headers.GetValues(McpOperatorDelegationOptions.HeaderName).Single();
                return Json(HttpStatusCode.OK, "{}");
            }))
            {
                BaseAddress = name == NetRatelMcpOutboundClient.ApiHttpClientName ? options.Target.ApiBaseUri : null
            }),
            options,
            new NetRatelMcpAccessTokenCache(),
            delegationContext);

        using (delegationContext.Begin("signed-delegation-assertion"))
        {
            await client.GetAsync("/api/v1/system/version");
        }

        appToken.Should().Be("isolated-app-token");
        delegationAssertion.Should().Be("signed-delegation-assertion");
        delegationContext.CurrentAssertion.Should().BeNull();
    }

    [Fact]
    public async Task Outbound_api_m2m_client_uses_standard_client_secret_credentials_without_legacy_oidc_fields()
    {
        var options = NetRatelMcpOutboundOptions.CreateApiM2M(
            CreateTarget("dev"),
            new Uri("https://dev-api.example/connect/token"),
            "netratel-mcp-http-dev",
            "test-only-api-secret",
            "orchestrator.api");
        string? tokenRequest = null;
        string? apiToken = null;
        var client = new NetRatelMcpOutboundClient(
            new StaticHttpClientFactory(name => new HttpClient(new DelegateHandler(async request =>
            {
                if (name == NetRatelMcpOutboundClient.TokenHttpClientName)
                {
                    tokenRequest = await request.Content!.ReadAsStringAsync();
                    return Json(HttpStatusCode.OK, "{\"access_token\":\"api-m2m-token\",\"expires_in\":300}");
                }

                apiToken = request.Headers.Authorization?.Parameter;
                return Json(HttpStatusCode.OK, "{}");
            }))
            {
                BaseAddress = name == NetRatelMcpOutboundClient.ApiHttpClientName ? options.Target.ApiBaseUri : null
            }),
            options,
            new NetRatelMcpAccessTokenCache(),
            new McpOperatorDelegationContext());

        await client.GetAsync("/api/v2/mcp/operator/access/whoami");

        tokenRequest.Should().Contain("grant_type=client_credentials")
            .And.Contain("client_id=netratel-mcp-http-dev")
            .And.Contain("client_secret=test-only-api-secret")
            .And.Contain("scope=orchestrator.api")
            .And.NotContain("username=")
            .And.NotContain("password=");
        apiToken.Should().Be("api-m2m-token");
    }

    [Fact]
    public async Task Outbound_client_rejects_absolute_paths_and_does_not_return_raw_upstream_error_bodies()
    {
        var client = new NetRatelMcpOutboundClient(
            new StaticHttpClientFactory(name => new HttpClient(new DelegateHandler(_ => Task.FromResult(
                name == NetRatelMcpOutboundClient.TokenHttpClientName
                    ? Json(HttpStatusCode.OK, "{\"access_token\":\"isolated-app-token\",\"expires_in\":300}")
                    : Json(HttpStatusCode.Conflict, "{\"code\":\"marker_job_run_terminal\",\"detail\":\"raw-upstream-error\"}"))))
            {
                BaseAddress = new Uri("https://dev-api.example")
            }),
            CreateOptions("dev"),
            new NetRatelMcpAccessTokenCache(),
            new McpOperatorDelegationContext());

        Func<Task> absolutePath = () => client.GetAsync("https://untrusted.example/api/v1/system/version");
        await absolutePath.Should().ThrowAsync<AgentClientValidationException>();

        Func<Task> failedRequest = () => client.GetAsync("/api/v1/system/version");
        var exception = await failedRequest.Should().ThrowAsync<AgentClientRemoteException>();
        exception.Which.ResponseBody.Should().BeNull();
        exception.Which.RemoteCode.Should().Be("marker_job_run_terminal");
        exception.Which.Message.Should().NotContain("raw-upstream-error");
    }

    [Fact]
    public async Task Outbound_client_preserves_a_safe_nested_operator_failure_code()
    {
        var client = new NetRatelMcpOutboundClient(
            new StaticHttpClientFactory(name => new HttpClient(new DelegateHandler(_ => Task.FromResult(
                name == NetRatelMcpOutboundClient.TokenHttpClientName
                    ? Json(HttpStatusCode.OK, "{\"access_token\":\"isolated-app-token\",\"expires_in\":300}")
                    : Json(HttpStatusCode.Forbidden, "{\"failure\":{\"code\":\"target_policy_missing\"},\"detail\":\"raw-upstream-error\"}"))))
            {
                BaseAddress = new Uri("https://dev-api.example")
            }),
            CreateOptions("dev"),
            new NetRatelMcpAccessTokenCache(),
            new McpOperatorDelegationContext());

        Func<Task> failedRequest = () => client.GetAsync("/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/availability");

        var exception = await failedRequest.Should().ThrowAsync<AgentClientRemoteException>();

        exception.Which.RemoteCode.Should().Be("target_policy_missing");
        exception.Which.ResponseBody.Should().BeNull();
        exception.Which.Message.Should().NotContain("raw-upstream-error");
    }

    private static NetRatelMcpOutboundClient CreateClient(string instance, string token, List<(Uri Uri, string Token)> apiRequests)
    {
        var options = CreateOptions(instance);
        return new NetRatelMcpOutboundClient(
            new StaticHttpClientFactory(name => new HttpClient(new DelegateHandler(async request =>
            {
                if (name == NetRatelMcpOutboundClient.TokenHttpClientName)
                {
                    var body = await request.Content!.ReadAsStringAsync();
                    body.Should().Contain("grant_type=client_credentials");
                    return Json(HttpStatusCode.OK, $$"""{"access_token":"{{token}}","expires_in":300}""");
                }

                apiRequests.Add((request.RequestUri!, request.Headers.Authorization!.Parameter!));
                return Json(HttpStatusCode.OK, "{}");
            }))
            {
                BaseAddress = name == NetRatelMcpOutboundClient.ApiHttpClientName ? options.Target.ApiBaseUri : null
            }),
            options,
            new NetRatelMcpAccessTokenCache(),
            new McpOperatorDelegationContext());
    }

    private static NetRatelMcpOutboundOptions CreateOptions(string instance) => new(
        CreateTarget(instance),
        new Uri($"https://{instance}-auth.example/connect/token"),
        "client-id",
        "agent-user",
        "not-for-output",
        "openid profile");

    private static NetRatelMcpTarget CreateTarget(string instance) => new(
        instance,
        new Uri($"https://{instance}-api.example"),
        new Uri($"netratel://{instance}/status"),
        NetRatelMcpCatalog.Revision);

    private static string FindRepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    private static string FindStdioHostAssembly()
    {
        var configuration = typeof(ReadOnlyTools).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (string.IsNullOrWhiteSpace(configuration))
            throw new InvalidOperationException("The referenced NetRatel MCP assembly does not declare its build configuration.");

        var targetFramework = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
        var hostAssembly = Path.Combine(
            FindRepoRoot(),
            "src",
            "NetRatel",
            "NetRatel.Mcp",
            "bin",
            configuration,
            targetFramework,
            "NetRatel.Mcp.dll");

        return File.Exists(hostAssembly) && File.Exists(Path.ChangeExtension(hostAssembly, "runtimeconfig.json"))
            ? hostAssembly
            : throw new FileNotFoundException("Could not locate the built NetRatel MCP stdio host for the active test configuration.");
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static IReadOnlyDictionary<string, JsonElement> AlternativesByOperation(JsonElement schema)
        => schema.GetProperty("oneOf").EnumerateArray().ToDictionary(
            alternative => alternative.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString()!,
            alternative => alternative,
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, Type> StdioCompatibilityHandlers { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["netratel_config"] = typeof(ReadOnlyTools),
        ["netratel_remote_support_v2"] = typeof(MutationTools)
    };

    private sealed class StaticHttpClientFactory(Func<string, HttpClient> createClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => createClient(name);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }

    private sealed class TestMcpApiClient : INetRatelMcpApiClient
    {
        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonNode?>(new JsonObject { ["path"] = path });

        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonNode?>(new JsonObject { ["path"] = path });
    }

    private sealed class RecordingMcpApiClient : INetRatelMcpApiClient
    {
        public List<(HttpMethod Method, string Path, JsonNode? Body)> Requests { get; } = [];

        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonNode?>(new JsonObject { ["path"] = path });

        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, path, body));
            return Task.FromResult<JsonNode?>(new JsonObject { ["path"] = path });
        }
    }

    private sealed class StdioMcpClient(Process process, string configurationPath) : IAsyncDisposable
    {
        private readonly Process _process = process;
        private readonly string _configurationPath = configurationPath;

        public static StdioMcpClient Start()
        {
            var configurationPath = Path.Combine(Path.GetTempPath(), $"netratel-mcp-{Guid.NewGuid():N}.json");
            new AgentClientConfigurationStore(configurationPath).Save(new AgentClientConfiguration(
                "https://api.example",
                "https://auth.example/token",
                "netratel-mcp-test",
                "netratel-mcp-test",
                "test-only-password",
                "openid profile"));

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(FindStdioHostAssembly());
            start.Environment["NETRATEL_MCP_CONFIG"] = configurationPath;
            start.Environment["NETRATEL_MCP_INSTANCE"] = "dev";

            return new StdioMcpClient(
                Process.Start(start) ?? throw new InvalidOperationException("Could not start the NetRatel MCP stdio host."),
                configurationPath);
        }

        public async Task<JsonDocument> CallAsync(int id, string method, object parameters)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            line.Should().NotBeNullOrWhiteSpace($"The stdio MCP host did not respond to '{method}'.");
            return JsonDocument.Parse(line!);
        }

        public async Task NotifyAsync(string method, object parameters)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            finally
            {
                _process.Dispose();
                File.Delete(_configurationPath);
            }
        }
    }
}
