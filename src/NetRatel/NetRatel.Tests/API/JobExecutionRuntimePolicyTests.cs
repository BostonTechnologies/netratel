using AwesomeAssertions;
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

    [Theory]
    [InlineData(null, null, null, 120, 30, 500)]
    [InlineData(200, 40, null, 200, 40, 500)]
    [InlineData(200, 40, 600, 200, 40, 600)]
    public void RuntimeRequest_MergesEachSelectedValueWithTheStoredDefinition(int? expected, int? grace, int? hard, int effectiveExpected, int effectiveGrace, int effectiveHard)
    {
        var job = new JobDefinitionInfo(1, "job", "/", null, 7, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "{\"executionPolicy\":{\"expectedRuntimeSeconds\":120,\"graceSeconds\":30,\"hardTimeoutSeconds\":500}}");
        var request = new NetRatel.Shared.Contracts.Jobs.RunJobRequest("test", null, null, expected, grace, hard);
        var policy = JobExecutionRuntimePolicy.FromJobAndRequest(job, request);
        policy.Should().Be(new JobExecutionRuntimePolicy(effectiveExpected, effectiveGrace, effectiveHard));
        JobExecutionRuntimePolicy.FromOptions(JobExecutionRuntimePolicy.MergeOptionsJson(job.OptionsJson, policy)).Should().Be(policy);
    }

    [Fact]
    public void ExecutionContext_PreservesUnknownHistoryAndRoundTripsSelectedEnvironment()
    {
        JobExecutionContextPolicy.FromOptions(null).Should().BeNull();
        JobExecutionContextPolicy.FromOptions("{}").Should().BeNull();
        JobExecutionContextPolicy.FromOptions(JobExecutionContextPolicy.MergeOptionsJson("{\"unrelated\":true}", NetRatel.Shared.ClientEnvironment.Dev))
            .Should().Be(NetRatel.Shared.ClientEnvironment.Dev);
    }

    [Fact]
    public void ResultPayloadHasSuccessfulExitCode_Treats_ExitZero_As_Success()
    {
        var payload = """{"exitCode":0,"stdout":["Directory: C:\\"]}""";

        JobTaskBridge.ResultPayloadHasSuccessfulExitCode(payload).Should().BeTrue();
    }
}
