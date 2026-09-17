using FluentAssertions;
using NetRatel.Shared.Tooling;
using Xunit;

namespace NetRatel.Shared.Tests;

public class ToolTemplateRendererTests
{
    [Fact]
    public void Render_Replaces_Placeholders_With_InputValues()
    {
        const string template = "Write-Output \"Hello ${name}\"";
        const string input = "{\"name\":\"World\"}";

        var result = ToolTemplateRenderer.Render(template, input);

        result.Should().Be("Write-Output \"Hello World\"");
    }
}
