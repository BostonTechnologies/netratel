using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Web.Components.Pages.Jobs;
using NetRatel.Web.Services.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RunJobBooleanInteractionTests : AsyncBunitContext
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Boolean_defaults_and_toggles_reach_the_real_run_request(bool initial, bool toggle)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new JobTransport(initial);
        Services.AddSingleton<IHttpClientFactory>(transport);
        Services.AddScoped<IJobApiClient, JobApiClient>();
        var provider = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<RunJobDialog>("Run",
            new DialogParameters<RunJobDialog>
            {
                { dialog => dialog.Job, new JobDto(42, "Boolean workflow", "/", null, 1, "client", 0, 0) }
            });
        provider.WaitForElement("input[type=checkbox]");
        ((IHtmlInputElement)provider.Find("input[type=checkbox]")).IsChecked.Should().Be(initial);
        if (toggle)
            provider.Find("input[type=checkbox]").Change(!initial);
        provider.FindAll("button").Single(button => button.TextContent.Contains("Start Run")).Click();
        provider.WaitForAssertion(() => transport.Request.Should().NotBeNull());
        using var inputs = JsonDocument.Parse(transport.Request!.InputsJson!);
        inputs.RootElement.GetProperty("Enabled").GetBoolean().Should().Be(toggle ? !initial : initial);
        transport.RunPath.Should().Be("/api/v1/jobruns/start/42");
    }

    private sealed class JobTransport(bool initial) : HttpMessageHandler, IHttpClientFactory
    {
        public RunJobRequest? Request { get; private set; }
        public string? RunPath { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/jobs/42/params")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] {
                    new JobParamDto(1, 42, "Enabled", "bool", true, initial ? "true" : "false", null, null) }) };
            if (request.Method == HttpMethod.Post)
            {
                RunPath = request.RequestUri!.AbsolutePath;
                Request = await request.Content!.ReadFromJsonAsync<RunJobRequest>(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                    new JobRunDto(9, 42, "Boolean workflow", 1, "client", "ui", JobRunStatusDto.Pending, 0, 0, null, null, null, Request!.InputsJson)) };
            }
            throw new InvalidOperationException($"Unexpected API request: {request.Method} {request.RequestUri}");
        }
    }
}
