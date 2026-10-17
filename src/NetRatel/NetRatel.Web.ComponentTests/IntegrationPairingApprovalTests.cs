using System.Net;
using System.Net.Http.Json;
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
        await panel.InvokeAsync(() => panel.Instance.GetType().GetMethod("ExpiryChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel.Instance, [NetRatel.Web.Services.IntegrationCredentialExpiry.Format(DateTimeOffset.UtcNow.AddDays(20))]));
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
        public bool DurableAuthorityRejection { get; set; }
        public IntegrationPairingApprovalRequest? Approved { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://api.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v2/access/self") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { isInstanceAdministrator = Administrator }) };
            if (path.EndsWith("/lookup", StringComparison.Ordinal) && LookupStatus != HttpStatusCode.OK) return new(LookupStatus);
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
                "https://netratel.example.test", null, ["telemetry.read"], DateTimeOffset.UtcNow.AddMinutes(10), "pending") { TemporaryVerifier = TemporaryVerifier, CredentialExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15) }) };
            if (path.EndsWith("/authority", StringComparison.Ordinal)) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { tenants = new[] { new { tenantId = 7, name = "Tenant A", permissions = new[] {
                new { id = "telemetry.read", label = "Read telemetry", description = "Read tenant telemetry." } } } }, instancePermissions = Array.Empty<object>() }) };
            if (path.EndsWith("/approve", StringComparison.Ordinal))
            {
                ApprovalRequests++;
                Approved = await request.Content!.ReadFromJsonAsync<IntegrationPairingApprovalRequest>(cancellationToken);
                if (RejectApproval && DurableAuthorityRejection) return new(HttpStatusCode.BadRequest)
                { Content = JsonContent.Create(new { errors = new Dictionary<string, string[]> { ["durableAuthority"] = [IntegrationPairingProtocol.DurableAuthorityGuidance] } }) };
                return new(RejectApproval ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "approved" }) };
            }
            throw new InvalidOperationException("Unexpected pairing test request.");
        }
    }
}
