using FluentAssertions;
using NetRatel.Mcp.Core;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class NetRatelMcpHttpToolTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task Canonical_script_mutations_forward_source_revision_and_opaque_confirmation(string operation)
    {
        var client = new RecordingApiClient();
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("https://api.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test", operatorSurfaceEnabled: true);
        var tools = new NetRatelMcpOperationalTools(client, host);
        var request = new JsonObject
        {
            ["tenantId"] = 42,
            ["agentId"] = Guid.NewGuid().ToString("D"),
            ["source"] = new JsonObject { ["name"] = "source", ["content"] = "echo source", ["scriptType"] = "bash" }
        };
        if (operation == "update")
        {
            request["scriptId"] = 901; request["expectedSourceRevision"] = 7;
            request["expectedContentHash"] = new string('A', 64);
        }
        (await tools.netratel_scripts(operation, Request(request))).Success.Should().BeTrue();
        client.Mutations.Single().Path.Should().EndWith("/preview/" + operation);
        client.Mutations.Single().Body!["source"]!["content"]!.GetValue<string>().Should().Be("echo source");
        client.Mutations.Single().Body!["script"].Should().BeNull();
        (await tools.netratel_scripts(operation, Request(request), confirm: true)).Success.Should().BeFalse();
        request["planToken"] = new string('p', 32); request["idempotencyKey"] = new string('i', 32);
        (await tools.netratel_scripts(operation, Request(request), confirm: true)).Success.Should().BeTrue();
        client.Mutations.Last().Path.Should().EndWith("/confirm/" + operation);
        request["expectedVersion"] = 1;
        (await tools.netratel_scripts(operation, Request(request))).Success.Should().BeFalse();
        client.Mutations.Should().HaveCount(2);
    }

    [Fact]
    public async Task Canonical_script_catalog_and_paging_are_only_advertised_for_Dev_operator_hosts()
    {
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("https://api.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test", operatorSurfaceEnabled: true);
        var dev = NetRatelMcpToolDefinitions.CreateForHttp(host).Single(tool => tool.ProtocolTool.Name == "netratel_scripts").ProtocolTool;
        dev.InputSchema.GetRawText().Should().Contain("expectedSourceRevision").And.Contain("expectedContentHash");
        var prod = NetRatelMcpToolDefinitions.CreateForHttp("prod").Single(tool => tool.ProtocolTool.Name == "netratel_scripts").ProtocolTool;
        prod.InputSchema.GetRawText().Should().NotContain("expectedSourceRevision");
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agent = Guid.NewGuid();
        var request = new JsonObject { ["tenantId"] = 42, ["agentId"] = agent.ToString("D"), ["afterId"] = 900, ["limit"] = 25 };
        (await tools.netratel_scripts("list", Request(request))).Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().Be($"/api/v2/mcp/operator/agents/42/{agent:D}/scripts?afterId=900&limit=25");
        request["limit"] = 101;
        (await tools.netratel_scripts("list", Request(request))).Success.Should().BeFalse();
        client.Paths.Should().ContainSingle();
    }

    [Theory]
    [InlineData("presence")]
    [InlineData("capabilities")]
    [InlineData("inventory")]
    public async Task Remote_support_http_reads_use_exact_delegated_operator_routes(string operation)
    {
        var client = new RecordingApiClient();
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("https://api.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test", operatorSurfaceEnabled: true);
        var tools = new NetRatelMcpOperationalTools(client, context);
        var target = Guid.NewGuid();
        var request = new JsonObject { ["tenantId"] = 42, ["agentId"] = target.ToString("D") };
        (await tools.netratel_remote_support_v2(operation, Request(request))).Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().Be($"/api/v2/mcp/operator/agents/42/{target:D}/remote-support/{operation}");
        request["operatorId"] = "another-user";
        (await tools.netratel_remote_support_v2(operation, Request(request))).Success.Should().BeFalse();
        client.Paths.Should().ContainSingle();
    }

    [Fact]
    public async Task Remote_support_refresh_uses_preview_then_opaque_confirmation()
    {
        var client = new MutationRecordingApiClient();
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("https://api.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test", operatorSurfaceEnabled: true);
        var tools = new NetRatelMcpOperationalTools(client, context);
        var target = Guid.NewGuid();
        var request = new JsonObject { ["tenantId"] = 42, ["agentId"] = target.ToString("D") };
        (await tools.netratel_remote_support_v2("refresh_inventory", Request(request))).Success.Should().BeTrue();
        client.Mutations.Should().ContainSingle().Which.Path.Should().EndWith("/refresh-inventory/preview");
        (await tools.netratel_remote_support_v2("refresh_inventory", Request(request), true)).Success.Should().BeFalse();
        client.Mutations.Should().ContainSingle();
        request["planToken"] = new string('p', 32);
        request["idempotencyKey"] = new string('i', 32);
        (await tools.netratel_remote_support_v2("refresh_inventory", Request(request), true)).Success.Should().BeTrue();
        client.Mutations.Should().HaveCount(2);
        client.Mutations[1].Path.Should().EndWith("/refresh-inventory/confirm");
    }

    [Theory]
    [InlineData("netratel_notifications", "/api/v2/mcp/operator/notifications")]
    [InlineData("netratel_events", "/api/v2/mcp/operator/events")]
    public async Task Control_plane_lists_forward_pagination_and_reject_unbounded_requests(string tool, string path)
    {
        var client = new RecordingApiClient();
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("https://api.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test", operatorSurfaceEnabled: true);
        var tools = new NetRatelMcpOperationalTools(client, context);
        async Task<NetRatelToolResponse> List(JsonObject request) => tool == "netratel_events"
            ? await tools.netratel_events("list", Request(request))
            : await tools.netratel_notifications("list", Request(request));

        (await List(new JsonObject { ["page"] = 2, ["pageSize"] = 75 })).Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().Be(path + "?page=2&pageSize=75");
        (await List(new JsonObject { ["page"] = 0 })).Success.Should().BeFalse();
        (await List(new JsonObject { ["pageSize"] = 101 })).Success.Should().BeFalse();
        (await List(new JsonObject { ["endpoint"] = "https://untrusted.example" })).Success.Should().BeFalse();
        client.Paths.Should().ContainSingle();
    }

    [Theory]
    [InlineData("line\n")]
    [InlineData("  indented\r\n\t")]
    [InlineData(" \n")]
    [InlineData("Καλημέρα\n")]
    public async Task File_write_preview_and_confirm_preserve_exact_utf8_text(string text)
    {
        var client = new RecordingApiClient();
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("prod", new Uri("https://api.example"), new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test");
        var tools = new NetRatelMcpOperationalTools(client, context);
        var payload = new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = Guid.NewGuid().ToString("D"),
            ["path"] = "/tmp/content.txt",
            ["text"] = text
        };
        (await tools.netratel_files("preview_write_text", Request(payload))).Success.Should().BeTrue();
        payload["planToken"] = new string('p', 40);
        payload["idempotencyKey"] = new string('i', 40);
        (await tools.netratel_files("confirm_write_text", Request(payload), confirm: true)).Success.Should().BeTrue();
        client.Mutations.Should().HaveCount(2);
        client.Mutations.Should().OnlyContain(mutation => mutation.Body!["text"]!.GetValue<string>() == text);
    }

    [Fact]
    public async Task Health_tool_uses_the_exact_reviewed_outbound_route()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["status"] = "healthy" });
        var tools = new NetRatelMcpOperationalTools(client);

        var result = await tools.netratel_health();

        result.Success.Should().BeTrue();
        result.Status.Should().Be("completed");
        result.Data!.ToJsonString().Should().Contain("healthy");
        client.Paths.Should().ContainSingle().Which.Should().Be("/health/ready");
    }

    [Fact]
    public async Task AuthenticationStatus_tool_uses_the_delegation_bound_v2_route()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["authenticated"] = true });
        var tools = new NetRatelMcpOperationalTools(client);

        var result = await tools.netratel_auth();

        result.Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().Be("/api/v2/mcp/operator/auth/status");
    }

    [Fact]
    public async Task Health_tool_maps_a_remote_failure_without_returning_the_raw_upstream_body()
    {
        var client = new RecordingApiClient(exception: new NetRatelMcpApiException("remote_request_failed", "NetRatel API request failed with HTTP 503.", 503, retryable: true));
        var tools = new NetRatelMcpOperationalTools(client);

        var result = await tools.netratel_health();

        result.Success.Should().BeFalse();
        result.Status.Should().Be("failed");
        result.Error.Should().BeEquivalentTo(new NetRatelToolError("remote_request_failed", true, 503));
        result.Failure.Should().BeEquivalentTo(new NetRatelToolFailure(
            "remote_request_failed",
            "upstream",
            true,
            ["netratel.mcp.read"],
            "netratel_health/get",
            null,
            "The operation did not complete. No upstream response content is exposed.",
            "Retry only when the failure is marked retryable; otherwise inspect the safe failure details and correlation ID."));
        result.CorrelationId.Should().NotBeNullOrWhiteSpace();
        result.Summary.Should().NotContain("raw-upstream-error");
    }

    [Fact]
    public async Task Health_tool_preserves_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var action = () => tools.netratel_health(cancellationToken: cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        client.ObservedCancellation.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task Access_tool_forwards_only_closed_delegation_bound_inspection_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var whoAmI = await tools.netratel_access("whoami");
        var target = await tools.netratel_access("target", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var effective = await tools.netratel_access("effective", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var evaluate = await tools.netratel_access("evaluate", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["tool"] = "netratel_terminal",
            ["operation"] = "availability"
        }));

        whoAmI.Success.Should().BeTrue();
        target.Success.Should().BeTrue();
        effective.Success.Should().BeTrue();
        evaluate.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/access/whoami",
            "/api/v2/mcp/operator/access/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            "/api/v2/mcp/operator/access/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/effective",
            "/api/v2/mcp/operator/access/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/evaluate?tool=netratel_terminal&operation=availability");
    }

    [Fact]
    public async Task Access_tool_rejects_open_or_invalid_inspection_requests_before_outbound_dispatch()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var whoAmIWithRequest = await tools.netratel_access("whoami", Request(new JsonObject()));
        var targetWithExtraProperty = await tools.netratel_access("target", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = Guid.NewGuid().ToString("D"),
            ["includeSecrets"] = true
        }));
        var invalidEvaluation = await tools.netratel_access("evaluate", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = Guid.NewGuid().ToString("D"),
            ["tool"] = "netratel terminal",
            ["operation"] = "availability"
        }));

        whoAmIWithRequest.Status.Should().Be("invalid_request");
        targetWithExtraProperty.Status.Should().Be("invalid_request");
        invalidEvaluation.Status.Should().Be("invalid_request");
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Policy_inspection_tool_forwards_only_closed_administrator_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var policyId = Guid.Parse("435ff817-c8fc-4bb4-81ea-5e910abc5d2a");
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var policies = await tools.netratel_policy("policies", Request(new JsonObject { ["environment"] = "Production", ["tenantId"] = 7 }));
        var policy = await tools.netratel_policy("policy", Request(new JsonObject { ["policyId"] = policyId.ToString("D") }));
        var changeAudits = await tools.netratel_policy("change_audits", Request(new JsonObject { ["tenantId"] = 7, ["policyId"] = policyId.ToString("D"), ["agentId"] = agentId.ToString("D") }));
        var acceptedAudits = await tools.netratel_policy("accepted_audits", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["subject"] = "policy.admin@example.test", ["limit"] = 25 }));
        var target = await tools.netratel_policy("target", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") }));
        var matches = await tools.netratel_policy("matches", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") }));
        var evaluate = await tools.netratel_policy("evaluate", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["tool"] = "netratel_terminal",
            ["operation"] = "availability"
        }));

        policies.Success.Should().BeTrue();
        policy.Success.Should().BeTrue();
        changeAudits.Success.Should().BeTrue();
        acceptedAudits.Success.Should().BeTrue();
        target.Success.Should().BeTrue();
        matches.Success.Should().BeTrue();
        evaluate.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/policy/policies?environment=Production&tenantId=7",
            "/api/v2/mcp/operator/policy/policies/435ff817-c8fc-4bb4-81ea-5e910abc5d2a",
            "/api/v2/mcp/operator/policy/change-audits?tenantId=7&policyId=435ff817-c8fc-4bb4-81ea-5e910abc5d2a&agentId=35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            "/api/v2/mcp/operator/policy/accepted-audits?tenantId=7&agentId=35ba3a1d-8665-499d-a8a6-42fa07e9a633&subject=policy.admin%40example.test&limit=25",
            "/api/v2/mcp/operator/policy/targets/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            "/api/v2/mcp/operator/policy/matches/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            "/api/v2/mcp/operator/policy/evaluate/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633?tool=netratel_terminal&operation=availability");
    }

    [Fact]
    public async Task Policy_inspection_tool_rejects_unbounded_or_cross_tenant_filters_before_dispatch()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var invalidEnvironment = await tools.netratel_policy("policies", Request(new JsonObject { ["environment"] = "prod" }));
        var agentWithoutTenant = await tools.netratel_policy("accepted_audits", Request(new JsonObject { ["agentId"] = Guid.NewGuid().ToString("D") }));
        var mutation = await tools.netratel_policy("create", Request(new JsonObject()));

        invalidEnvironment.Status.Should().Be("invalid_request");
        agentWithoutTenant.Status.Should().Be("invalid_request");
        mutation.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["policies", "policy", "change_audits", "accepted_audits", "target", "matches", "evaluate", "preview_create", "confirm_create", "preview_replace", "confirm_replace", "preview_disable", "confirm_disable", "preview_revoke", "confirm_revoke", "preview_target_profile", "confirm_target_profile"]));
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Policy_lifecycle_uses_server_previews_then_forwards_only_exact_confirmed_plans()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var draft = new JsonObject
        {
            ["name"] = "reviewed tenant observation",
            ["environment"] = 1,
            ["effect"] = 2,
            ["priority"] = 10,
            ["principalSelector"] = new JsonObject { ["kind"] = 3, ["value"] = "incident-responders" },
            ["targetSelector"] = new JsonObject { ["kind"] = 2, ["tenantId"] = 7 },
            ["operationFamily"] = 1,
            ["constraints"] = new JsonObject()
        };
        var previewRequest = Request(new JsonObject { ["policy"] = draft.DeepClone() });
        var confirmRequest = Request(new JsonObject
        {
            ["policy"] = draft.DeepClone(),
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        });
        var policyId = "68310e19-5f49-4e63-a73d-342e98940c26";
        var target = new JsonObject { ["kind"] = 2, ["tenantId"] = 7 };
        var replacePreviewRequest = Request(new JsonObject
        {
            ["policyId"] = policyId,
            ["expectedVersion"] = 1,
            ["policy"] = draft.DeepClone()
        });
        var replaceConfirmRequest = Request(new JsonObject
        {
            ["policyId"] = policyId,
            ["expectedVersion"] = 1,
            ["policy"] = draft.DeepClone(),
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        });
        var disablePreviewRequest = Request(new JsonObject
        {
            ["policyId"] = policyId,
            ["expectedVersion"] = 1,
            ["target"] = target.DeepClone()
        });
        var disableConfirmRequest = Request(new JsonObject
        {
            ["policyId"] = policyId,
            ["expectedVersion"] = 1,
            ["target"] = target.DeepClone(),
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        });
        var revokePreviewRequest = Request(new JsonObject
        {
            ["policyId"] = policyId,
            ["expectedVersion"] = 1,
            ["target"] = target.DeepClone()
        });
        var revokeConfirmRequest = Request(new JsonObject
        {
            ["policyId"] = policyId,
            ["expectedVersion"] = 1,
            ["target"] = target.DeepClone(),
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        });

        var preview = await tools.netratel_policy("preview_create", previewRequest);
        var unconfirmed = await tools.netratel_policy("confirm_create", confirmRequest);
        var confirmed = await tools.netratel_policy("confirm_create", confirmRequest, confirm: true);
        var replacePreview = await tools.netratel_policy("preview_replace", replacePreviewRequest);
        var replaceUnconfirmed = await tools.netratel_policy("confirm_replace", replaceConfirmRequest);
        var replaceConfirmed = await tools.netratel_policy("confirm_replace", replaceConfirmRequest, confirm: true);
        var disablePreview = await tools.netratel_policy("preview_disable", disablePreviewRequest);
        var disableUnconfirmed = await tools.netratel_policy("confirm_disable", disableConfirmRequest);
        var disableConfirmed = await tools.netratel_policy("confirm_disable", disableConfirmRequest, confirm: true);
        var revokePreview = await tools.netratel_policy("preview_revoke", revokePreviewRequest);
        var revokeUnconfirmed = await tools.netratel_policy("confirm_revoke", revokeConfirmRequest);
        var revokeConfirmed = await tools.netratel_policy("confirm_revoke", revokeConfirmRequest, confirm: true);

        preview.Success.Should().BeTrue();
        unconfirmed.Status.Should().Be("confirmation_required");
        confirmed.Success.Should().BeTrue();
        replacePreview.Success.Should().BeTrue();
        replaceUnconfirmed.Status.Should().Be("confirmation_required");
        replaceConfirmed.Success.Should().BeTrue();
        disablePreview.Success.Should().BeTrue();
        disableUnconfirmed.Status.Should().Be("confirmation_required");
        disableConfirmed.Success.Should().BeTrue();
        revokePreview.Success.Should().BeTrue();
        revokeUnconfirmed.Status.Should().Be("confirmation_required");
        revokeConfirmed.Success.Should().BeTrue();
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path)).Should().Equal(
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/create/preview"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/create/confirm"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/replace/preview"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/replace/confirm"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/disable/preview"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/disable/confirm"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/revoke/preview"),
            (HttpMethod.Post, "/api/v2/mcp/operator/policy/revoke/confirm"));
        client.Mutations[0].Body!.AsObject()["policy"]!.ToJsonString().Should().Be(draft.ToJsonString());
        client.Mutations[1].Body!.AsObject()["planToken"]!.GetValue<string>().Should().Be("1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko");
        client.Mutations[3].Body!.AsObject()["policyId"]!.GetValue<string>().Should().Be(policyId);
        client.Mutations[5].Body!.AsObject()["target"]!.ToJsonString().Should().Be(target.ToJsonString());
        client.Mutations[7].Body!.AsObject()["target"]!.ToJsonString().Should().Be(target.ToJsonString());
    }

    [Fact]
    public async Task Operational_tools_map_read_operations_to_reviewed_api_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);

        var jobList = await tools.netratel_jobs("list", Request(new JsonObject { ["folder"] = "/ops", ["search"] = "Inventory" }));
        var jobParams = await tools.netratel_jobs("params", Request(new JsonObject { ["jobId"] = "42" }));
        var taskLogs = await tools.netratel_tasks("logs", Request(new JsonObject { ["taskId"] = 42, ["sinceId"] = 7, ["stream"] = "stderr" }));
        var search = await tools.netratel_search("clients", Request(new JsonObject { ["q"] = "branch office" }));

        jobList.Success.Should().BeTrue();
        jobParams.Success.Should().BeTrue();
        taskLogs.Success.Should().BeTrue();
        search.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v1/jobs/?folder=%2Fops&search=Inventory",
            "/api/v1/jobs/42/params",
            "/api/v2/tasks/42/logs?sinceId=7&stream=stderr",
            "/api/v1/global-search/clients?q=branch%20office");
    }

    [Theory]
    [InlineData("dev", "clients")]
    [InlineData("prod", "clients")]
    [InlineData("dev", "tenants")]
    [InlineData("prod", "tenants")]
    public async Task Http_discovery_uses_the_delegated_route(string instance, string operation)
    {
        var client = new RecordingApiClient(data: new JsonObject { ["items"] = new JsonArray() });
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(instance, new Uri("https://api.example"), new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test");
        var tools = new NetRatelMcpOperationalTools(client, context);

        var result = await tools.netratel_search(operation, Request(new JsonObject { ["q"] = "branch office" }));

        result.Success.Should().BeTrue();
        client.Paths.Should().Equal($"/api/v2/mcp/operator/search/{operation}?q=branch%20office");
    }

    [Theory]
    [InlineData("clients")]
    [InlineData("tenants")]
    public async Task Discovery_forwards_bounded_continuation_and_rejects_unknown_inputs(string operation)
    {
        var client = new RecordingApiClient(data: new JsonObject());
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test");
        var tools = new NetRatelMcpOperationalTools(client, context);
        var result = await tools.netratel_search(operation, Request(new JsonObject { ["offset"] = 25, ["limit"] = 25 }));
        result.Success.Should().BeTrue();
        client.Paths.Should().Equal($"/api/v2/mcp/operator/search/{operation}?offset=25&limit=25");
        (await tools.netratel_search(operation, Request(new JsonObject { ["offset"] = -1 }))).Success.Should().BeFalse();
        (await tools.netratel_search(operation, Request(new JsonObject { ["subject"] = "another-operator" }))).Success.Should().BeFalse();
        client.Paths.Should().ContainSingle();
    }

    [Fact]
    public async Task Http_platform_observation_uses_delegated_routes()
    {
        var client = new RecordingApiClient();
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test");
        var tools = new NetRatelMcpOperationalTools(client, context);

        (await tools.netratel_telemetry()).Success.Should().BeTrue();
        (await tools.netratel_logs()).Success.Should().BeTrue();
        client.Paths.Should().Equal("/api/v2/mcp/operator/telemetry/overview", "/api/v2/mcp/operator/logs");
    }

    [Theory]
    [InlineData("scripts")]
    [InlineData("jobs")]
    [InlineData("requests")]
    [InlineData("tasks")]
    public async Task Http_search_does_not_fall_back_to_interactive_routes(string operation)
    {
        var client = new RecordingApiClient();
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://api.example"), new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp, "test");
        var tools = new NetRatelMcpOperationalTools(client, context);

        var result = await tools.netratel_search(operation);

        result.Success.Should().BeTrue();
        client.Paths.Should().Equal($"/api/v2/mcp/operator/search/{operation}");
        var descriptor = NetRatelMcpCatalog.Tools.Single(tool => tool.Name == "netratel_search");
        descriptor.Operations.Where(op => op.AvailableOverHttp).Select(op => op.Name)
            .Should().BeEquivalentTo("tenants", "clients", "scripts", "jobs", "requests", "tasks");
    }

    [Theory]
    [InlineData("oauth_scope_missing", 403, "oauth_scope")]
    [InlineData("target_operation_not_authorized", 403, "policy")]
    [InlineData("tenant_not_authorized", 403, "tenant")]
    [InlineData("search_query_too_broad", 400, "input")]
    [InlineData("search_visibility_limit_exceeded", 403, "policy")]
    public async Task Client_search_retains_safe_upstream_failure_codes(string code, int status, string layer)
    {
        var client = new RecordingApiClient(exception: new NetRatelMcpApiException(
            "remote_request_failed", "Search was not admitted.", status, false, code));
        var tools = new NetRatelMcpOperationalTools(client);

        var result = await tools.netratel_search("clients", Request(new JsonObject { ["q"] = "example" }));

        result.Success.Should().BeFalse();
        result.Error!.UpstreamStatus.Should().Be(status);
        result.Failure!.Code.Should().Be(code);
        result.Failure.Layer.Should().Be(layer);
        result.Failure.RequiredScopes.Should().Contain("netratel.mcp.read");
        result.Failure.RequiredOperation.Should().Be("netratel_search/clients");
        result.Failure.SafeDetails.Should().NotContain("No upstream response content");
        result.Failure.Remediation.Should().NotContain("Retry only");
    }

    [Fact]
    public async Task Source_backed_v2_task_reads_forward_only_the_closed_reviewed_contracts()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var byRequest = await tools.netratel_tasks("list", Request(new JsonObject { ["requestId"] = "request-1" }));
        var recent = await tools.netratel_tasks("recent", Request(new JsonObject
        {
            ["limit"] = 25,
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["taskType"] = "exec-shell-cmd",
            ["status"] = "Completed"
        }));
        var byId = await tools.netratel_tasks("get", Request(new JsonObject { ["taskId"] = "42" }));
        var logs = await tools.netratel_tasks("logs", Request(new JsonObject { ["taskId"] = 42, ["sinceId"] = 7, ["stream"] = "stderr" }));
        var logsByRequest = await tools.netratel_tasks("logs_by_request", Request(new JsonObject { ["requestId"] = "request-1", ["sinceId"] = 9, ["stream"] = "stdout" }));

        byRequest.Success.Should().BeTrue();
        recent.Success.Should().BeTrue();
        byId.Success.Should().BeTrue();
        logs.Success.Should().BeTrue();
        logsByRequest.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/tasks?requestId=request-1",
            "/api/v2/tasks/recent?limit=25&tenantId=7&agentId=35ba3a1d-8665-499d-a8a6-42fa07e9a633&taskType=exec-shell-cmd&status=Completed",
            "/api/v2/tasks/42",
            "/api/v2/tasks/42/logs?sinceId=7&stream=stderr",
            "/api/v2/tasks/logs?requestId=request-1&sinceId=9&stream=stdout");
    }

    [Fact]
    public async Task Source_backed_v2_task_reads_reject_open_or_invalid_requests_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var openRecent = await tools.netratel_tasks("recent", Request(new JsonObject { ["environment"] = "Development" }));
        var invalidRequestId = await tools.netratel_tasks("list", Request(new JsonObject { ["requestId"] = "" }));
        var zeroId = await tools.netratel_tasks("get", Request(new JsonObject { ["taskId"] = 0 }));
        var invalidStream = await tools.netratel_tasks("logs", Request(new JsonObject { ["taskId"] = 42, ["stream"] = "combined" }));

        openRecent.Status.Should().Be("invalid_request");
        invalidRequestId.Status.Should().Be("invalid_request");
        zeroId.Status.Should().Be("invalid_request");
        invalidStream.Status.Should().Be("invalid_request");
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Production_request_contract_uses_only_owned_bounded_preview_confirmation_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("prod", new Uri("https://api.prod.example/"), new Uri("https://mcp.prod.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        const string planToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string idempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        var create = new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["jobId"] = 42, ["summary"] = "Apply reviewed setting" };
        var confirmedCreate = create.DeepClone().AsObject();
        confirmedCreate["planToken"] = planToken;
        confirmedCreate["idempotencyKey"] = idempotencyKey;
        var claim = new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["requestId"] = 14, ["expectedVersion"] = 1, ["claimReference"] = "worker-42", ["planToken"] = planToken, ["idempotencyKey"] = idempotencyKey };

        var list = await tools.netratel_requests("list", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["state"] = "Pending", ["jobId"] = 42, ["limit"] = 10 }));
        var get = await tools.netratel_requests("get", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["requestId"] = 14 }));
        var preview = await tools.netratel_requests("create", Request(create));
        var confirmed = await tools.netratel_requests("create", Request(confirmedCreate), confirm: true);
        var claimPreview = await tools.netratel_requests("claim", Request(claim));
        var claimed = await tools.netratel_requests("claim", Request(claim), confirm: true);

        list.Success.Should().BeTrue();
        get.Success.Should().BeTrue();
        preview.Success.Should().BeTrue();
        confirmed.Success.Should().BeTrue();
        claimPreview.Success.Should().BeTrue();
        claimed.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/requests?state=Pending&jobId=42&limit=10",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/requests/14");
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/requests/preview/create", "{\"jobId\":42,\"summary\":\"Apply reviewed setting\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/requests/confirm/create", "{\"jobId\":42,\"summary\":\"Apply reviewed setting\",\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/requests/preview/claim", "{\"requestId\":14,\"expectedVersion\":1,\"claimReference\":\"worker-42\",\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/requests/confirm/claim", "{\"requestId\":14,\"expectedVersion\":1,\"claimReference\":\"worker-42\",\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"));
    }

    [Fact]
    public async Task Production_client_actions_use_only_exact_preview_confirmation_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("prod", new Uri("https://api.prod.example/"), new Uri("https://mcp.prod.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        const string planToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string idempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        var target = new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") };
        var ping = target.DeepClone().AsObject();
        ping["planToken"] = planToken;
        ping["idempotencyKey"] = idempotencyKey;
        var softwareUpdate = target.DeepClone().AsObject();
        softwareUpdate["planToken"] = planToken;
        softwareUpdate["idempotencyKey"] = idempotencyKey;
        var disable = target.DeepClone().AsObject();
        disable["reason"] = "Approved maintenance window";
        var confirmedDisable = disable.DeepClone().AsObject();
        confirmedDisable["planToken"] = planToken;
        confirmedDisable["idempotencyKey"] = idempotencyKey;
        var enable = target.DeepClone().AsObject();
        enable["planToken"] = planToken;
        enable["idempotencyKey"] = idempotencyKey;
        var delete = target.DeepClone().AsObject();
        delete["reason"] = "Machine retired";
        var confirmedDelete = delete.DeepClone().AsObject();
        confirmedDelete["planToken"] = planToken;
        confirmedDelete["idempotencyKey"] = idempotencyKey;

        (await tools.netratel_clients("preview_ping", Request(target))).Success.Should().BeTrue();
        (await tools.netratel_clients("ping", Request(ping), confirm: true)).Success.Should().BeTrue();
        (await tools.netratel_clients("preview_software_update", Request(target))).Success.Should().BeTrue();
        (await tools.netratel_clients("software_update", Request(softwareUpdate), confirm: true)).Success.Should().BeTrue();
        (await tools.netratel_clients("preview_disable", Request(disable))).Success.Should().BeTrue();
        (await tools.netratel_clients("disable", Request(confirmedDisable), confirm: true)).Success.Should().BeTrue();
        (await tools.netratel_clients("preview_enable", Request(target))).Success.Should().BeTrue();
        (await tools.netratel_clients("enable", Request(enable), confirm: true)).Success.Should().BeTrue();
        (await tools.netratel_clients("preview_delete", Request(delete))).Success.Should().BeTrue();
        (await tools.netratel_clients("delete", Request(confirmedDelete), confirm: true)).Success.Should().BeTrue();

        client.Paths.Should().BeEmpty();
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/ping/preview", null),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/ping/confirm", "{\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/software-update/preview", null),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/software-update/confirm", "{\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/disable/preview", "{\"reason\":\"Approved maintenance window\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/disable/confirm", "{\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\",\"reason\":\"Approved maintenance window\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/enable/preview", null),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/enable/confirm", "{\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/delete/preview", "{\"reason\":\"Machine retired\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/clients/delete/confirm", "{\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\",\"reason\":\"Machine retired\"}"));
    }

    [Fact]
    public async Task Target_owned_marker_script_reads_forward_only_closed_reviewed_contracts()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var list = await tools.netratel_scripts("list", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") }));
        var get = await tools.netratel_scripts("get", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = "42" }));
        var parameters = await tools.netratel_scripts("params", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = 42 }));

        list.Success.Should().BeTrue();
        get.Success.Should().BeTrue();
        parameters.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts/42",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts/42/params");
    }

    [Fact]
    public async Task Target_owned_marker_scripts_preview_before_mutating_and_forward_exact_confirmed_requests()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        var createRequest = Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["marker"] = "mcp-qa-script-001",
            ["name"] = "QA script",
            ["description"] = "marker only",
            ["shell"] = "sh"
        });
        var target = new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = 42 };

        var preview = await tools.netratel_scripts("create_marker", createRequest);
        var create = await tools.netratel_scripts("create_marker", createRequest, confirm: true);
        var update = await tools.netratel_scripts("update_marker", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["scriptId"] = 42,
            ["marker"] = "mcp-qa-script-002"
        }), confirm: true);
        var parse = await tools.netratel_scripts("parse_manifest", Request(target), confirm: true);
        var run = await tools.netratel_scripts("run", Request(target), confirm: true);
        var delete = await tools.netratel_scripts("delete", Request(target), confirm: true);

        preview.Status.Should().Be("confirmation_required");
        preview.RequiresConfirmation.Should().BeTrue();
        create.Success.Should().BeTrue();
        update.Success.Should().BeTrue();
        parse.Success.Should().BeTrue();
        run.Success.Should().BeTrue();
        delete.Success.Should().BeTrue();
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts", "{\"marker\":\"mcp-qa-script-001\",\"shell\":\"sh\",\"name\":\"QA script\",\"description\":\"marker only\"}"),
            (HttpMethod.Put, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts/42", "{\"marker\":\"mcp-qa-script-002\"}"),
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts/42/parse-manifest", null),
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts/42/runs", null),
            (HttpMethod.Delete, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/scripts/42", null));
    }

    [Fact]
    public async Task Target_owned_marker_scripts_forward_only_the_fixed_cancellation_probe_mode()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var result = await tools.netratel_scripts("create_marker", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["marker"] = "mcp-qa-cancel-probe",
            ["shell"] = "sh",
            ["executionMode"] = "cancellation_probe"
        }), confirm: true);

        result.Success.Should().BeTrue();
        client.Mutations.Should().ContainSingle().Which.Body!.ToJsonString().Should().Be("{\"marker\":\"mcp-qa-cancel-probe\",\"shell\":\"sh\",\"executionMode\":\"cancellation_probe\"}");
    }

    [Fact]
    public async Task Target_owned_marker_scripts_reject_open_invalid_or_unconfirmed_requests_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var zeroId = await tools.netratel_scripts("get", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = 0 }));
        var openRequest = await tools.netratel_scripts("params", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = 42, ["environment"] = "Development" }));
        var invalidMarker = await tools.netratel_scripts("create_marker", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["marker"] = "bad marker", ["shell"] = "sh" }));
        var run = await tools.netratel_scripts("run", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = 42 }));
        var mutation = await tools.netratel_scripts("delete", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["scriptId"] = 42 }));

        zeroId.Status.Should().Be("invalid_request");
        openRequest.Status.Should().Be("invalid_request");
        invalidMarker.Status.Should().Be("invalid_request");
        run.Status.Should().Be("confirmation_required");
        mutation.Status.Should().Be("confirmation_required");
        client.Paths.Should().BeEmpty();
        client.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Target_owned_marker_jobs_preserve_unsafe_run_identifiers_and_forward_closed_confirmed_requests()
    {
        const ulong runId = 639_233_171_822_224_896;
        var client = new RecordingApiClient(data: new JsonObject
        {
            ["jobId"] = 21,
            ["runId"] = JsonValue.Create(runId)
        });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        var target = new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["jobId"] = 21 };

        var preview = await tools.netratel_marker_jobs("run", Request(target));
        var create = await tools.netratel_marker_jobs("create", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["scriptId"] = 42
        }), confirm: true);
        var run = await tools.netratel_marker_jobs("run", Request(target), confirm: true);
        var cancel = await tools.netratel_marker_jobs("cancel", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["jobId"] = 21,
            ["runId"] = runId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }), confirm: true);
        var delete = await tools.netratel_marker_jobs("delete", Request(target), confirm: true);

        preview.Status.Should().Be("confirmation_required");
        create.Success.Should().BeTrue();
        run.Success.Should().BeTrue();
        cancel.Success.Should().BeTrue();
        delete.Success.Should().BeTrue();
        run.Data!.AsObject()["runId"]!.GetValue<string>().Should().Be(runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/marker-jobs/42", null),
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/marker-jobs/21/runs", null),
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/marker-jobs/21/runs/639233171822224896/cancel", null),
            (HttpMethod.Delete, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/marker-jobs/21", null));
    }

    [Fact]
    public async Task Target_owned_marker_job_cancel_maps_terminal_conflict_to_an_actionable_non_retryable_error()
    {
        var client = new MutationRecordingApiClient(new NetRatelMcpApiException(
            "remote_request_failed",
            "NetRatel API request failed with HTTP 409.",
            409,
            retryable: false,
            remoteCode: "marker_job_run_terminal"));
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var result = await tools.netratel_marker_jobs("cancel", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["jobId"] = 21,
            ["runId"] = "639233171822224896"
        }), confirm: true);

        result.Success.Should().BeFalse();
        result.Status.Should().Be("failed");
        result.Error.Should().BeEquivalentTo(new NetRatelToolError("marker_job_run_terminal", false, 409));
        result.Summary.Should().Contain("already terminal").And.NotContain("HTTP 409");
        client.Mutations.Should().ContainSingle().Which.Path.Should().EndWith("/marker-jobs/21/runs/639233171822224896/cancel");
    }

    [Fact]
    public async Task Development_terminal_contract_is_closed_bounded_and_never_forwards_arbitrary_input()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        const string sessionId = "0123456789abcdef0123456789abcdef";

        var availability = await tools.netratel_terminal("availability", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") }));
        var get = await tools.netratel_terminal("get", Request(new JsonObject { ["sessionId"] = sessionId }));
        var stream = await tools.netratel_terminal("stream", Request(new JsonObject { ["sessionId"] = sessionId, ["windowSeconds"] = 3, ["maxRecords"] = 7 }));
        var preview = await tools.netratel_terminal("open", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["shell"] = "sh" }));
        var open = await tools.netratel_terminal("open", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["shell"] = "powershell", ["cols"] = 100, ["rows"] = 30 }), confirm: true);
        var selfTest = await tools.netratel_terminal("self_test", Request(new JsonObject { ["sessionId"] = sessionId }), confirm: true);
        var deploymentControlPlaneInspectPreview = await tools.netratel_terminal("deployment-control-plane_inspect", Request(new JsonObject { ["sessionId"] = sessionId }));
        var deploymentControlPlaneInspect = await tools.netratel_terminal("deployment-control-plane_inspect", Request(new JsonObject { ["sessionId"] = sessionId }), confirm: true);
        var fixturePreview = await tools.netratel_terminal("fixture", Request(new JsonObject { ["sessionId"] = sessionId, ["action"] = "create", ["marker"] = "MCP-QA-terminal-001" }));
        var fixture = await tools.netratel_terminal("fixture", Request(new JsonObject { ["sessionId"] = sessionId, ["action"] = "delete", ["marker"] = "MCP-QA-terminal-001" }), confirm: true);
        var resize = await tools.netratel_terminal("resize", Request(new JsonObject { ["sessionId"] = sessionId, ["cols"] = 120, ["rows"] = 40 }), confirm: true);
        var close = await tools.netratel_terminal("close", Request(new JsonObject { ["sessionId"] = sessionId }), confirm: true);

        availability.Success.Should().BeTrue();
        get.Success.Should().BeTrue();
        stream.Success.Should().BeTrue();
        preview.Status.Should().Be("confirmation_required");
        open.Success.Should().BeTrue();
        selfTest.Success.Should().BeTrue();
        deploymentControlPlaneInspectPreview.Status.Should().Be("confirmation_required");
        deploymentControlPlaneInspect.Success.Should().BeTrue();
        fixturePreview.Status.Should().Be("confirmation_required");
        fixture.Success.Should().BeTrue();
        resize.Success.Should().BeTrue();
        close.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/availability",
            "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef",
            "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef/stream?windowSeconds=3&maxRecords=7");
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions", "{\"shell\":\"powershell\",\"cols\":100,\"rows\":30}"),
            (HttpMethod.Post, "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef/self-test", null),
            (HttpMethod.Post, "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef/deployment-control-plane-inspect", null),
            (HttpMethod.Post, "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef/fixture", "{\"action\":\"delete\",\"marker\":\"MCP-QA-terminal-001\"}"),
            (HttpMethod.Post, "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef/resize", "{\"cols\":120,\"rows\":40}"),
            (HttpMethod.Post, "/api/v2/development/mcp/terminal-sessions/0123456789abcdef0123456789abcdef/close", null));
    }

    [Fact]
    public async Task Production_terminal_availability_uses_the_general_operator_route()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["availableShells"] = new JsonArray("sh") });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(
                "prod",
                new Uri("https://api.prod.example/"),
                new Uri("https://mcp.prod.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var result = await tools.netratel_terminal("availability", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));

        result.Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().Be(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/availability");
    }

    [Fact]
    public async Task Development_operator_surface_terminal_availability_uses_the_general_operator_route()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["availableShells"] = new JsonArray("sh") });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(
                "dev",
                new Uri("https://api.dev.example/"),
                new Uri("https://mcp.dev.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests",
            operatorSurfaceEnabled: true);
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var result = await tools.netratel_terminal("availability", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));

        result.Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().Be(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/availability");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("18446744073709551615")]
    public async Task Terminal_output_cursor_is_forwarded_without_numeric_precision_loss(string cursor)
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(new NetRatelMcpTarget("prod", new Uri("https://api.prod.example/"),
            new Uri("https://mcp.prod.example/mcp"), NetRatelMcpCatalog.Revision), NetRatelMcpTransport.StreamableHttp, "tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var result = await tools.netratel_terminal("stream_window", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = "35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            ["sessionId"] = "0123456789abcdef0123456789abcdef",
            ["afterSequence"] = cursor
        }));
        result.Success.Should().BeTrue();
        client.Paths.Should().ContainSingle().Which.Should().EndWith($"/stream-window?afterSequence={cursor}");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("1.2")]
    [InlineData("bad")]
    public async Task Terminal_output_cursor_rejects_invalid_values_before_dispatch(string cursor)
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(new NetRatelMcpTarget("prod", new Uri("https://api.prod.example/"),
            new Uri("https://mcp.prod.example/mcp"), NetRatelMcpCatalog.Revision), NetRatelMcpTransport.StreamableHttp, "tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var result = await tools.netratel_terminal("stream_window", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = "35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            ["sessionId"] = "0123456789abcdef0123456789abcdef",
            ["afterSequence"] = cursor
        }));
        result.Success.Should().BeFalse();
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Production_terminal_contract_uses_only_owned_policy_bound_routes_and_preserves_preview_credentials()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(
                "prod",
                new Uri("https://api.prod.example/"),
                new Uri("https://mcp.prod.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string planToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string idempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";

        var preview = await tools.netratel_terminal("preview_open", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["shell"] = "sh",
            ["workingDirectory"] = "/srv/netratel",
            ["columns"] = 120,
            ["rows"] = 32
        }));
        var openPreview = await tools.netratel_terminal("open", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["shell"] = "sh",
            ["workingDirectory"] = "/srv/netratel",
            ["planToken"] = planToken,
            ["idempotencyKey"] = idempotencyKey
        }));
        var open = await tools.netratel_terminal("open", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["shell"] = "sh",
            ["workingDirectory"] = "/srv/netratel",
            ["planToken"] = planToken,
            ["idempotencyKey"] = idempotencyKey
        }), confirm: true);
        var get = await tools.netratel_terminal("get", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId }));
        var stream = await tools.netratel_terminal("stream_window", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId, ["windowSeconds"] = 3, ["maxRecords"] = 7 }));
        var input = await tools.netratel_terminal("send_input", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId, ["input"] = "whoami\n" }));
        var resizePreview = await tools.netratel_terminal("resize", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId, ["columns"] = 100, ["rows"] = 30 }));
        var resize = await tools.netratel_terminal("resize", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId, ["columns"] = 100, ["rows"] = 30 }), confirm: true);
        var closePreview = await tools.netratel_terminal("close", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId }));
        var close = await tools.netratel_terminal("close", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId }), confirm: true);
        var diagnostics = await tools.netratel_terminal("diagnostics", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sessionId"] = sessionId }));

        preview.Success.Should().BeTrue();
        openPreview.Status.Should().Be("confirmation_required");
        open.Success.Should().BeTrue();
        get.Success.Should().BeTrue();
        stream.Success.Should().BeTrue();
        input.Success.Should().BeTrue();
        resizePreview.Status.Should().Be("confirmation_required");
        resize.Success.Should().BeTrue();
        closePreview.Status.Should().Be("confirmation_required");
        close.Success.Should().BeTrue();
        diagnostics.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/0123456789abcdef0123456789abcdef",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/0123456789abcdef0123456789abcdef/stream-window?windowSeconds=3&maxRecords=7",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/0123456789abcdef0123456789abcdef/diagnostics");
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/preview", "{\"shell\":\"sh\",\"workingDirectory\":\"/srv/netratel\",\"columns\":120,\"rows\":32}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/confirm", "{\"shell\":\"sh\",\"workingDirectory\":\"/srv/netratel\",\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/0123456789abcdef0123456789abcdef/input", "{\"input\":\"whoami\\n\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/0123456789abcdef0123456789abcdef/resize", "{\"columns\":100,\"rows\":30}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/terminal/sessions/0123456789abcdef0123456789abcdef/close", null));
    }

    [Fact]
    public async Task Production_command_contract_uses_preview_confirmation_and_owned_result_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("prod", new Uri("https://api.prod.example/"), new Uri("https://mcp.prod.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        const string commandId = "0123456789abcdef0123456789abcdef";
        const string planToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string idempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        var command = new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["shell"] = "bash",
            ["command"] = "printf operator-command",
            ["workingDirectory"] = "/srv/netratel",
            ["environmentReferences"] = new JsonArray("NETRATEL_TOKEN"),
            ["timeoutSeconds"] = 60,
            ["maximumOutputBytes"] = 4096
        };
        var confirmedCommand = command.DeepClone().AsObject();
        confirmedCommand["planToken"] = planToken;
        confirmedCommand["idempotencyKey"] = idempotencyKey;

        var availability = await tools.netratel_commands("availability", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") }));
        var preview = await tools.netratel_commands("preview_execute", Request(command));
        var executePreview = await tools.netratel_commands("execute", Request(confirmedCommand));
        var execute = await tools.netratel_commands("execute", Request(confirmedCommand), confirm: true);
        var get = await tools.netratel_commands("get", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["commandId"] = commandId }));
        var cancelPreview = await tools.netratel_commands("cancel", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["commandId"] = commandId }));
        var cancel = await tools.netratel_commands("cancel", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["commandId"] = commandId }), confirm: true);

        availability.Success.Should().BeTrue();
        preview.Success.Should().BeTrue();
        executePreview.Status.Should().Be("confirmation_required");
        execute.Success.Should().BeTrue();
        get.Success.Should().BeTrue();
        cancelPreview.Status.Should().Be("confirmation_required");
        cancel.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/commands/availability",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/commands/0123456789abcdef0123456789abcdef");
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/commands/preview", "{\"shell\":\"bash\",\"command\":\"printf operator-command\",\"workingDirectory\":\"/srv/netratel\",\"environmentReferences\":[\"NETRATEL_TOKEN\"],\"timeoutSeconds\":60,\"maximumOutputBytes\":4096}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/commands/confirm", "{\"shell\":\"bash\",\"command\":\"printf operator-command\",\"workingDirectory\":\"/srv/netratel\",\"environmentReferences\":[\"NETRATEL_TOKEN\"],\"timeoutSeconds\":60,\"maximumOutputBytes\":4096,\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/commands/0123456789abcdef0123456789abcdef/cancel", null));
    }

    [Fact]
    public async Task Production_file_artifact_lifecycle_uses_only_the_operator_routes_and_explicit_confirmations()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(
                "prod",
                new Uri("https://api.prod.example/"),
                new Uri("https://mcp.prod.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        var artifactId = Guid.Parse("408bc540-77b0-453a-99df-af20d2b024af");
        var credentials = new JsonObject
        {
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        };

        var previewCollect = await tools.netratel_files("preview_collect", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/log/netratel/agent.bin"
        }));
        var collectConfirmation = await tools.netratel_files("confirm_collect", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/log/netratel/agent.bin",
            ["planToken"] = credentials["planToken"]!.GetValue<string>(),
            ["idempotencyKey"] = credentials["idempotencyKey"]!.GetValue<string>()
        }));
        var confirmCollect = await tools.netratel_files("confirm_collect", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/log/netratel/agent.bin",
            ["planToken"] = credentials["planToken"]!.GetValue<string>(),
            ["idempotencyKey"] = credentials["idempotencyKey"]!.GetValue<string>()
        }), confirm: true);
        var status = await tools.netratel_files("artifact_status", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D")
        }));
        var download = await tools.netratel_files("download", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D")
        }));
        var previewCleanup = await tools.netratel_files("preview_artifact_cleanup", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D")
        }));
        var cleanupConfirmation = await tools.netratel_files("confirm_artifact_cleanup", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D"),
            ["planToken"] = credentials["planToken"]!.GetValue<string>(),
            ["idempotencyKey"] = credentials["idempotencyKey"]!.GetValue<string>()
        }));
        var confirmCleanup = await tools.netratel_files("confirm_artifact_cleanup", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D"),
            ["planToken"] = credentials["planToken"]!.GetValue<string>(),
            ["idempotencyKey"] = credentials["idempotencyKey"]!.GetValue<string>()
        }), confirm: true);

        previewCollect.Success.Should().BeTrue();
        collectConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmCollect.Success.Should().BeTrue();
        status.Success.Should().BeTrue();
        download.Success.Should().BeTrue();
        previewCleanup.Success.Should().BeTrue();
        cleanupConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmCleanup.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/408bc540-77b0-453a-99df-af20d2b024af",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/408bc540-77b0-453a-99df-af20d2b024af/download");
        client.Mutations.Select(mutation => mutation.Path).Should().Equal(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/collect/preview",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/collect/confirm",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/408bc540-77b0-453a-99df-af20d2b024af/cleanup/preview",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/408bc540-77b0-453a-99df-af20d2b024af/cleanup/confirm");
    }

    [Fact]
    public async Task Production_client_observability_reads_and_resync_plan_workflow_use_general_operator_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget(
                "prod",
                new Uri("https://api.prod.example/"),
                new Uri("https://mcp.prod.example/mcp"),
                NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        var browse = await tools.netratel_files("browse", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/log/netratel",
            ["pageSize"] = 20
        }));
        var stat = await tools.netratel_files("stat", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/log/netratel/agent.log"
        }));
        var read = await tools.netratel_files("read", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/log/netratel/agent.log"
        }));
        var previewWrite = await tools.netratel_files("preview_write_text", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/agent.txt",
            ["text"] = "healthy"
        }));
        var writeConfirmation = await tools.netratel_files("confirm_write_text", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/agent.txt",
            ["text"] = "healthy",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }));
        var confirmWrite = await tools.netratel_files("confirm_write_text", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/agent.txt",
            ["text"] = "healthy",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);
        var previewUpload = await tools.netratel_files("preview_upload", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/agent.bin",
            ["contentBase64"] = "AAEC/w=="
        }));
        var uploadConfirmation = await tools.netratel_files("confirm_upload", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/agent.bin",
            ["contentBase64"] = "AAEC/w==",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }));
        var confirmUpload = await tools.netratel_files("confirm_upload", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/agent.bin",
            ["contentBase64"] = "AAEC/w==",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);
        var previewCreateDirectory = await tools.netratel_files("preview_create_directory", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/exports"
        }));
        var createDirectoryConfirmation = await tools.netratel_files("confirm_create_directory", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/exports",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }));
        var confirmCreateDirectory = await tools.netratel_files("confirm_create_directory", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/exports",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);
        var previewDelete = await tools.netratel_files("preview_delete", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/exports/marker.txt"
        }));
        var deleteConfirmation = await tools.netratel_files("confirm_delete", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/exports/marker.txt",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }));
        var confirmDelete = await tools.netratel_files("confirm_delete", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/var/lib/netratel/exports/marker.txt",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);
        var previewCopy = await tools.netratel_files("preview_copy", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourcePath"] = "/var/log/netratel/agent.log",
            ["destinationPath"] = "/var/lib/netratel/exports/agent-copy.log"
        }));
        var copyConfirmation = await tools.netratel_files("confirm_copy", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourcePath"] = "/var/log/netratel/agent.log",
            ["destinationPath"] = "/var/lib/netratel/exports/agent-copy.log",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }));
        var confirmCopy = await tools.netratel_files("confirm_copy", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourcePath"] = "/var/log/netratel/agent.log",
            ["destinationPath"] = "/var/lib/netratel/exports/agent-copy.log",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);
        var previewMove = await tools.netratel_files("preview_move", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourcePath"] = "/var/lib/netratel/exports/agent-copy.log",
            ["destinationPath"] = "/var/lib/netratel/exports/agent-moved.log"
        }));
        var moveConfirmation = await tools.netratel_files("confirm_move", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourcePath"] = "/var/lib/netratel/exports/agent-copy.log",
            ["destinationPath"] = "/var/lib/netratel/exports/agent-moved.log",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }));
        var confirmMove = await tools.netratel_files("confirm_move", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourcePath"] = "/var/lib/netratel/exports/agent-copy.log",
            ["destinationPath"] = "/var/lib/netratel/exports/agent-moved.log",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);
        var sources = await tools.netratel_client_logs("sources", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var history = await tools.netratel_client_logs("history", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime"
        }));
        var search = await tools.netratel_client_logs("search", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime",
            ["text"] = "health"
        }));
        var tail = await tools.netratel_client_logs("tail", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime"
        }));
        var snapshot = await tools.netratel_client_telemetry("snapshot", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var window = await tools.netratel_client_telemetry("stream_window", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var resync = await tools.netratel_client_logs("resync", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime"
        }), confirm: true);
        var previewResync = await tools.netratel_client_logs("preview_resync", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime"
        }));
        var confirmResync = await tools.netratel_client_logs("confirm_resync", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime",
            ["planToken"] = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko",
            ["idempotencyKey"] = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0"
        }), confirm: true);

        browse.Success.Should().BeTrue();
        stat.Success.Should().BeTrue();
        read.Success.Should().BeTrue();
        previewWrite.Success.Should().BeTrue();
        writeConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmWrite.Success.Should().BeTrue();
        previewUpload.Success.Should().BeTrue();
        uploadConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmUpload.Success.Should().BeTrue();
        previewCreateDirectory.Success.Should().BeTrue();
        createDirectoryConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmCreateDirectory.Success.Should().BeTrue();
        previewDelete.Success.Should().BeTrue();
        deleteConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmDelete.Success.Should().BeTrue();
        previewCopy.Success.Should().BeTrue();
        copyConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmCopy.Success.Should().BeTrue();
        previewMove.Success.Should().BeTrue();
        moveConfirmation.RequiresConfirmation.Should().BeTrue();
        confirmMove.Success.Should().BeTrue();
        sources.Success.Should().BeTrue();
        history.Success.Should().BeTrue();
        search.Success.Should().BeTrue();
        tail.Success.Should().BeTrue();
        snapshot.Success.Should().BeTrue();
        window.Success.Should().BeTrue();
        resync.Error.Should().BeEquivalentTo(new NetRatelToolError(
            "unsupported_operation",
            false,
            AllowedOperations: ["sources", "history", "search", "tail", "preview_resync", "confirm_resync"]));
        previewResync.Success.Should().BeTrue();
        confirmResync.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/browse?path=%2Fvar%2Flog%2Fnetratel&pageSize=20",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/stat?path=%2Fvar%2Flog%2Fnetratel%2Fagent.log",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/read?path=%2Fvar%2Flog%2Fnetratel%2Fagent.log",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/sources",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/history?sourceId=netratel-runtime",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/search?sourceId=netratel-runtime&text=health",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/tail?sourceId=netratel-runtime",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/telemetry/snapshot",
            "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/telemetry/stream-window");
        client.Mutations.Should().Contain(mutation => mutation.Method == HttpMethod.Post && mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/resync/preview");
        client.Mutations.Should().Contain(mutation => mutation.Method == HttpMethod.Post && mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/resync/confirm");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/write-text/preview" &&
            mutation.Body!["path"]!.GetValue<string>() == "/var/lib/netratel/agent.txt" &&
            mutation.Body["text"]!.GetValue<string>() == "healthy");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/write-text/confirm" &&
            mutation.Body!["planToken"]!.GetValue<string>() == "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko" &&
            mutation.Body["idempotencyKey"]!.GetValue<string>() == "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/upload/preview" &&
            mutation.Body!["contentBase64"]!.GetValue<string>() == "AAEC/w==");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/upload/confirm" &&
            mutation.Body!["contentBase64"]!.GetValue<string>() == "AAEC/w==" &&
            mutation.Body["planToken"]!.GetValue<string>() == "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/create-directory/preview" &&
            mutation.Body!["path"]!.GetValue<string>() == "/var/lib/netratel/exports");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/create-directory/confirm" &&
            mutation.Body!["planToken"]!.GetValue<string>() == "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko" &&
            mutation.Body["idempotencyKey"]!.GetValue<string>() == "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/delete/preview" &&
            mutation.Body!["path"]!.GetValue<string>() == "/var/lib/netratel/exports/marker.txt");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/delete/confirm" &&
            mutation.Body!["planToken"]!.GetValue<string>() == "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko" &&
            mutation.Body["idempotencyKey"]!.GetValue<string>() == "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/copy/preview" &&
            mutation.Body!["sourcePath"]!.GetValue<string>() == "/var/log/netratel/agent.log" &&
            mutation.Body["destinationPath"]!.GetValue<string>() == "/var/lib/netratel/exports/agent-copy.log");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/copy/confirm" &&
            mutation.Body!["planToken"]!.GetValue<string>() == "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko" &&
            mutation.Body["destinationPath"]!.GetValue<string>() == "/var/lib/netratel/exports/agent-copy.log");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/move/preview" &&
            mutation.Body!["sourcePath"]!.GetValue<string>() == "/var/lib/netratel/exports/agent-copy.log" &&
            mutation.Body["destinationPath"]!.GetValue<string>() == "/var/lib/netratel/exports/agent-moved.log");
        client.Mutations.Should().Contain(mutation =>
            mutation.Method == HttpMethod.Post &&
            mutation.Path == "/api/v2/mcp/operator/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/move/confirm" &&
            mutation.Body!["planToken"]!.GetValue<string>() == "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko" &&
            mutation.Body["destinationPath"]!.GetValue<string>() == "/var/lib/netratel/exports/agent-moved.log");
    }

    [Fact]
    public async Task Operator_route_failure_exposes_its_safe_policy_code_to_the_mcp_caller()
    {
        var client = new RecordingApiClient(exception: new NetRatelMcpApiException(
            "remote_request_failed",
            "NetRatel API request failed with HTTP 403.",
            403,
            retryable: false,
            remoteCode: "target_policy_missing"));
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var result = await tools.netratel_terminal("availability", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));

        result.Error.Should().BeEquivalentTo(new NetRatelToolError("target_policy_missing", false, 403));
        result.Failure.Should().BeEquivalentTo(new NetRatelToolFailure(
            "target_policy_missing",
            "policy",
            false,
            ["netratel.mcp.observe"],
            "netratel_terminal/availability",
            null,
            "The requested target operation did not satisfy the current operator policy.",
            "Use netratel_access to inspect the target policy and ask a policy administrator for a reviewed change."));
    }

    [Fact]
    public async Task Development_terminal_rejects_unbounded_or_arbitrary_input_requests_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var invalidSession = await tools.netratel_terminal("get", Request(new JsonObject { ["sessionId"] = "invalid" }));
        var openInput = await tools.netratel_terminal("open", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = Guid.NewGuid().ToString("D"), ["shell"] = "sh", ["command"] = "whoami" }));
        var invalidStream = await tools.netratel_terminal("stream", Request(new JsonObject { ["sessionId"] = "0123456789abcdef0123456789abcdef", ["windowSeconds"] = 16 }));
        var input = await tools.netratel_terminal("stdin", Request(new JsonObject { ["sessionId"] = "0123456789abcdef0123456789abcdef", ["data"] = "whoami" }), confirm: true);
        var fixture = await tools.netratel_terminal("fixture", Request(new JsonObject { ["sessionId"] = "0123456789abcdef0123456789abcdef", ["action"] = "create", ["marker"] = "unsafe;whoami" }), confirm: true);

        invalidSession.Status.Should().Be("invalid_request");
        openInput.Status.Should().Be("invalid_request");
        invalidStream.Status.Should().Be("invalid_request");
        input.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["availability", "get", "stream", "open", "self_test", "deployment-control-plane_inspect", "fixture", "resize", "close"]));
        fixture.Status.Should().Be("invalid_request");
        client.Paths.Should().BeEmpty();
        client.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Development_onboarding_contract_is_closed_target_owned_and_returns_a_code_only_from_confirmed_creation()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");
        var enrollmentCodeId = Guid.Parse("10d5f8a2-4d9b-41e8-8a16-b0b7f230a525");
        const string marker = "MCP-QA-onboarding-001";

        var collateral = await tools.netratel_onboarding("collateral", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["runtime"] = "linux-x64" }));
        var metadata = await tools.netratel_onboarding("get_enrollment", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["enrollmentCodeId"] = enrollmentCodeId.ToString("D") }));
        var preview = await tools.netratel_onboarding("create_enrollment", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["validForMinutes"] = 5, ["marker"] = marker }));
        var create = await tools.netratel_onboarding("create_enrollment", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["validForMinutes"] = 10, ["marker"] = marker }), confirm: true);
        var revoke = await tools.netratel_onboarding("revoke_enrollment", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["enrollmentCodeId"] = enrollmentCodeId.ToString("D") }), confirm: true);

        collateral.Success.Should().BeTrue();
        metadata.Success.Should().BeTrue();
        preview.Status.Should().Be("confirmation_required");
        create.Success.Should().BeTrue();
        revoke.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/onboarding/collateral/linux-x64",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/onboarding/enrollment-codes/10d5f8a2-4d9b-41e8-8a16-b0b7f230a525");
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/onboarding/enrollment-codes", "{\"validForMinutes\":10,\"marker\":\"MCP-QA-onboarding-001\"}"),
            (HttpMethod.Post, "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/onboarding/enrollment-codes/10d5f8a2-4d9b-41e8-8a16-b0b7f230a525/revoke", null));
    }

    [Fact]
    public async Task Development_onboarding_rejects_caller_supplied_codes_and_invalid_markers_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.NewGuid();

        var invalidMarker = await tools.netratel_onboarding("create_enrollment", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["validForMinutes"] = 5, ["marker"] = "not-a-marker" }), confirm: true);
        var callerCode = await tools.netratel_onboarding("create_enrollment", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["validForMinutes"] = 5, ["marker"] = "MCP-QA-ok", ["code"] = "caller-supplied" }), confirm: true);
        var unsupported = await tools.netratel_onboarding("download", Request(new JsonObject()));

        invalidMarker.Status.Should().Be("invalid_request");
        callerCode.Status.Should().Be("invalid_request");
        unsupported.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["collateral", "get_enrollment", "create_enrollment", "revoke_enrollment"]));
        client.Paths.Should().BeEmpty();
        client.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Production_onboarding_uses_tenant_scoped_policy_bound_routes_and_preview_credentials()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var host = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("prod", new Uri("https://api.prod.example/"), new Uri("https://mcp.prod.example/mcp"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "NetRatel.Mcp.Http.Tests");
        var tools = new NetRatelMcpOperationalTools(client, host);
        var enrollmentCodeId = Guid.Parse("10d5f8a2-4d9b-41e8-8a16-b0b7f230a525");
        const string planToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        const string idempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        var create = new JsonObject { ["tenantId"] = 7, ["runtime"] = "linux-x64", ["validForMinutes"] = 5, ["maxUses"] = 1 };
        var confirmedCreate = create.DeepClone().AsObject();
        confirmedCreate["planToken"] = planToken;
        confirmedCreate["idempotencyKey"] = idempotencyKey;
        var revoke = new JsonObject { ["tenantId"] = 7, ["enrollmentCodeId"] = enrollmentCodeId.ToString("D") };
        var confirmedRevoke = revoke.DeepClone().AsObject();
        confirmedRevoke["planToken"] = planToken;
        confirmedRevoke["idempotencyKey"] = idempotencyKey;

        var collateral = await tools.netratel_onboarding("collateral", Request(new JsonObject { ["tenantId"] = 7, ["runtime"] = "linux-x64" }));
        var download = await tools.netratel_onboarding("collateral_download", Request(new JsonObject { ["tenantId"] = 7, ["runtime"] = "linux-x64" }));
        var metadata = await tools.netratel_onboarding("get_enrollment", Request(revoke));
        var list = await tools.netratel_onboarding("list_enrollments", Request(new JsonObject { ["tenantId"] = 7, ["status"] = "active", ["cursor"] = 123L, ["limit"] = 25 }));
        var createPreview = await tools.netratel_onboarding("create_enrollment", Request(create));
        var createConfirm = await tools.netratel_onboarding("create_enrollment", Request(confirmedCreate), confirm: true);
        var revokePreview = await tools.netratel_onboarding("revoke_enrollment", Request(revoke));
        var revokeConfirm = await tools.netratel_onboarding("revoke_enrollment", Request(confirmedRevoke), confirm: true);
        var targetBound = await tools.netratel_onboarding("collateral", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = Guid.NewGuid().ToString("D"), ["runtime"] = "linux-x64" }));

        collateral.Success.Should().BeTrue();
        download.Success.Should().BeTrue();
        metadata.Success.Should().BeTrue();
        list.Success.Should().BeTrue();
        createPreview.Success.Should().BeTrue();
        createConfirm.Success.Should().BeTrue();
        revokePreview.Success.Should().BeTrue();
        revokeConfirm.Success.Should().BeTrue();
        targetBound.Status.Should().Be("invalid_request");
        client.Paths.Should().Equal(
            "/api/v2/mcp/operator/tenants/7/onboarding/collateral/linux-x64",
            "/api/v2/mcp/operator/tenants/7/onboarding/collateral/linux-x64/download",
            "/api/v2/mcp/operator/tenants/7/onboarding/enrollments/10d5f8a2-4d9b-41e8-8a16-b0b7f230a525",
            "/api/v2/mcp/operator/tenants/7/onboarding/enrollments?status=active&cursor=123&limit=25");
        client.Mutations.Select(mutation => (mutation.Method, mutation.Path, Body: mutation.Body?.ToJsonString())).Should().Equal(
            (HttpMethod.Post, "/api/v2/mcp/operator/tenants/7/onboarding/preview/create-enrollment", "{\"runtime\":\"linux-x64\",\"validForMinutes\":5,\"maxUses\":1}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/tenants/7/onboarding/confirm/create-enrollment", "{\"runtime\":\"linux-x64\",\"validForMinutes\":5,\"maxUses\":1,\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/tenants/7/onboarding/preview/revoke-enrollment", "{\"enrollmentCodeId\":\"10d5f8a2-4d9b-41e8-8a16-b0b7f230a525\"}"),
            (HttpMethod.Post, "/api/v2/mcp/operator/tenants/7/onboarding/confirm/revoke-enrollment", "{\"enrollmentCodeId\":\"10d5f8a2-4d9b-41e8-8a16-b0b7f230a525\",\"planToken\":\"1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko\",\"idempotencyKey\":\"M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0\"}"));
    }

    [Fact]
    public async Task Source_backed_tenant_reads_forward_only_closed_reviewed_contracts()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);

        var list = await tools.netratel_tenants("list");
        var get = await tools.netratel_tenants("get", Request(new JsonObject { ["tenantId"] = "42" }));

        list.Success.Should().BeTrue();
        get.Success.Should().BeTrue();
        client.Paths.Should().Equal("/api/v1/tenants/", "/api/v1/tenants/42");
    }

    [Fact]
    public async Task Source_backed_tenant_reads_reject_open_invalid_or_mutating_requests_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var zeroId = await tools.netratel_tenants("get", Request(new JsonObject { ["tenantId"] = 0 }));
        var oversizedId = await tools.netratel_tenants("get", Request(new JsonObject { ["tenantId"] = "2147483648" }));
        var openRequest = await tools.netratel_tenants("get", Request(new JsonObject { ["tenantId"] = 42, ["environment"] = "Development" }));
        var mutation = await tools.netratel_tenants("delete", Request(new JsonObject { ["tenantId"] = 42 }));

        zeroId.Status.Should().Be("invalid_request");
        oversizedId.Status.Should().Be("invalid_request");
        openRequest.Status.Should().Be("invalid_request");
        mutation.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["list", "get"]));
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Job_definition_reads_reject_open_or_nonpositive_requests_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var openList = await tools.netratel_jobs("list", Request(new JsonObject { ["tenantId"] = 7 }));
        var zeroId = await tools.netratel_jobs("details", Request(new JsonObject { ["jobId"] = 0 }));
        var openId = await tools.netratel_jobs("steps", Request(new JsonObject { ["jobId"] = 7, ["extra"] = true }));
        var tooLargeId = await tools.netratel_jobs("get", Request(new JsonObject { ["jobId"] = "9223372036854775808" }));

        openList.Status.Should().Be("invalid_request");
        zeroId.Status.Should().Be("invalid_request");
        openId.Status.Should().Be("invalid_request");
        tooLargeId.Status.Should().Be("invalid_request");
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Source_backed_client_reads_forward_only_the_closed_reviewed_contracts()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var attempts = await tools.netratel_clients("update_attempts", Request(new JsonObject
        {
            ["clientIdentity"] = agentId.ToString("D"),
            ["releaseId"] = 3,
            ["status"] = "accepted"
        }));
        var defaultPresence = await tools.netratel_clients("presence");
        var presence = await tools.netratel_clients("presence", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["search"] = "qa-agent",
            ["online"] = true,
            ["limit"] = 20
        }));
        var binding = await tools.netratel_clients("binding", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var telemetry = await tools.netratel_clients("telemetry", Request(new JsonObject { ["clientIdentity"] = "client-1" }));
        var developmentTelemetry = await tools.netratel_clients("telemetry", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var invalidTelemetry = await tools.netratel_clients("telemetry", Request(new JsonObject
        {
            ["tenantId"] = 0,
            ["agentId"] = agentId.ToString("D")
        }));
        var invalid = await tools.netratel_clients("update_attempts", Request(new JsonObject { ["clientIdentity"] = "not-a-guid" }));

        attempts.Success.Should().BeTrue();
        defaultPresence.Success.Should().BeTrue();
        presence.Success.Should().BeTrue();
        binding.Success.Should().BeTrue();
        telemetry.Success.Should().BeTrue();
        developmentTelemetry.Success.Should().BeTrue();
        invalidTelemetry.Status.Should().Be("invalid_request");
        invalid.Status.Should().Be("invalid_request");
        client.Paths.Should().Equal(
            "/api/v1/client-updates/attempts?clientIdentity=35ba3a1d-8665-499d-a8a6-42fa07e9a633&releaseId=3&status=Accepted",
            "/api/v2/client-presence/",
            "/api/v2/client-presence/?tenantId=7&search=qa-agent&online=true&limit=20",
            "/api/v2/tenants/7/primary-client-bindings/agents/35ba3a1d-8665-499d-a8a6-42fa07e9a633",
            "/api/v1/clients/client-1/telemetry",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/telemetry/snapshot");
    }

    [Fact]
    public async Task Source_backed_client_presence_and_binding_reject_open_invalid_or_mutating_requests_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var tooLargeLimit = await tools.netratel_clients("presence", Request(new JsonObject { ["limit"] = 101 }));
        var openPresence = await tools.netratel_clients("presence", Request(new JsonObject { ["search"] = "qa", ["environment"] = "Development" }));
        var invalidOnline = await tools.netratel_clients("presence", Request(new JsonObject { ["online"] = "true" }));
        var invalidBinding = await tools.netratel_clients("binding", Request(new JsonObject { ["tenantId"] = 0, ["agentId"] = "not-a-guid" }));
        var mutation = await tools.netratel_clients("grant", Request(new JsonObject { ["tenantId"] = 7 }));

        tooLargeLimit.Status.Should().Be("invalid_request");
        openPresence.Status.Should().Be("invalid_request");
        invalidOnline.Status.Should().Be("invalid_request");
        invalidBinding.Status.Should().Be("invalid_request");
        mutation.Status.Should().Be("invalid_request");
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Development_fixture_file_reads_forward_only_closed_target_and_path_contracts()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var browse = await tools.netratel_files("browse", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/tmp/netratel-mcp-qa",
            ["pageSize"] = 20
        }));
        var read = await tools.netratel_files("read", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/tmp/netratel-mcp-qa/marker.txt"
        }));
        var open = await tools.netratel_files("browse", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/tmp/netratel-mcp-qa",
            ["environment"] = "Development"
        }));
        var oversizedPage = await tools.netratel_files("browse", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/tmp/netratel-mcp-qa",
            ["pageSize"] = 101
        }));
        var artifactId = Guid.Parse("408bc540-77b0-453a-99df-af20d2b024af");
        var collectRequest = Request(new JsonObject
        {
            ["tenantId"] = "7",
            ["agentId"] = agentId.ToString("D"),
            ["path"] = "/tmp/netratel-mcp-qa/MCP-QA-file-artifact.txt",
            ["marker"] = "MCP-QA-file-artifact"
        });
        var collectPreview = await tools.netratel_files("collect", collectRequest);
        var collect = await tools.netratel_files("collect", collectRequest, confirm: true);
        var status = await tools.netratel_files("status", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D")
        }));
        var cleanupPreview = await tools.netratel_files("cleanup", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D")
        }));
        var cleanup = await tools.netratel_files("cleanup", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["artifactId"] = artifactId.ToString("D")
        }), confirm: true);

        browse.Success.Should().BeTrue();
        read.Success.Should().BeTrue();
        open.Status.Should().Be("invalid_request");
        oversizedPage.Status.Should().Be("invalid_request");
        collectPreview.Status.Should().Be("confirmation_required");
        collectPreview.RequiresConfirmation.Should().BeTrue();
        collect.Success.Should().BeTrue();
        status.Success.Should().BeTrue();
        cleanupPreview.Status.Should().Be("confirmation_required");
        cleanup.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files?path=%2Ftmp%2Fnetratel-mcp-qa&pageSize=20",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/inline?path=%2Ftmp%2Fnetratel-mcp-qa%2Fmarker.txt",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/files/artifacts/408bc540-77b0-453a-99df-af20d2b024af");
        client.Mutations.Should().ContainSingle(mutation => mutation.Method == HttpMethod.Post && mutation.Path.EndsWith("/files/collect", StringComparison.Ordinal));
        client.Mutations.Should().ContainSingle(mutation => mutation.Method == HttpMethod.Delete && mutation.Path.EndsWith("/artifacts/408bc540-77b0-453a-99df-af20d2b024af", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Development_client_observability_forwards_closed_read_and_confirmed_resync_contracts()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var sources = await tools.netratel_client_logs("sources", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var history = await tools.netratel_client_logs("history", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime",
            ["cursor"] = "before:abc",
            ["pageSize"] = 25,
            ["severity"] = "Information",
            ["prefix"] = "Gateway",
            ["category"] = "Gateway",
            ["text"] = "health check"
        }));
        var search = await tools.netratel_client_logs("search", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime",
            ["text"] = "health check"
        }));
        var tail = await tools.netratel_client_logs("tail", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime",
            ["windowSeconds"] = 3,
            ["maxRecords"] = 20
        }));
        var resyncRequest = Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["sourceId"] = "netratel-runtime"
        });
        var resyncPreview = await tools.netratel_client_logs("resync", resyncRequest);
        var resync = await tools.netratel_client_logs("resync", resyncRequest, confirm: true);
        var snapshot = await tools.netratel_client_telemetry("snapshot", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D")
        }));
        var window = await tools.netratel_client_telemetry("stream_window", Request(new JsonObject
        {
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["windowSeconds"] = 3,
            ["maxSamples"] = 5
        }));

        sources.Success.Should().BeTrue();
        history.Success.Should().BeTrue();
        search.Success.Should().BeTrue();
        tail.Success.Should().BeTrue();
        resyncPreview.Status.Should().Be("confirmation_required");
        resync.Success.Should().BeTrue();
        snapshot.Success.Should().BeTrue();
        window.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/sources",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/history?sourceId=netratel-runtime&cursor=before%3Aabc&pageSize=25&severity=Information&prefix=Gateway&category=Gateway&text=health%20check",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/search?sourceId=netratel-runtime&text=health%20check",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/logs/tail?sourceId=netratel-runtime&windowSeconds=3&maxRecords=20",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/telemetry/snapshot",
            "/api/v2/development/mcp/agents/7/35ba3a1d-8665-499d-a8a6-42fa07e9a633/telemetry/stream-window?windowSeconds=3&maxSamples=5");
        client.Mutations.Should().ContainSingle(mutation => mutation.Method == HttpMethod.Post && mutation.Path.EndsWith("/logs/resync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Development_client_observability_reads_reject_open_or_out_of_range_requests_before_dispatch()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var openSources = await tools.netratel_client_logs("sources", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["environment"] = "Development" }));
        var invalidHistory = await tools.netratel_client_logs("history", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sourceId"] = "runtime", ["pageSize"] = 101 }));
        var missingSearchText = await tools.netratel_client_logs("search", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sourceId"] = "runtime" }));
        var invalidTail = await tools.netratel_client_logs("tail", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sourceId"] = "runtime", ["windowSeconds"] = 16 }));
        var invalidResync = await tools.netratel_client_logs("resync", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["sourceId"] = "runtime", ["unexpected"] = true }), confirm: true);
        var invalidWindow = await tools.netratel_client_telemetry("stream_window", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D"), ["maxSamples"] = 21 }));
        var mutation = await tools.netratel_client_telemetry("refresh", Request(new JsonObject { ["tenantId"] = 7, ["agentId"] = agentId.ToString("D") }));

        openSources.Status.Should().Be("invalid_request");
        invalidHistory.Status.Should().Be("invalid_request");
        missingSearchText.Status.Should().Be("invalid_request");
        invalidTail.Status.Should().Be("invalid_request");
        invalidResync.Status.Should().Be("invalid_request");
        invalidWindow.Status.Should().Be("invalid_request");
        mutation.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["snapshot", "stream_window"]));
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Operational_tools_reject_mutations_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var result = await tools.netratel_jobs("delete", Request(new JsonObject { ["jobId"] = "42" }));

        result.Success.Should().BeFalse();
        result.Status.Should().Be("invalid_request");
        result.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["list", "get", "details", "params", "steps"]));
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Operational_tools_reject_invalid_bounded_log_arguments_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var result = await tools.netratel_tasks("logs", Request(new JsonObject { ["taskId"] = "42", ["sinceId"] = -1 }));

        result.Success.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new NetRatelToolError("validation_error", false));
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Job_run_reads_forward_only_the_closed_bounded_filters_to_reviewed_routes()
    {
        var client = new RecordingApiClient(data: new JsonObject { ["result"] = "ok" });
        var tools = new NetRatelMcpOperationalTools(client);
        var agentId = Guid.Parse("35ba3a1d-8665-499d-a8a6-42fa07e9a633");

        var list = await tools.netratel_job_runs("list", Request(new JsonObject
        {
            ["status"] = "running",
            ["jobId"] = "42",
            ["tenantId"] = 7,
            ["take"] = 25
        }));
        var query = await tools.netratel_job_runs("query", Request(new JsonObject
        {
            ["status"] = "Failed",
            ["jobId"] = 42,
            ["tenantId"] = 7,
            ["agentId"] = agentId.ToString("D"),
            ["search"] = "branch office",
            ["page"] = 2,
            ["pageSize"] = 50
        }));

        list.Success.Should().BeTrue();
        query.Success.Should().BeTrue();
        client.Paths.Should().Equal(
            "/api/v1/jobruns/?status=Running&jobId=42&tenantId=7&take=25",
            "/api/v1/jobruns/query?status=Failed&jobId=42&tenantId=7&agentId=35ba3a1d-8665-499d-a8a6-42fa07e9a633&search=branch%20office&page=2&pageSize=50");
    }

    [Fact]
    public async Task Job_run_reads_reject_open_or_out_of_range_filters_before_any_outbound_request()
    {
        var client = new RecordingApiClient();
        var tools = new NetRatelMcpOperationalTools(client);

        var open = await tools.netratel_job_runs("query", Request(new JsonObject { ["unexpected"] = true }));
        var unbounded = await tools.netratel_job_runs("list", Request(new JsonObject { ["take"] = 101 }));

        open.Status.Should().Be("invalid_request");
        unbounded.Status.Should().Be("invalid_request");
        client.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Stdio_notification_mutation_returns_a_no_call_preview_then_executes_exactly_once()
    {
        var client = new MutationRecordingApiClient();
        var tools = new NetRatelMcpStdioNotificationTools(client, new NetRatelMcpOperationalTools(client));
        var request = Request(new JsonObject { ["ids"] = new JsonArray("notification-1", "notification-2") });

        var preview = await tools.netratel_notifications("mark_read", request);

        preview.Status.Should().Be("confirmation_required");
        preview.RequiresConfirmation.Should().BeTrue();
        preview.Confirmation.Should().BeEquivalentTo(new NetRatelConfirmation("confirm", true, "mark_read", ["notification-1", "notification-2"]));
        client.Mutations.Should().BeEmpty();

        var confirmed = await tools.netratel_notifications("mark_read", request, confirm: true);

        confirmed.Success.Should().BeTrue();
        confirmed.AffectedIds.Should().Equal("notification-1", "notification-2");
        client.Mutations.Should().ContainSingle();
        var mutation = client.Mutations.Single();
        mutation.Method.Should().Be(HttpMethod.Post);
        mutation.Path.Should().Be("/api/v1/notifications/mark-read");
        mutation.Body!.ToJsonString().Should().Be("{\"ids\":[\"notification-1\",\"notification-2\"]}");
    }

    [Fact]
    public async Task Stdio_notification_mutation_rejects_invalid_identifiers_without_an_upstream_call()
    {
        var client = new MutationRecordingApiClient();
        var tools = new NetRatelMcpStdioNotificationTools(client, new NetRatelMcpOperationalTools(client));

        var result = await tools.netratel_notifications("mark_read", Request(new JsonObject { ["ids"] = new JsonArray("notification-1", " ") }), confirm: true);

        result.Status.Should().Be("invalid_request");
        result.Error.Should().BeEquivalentTo(new NetRatelToolError("validation_error", false));
        client.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Stdio_notification_mutation_maps_a_safe_upstream_failure_after_the_confirmed_call()
    {
        var client = new MutationRecordingApiClient(new NetRatelMcpApiException("remote_request_failed", "NetRatel API request failed with HTTP 503.", 503, retryable: true));
        var tools = new NetRatelMcpStdioNotificationTools(client, new NetRatelMcpOperationalTools(client));

        var result = await tools.netratel_notifications("mark_read", Request(new JsonObject { ["ids"] = new JsonArray("notification-1") }), confirm: true);

        result.Status.Should().Be("failed");
        result.Error.Should().BeEquivalentTo(new NetRatelToolError("remote_request_failed", true, 503));
        client.Mutations.Should().ContainSingle();
    }

    [Fact]
    public void Catalog_primitives_report_the_complete_ledger_but_never_enable_excluded_operations()
    {
        var context = new NetRatelMcpHostContext(
            new NetRatelMcpTarget("dev", new Uri("https://dev-api.example"), new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
            NetRatelMcpTransport.StreamableHttp,
            "test");
        var tools = new NetRatelMcpCatalogTools(context);
        var resources = new NetRatelMcpCatalogResources(context);

        var capability = tools.netratel_capabilities();
        var unsupported = tools.netratel_capabilities("unsupported");

        capability.Success.Should().BeTrue();
        capability.Data!.ToJsonString().Should().Contain("netratel_terminal").And.Contain("netratel_onboarding").And.NotContain("stdin").And.NotContain("netratel_client_files");
        var catalog = capability.Data ?? throw new InvalidOperationException("The successful capability response must include catalog data.");
        var catalogTools = catalog["tools"]?.AsArray() ?? throw new InvalidOperationException("The catalog must include tools.");
        var clients = catalogTools.SingleOrDefault(tool => tool?["Name"]?.GetValue<string>() == "netratel_clients")
            ?? throw new InvalidOperationException("The catalog must include netratel_clients.");
        var clientOperations = clients["operations"]?.AsArray() ?? throw new InvalidOperationException("netratel_clients must include operations.");
        var presence = clientOperations.SingleOrDefault(operation => operation?["Name"]?.GetValue<string>() == "presence")
            ?? throw new InvalidOperationException("netratel_clients must include presence.");
        presence["safety"]!.GetValue<string>().Should().Be("Read");
        presence["RequiresConfirmation"]!.GetValue<bool>().Should().BeFalse();
        presence["RequiresIdempotency"]!.GetValue<bool>().Should().BeFalse();
        presence["AvailableOverHttp"]!.GetValue<bool>().Should().BeTrue();
        unsupported.Error.Should().BeEquivalentTo(new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["get"]));
        unsupported.Failure!.RequiredOperation.Should().Be("netratel_capabilities/unsupported");
        unsupported.Failure.RequiredScopes.Should().BeEmpty();
        resources.ServerStatus().Should().Contain("\"mutationToolsEnabled\":true");
        resources.Health().Should().Contain("\"state\":\"ready\"").And.Contain("netratel_health");
        resources.RedactedConfiguration().Should().Contain("\"secretsExposed\":false").And.Contain("\"configurationMutationEnabled\":false");
        resources.Capabilities().Should().NotContain("netratel_remote_support_v2").And.NotContain("mark_read");
        resources.CatalogSchema().Should().Contain(NetRatelMcpCatalog.Revision);
        resources.ResponseSchema().Should().Contain("NetRatelToolResponse");
        resources.ResponseSchema().Should().Contain("failureFields").And.Contain("correlationId");
    }

    private static System.Text.Json.JsonElement Request(JsonObject request)
        => System.Text.Json.JsonDocument.Parse(request.ToJsonString()).RootElement.Clone();

    private sealed class RecordingApiClient : INetRatelMcpApiClient
    {
        private readonly JsonNode? _data;
        private readonly Exception? _exception;

        public RecordingApiClient(JsonNode? data = null, Exception? exception = null)
        {
            _data = data;
            _exception = exception;
        }

        public List<string> Paths { get; } = [];
        public List<(HttpMethod Method, string Path, JsonNode? Body)> Mutations { get; } = [];
        public CancellationToken ObservedCancellation { get; private set; }

        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            ObservedCancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception is not null) return Task.FromException<JsonNode?>(_exception);
            return Task.FromResult(_data);
        }

        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Mutations.Add((method, path, body?.DeepClone()));
            return Task.FromResult(_data?.DeepClone());
        }
    }

    private sealed class MutationRecordingApiClient : INetRatelMcpApiClient
    {
        private readonly Exception? _exception;
        private readonly JsonNode? _readData;

        public MutationRecordingApiClient(JsonNode? readData = null, Exception? exception = null)
        {
            _readData = readData;
            _exception = exception;
        }

        public MutationRecordingApiClient(Exception exception)
            : this(null, exception)
        {
        }

        public List<string> Reads { get; } = [];
        public List<(HttpMethod Method, string Path, JsonNode? Body)> Mutations { get; } = [];

        public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            Reads.Add(path);
            return Task.FromResult<JsonNode?>(_readData?.DeepClone() ?? new JsonObject { ["path"] = path });
        }

        public Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
        {
            Mutations.Add((method, path, body?.DeepClone()));
            if (_exception is not null) return Task.FromException<JsonNode?>(_exception);
            return Task.FromResult<JsonNode?>(new JsonObject { ["marked"] = true, ["inputsJson"] = "write-only-input" });
        }
    }
}
