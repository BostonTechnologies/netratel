using FluentAssertions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobExecutionRuntimePolicyTests
{
    [Fact]
    public void FromValues_Defaults_To_Thirty_Minutes_With_No_Grace()
    {
        var policy = JobExecutionRuntimePolicy.FromValues(null, null, null);

        policy.ExpectedRuntimeSeconds.Should().Be(1800);
        policy.GraceSeconds.Should().Be(0);
        policy.HardTimeoutSeconds.Should().Be(1800);
    }

    [Fact]
    public void FromValues_Adds_ExternalService_Grace_To_Expected_Runtime()
    {
        var policy = JobExecutionRuntimePolicy.FromValues(1800, 600, null);

        policy.ExpectedRuntimeSeconds.Should().Be(1800);
        policy.GraceSeconds.Should().Be(600);
        policy.HardTimeoutSeconds.Should().Be(2400);
    }

    [Fact]
    public void FromJob_Reads_ExecutionPolicy_From_OptionsJson()
    {
        var job = new JobDefinitionInfo(
            Id: 14,
            Name: "Get-Childitem pathparam",
            FolderPath: "/Camelot",
            Description: null,
            TenantId: 2,
            ClientIdentity: "client",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            OptionsJson: """
            {"executionPolicy":{"expectedRuntimeSeconds":120,"graceSeconds":30,"hardTimeoutSeconds":150}}
            """);

        var policy = JobExecutionRuntimePolicy.FromJob(job);

        policy.ExpectedRuntimeSeconds.Should().Be(120);
        policy.GraceSeconds.Should().Be(30);
        policy.HardTimeoutSeconds.Should().Be(150);
    }

    [Fact]
    public void ResultPayloadHasSuccessfulExitCode_Treats_ExitZero_As_Success()
    {
        var payload = """{"exitCode":0,"stdout":["Directory: C:\\"]}""";

        JobTaskBridge.ResultPayloadHasSuccessfulExitCode(payload).Should().BeTrue();
    }
}
