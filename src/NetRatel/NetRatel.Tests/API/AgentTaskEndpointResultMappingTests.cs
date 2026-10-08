using System.Text.Json;
using AwesomeAssertions;
using NetRatel.API.Endpoints;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTaskEndpointResultMappingTests
{
    [Theory]
    [InlineData(TaskKinds.Legacy_RunPowerShell, "Write-Output 'legacy'")]
    [InlineData(TaskKinds.Legacy_ExecPs, "{\"script\":\"Write-Output 'legacy'\"}")]
    public async Task Legacy_PowerShell_requests_dispatch_as_PowerShell_scripts_and_preserve_execution_options(string kind, string payload)
    {
        var request = new TaskCreateRequestDto
        {
            TaskType = kind,
            Payload = payload,
            Preferred = ShellExecutor.WindowsPowerShell,
            WorkingDirectory = "working-directory",
            TimeoutSeconds = 13,
            Parameters = new Dictionary<string, string> { ["Literal"] = "quote' and Unicode é" }
        };
        var taskType = AgentTaskEndpoints.NormalizeTaskType(kind);
        var result = await AgentTaskEndpoints.BuildPayloadAsync(request, taskType, null!, CancellationToken.None);

        taskType.Should().Be(TaskKinds.ExecLibraryScript);
        result.Error.Should().BeNull();
        var execution = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(result.Value!)!;
        execution.ScriptType.Should().Be(ScriptType.PowerShell);
        execution.ScriptContent.Should().Be("Write-Output 'legacy'");
        execution.ScriptId.Should().Be(0);
        execution.Preferred.Should().Be(ShellExecutor.WindowsPowerShell);
        execution.WorkingDirectory.Should().Be(request.WorkingDirectory);
        execution.TimeoutSeconds.Should().Be(13);
        execution.Parameters.Should().Contain("Literal", "quote' and Unicode é");
    }

    [Fact]
    public async Task Legacy_PowerShell_alias_rejects_environment_requirements_that_its_script_payload_cannot_carry()
    {
        var request = new TaskCreateRequestDto
        {
            TaskType = TaskKinds.Legacy_RunPowerShell,
            ShellCommand = new ExecShellCommandPayload
            {
                Command = "Write-Output 'legacy'",
                EnvironmentReferences = ["REQUIRED_SECRET"]
            }
        };
        var result = await AgentTaskEndpoints.BuildPayloadAsync(request,
            AgentTaskEndpoints.NormalizeTaskType(request.TaskType), null!, CancellationToken.None);
        result.Value.Should().BeNull();
        result.Error.Should().Contain("EnvironmentReferences require exec-shell-cmd");
    }

    [Fact]
    public async Task Legacy_ExecPs_rejects_a_missing_script_instead_of_executing_JSON_as_a_command()
    {
        var request = new TaskCreateRequestDto { TaskType = TaskKinds.Legacy_ExecPs, Payload = "{\"other\":\"value\"}" };
        var result = await AgentTaskEndpoints.BuildPayloadAsync(request,
            AgentTaskEndpoints.NormalizeTaskType(request.TaskType), null!, CancellationToken.None);
        result.Value.Should().BeNull();
        result.Error.Should().Contain("PowerShell script is required");
    }

    [Fact]
    public void Map_KeepsThePersistedLegacyArrayWithoutRequiringAnEnvelope()
    {
        const string result = "[{\"Name\":\"legacy\",\"Count\":3}]";
        var task = AgentTaskEndpoints.Map(CreateActivity("Completed", null, result), null);
        task.ReturnData.Should().Be(result);
        task.ExitCode.Should().BeNull();
    }

    [Fact]
    public void Map_UsesPersistedResultJsonForReturnDataAndKeepsErrorAsStatusSummary()
    {
        const string result = "{\"stdout\":[\"done\"],\"exitCode\":0}";
        var task = AgentTaskEndpoints.Map(CreateActivity("Completed", null, result), null);

        task.ReturnData.Should().Be(result);
        task.StatusMessage.Should().BeNull();
        task.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Map_ProjectsLegacyJsonStoredInErrorAsReturnDataWithoutExposingItAsStatus()
    {
        const string legacyResult = "{\"stdout\":[\"done\"],\"stderr\":[\"permission denied\"],\"exitCode\":1}";
        var task = AgentTaskEndpoints.Map(CreateActivity("Failed", legacyResult), null);

        task.ReturnData.Should().Be(legacyResult);
        task.StatusMessage.Should().Be("permission denied");
        task.ExitCode.Should().Be(1);
    }

    private static JobTaskActivityInfo CreateActivity(string status, string? error, string? resultJson = null) => new(
        42,
        "request-42",
        null,
        null,
        "client",
        3,
        "exec-library-script",
        status,
        error,
        DateTimeOffset.UtcNow,
        status == "Processing" ? null : DateTimeOffset.UtcNow,
        null,
        resultJson);
}
