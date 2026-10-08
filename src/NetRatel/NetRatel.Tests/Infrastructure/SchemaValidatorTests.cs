using AwesomeAssertions;
using NetRatel.Infrastructure.Tooling;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public class SchemaValidatorTests
{
    [Fact]
    public void Invalid_Input_Returns_Errors()
    {
        var schema = "{" +
                     "\"type\":\"object\"," +
                     "\"properties\":{\"name\":{\"type\":\"string\"}}," +
                     "\"required\":[\"name\"]" +
                     "}";
        var json = "{}"; // Missing required 'name'

        var valid = SchemaValidator.TryValidate(schema, json, out var errors);

        valid.Should().BeFalse();
        errors.Should().NotBeEmpty();
    }
}
