using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Components.Shared.Integrations;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class IntegrationPairingApprovalTests : AsyncBunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reviewed_access_explains_principal_eligibility_and_preserves_exact_independent_consent(bool eligible)
    {
        using var transport = Configure();
        transport.ReviewedAccess = true;
        transport.ReviewEligible = eligible;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-eligibility']").TextContent.Should().Contain(eligible ? "eligible to review" : "cannot approve"));
        panel.Find("[data-testid='pairing-approve']").HasAttribute("disabled").Should().BeTrue();
        transport.ApprovalRequests.Should().Be(0);
        panel.FindAll("[data-testid='permission-selector']").Should().BeEmpty();
        if (!eligible)
        {
            panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain("different authorized operator account");
            panel.Find("[data-testid='pairing-failure-reference']").TextContent.Should().Contain("review_independent_approver_required").And.Contain("synthetic-review-reference");
            panel.Find("[data-testid='pairing-confirm']").HasAttribute("disabled").Should().BeTrue();
            return;
        }
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").Click();
        panel.WaitForAssertion(() => transport.Approved.Should().NotBeNull());
        transport.Approved!.ReviewedProposalHash.Should().Be(new string('A', 64));
        transport.Approved.ExpiresAtUtc.Should().Be(transport.ReviewedProposal.ExpiresAtUtc);
        transport.Approved.Grants.Should().Equal(transport.ReviewedProposal.Grants);
        transport.Approved.InstancePermissions.Should().Equal(transport.ReviewedProposal.InstancePermissions);
        transport.ApprovalRequests.Should().Be(1);
    }

    [Fact]
    public void Structured_review_failure_shows_safe_action_and_correlation_and_requires_fresh_lookup()
    {
        using var transport = Configure();
        transport.ReviewedAccess = true;
        transport.ReviewFailureCode = "review_policy_changed";
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH"); panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-eligibility']").TextContent.Should().Contain("eligible to review"));
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").Click();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain("policy changed"));
        panel.Find("[data-testid='pairing-failure-reference']").TextContent.Should().Contain("review_policy_changed").And.Contain("synthetic-review-reference");
        panel.Find("[data-testid='pairing-confirm-code']").TextContent.Should().Be("ABCD-EFGH");
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("checked").Should().BeFalse();
        panel.Find("[data-testid='pairing-approve']").HasAttribute("disabled").Should().BeTrue();
        panel.Markup.Should().NotContain("synthetic-untrusted-upstream-body");
        transport.ApprovalRequests.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in")]
    [InlineData(HttpStatusCode.Forbidden, "current account cannot open")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limited")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "service could not complete")]
    public void Lookup_authentication_permission_rate_and_service_failures_do_not_claim_expiry(HttpStatusCode status, string message)
    {
        using var transport = Configure();
        transport.LookupStatus = status;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH"); panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain(message).And.NotContain("unavailable or expired"));
        panel.Find("input").GetAttribute("value").Should().Be("ABCD-EFGH");
        transport.ApprovalRequests.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HTML_and_oversized_streamed_lookup_failures_preserve_status_guidance_with_bounded_reads(bool oversized)
    {
        using var transport = Configure();
        transport.LookupStatus = HttpStatusCode.ServiceUnavailable;
        transport.LookupBody = oversized ? new string('x', 64 * 1024) : "<html>Synthetic upstream outage body</html>";
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH"); panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain("service could not complete lookup"));
        panel.Markup.Should().NotContain("Synthetic upstream outage body").And.NotContain("unavailable or expired");
        transport.LookupStream!.BytesRead.Should().BeLessThanOrEqualTo(16 * 1024 + 1);
        if (oversized) transport.LookupStream.BytesRead.Should().Be(16 * 1024 + 1);
        panel.Find("input").GetAttribute("value").Should().Be("ABCD-EFGH");
        transport.ApprovalRequests.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "service could not complete lookup")]
    [InlineData(HttpStatusCode.OK, "Your code is preserved")]
    public async Task Headers_without_a_completing_lookup_body_release_the_form_and_preserve_the_code(HttpStatusCode status, string guidance)
    {
        using var transport = Configure();
        transport.LookupStatus = status;
        transport.StallLookupBody = true;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        var submission = panel.Find("form").SubmitAsync(EventArgs.Empty);
        await transport.StalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await submission.WaitAsync(TimeSpan.FromSeconds(8));
        panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain(guidance).And.NotContain("unavailable or expired");
        panel.Find("input").GetAttribute("value").Should().Be("ABCD-EFGH");
        panel.Find("[data-testid='pairing-lookup']").HasAttribute("disabled").Should().BeFalse();
        transport.StalledBody.CancellationObserved.Should().BeTrue();
        transport.ApprovalRequests.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "service could not complete lookup")]
    [InlineData(HttpStatusCode.OK, "Your code is preserved")]
    public async Task Reset_lookup_body_preserves_safe_status_guidance_and_the_code(HttpStatusCode status, string guidance)
    {
        using var transport = Configure();
        transport.LookupStatus = status;
        transport.ResetLookupBody = true;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        await panel.Find("form").SubmitAsync(EventArgs.Empty);
        panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain(guidance).And.NotContain("unavailable or expired");
        panel.Markup.Should().NotContain("synthetic-private-stream-diagnostic");
        panel.Find("input").GetAttribute("value").Should().Be("ABCD-EFGH");
        panel.Find("[data-testid='pairing-lookup']").HasAttribute("disabled").Should().BeFalse();
        transport.ApprovalRequests.Should().Be(0);
    }

    [Fact]
    public async Task Stalled_decision_error_preserves_uncertain_outcome_and_requires_confirmation_again()
    {
        using var transport = Configure();
        transport.ReviewedAccess = true;
        transport.StallApprovalBody = true;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        await panel.Find("form").SubmitAsync(EventArgs.Empty);
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        var decision = panel.Find("[data-testid='pairing-approve']").ClickAsync(new());
        await transport.StalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await decision.WaitAsync(TimeSpan.FromSeconds(8));
        panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain("could not confirm your decision").And.Contain("inspect the client receipt");
        panel.Find("[data-testid='pairing-confirm-code']").TextContent.Should().Be("ABCD-EFGH");
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("checked").Should().BeFalse();
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("disabled").Should().BeFalse();
        panel.Find("[data-testid='pairing-approve']").HasAttribute("disabled").Should().BeTrue();
        transport.StalledBody.CancellationObserved.Should().BeTrue();
        transport.ApprovalRequests.Should().Be(1);
    }

    [Fact]
    public async Task Disposing_the_component_cancels_a_stalled_body_without_waiting_for_the_read_timeout()
    {
        using var transport = Configure();
        transport.LookupStatus = HttpStatusCode.ServiceUnavailable;
        transport.StallLookupBody = true;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        var submission = panel.Find("form").SubmitAsync(EventArgs.Empty);
        await transport.StalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await DisposeComponentsAsync();
        await submission.WaitAsync(TimeSpan.FromSeconds(2));
        transport.StalledBody.CancellationObserved.Should().BeTrue();
        transport.ApprovalRequests.Should().Be(0);
    }

    [Fact]
    public void Host_approval_reviews_exact_generation_and_keeps_personal_credential_approval_separate()
    {
        using var transport = Configure();
        var panel = Render<HttpHostSetupApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='host-setup-fingerprint']").TextContent.Should().Be(new string('A', 64)));
        panel.Markup.Should().Contain("netratel-mcp-http").And.Contain("https://netratel.example.test/mcp");
        panel.Find("[data-testid='host-setup-approve']").HasAttribute("disabled").Should().BeTrue();
        transport.ApprovalRequests.Should().Be(0);
        transport.HostApprovalRequests.Should().Be(0);
        panel.Find("[data-testid='host-setup-confirm']").Change(true);
        panel.Find("[data-testid='host-setup-approve']").Click();
        panel.WaitForAssertion(() => panel.Find("[data-testid='host-setup-complete']").TextContent.Should().Contain("apply the deployment"));
        transport.HostApprovalRequests.Should().Be(1);
        transport.ApprovalRequests.Should().Be(0);
        panel.Markup.Should().NotContain("nrt_ic_");
    }

    [Fact]
    public void Code_lookup_reviews_connection_with_empty_grants_and_requires_deliberate_confirmation()
    {
        using var transport = Configure();
        var panel = Render<IntegrationPairingApprovalPanel>();
        transport.ApprovalRequests.Should().Be(0);
        panel.Find("input").Change("ABCD-EFGH");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Markup.Should().Contain("Confirm code").And.Contain("CLI profile").And.Contain("https://netratel.example.test"));
        panel.FindAll("input[type='checkbox']").Should().OnlyContain(input => !input.HasAttribute("checked"));
        panel.Find("[data-testid='pairing-approve']").HasAttribute("disabled").Should().BeTrue();
        transport.ApprovalRequests.Should().Be(0);
        panel.Find("[data-testid='pairing-grant-7-telemetry.read']").Change(true);
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").HasAttribute("disabled").Should().BeFalse();
        panel.Find("[data-testid='pairing-approve']").Click();
        transport.ApprovalRequests.Should().Be(1);
        transport.Approved!.UserCode.Should().Be("ABCD-EFGH");
        transport.Approved.Grants.Should().Equal(new IntegrationPairingGrant(7, "telemetry.read"));
        transport.Approved.InstancePermissions.Should().BeEmpty();
        panel.WaitForAssertion(() => panel.Markup.Should().Contain("Return to your client"));
        panel.Markup.Should().NotContain("nrt_ic_");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejected_approval_preserves_code_and_grants_but_requires_confirmation_again(bool durableAuthority)
    {
        using var transport = Configure();
        transport.RejectApproval = true;
        transport.DurableAuthorityRejection = durableAuthority;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-confirm-code']").TextContent.Should().Be("ABCD-EFGH"));
        panel.Find("[data-testid='pairing-grant-7-telemetry.read']").Change(true);
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").Click();
        panel.Find("[data-testid='pairing-confirm-code']").TextContent.Should().Be("ABCD-EFGH");
        panel.Find("[data-testid='pairing-grant-7-telemetry.read']").HasAttribute("checked").Should().BeTrue();
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("checked").Should().BeFalse();
        panel.WaitForAssertion(() => panel.Markup.Should().Contain(durableAuthority ? "current NetRatel role assignments" : "your access is no longer available"));
        transport.RejectApproval = false;
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").Click();
        panel.WaitForAssertion(() => panel.Markup.Should().Contain("Connection approved"));
    }

    [Fact]
    public void Host_deep_link_seeds_code_without_lookup_and_background_parameters_preserve_edits()
    {
        using var transport = Configure();
        var panel = Render<HttpHostSetupApprovalPanel>(parameters => parameters.Add(component => component.InitialCode, "ABCD-EFGH"));
        panel.WaitForAssertion(() => panel.Find("input").GetAttribute("value").Should().Be("ABCD-EFGH"));
        panel.FindAll("[role='dialog']").Should().BeEmpty();
        panel.Find("input").Change("IJKL-MNOP");
        panel.Render(parameters => parameters.Add(component => component.InitialCode, "ABCD-EFGH"));
        panel.Find("input").GetAttribute("value").Should().Be("IJKL-MNOP");
        transport.HostApprovalRequests.Should().Be(0);
    }

    [Fact]
    public void Host_entry_requires_instance_administrator_before_showing_working_form()
    {
        using var transport = Configure();
        transport.Administrator = false;
        var panel = Render<HttpHostSetupApprovalPanel>();
        panel.WaitForAssertion(() => panel.Find("[data-testid='host-setup-authority']").TextContent.Should().Contain("instance administrator"));
        panel.FindAll("form").Should().BeEmpty();
        panel.Markup.Should().Contain("Approve HTTP MCP host").And.Contain("Connect the HTTP MCP service");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Gone)]
    public void Unavailable_wrong_kind_or_expired_code_preserves_entry_without_switching_approval_kind(HttpStatusCode status)
    {
        using var transport = Configure();
        transport.LookupStatus = status;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-error']").TextContent.Should().Contain("unavailable or expired"));
        panel.Find("input").GetAttribute("value").Should().Be("ABCD-EFGH");
        panel.FindAll("[role='dialog']").Should().BeEmpty();
        transport.ApprovalRequests.Should().Be(0);
    }

    [Fact]
    public void Temporary_verifier_shows_only_one_explicit_read_permission_and_zero_tenant_access()
    {
        using var transport = Configure();
        transport.TemporaryVerifier = true;
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[role='dialog']").TextContent.Should().Contain("Temporary verification client"));
        panel.FindAll("[data-testid='permission-selector']").Should().BeEmpty();
        panel.Markup.Should().Contain("No tenant or customer access").And.Contain("mcp.discovery.read");
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").HasAttribute("disabled").Should().BeTrue();
        panel.Find("[data-testid='pairing-verifier-consent']").Change(true);
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("checked").Should().BeFalse();
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-approve']").Click();
        panel.WaitForAssertion(() => transport.Approved.Should().NotBeNull());
        transport.Approved!.VerifierConsent.Should().BeTrue();
        transport.Approved.Grants.Should().BeEmpty();
        transport.Approved.InstancePermissions.Should().Equal("mcp.discovery.read");
        transport.Approved.ExpiresAtUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task Changing_selected_access_or_expiry_invalidates_recognition_and_deny_issues_no_credential()
    {
        using var transport = Configure();
        var panel = Render<IntegrationPairingApprovalPanel>();
        panel.Find("input").Change("ABCD-EFGH"); panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find("[data-testid='pairing-confirm-code']").TextContent.Should().Be("ABCD-EFGH"));
        panel.Find("[data-testid='pairing-grant-7-telemetry.read']").Change(true);
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        await panel.InvokeAsync(() => panel.Instance.GetType().GetMethod("ExpiryChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel.Instance, [NetRatel.Web.Services.IntegrationCredentialExpiry.Format(NetRatel.Web.Services.IntegrationCredentialExpiry.Reference(DateTimeOffset.UtcNow).AddDays(20))]));
        panel.Render();
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("checked").Should().BeFalse();
        panel.Find("[data-testid='pairing-confirm']").Change(true);
        panel.Find("[data-testid='pairing-grant-7-telemetry.read']").Change(false);
        panel.Find("[data-testid='pairing-confirm']").HasAttribute("checked").Should().BeFalse();
        panel.Find("[data-testid='pairing-deny']").Click();
        panel.WaitForAssertion(() => panel.Markup.Should().Contain("Connection denied"));
        transport.ApprovalRequests.Should().Be(0);
        panel.FindAll("[role='dialog']").Should().BeEmpty();
    }

    private Transport Configure()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var transport = new Transport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        return transport;
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public int ApprovalRequests { get; private set; }
        public int HostApprovalRequests { get; private set; }
        public bool RejectApproval { get; set; }
        public bool TemporaryVerifier { get; set; }
        public bool Administrator { get; set; } = true;
        public HttpStatusCode LookupStatus { get; set; } = HttpStatusCode.OK;
        public string? LookupBody { get; set; }
        public TrackedResponseStream? LookupStream { get; private set; }
        public bool StallLookupBody { get; set; }
        public bool ResetLookupBody { get; set; }
        public bool StallApprovalBody { get; set; }
        public StalledResponseStream StalledBody { get; } = new();
        public bool DurableAuthorityRejection { get; set; }
        public bool ReviewedAccess { get; set; }
        public bool ReviewEligible { get; set; } = true;
        public string? ReviewFailureCode { get; set; }
        public ReviewedMcpAccessProposal ReviewedProposal { get; } = new("b2222222222242228222222222222222", "Synthetic reviewed client", "reconcile",
            DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddDays(30).AddTicks(1234560),
            [new(17, "flow.read"), new(29, "flow.read")], ["mcp.discovery.read"],
            [new(17, "netratel_flows", "list"), new(29, "netratel_flows", "list")], "c3333333333343338333333333333333");
        public IntegrationPairingApprovalRequest? Approved { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://api.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v2/access/self") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { isInstanceAdministrator = Administrator }) };
            if (path.EndsWith("/lookup", StringComparison.Ordinal) && ResetLookupBody)
                return new(LookupStatus) { Content = new StreamContent(new ResetResponseStream()) };
            if (path.EndsWith("/lookup", StringComparison.Ordinal) && StallLookupBody)
                return new(LookupStatus) { Content = new StreamContent(StalledBody) };
            if (path.EndsWith("/lookup", StringComparison.Ordinal) && LookupStatus != HttpStatusCode.OK)
            {
                if (LookupBody is null) return new(LookupStatus);
                LookupStream = new(Encoding.UTF8.GetBytes(LookupBody));
                return new(LookupStatus) { Content = new StreamContent(LookupStream) };
            }
            if (path.EndsWith("/deny", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "denied" }) };
            if (path.StartsWith(HttpHostSetupProtocol.AccountRoute, StringComparison.Ordinal))
            {
                if (path.EndsWith("/lookup", StringComparison.Ordinal)) return new(HttpStatusCode.OK)
                { Content = JsonContent.Create(new HttpHostSetupReview("host", "ABCD-EFGH", "https://netratel.example.test", "https://netratel.example.test/mcp",
                    "netratel-mcp-http", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(10), "pending")) };
                if (path.EndsWith("/approve", StringComparison.Ordinal))
                {
                    HostApprovalRequests++;
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HttpHostSetupPollResponse("approved", ApprovalId: "host", RegistrationFingerprint: new string('A', 64))) };
                }
            }
            if (path.EndsWith("/lookup", StringComparison.Ordinal)) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new IntegrationPairingReview("request", "ABCD-EFGH", "CLI profile", PairingCredentialPurpose.Api,
                "https://netratel.example.test", null, ["telemetry.read"], DateTimeOffset.UtcNow.AddMinutes(10), "pending")
                {
                    TemporaryVerifier = TemporaryVerifier, CredentialExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
                    ReviewedAccess = ReviewedAccess ? new(ReviewedProposal.RequestId, "pending", "https://netratel.example.test/account/integration-credentials", "ABCD-EFGH", ReviewedProposal,
                        "https://netratel.example.test/mcp", "dev", "Development", "synthetic-requester", ReviewedProposal.CredentialId!, 1, new string('A', 64)) : null,
                    Eligibility = ReviewedAccess ? new(ReviewEligible, ReviewEligible ? null : IntegrationPairingFailure.ForCode("review_independent_approver_required", "lookup") with { CorrelationId = "synthetic-review-reference" }) : null
                }) };
            if (path.EndsWith("/authority", StringComparison.Ordinal)) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { tenants = new[] { new { tenantId = 7, name = "Tenant A", permissions = new[] {
                new { id = "telemetry.read", label = "Read telemetry", description = "Read tenant telemetry." } } } }, instancePermissions = Array.Empty<object>() }) };
            if (path.EndsWith("/approve", StringComparison.Ordinal))
            {
                ApprovalRequests++;
                Approved = await request.Content!.ReadFromJsonAsync<IntegrationPairingApprovalRequest>(cancellationToken);
                if (StallApprovalBody) return new(HttpStatusCode.ServiceUnavailable) { Content = new StreamContent(StalledBody) };
                if (ReviewFailureCode is { } reviewCode) return new(HttpStatusCode.Forbidden)
                { Content = JsonContent.Create(IntegrationPairingFailure.ForCode(reviewCode, "approval") with { CorrelationId = "synthetic-review-reference", Remediation = "synthetic-untrusted-upstream-body" }) };
                if (RejectApproval && DurableAuthorityRejection) return new(HttpStatusCode.BadRequest)
                { Content = JsonContent.Create(new { errors = new Dictionary<string, string[]> { ["durableAuthority"] = [IntegrationPairingProtocol.DurableAuthorityGuidance] } }) };
                return new(RejectApproval ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "approved" }) };
            }
            throw new InvalidOperationException("Unexpected pairing test request.");
        }
    }
    private sealed class TrackedResponseStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
    private sealed class StalledResponseStream : MemoryStream
    {
        private readonly TaskCompletionSource<int> _uncompletedBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try { return await _uncompletedBody.Task.WaitAsync(cancellationToken); }
            finally { CancellationObserved = cancellationToken.IsCancellationRequested; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    private sealed class ResetResponseStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("synthetic-private-stream-diagnostic"));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
