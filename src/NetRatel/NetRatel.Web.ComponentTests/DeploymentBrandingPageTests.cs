using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Web.Components.Pages.Settings;
using NetRatel.Web.Services.Branding;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class DeploymentBrandingPageTests : AsyncBunitContext
{
    private readonly BrandingApi _api = new();

    public DeploymentBrandingPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IDeploymentBrandingApiService>(_api);
    }

    [Fact]
    public void Gateway_can_be_discarded_saved_and_loaded_again()
    {
        var cut = Render<DeploymentBranding>();
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").Input("https://gateway.example.test");
        cut.Find("[data-testid=branding-discard]").Click();
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").GetAttribute("value").Should().Be("https://old.example.test");
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").Input("https://gateway.example.test");
        cut.Find("[data-testid=branding-save]").Click();
        _api.Updates.Single().Should().Equal(new BrandingFieldUpdate("gatewayUrl", "https://gateway.example.test", false));
        var reloaded = Render<DeploymentBranding>();
        reloaded.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").GetAttribute("value").Should().Be("https://gateway.example.test");
    }

    [Fact]
    public void Deployment_gateway_is_locked_and_omitted_from_reset()
    {
        _api.Value = _api.Value with { GatewayUrl = new("https://deployment.example.test", BrandingValueSource.Deployment, true) };
        var cut = Render<DeploymentBranding>();
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Managed by deployment configuration");
        cut.Find("[data-testid=branding-reset]").Click();
        _api.Updates.Single().Should().NotContain(field => field.Field == "gatewayUrl");
    }

    [Fact]
    public void Failed_save_retains_the_gateway_draft_for_correction()
    {
        _api.FailUpdate = true;
        var cut = Render<DeploymentBranding>();
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").Input("http://api:9223");
        cut.Find("[data-testid=branding-save]").Click();
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Gateway URL").Find("input").GetAttribute("value").Should().Be("http://api:9223");
        cut.Find("[data-testid=branding-save]").HasAttribute("disabled").Should().BeFalse();
        _api.Value.GatewayUrl!.Value.Should().Be("https://old.example.test");
    }

    private sealed class BrandingApi : IDeploymentBrandingApiService
    {
        private static BrandingField Field(string value) => new(value, BrandingValueSource.Administrator, false);
        public EffectiveDeploymentBranding Value { get; set; } = new(
            Field("NetRatel"), Field("Example"), Field("Automation"), Field("/logo.png"), Field("/logo.png"),
            Field("/logo.png"), Field("/favicon.ico"), Field(""), Field("https://web.example.test"), 1,
            Field("https://old.example.test"));
        public List<IReadOnlyList<BrandingFieldUpdate>> Updates { get; } = [];
        public bool FailUpdate { get; set; }
        public Task<EffectiveDeploymentBranding> GetPublicAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task<EffectiveDeploymentBranding> UpdateAsync(IReadOnlyList<BrandingFieldUpdate> fields, CancellationToken cancellationToken = default)
        {
            Updates.Add(fields);
            if (FailUpdate) throw new HttpRequestException("Public HTTPS origin required.");
            foreach (var field in fields.Where(field => field.Field == "gatewayUrl"))
                Value = Value with { GatewayUrl = Field(field.Reset ? "" : field.Value!), Version = Value.Version + 1 };
            return Task.FromResult(Value);
        }
        public Task<BrandingAssetUploadResult> UploadAsync(string slot, IBrowserFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
