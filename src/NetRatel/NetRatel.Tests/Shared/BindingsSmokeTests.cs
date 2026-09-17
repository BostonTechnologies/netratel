using FluentAssertions;
using Xunit;

public class BindingsSmokeTests
{
    [Fact]
    public void Assembly_is_not_null()
    {
        typeof(BindingsSmokeTests).Assembly.Should().NotBeNull();
    }
}
