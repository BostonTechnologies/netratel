using BlazorMonaco.Editor;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class GeneratedInstallScriptDisplayTests : AsyncBunitContext
{
    public GeneratedInstallScriptDisplayTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
    }

    [Theory]
    [InlineData("Install command", "Copy command", "echo '<script>alert(1)</script>'\nprintf '%s' \"two lines\"")]
    [InlineData("Public script URL", "Copy URL", "https://netratel.test/clients/install/link.sh?one=1&two=2")]
    public void CodeBlock_DisplaysLiteralText_AndCopiesTheCompleteValue(string label, string copyLabel, string value)
    {
        string? copied = null;
        var cut = Render<InstallCodeBlock>(parameters => parameters
            .Add(component => component.Label, label)
            .Add(component => component.CopyLabel, copyLabel)
            .Add(component => component.Value, value)
            .Add(component => component.OnCopy, (string text) => copied = text));

        cut.Find("pre code").TextContent.Should().Be(value);
        cut.FindAll("script").Should().BeEmpty();
        cut.FindComponent<MudIconButton>().Instance.Size.Should().Be(Size.Small);
        cut.Find($"button[aria-label='{copyLabel}']").Click();
        copied.Should().Be(value);
    }

    [Theory]
    [InlineData("win-x64", "powershell")]
    [InlineData("win-arm64", "powershell")]
    [InlineData("linux-x64", "shell")]
    [InlineData("osx-arm64", "shell")]
    public void ScriptPreview_IsReadOnly_AndUsesTheRuntimeLanguage(string runtimeId, string language)
    {
        const string script = "first line\nsecond line\n";
        var cut = Render<GeneratedInstallScriptPreview>(parameters => parameters
            .Add(component => component.RuntimeId, runtimeId)
            .Add(component => component.Script, script));

        var editor = cut.FindComponent<StandaloneCodeEditor>().Instance;
        var options = editor.ConstructionOptions(editor);
        options.ReadOnly.Should().BeTrue();
        options.Value.Should().Be(script);
        options.Language.Should().Be(language);
        options.AutomaticLayout.Should().BeTrue();
    }
}
