using FluentAssertions;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class EnrollmentStartupPolicyTests
{
    [Fact]
    public void ServiceMode_WithNoEnrollment_DoesNotPrompt_AndUsesConfigurationExitCode()
    {
        EnrollmentStartupPolicy.ShouldPromptForEnrollment(serviceMode: true, enrollmentCode: null).Should().BeFalse();
        EnrollmentStartupPolicy.GetMissingEnrollmentExitCode(serviceMode: true)
            .Should().Be(EnrollmentStartupPolicy.ConfigurationErrorExitCode);
    }

    [Fact]
    public void InteractiveMode_WithNoEnrollment_Prompts_AndUsesEnrollmentExitCode()
    {
        EnrollmentStartupPolicy.ShouldPromptForEnrollment(serviceMode: false, enrollmentCode: null).Should().BeTrue();
        EnrollmentStartupPolicy.GetMissingEnrollmentExitCode(serviceMode: false).Should().Be(11);
    }
}
