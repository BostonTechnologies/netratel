using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Web.Components.Pages.Jobs;
using NetRatel.Web.Services.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

// Rendered DOM/payload regressions. Native saved-credential autofill is separate browser acceptance.
public sealed class JobOperationalInputTests : AsyncBunitContext
{
    private readonly Transport _transport = new();
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;

    public JobOperationalInputTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddScoped<IJobApiClient, JobApiClient>();
        _dialogs = Render<MudDialogProvider>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_inputs_declare_operational_autocomplete_and_submit_original_keys_and_values(bool secretFirst)
    {
        if (secretFirst) _transport.Parameters.Reverse(0, 2);
        await OpenRunAsync();
        AssertOperationalForm(".run-job-dialog");
        var ordinary = Field("additional_text");
        ordinary.GetAttribute("value").Should().Be("intended default");
        var secret = Field("dummy_secret");
        secret.GetAttribute("type").Should().Be("password");
        secret.GetAttribute("value").Should().BeNullOrEmpty();
        Field("Settings").GetAttribute("autocomplete").Should().Be("off");
        Button("Cancel").Click();

        // A fresh dialog keeps configured defaults; its field names do not become payload keys.
        await OpenRunAsync();
        AssertOperationalForm(".run-job-dialog");
        Field("additional_text").GetAttribute("value").Should().Be("intended default");
        Field("dummy_secret").GetAttribute("value").Should().BeNullOrEmpty();
        Field("additional_text").Change("operator text");
        Field("dummy_secret").Change("operator secret");
        Button("Start Run").Click();
        _dialogs.WaitForAssertion(() => _transport.Run.Should().NotBeNull());
        using var payload = JsonDocument.Parse(_transport.Run!.InputsJson!);
        payload.RootElement.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(["additional_text", "dummy_secret", "Count", "Enabled", "Settings"]);
        payload.RootElement.GetProperty("additional_text").GetString().Should().Be("operator text");
        payload.RootElement.GetProperty("dummy_secret").GetString().Should().Be("operator secret");
        payload.RootElement.GetProperty("Count").GetString().Should().Be("0");
        payload.RootElement.GetProperty("Enabled").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("Settings").GetString().Should().Be("{\"enabled\":false}");
        _transport.Run.StartedBy.Should().Be("ui");
        _transport.RunCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("secret", "operator secret")]
    [InlineData("string", "operator text")]
    [InlineData("json", "{\"enabled\":false}")]
    public async Task Parameter_editor_has_operational_dom_semantics_and_preserves_default_and_parameter_key(string type, string value)
    {
        await Services.GetRequiredService<IDialogService>().ShowAsync<ParamEditDialog>("Edit parameter",
            new DialogParameters { ["JobId"] = 42UL, ["ExistingParam"] = new JobParamDto(7, 42, "dummy_secret", type, false, value, "Description", null) });
        _dialogs.WaitForElement(".param-editor");
        AssertOperationalForm(".param-editor");
        var input = Field("Default value");
        input.GetAttribute("autocomplete").Should().Be(type == "secret" ? "new-password" : "off");
        if (type == "secret") input.GetAttribute("type").Should().Be("password");
        Button("Save").Click();
        _dialogs.WaitForAssertion(() => _transport.Update.Should().NotBeNull());
        _transport.Update!.Name.Should().Be("dummy_secret");
        _transport.Update.Default.Should().Be(value);
    }

    private async Task OpenRunAsync()
    {
        await Services.GetRequiredService<IDialogService>().ShowAsync<RunJobDialog>("Run",
            new DialogParameters { ["Job"] = new JobDto(42, "Operational workflow", "/", null, 1, "client", 0, 0) });
        _dialogs.WaitForElement(".run-job-dialog input[type=password]");
    }

    private void AssertOperationalForm(string selector)
    {
        var form = _dialogs.Find(selector);
        form.TagName.Should().Be("FORM");
        form.GetAttribute("autocomplete").Should().Be("off");
        var inputs = form.QuerySelectorAll("input:not([type=checkbox]):not([type=hidden]), textarea");
        inputs.Should().NotBeEmpty();
        foreach (var input in inputs)
        {
            input.GetAttribute("autocomplete").Should().Be(input.GetAttribute("type") == "password" ? "new-password" : "off");
            input.GetAttribute("name").Should().StartWith("job-operation-");
            input.Id.Should().Be(input.GetAttribute("name"));
            form.QuerySelector($"label[for='{input.Id}']").Should().NotBeNull("labels remain associated with real inputs");
        }
        inputs.Select(input => input.Id).Should().OnlyHaveUniqueItems();
    }

    private IElement Field(string label) => _dialogs.FindComponents<MudTextField<string>>()
        .Single(field => field.Instance.Label == label).Find("input, textarea");
    private IElement Button(string name) => _dialogs.FindAll("button").Single(button => button.TextContent == name);

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public List<JobParamDto> Parameters { get; } = [
            new(1, 42, "additional_text", "string", true, "intended default", null, null),
            new(2, 42, "dummy_secret", "secret", false, null, null, null),
            new(3, 42, "Count", "int", true, "0", null, null),
            new(4, 42, "Enabled", "bool", true, "false", null, null),
            new(5, 42, "Settings", "json", true, "{\"enabled\":false}", null, null)];
        public RunJobRequest? Run { get; private set; }
        public UpdateJobParamRequest? Update { get; private set; }
        public int RunCalls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://jobs.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Ok(Parameters);
            if (request.Method == HttpMethod.Put)
            {
                Update = await request.Content!.ReadFromJsonAsync<UpdateJobParamRequest>(cancellationToken);
                return Ok(new JobParamDto(7, 42, Update!.Name!, Update.Type!, Update.Required ?? false, Update.Default, Update.Description, Update.OptionsJson));
            }
            RunCalls++;
            Run = await request.Content!.ReadFromJsonAsync<RunJobRequest>(cancellationToken);
            return Ok(new JobRunDto(9, 42, "Operational workflow", 1, "client", Run!.StartedBy, JobRunStatusDto.Pending, 0, 0, null, null, null, Run.InputsJson));
        }
        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
