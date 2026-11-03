using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.Shared;

public sealed class TaskOutputRedactorJsonTests
{
    [Theory]
    [InlineData("\"synthetic-canary\"", "\"[REDACTED]\"")]
    [InlineData("""{"nested":"\"synthetic-canary\""}""", """{"nested":"\u0022[REDACTED]\u0022"}""")]
    [InlineData("424242", "\"[REDACTED]\"")]
    public void FrozenScalarAndNestedJsonMaskingPreservesGeneralEncoding(string input, string expected)
    {
        var redactor = new TaskOutputRedactor(null, additionalSecretValues: new Dictionary<string, string>
        {
            ["canary"] = "synthetic-canary", ["number"] = "424242"
        });
        Assert.Equal(expected, redactor.Redact(input));
    }
}
