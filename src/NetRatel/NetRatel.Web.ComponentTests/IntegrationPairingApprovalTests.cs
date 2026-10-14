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
        panel.Find(".mud-expand-panel-header").Click();
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
        panel.Find(".mud-expand-panel-header").Click();
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
        public bool RejectApproval { get; set; }
        public bool DurableAuthorityRejection { get; set; }
        public IntegrationPairingApprovalRequest? Approved { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://api.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lookup", StringComparison.Ordinal)) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new IntegrationPairingReview("request", "ABCD-EFGH", "CLI profile", PairingCredentialPurpose.Api,
                "https://netratel.example.test", null, ["telemetry.read"], DateTimeOffset.UtcNow.AddMinutes(10), "pending")) };
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
