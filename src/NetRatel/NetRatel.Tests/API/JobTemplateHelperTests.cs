using FluentAssertions;
using NetRatel.API.Services.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public class JobTemplateHelperTests
{
    [Fact]
    public void ParseInputs_Flattens_ExternalService_InputEnvelope_For_RuntimeResolution()
    {
        const string payload = """
            {
              "meta": {
                "requestId": "req-1"
              },
              "input": {
                "path1": "c:\\",
                "path2": "d:\\temp"
              }
            }
            """;

        var inputs = JobTemplateHelper.ParseInputs(payload);

        inputs.Should().ContainKey("meta");
        inputs.Should().ContainKey("input");
        inputs["path1"]!.ToString().Should().Be(@"c:\");
        inputs["path2"]!.ToString().Should().Be(@"d:\temp");
        JobTemplateHelper.ResolvePlaceholders("ls @option.path1@", inputs).Should().Be(@"ls c:\");
    }

    [Fact]
    public void BuildOptionsFromInputs_Uses_Flattened_InputValues_For_ExternalServiceEnvelope()
    {
        const string payload = """
            {
              "meta": {
                "requestId": "req-1"
              },
              "input": {
                "path1": "c:\\",
                "path2": "d:\\temp"
              },
              "expectedRuntimeSeconds": 1800,
              "graceSeconds": 600,
              "hardTimeoutSeconds": 2400
            }
            """;

        var options = JobTemplateHelper.BuildOptionsFromInputs(payload);

        options.Should().Contain(x => x.Name == "path1" && x.Value == @"c:\" && x.Source == "@option.path1@");
        options.Should().Contain(x => x.Name == "path2" && x.Value == @"d:\temp" && x.Source == "@option.path2@");
        options.Should().NotContain(x => x.Name == "meta");
        options.Should().NotContain(x => x.Name == "input");
        options.Should().NotContain(x => x.Name == "expectedRuntimeSeconds");
        options.Should().NotContain(x => x.Name == "graceSeconds");
        options.Should().NotContain(x => x.Name == "hardTimeoutSeconds");
    }
}
