using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Infrastructure.Identity;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Components.Shared.Integrations;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class CredentialAccessEditorTests : AsyncBunitContext
{
    [Theory]
    [InlineData("/authority", "load-authority", HttpStatusCode.Unauthorized, "Sign in again")]
    [InlineData("/", "load-credentials", HttpStatusCode.Forbidden, "cannot approve or delegate")]
    [InlineData("/access-requests", "load-reviews", HttpStatusCode.BadRequest, "different HTTP MCP resource")]
    [InlineData("/access-requests", "load-reviews", HttpStatusCode.TooManyRequests, "wait a moment")]
    [InlineData("/access-requests", "load-reviews", HttpStatusCode.ServiceUnavailable, "temporarily unavailable")]
    public void Loading_failures_identify_the_request_and_recover_the_complete_snapshot(string suffix, string stage,
        HttpStatusCode status, string recovery)
    {
        using var transport = Configure();
        transport.LoadFailureSuffix = suffix;
        transport.LoadFailureStatus = status;
        var editor = RenderEditor();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-edit-error']").TextContent.Should().Contain(recovery));
        editor.Find("[data-testid='access-edit-diagnostic']").TextContent.Should()
            .Contain(stage).And.Contain(((int)status).ToString()).And.Contain("synthetic-trace-429");
        editor.FindAll("[data-testid='permission-selector']").Should().BeEmpty();
        editor.Markup.Should().NotContain("upstream-secret-marker");
        transport.Submitted.Should().BeEmpty();

        transport.LoadFailureSuffix = null;
        editor.Find("[data-testid='access-edit-retry-load']").Click();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-review']").Should().ContainSingle());
        editor.Find("[data-testid='access-edit-permission-17-flow.read']").HasAttribute("checked").Should().BeTrue();
        editor.Find("[data-testid='permission-scope']").Change("server");
        editor.Find("[data-testid='access-edit-instance-permission-mcp.discovery.read']").HasAttribute("checked").Should().BeTrue();
        editor.FindAll("[data-testid='access-edit-error']").Should().BeEmpty();
    }

    [Fact]
    public void Adding_a_tenant_preserves_existing_access_and_exact_expiry_and_requires_explicit_apply()
    {
        using var transport = Configure();
        ReviewedMcpAccessReceipt? applied = null;
        var editor = Render<CredentialAccessEditor>(parameters => parameters
            .Add(component => component.CredentialId, AccessTransport.CredentialId)
            .Add(component => component.Applied, receipt => applied = receipt));
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-add-tenant-select']").TextContent.Should().Contain("Customer B"));

        AddSecondTenant(editor);
        editor.Find("[data-testid='permission-scope']").Change("server");
        editor.Find("[data-testid='access-edit-instance-permission-mcp.discovery.read']").HasAttribute("checked").Should().BeTrue();
        editor.Find("[data-testid='permission-scope']").Change("17");
        editor.Find("[data-testid='access-edit-permission-17-flow.read']").HasAttribute("checked").Should().BeTrue();
        editor.Find("[data-testid='access-review']").Click();

        editor.WaitForAssertion(() => editor.Find("[data-testid='access-change-summary']").TextContent
            .Should().Contain("2 tenant grants").And.Contain("1 server-level grants").And.Contain("Customer B"));
        transport.Submitted.Single().Grants.Should().BeEquivalentTo([new IntegrationPairingGrant(17, "flow.read"), new(18, "flow.read")]);
        transport.Submitted.Single().InstancePermissions.Should().Equal("mcp.discovery.read");
        transport.SubmittedFields.Single().Should().BeEquivalentTo(["requestId", "grants", "instancePermissions"]);
        editor.Markup.Should().Contain("2030-04-01T10:11:12.123456Z");
        transport.Receipt!.Request.Proposal.Targets.Should().BeEmpty();
        editor.Find("[data-testid='access-apply']").HasAttribute("disabled").Should().BeTrue();
        transport.ApplyCalls.Should().Be(0);
        applied.Should().BeNull();

        editor.Find("[data-testid='access-review-confirm']").Change(true);
        editor.Find("[data-testid='access-apply']").Click();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-edit-saved']").TextContent.Should().Contain("saved secret"));
        transport.ApplyCalls.Should().Be(1);
        transport.ApplyDecisions.Single().Should().Be((AccessTransport.ProposalHash, true));
        applied.Should().NotBeNull();
        applied!.CredentialId.Should().Be(AccessTransport.CredentialId);
        applied.CredentialExpiresAtUtc.Should().Be(AccessTransport.ExactExpiry);
        applied.ConfiguredGrants.Should().BeEquivalentTo(transport.Submitted.Single().Grants);
        applied.ConfiguredInstancePermissions.Should().Equal("mcp.discovery.read");
    }

    [Fact]
    public void Tenant_permissions_and_server_permissions_can_change_independently_and_tenants_can_be_removed()
    {
        using var transport = Configure();
        transport.Credential = transport.Credential with
        {
            Grants = [new(17, "flow.read"), new(19, "telemetry.read")]
        };
        var editor = RenderEditor();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-remove-tenant-19']").Should().NotBeNull());
        editor.Find("[data-testid='access-edit-permission-17-flow.read']").Change(false);
        editor.Find("[data-testid='access-edit-permission-17-flow.edit']").Change(true);
        editor.Find("[data-testid='access-remove-tenant-19']").Click();
        editor.Find("[data-testid='permission-scope']").Change("server");
        editor.Find("[data-testid='access-edit-instance-permission-mcp.discovery.read']").Change(false);
        editor.Find("[data-testid='access-edit-instance-permission-telemetry.read']").Change(true);
        editor.Find("[data-testid='access-review']").Click();

        editor.WaitForAssertion(() => transport.Submitted.Should().ContainSingle());
        transport.Submitted.Single().Grants.Should().Equal(new IntegrationPairingGrant(17, "flow.edit"));
        transport.Submitted.Single().InstancePermissions.Should().Equal("telemetry.read");
        editor.Find("[data-testid='access-change-summary']").TextContent.Should()
            .Contain("Removed access").And.Contain("Customer C (ID 19): Read telemetry").And.Contain("Server-level: Discover MCP capabilities");
    }

    [Fact]
    public void Rejected_review_keeps_editable_selections_and_failed_save_keeps_review_until_cancelled()
    {
        using var transport = Configure();
        transport.ReviewStatus = HttpStatusCode.Forbidden;
        var editor = RenderEditor();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-review']").Should().ContainSingle());
        AddSecondTenant(editor);
        editor.Find("[data-testid='access-review']").Click();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-edit-error']").TextContent.Should().Contain("cannot approve or delegate"));
        editor.Find("[data-testid='access-edit-permission-18-flow.read']").HasAttribute("checked").Should().BeTrue();
        editor.Find("[data-testid='permission-scope']").HasAttribute("disabled").Should().BeFalse();
        var rejectedRequestId = transport.Submitted.Single().RequestId;

        transport.ReviewStatus = HttpStatusCode.OK;
        transport.ApplyStatus = HttpStatusCode.ServiceUnavailable;
        editor.Find("[data-testid='access-review']").Click();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-review-confirm']").Should().ContainSingle());
        transport.Submitted.Last().RequestId.Should().NotBe(rejectedRequestId);
        editor.Find("[data-testid='access-review-confirm']").Change(true);
        editor.Find("[data-testid='access-apply']").Click();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-edit-error']").TextContent.Should().Contain("draft is retained"));
        editor.Find("[data-testid='access-change-summary']").TextContent.Should().Contain("2 tenant grants");
        editor.FindAll("[data-testid='access-edit-saved']").Should().BeEmpty();

        editor.Find("[data-testid='access-back-to-edit']").Click();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-review']").Should().ContainSingle());
        transport.CancelCalls.Should().Be(1);
        editor.Find("[data-testid='permission-scope']").Change("18");
        editor.Find("[data-testid='access-edit-permission-18-flow.read']").HasAttribute("checked").Should().BeTrue();
        editor.Find("[data-testid='permission-scope']").Change("17");
        editor.Find("[data-testid='access-edit-permission-17-flow.read']").HasAttribute("checked").Should().BeTrue();
        editor.Find("[data-testid='permission-scope']").Change("server");
        editor.Find("[data-testid='access-edit-instance-permission-mcp.discovery.read']").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Reopening_pending_review_recovers_its_draft_and_applies_external_approval_without_another_apply_click()
    {
        using var transport = Configure();
        transport.Receipt = transport.CreateReceipt(new("4b1235d7858d4c378a42065d31c247d2",
            [new(17, "flow.read"), new(18, "flow.read")], ["mcp.discovery.read"]));
        ReviewedMcpAccessReceipt? applied = null;
        var editor = Render<CredentialAccessEditor>(parameters => parameters
            .Add(component => component.CredentialId, AccessTransport.CredentialId)
            .Add(component => component.Applied, receipt => applied = receipt));
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-pending-review']").TextContent.Should().Contain("ABCD-EFGH"));
        editor.Find("[data-testid='access-change-summary']").TextContent.Should().Contain("2 tenant grants").And.Contain("Customer B");
        transport.Submitted.Should().BeEmpty();
        transport.ApplyCalls.Should().Be(0);

        transport.Receipt = transport.Receipt with { Request = transport.Receipt.Request with { Status = "approved" } };
        editor.Find("[data-testid='access-check-review']").Click();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-edit-saved']").Should().ContainSingle());
        transport.ApplyCalls.Should().Be(1);
        transport.ApplyDecisions.Single().Should().Be((AccessTransport.ProposalHash, true));
        applied!.Request.Status.Should().Be("consumed");
        applied.ConfiguredGrants.Should().Contain(new IntegrationPairingGrant(18, "flow.read"));
    }

    [Fact]
    public void Existing_unavailable_tenant_selections_are_visible_and_preserved_when_other_access_is_changed()
    {
        using var transport = Configure();
        transport.Credential = transport.Credential with { Grants = [new(29, "flow.read")] };
        var editor = RenderEditor();
        editor.WaitForAssertion(() => editor.Find("[data-testid='permission-scope']").TextContent.Should().Contain("Tenant 29 (currently unavailable)"));
        editor.Find("[data-testid='access-edit-permission-29-flow.read']").HasAttribute("checked").Should().BeTrue();
        editor.Markup.Should().Contain("Existing selection");
        editor.Find("[data-testid='access-edit-permission-29-flow.read']").Change(false);
        editor.Find("[data-testid='access-edit-permission-29-flow.read']").HasAttribute("checked").Should().BeFalse();
        editor.Find("[data-testid='access-edit-permission-29-flow.read']").Change(true);
        editor.Find("[data-testid='permission-scope']").Change("server");
        editor.Find("[data-testid='access-edit-instance-permission-telemetry.read']").Change(true);
        editor.Find("[data-testid='access-review']").Click();
        editor.WaitForAssertion(() => transport.Submitted.Should().ContainSingle());
        transport.Submitted.Single().Grants.Should().Equal(new IntegrationPairingGrant(29, "flow.read"));
        transport.Submitted.Single().InstancePermissions.Should().BeEquivalentTo(["mcp.discovery.read", "telemetry.read"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_pending_lookup_retries_the_complete_draft_and_recognizes_concurrent_application(bool consumed)
    {
        using var transport = Configure();
        transport.Receipt = transport.CreateReceipt(new("4b1235d7858d4c378a42065d31c247d2",
            [new(17, "flow.read"), new(18, "flow.read")], ["mcp.discovery.read"]));
        transport.InspectStatus = HttpStatusCode.ServiceUnavailable;
        ReviewedMcpAccessReceipt? applied = null;
        var editor = Render<CredentialAccessEditor>(parameters => parameters
            .Add(component => component.CredentialId, AccessTransport.CredentialId)
            .Add(component => component.Applied, receipt => applied = receipt));

        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-edit-retry-load']").Should().ContainSingle());
        editor.FindAll("[data-testid='permission-selector']").Should().BeEmpty();
        editor.FindAll("[data-testid='access-review']").Should().BeEmpty();
        transport.InspectStatus = HttpStatusCode.OK;
        transport.ConsumeOnInspect = consumed;
        editor.Find("[data-testid='access-edit-retry-load']").Click();

        if (consumed)
        {
            editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-edit-saved']").Should().ContainSingle());
            applied!.ConfiguredGrants.Should().Contain(new IntegrationPairingGrant(18, "flow.read"));
        }
        else
        {
            editor.WaitForAssertion(() => editor.Find("[data-testid='access-change-summary']").TextContent.Should().Contain("Customer B"));
            editor.Find("[data-testid='access-pending-review']").TextContent.Should().Contain("ABCD-EFGH");
            applied.Should().BeNull();
        }
        transport.Submitted.Should().BeEmpty();
        transport.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public void Retrying_an_interrupted_review_recovers_an_applied_receipt_without_resubmitting_a_new_draft()
    {
        using var transport = Configure();
        transport.InterruptReviewResponse = true;
        ReviewedMcpAccessReceipt? applied = null;
        var editor = Render<CredentialAccessEditor>(parameters => parameters
            .Add(component => component.CredentialId, AccessTransport.CredentialId)
            .Add(component => component.Applied, receipt => applied = receipt));
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-review']").Should().ContainSingle());
        AddSecondTenant(editor);
        editor.Find("[data-testid='access-review']").Click();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-edit-error']").TextContent.Should().Contain("submitted draft is retained"));
        editor.Find("[data-testid='permission-scope']").HasAttribute("disabled").Should().BeTrue();

        transport.ConsumeReceipt();
        editor.Find("[data-testid='access-review']").Click();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-edit-saved']").Should().ContainSingle());
        transport.Submitted.Should().HaveCount(2);
        transport.Submitted.Select(draft => draft.RequestId).Distinct().Should().ContainSingle();
        transport.ApplyCalls.Should().Be(0);
        applied!.ConfiguredGrants.Should().Contain(new IntegrationPairingGrant(18, "flow.read"));
    }

    [Fact]
    public void Review_shows_removals_from_the_current_server_grants_when_another_edit_precedes_submission()
    {
        using var transport = Configure();
        var editor = RenderEditor();
        editor.WaitForAssertion(() => editor.FindAll("[data-testid='access-review']").Should().ContainSingle());
        AddSecondTenant(editor);
        transport.Credential = transport.Credential with
        {
            Grants = [new(17, "flow.read"), new(19, "telemetry.read")],
            InstancePermissions = ["mcp.discovery.read", "telemetry.read"]
        };
        editor.Find("[data-testid='access-review']").Click();
        editor.WaitForAssertion(() => editor.Find("[data-testid='access-change-summary']").TextContent.Should()
            .Contain("Customer C (ID 19): Read telemetry").And.Contain("Server-level: Read telemetry"));
        transport.Submitted.Single().Grants.Should().BeEquivalentTo([new IntegrationPairingGrant(17, "flow.read"), new(18, "flow.read")]);
        editor.Find("[data-testid='access-apply']").HasAttribute("disabled").Should().BeTrue();
        transport.ApplyCalls.Should().Be(0);
    }

    private AccessTransport Configure()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var transport = new AccessTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        return transport;
    }

    private IRenderedComponent<CredentialAccessEditor> RenderEditor() => Render<CredentialAccessEditor>(parameters =>
        parameters.Add(component => component.CredentialId, AccessTransport.CredentialId));

    private static void AddSecondTenant(IRenderedComponent<CredentialAccessEditor> editor)
    {
        editor.Find("[data-testid='access-add-tenant-select']").Change("18");
        editor.Find("[data-testid='access-add-tenant']").Click();
        editor.Find("[data-testid='permission-scope']").Change("18");
        editor.Find("[data-testid='access-edit-permission-18-flow.read']").Change(true);
    }

    private sealed record Draft(string RequestId, IReadOnlyList<IntegrationPairingGrant> Grants, IReadOnlyList<string> InstancePermissions);

    private sealed class AccessTransport : HttpMessageHandler, IHttpClientFactory
    {
        public const string CredentialId = "fbd7b3a9da4b4f01a643c5c71dbec5cb";
        public const string ProposalHash = "synthetic-exact-reviewed-proposal-hash";
        public static readonly DateTimeOffset ExactExpiry = DateTimeOffset.Parse("2030-04-01T10:11:12.123456Z");
        private const string Route = "/api/v2/account/integration-credentials/" + CredentialId + "/access-requests";
        public IntegrationCredentialSummary Credential { get; set; } = new(CredentialId, "public-id", "synthetic-prefix", "Customer connection",
            IntegrationCredentialPurpose.HttpMcp, "https://mcp.example.test/mcp", DateTimeOffset.Parse("2026-10-01T12:00:00Z"), ExactExpiry,
            null, null, [new(17, "flow.read")]) { InstancePermissions = ["mcp.discovery.read"] };
        public ReviewedMcpAccessReceipt? Receipt { get; set; }
        public List<Draft> Submitted { get; } = [];
        public List<string[]> SubmittedFields { get; } = [];
        public List<(string ProposalHash, bool Confirm)> ApplyDecisions { get; } = [];
        public int ApplyCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public HttpStatusCode ReviewStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode ApplyStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode InspectStatus { get; set; } = HttpStatusCode.OK;
        public string? LoadFailureSuffix { get; set; }
        public HttpStatusCode LoadFailureStatus { get; set; }
        public bool InterruptReviewResponse { get; set; }
        public bool ConsumeOnInspect { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };

        public ReviewedMcpAccessReceipt CreateReceipt(Draft draft)
        {
            var proposal = new ReviewedMcpAccessProposal(draft.RequestId, Credential.Name, "reconcile", Credential.ExpiresAtUtc,
                draft.Grants, draft.InstancePermissions, [], CredentialId);
            var request = new ReviewedMcpAccessRequest(draft.RequestId, "pending", "https://netratel.example.test/account/integration-credentials?tab=credentials",
                "ABCD-EFGH", proposal, Credential.Resource!, "prod", "Production", "synthetic-owner", CredentialId, 1, ProposalHash);
            return new(request, CredentialId, ExactExpiry, "active", Credential.Grants.Select(grant => new IntegrationPairingGrant(grant.TenantId, grant.Permission)).ToArray(),
                Credential.InstancePermissions, [], ["Matching operation policies are a separate prerequisite."], "Review and apply the exact access changes.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && LoadFailureSuffix is { } suffix && path.EndsWith(suffix, StringComparison.Ordinal))
            {
                var code = LoadFailureStatus switch { HttpStatusCode.BadRequest => "review_resource_mismatch", HttpStatusCode.ServiceUnavailable => "review_service_unavailable", _ => "upstream-secret-marker" };
                var response = Json(new { code, stage = "list", correlationId = "synthetic-trace-429", remediation = "upstream-secret-marker" }, LoadFailureStatus);
                response.Headers.Add("X-Correlation-ID", "synthetic-trace-429");
                return response;
            }
            if (path.EndsWith("/authority", StringComparison.Ordinal))
                return Json(new
                {
                    tenants = new[] {
                        new { tenantId = 17, name = "Customer A", permissions = new[] { Permission("flow.read", "Read flows"), Permission("flow.edit", "Edit flows") } },
                        new { tenantId = 18, name = "Customer B", permissions = new[] { Permission("flow.read", "Read flows"), Permission("flow.edit", "Edit flows") } },
                        new { tenantId = 19, name = "Customer C", permissions = new[] { Permission("telemetry.read", "Read telemetry") } }
                    },
                    instancePermissions = new[] { Permission("mcp.discovery.read", "Discover MCP capabilities"), Permission("telemetry.read", "Read telemetry") }
                });
            if (path == "/api/v2/account/integration-credentials/") return Json(new[] { Credential });
            if (path == Route && request.Method == HttpMethod.Get)
                return Json(Receipt is null ? Array.Empty<ReviewedMcpAccessRequest>() : [Receipt.Request]);
            if (path == Route && request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                SubmittedFields.Add(document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
                var draft = document.RootElement.Deserialize<Draft>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Submitted.Add(draft);
                if (ReviewStatus != HttpStatusCode.OK) return Json(new { code = "synthetic-validation-failure" }, ReviewStatus);
                if (Receipt?.Request.RequestId != draft.RequestId) Receipt = CreateReceipt(draft);
                if (InterruptReviewResponse) { InterruptReviewResponse = false; throw new HttpRequestException("Synthetic interrupted review response."); }
                return Json(Receipt);
            }
            if (path.StartsWith(Route + "/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                if (InspectStatus != HttpStatusCode.OK) return Json(new { code = "synthetic-inspection-failure" }, InspectStatus);
                if (ConsumeOnInspect) ConsumeReceipt();
                return Json(Receipt);
            }
            if (path.EndsWith("/apply", StringComparison.Ordinal))
            {
                ApplyCalls++;
                using var decision = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                ApplyDecisions.Add((decision.RootElement.GetProperty("proposalHash").GetString()!, decision.RootElement.GetProperty("confirm").GetBoolean()));
                if (ApplyStatus != HttpStatusCode.OK) return Json(new { code = "synthetic-save-failure" }, ApplyStatus);
                ConsumeReceipt();
                return Json(Receipt);
            }
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                CancelCalls++;
                Receipt = Receipt! with { Request = Receipt!.Request with { Status = "cancelled" } };
                return Json(Receipt);
            }
            throw new InvalidOperationException($"Unexpected editor request: {request.Method} {path}");
        }

        public void ConsumeReceipt() => Receipt = Receipt! with
        {
            Request = Receipt!.Request with { Status = "consumed" }, ConfiguredGrants = Receipt.Request.Proposal.Grants,
            ConfiguredInstancePermissions = Receipt.Request.Proposal.InstancePermissions
        };

        private static IntegrationPermission Permission(string id, string label) => new(id, label, label);
        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }
}
