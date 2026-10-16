using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Web.Components.Pages.Jobs;
using NetRatel.Web.Services.Jobs;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class JobParameterDialogTests : AsyncBunitContext
{
    private readonly Transport _transport = new();
    private readonly IRenderedComponent<MudDialogProvider> _dialogs;
    public JobParameterDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddScoped<IJobApiClient, JobApiClient>();
        _dialogs = Render<MudDialogProvider>();
    }

    [Theory]
    [InlineData("int", "0", null)]
    [InlineData("bool", "false", null)]
    [InlineData("json", "{\"enabled\":false}", null)]
    [InlineData("select", "safe", "[\"safe\",\"preview\"]")]
    [InlineData("string", "text", "{\"retained\":true}")]
    [InlineData("secret", "fixture-secret", null)]
    public async Task Editing_each_type_preserves_default_options_and_uses_masked_secret_input(string type, string value, string? options)
    {
        var existing = new JobParamDto(4, 42, "Input", type, false, value, "Description", options);
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<ParamEditDialog>("Edit parameter",
            new DialogParameters { ["JobId"] = 42UL, ["ExistingParam"] = existing });
        _dialogs.WaitForElement(".param-editor");
        if (type == "secret") _dialogs.Find("input[type=password]").GetAttribute("value").Should().Be(value);
        Save().Click();
        (await reference.Result)!.Data.Should().BeOfType<JobParamDto>();
        _transport.Updated!.Default.Should().Be(value);
        _transport.Updated.OptionsJson.Should().Be(options);
        _transport.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_json_is_inline_and_retry_keeps_input_without_echoing_api_content()
    {
        await Services.GetRequiredService<IDialogService>().ShowAsync<ParamEditDialog>("Edit parameter", new DialogParameters
        { ["JobId"] = 42UL, ["ExistingParam"] = new JobParamDto(4, 42, "Config", "json", false, "{bad", null, null) });
        _dialogs.WaitForElement(".param-editor");
        Save().Click();
        _dialogs.Markup.Should().Contain("Enter valid JSON.");
        _transport.Calls.Should().Be(0);
        var field = _dialogs.FindComponents<MudTextField<string>>().Single(component => component.Instance.Label == "Default value");
        field.Find("textarea").Change("{\"safe\":true}");
        _transport.Fail = true;
        Save().Click();
        _dialogs.WaitForAssertion(() => _dialogs.Markup.Should().Contain("The parameter could not be saved."));
        _dialogs.Markup.Should().NotContain("sensitive-response-value");
        field.Instance.Value.Should().Be("{\"safe\":true}");
        _transport.Fail = false;
        Save().Click();
        _dialogs.WaitForAssertion(() => _transport.Calls.Should().Be(2));
    }

    [Fact]
    public async Task Switching_types_keeps_default_and_hidden_metadata_until_explicitly_edited()
    {
        await Services.GetRequiredService<IDialogService>().ShowAsync<ParamEditDialog>("Edit parameter", new DialogParameters
        { ["JobId"] = 42UL, ["ExistingParam"] = new JobParamDto(4, 42, "Count", "int", false, "0", null, "[\"zero\",\"one\"]") });
        _dialogs.WaitForElement(".param-editor");
        var selector = _dialogs.FindComponents<MudSelect<string>>().Single(component => component.Instance.Label == "Type");
        await _dialogs.InvokeAsync(() => selector.Instance.ValueChanged.InvokeAsync("string"));
        await _dialogs.InvokeAsync(() => selector.Instance.ValueChanged.InvokeAsync("int"));
        Save().Click();
        _dialogs.WaitForAssertion(() => _transport.Calls.Should().Be(1));
        _transport.Updated!.Default.Should().Be("0");
        _transport.Updated.OptionsJson.Should().Be("[\"zero\",\"one\"]");
    }

    [Fact]
    public async Task Inflight_save_blocks_repeat_and_cancel_and_returns_once()
    {
        var reference = await Services.GetRequiredService<IDialogService>().ShowAsync<ParamEditDialog>("Edit parameter", new DialogParameters
        { ["JobId"] = 42UL, ["ExistingParam"] = new JobParamDto(4, 42, "Name", "string", false, null, null, null) });
        _dialogs.WaitForElement(".param-editor");
        _transport.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = _dialogs.InvokeAsync(() => Save().ClickAsync(new MouseEventArgs()));
        await _dialogs.InvokeAsync(() => Save().ClickAsync(new MouseEventArgs()));
        _dialogs.FindAll("button").Single(button => button.TextContent == "Cancel").Click();
        _transport.Calls.Should().Be(1);
        _dialogs.FindComponents<ParamEditDialog>().Should().ContainSingle();
        _transport.Pending.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new JobParamDto(4, 42, "Name", "string", false, null, null, null)) });
        await save;
        (await reference.Result)!.Canceled.Should().BeFalse();
    }

    [Theory]
    [InlineData("string", "", false, null, null)]
    [InlineData("string", "", true, null, "A value is required.")]
    [InlineData("int", "0", true, null, null)]
    [InlineData("int", "1.5", false, null, "Enter a whole number.")]
    [InlineData("bool", "false", true, null, null)]
    [InlineData("bool", "invalid", false, null, "Choose true or false.")]
    [InlineData("json", "{bad", false, null, "Enter valid JSON.")]
    [InlineData("select", "wrong", false, "[\"safe\"]", "Choose one of the configured options.")]
    public void Input_validation_preserves_optional_empty_false_zero_and_reports_types(string type, string value, bool required, string? options, string? error) =>
        JobParameterInput.Validate(type, value, required, options).Should().Be(error);

    private AngleSharp.Dom.IElement Save() => _dialogs.FindAll("button").Single(button => button.TextContent == "Save");
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public UpdateJobParamRequest? Updated { get; private set; }
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? Pending { get; set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://jobs.invalid") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Updated = (await request.Content!.ReadFromJsonAsync<UpdateJobParamRequest>(cancellationToken))!;
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = new StringContent("sensitive-response-value") };
            if (Pending is not null) return await Pending.Task;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new JobParamDto(4, 42, Updated.Name!, Updated.Type!, Updated.Required ?? false, Updated.Default, Updated.Description, Updated.OptionsJson)) };
        }
    }
}
