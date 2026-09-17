using FluentAssertions;
using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.Shared;

public sealed class OperatorOutputRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer [REDACTED]")]
    [InlineData("token=not-for-return", "token=[REDACTED]")]
    [InlineData("Host=db.example;Password=not-for-return", "Host=[REDACTED];Password=[REDACTED]")]
    public void Redact_RemovesCommonCredentialForms(string input, string expected)
    {
        OperatorOutputRedactor.Redact(input).Should().Be(expected);
    }
}
