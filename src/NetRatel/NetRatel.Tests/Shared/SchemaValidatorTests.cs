using FluentAssertions;
using NetRatel.Shared.Tooling;
using Xunit;

namespace NetRatel.Shared.Tests;

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
