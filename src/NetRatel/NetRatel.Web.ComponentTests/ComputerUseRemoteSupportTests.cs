using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.ComputerUse;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ComputerUseRemoteSupportTests : AsyncBunitContext
{
    private readonly Api _api = new();

    public ComputerUseRemoteSupportTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<GatewayRemoteSupportApiService>();
    }

    [Fact]
    public async Task ViewPermissionShowsAutomationWithoutOfferingControl()
    {
        _api.CanControl = false;
        var cut = await OpenAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Automation active", cut.Find("[data-testid='computer-use-controls']").TextContent);
            Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Take control", StringComparison.Ordinal));
            Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Pause automation", StringComparison.Ordinal));
        });
        Assert.Equal(0, _api.Mutations);
    }

    [Fact]
    public async Task UnconfirmedTerminationDoesNotClaimHumanTakeover()
    {
        _api.ResultStatus = ComputerUseStatuses.Unknown;
        var cut = await OpenAsync();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Take control", StringComparison.Ordinal)));
        cut.FindAll("button").Single(button => button.TextContent.Contains("Take control", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("not been confirmed", cut.Find("[data-testid='computer-use-controls']").TextContent);
            Assert.DoesNotContain("Automation terminated.", cut.Markup);
        });
        Assert.Equal(1, _api.Mutations);
        Assert.EndsWith($"/session/{_api.SessionId:D}/terminate", _api.MutationPath);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "netratelRemoteSupport.setControlEnabled");
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync()
    {
        var cut = Render<MudDialogProvider>();
        await cut.InvokeAsync(async () => await Services.GetRequiredService<IDialogService>().ShowAsync<GatewayRemoteSupportDialog>(
            "Remote Support", new DialogParameters<GatewayRemoteSupportDialog>
            {
                { component => component.TenantId, 7 },
                { component => component.AgentId, _api.AgentId },
                { component => component.HostLabel, "fixture-client" }
            }));
        return cut;
    }

    private sealed class Api : IHttpClientFactory
    {
        public Guid AgentId { get; } = Guid.NewGuid();
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool CanControl { get; set; } = true;
        public string ResultStatus { get; set; } = ComputerUseStatuses.Completed;
        public int Mutations { get; private set; }
        public string? MutationPath { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new("https://netratel.test") };

        private sealed class Handler(Api owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!request.RequestUri!.AbsolutePath.Contains("/computer-use/", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                if (request.Method == HttpMethod.Get)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new ComputerUseOperatorStatus(true, new(owner.SessionId,
                            new(3, "fixture-user", 100, "42"), ComputerUseProviders.CuaDriver, "active", true,
                            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(5), 1), owner.CanControl))
                    });
                owner.Mutations++;
                owner.MutationPath = request.RequestUri.AbsolutePath;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        result = new ComputerUseResult(ComputerUseContract.Version, Guid.NewGuid(), owner.SessionId,
                            owner.ResultStatus, Provider: ComputerUseProviders.CuaDriver)
                    })
                });
            }
        }
    }
}
