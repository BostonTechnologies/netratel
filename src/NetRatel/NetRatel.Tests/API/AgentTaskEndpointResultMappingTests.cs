using System.Text.Json;
using AwesomeAssertions;
using NetRatel.API.Endpoints;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTaskEndpointResultMappingTests
{
    [Fact]
    public async Task Generic_task_allowlist_rejects_typed_service_control_even_with_a_supplied_request_id()
    {
        var request = new TaskCreateRequestDto
        {
            TaskType = ClientServiceControlRules.TaskType,
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = "{\"serviceName\":\"Spooler\",\"action\":\"Stop\"}"
        };
        var result = await AgentTaskEndpoints.BuildPayloadAsync(request,
            AgentTaskEndpoints.NormalizeTaskType(request.TaskType), null!, CancellationToken.None);
        result.Value.Should().BeNull();
        result.Error.Should().Contain("Unsupported TaskType");
    }

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

    [Fact]
    public async Task Command_title_redacts_private_assignments_before_truncation_without_altering_dispatch()
    {
        var secret = new string('s', 200);
        var command = $"echo safe client_secret={secret} public-label";
        var request = new TaskCreateRequestDto { TaskType = TaskKinds.ExecShellCommand, ShellCommand = new() { Command = command } };
        var prepared = await AgentTaskEndpoints.BuildPayloadAsync(request, request.TaskType, null!, CancellationToken.None);
        var metadata = AgentTaskEndpoints.BuildExecutionMetadata(request.TaskType, prepared.Value!, prepared.ScriptName);

        metadata.CommandLabel.Should().Be("echo safe client_secret=[REDACTED] public-label");
        metadata.CommandLabel.Should().NotContain(secret);
        JsonSerializer.Deserialize<ExecShellCommandPayload>(prepared.Value!)!.Command.Should().Be(command);
    }

    [Fact]
    public void Map_preserves_recorded_context_and_does_not_invent_historical_start_environment_or_exit()
    {
        var historical = AgentTaskEndpoints.Map(CreateActivity("Pending", null), null);
        historical.Environment.Should().BeNull();
        historical.StartedAt.Should().BeNull();
        historical.ExitCode.Should().BeNull();
        historical.ClientDisplayName.Should().Be("Historical target: client");

        var created = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();
        var activity = CreateActivity("Failed", null) with
        {
            AgentId = agentId,
            CreatedAtUtc = created,
            Environment = ClientEnvironment.Dev,
            StartedAtUtc = created.AddSeconds(1),
            ExitCode = 23,
            TargetDisplayName = "stored-host",
            TargetHostName = "stored-host",
            ScriptName = "Script at dispatch",
            ScriptType = "PowerShell"
        };
        var currentAgent = new Agent { Id = agentId, TenantId = 3, Name = "renamed-agent", DeviceInfoJson = """{"hostName":"renamed-host"}""" };
        var recorded = AgentTaskEndpoints.Map(activity, currentAgent);
        recorded.ClientDisplayName.Should().Be("stored-host");
        recorded.ClientName.Should().BeNull();
        recorded.Environment.Should().Be(ClientEnvironment.Dev);
        recorded.StartedAt.Should().Be(created.AddSeconds(1));
        recorded.ExitCode.Should().Be(23);
        recorded.ScriptName.Should().Be("Script at dispatch");
        recorded.ScriptType.Should().Be("PowerShell");
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
