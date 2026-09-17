using System.Text.Json;
using FluentAssertions;
using NetRatel.Client.Service.Tasks;
using NetRatel.API.Services.Jobs;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Data.Task;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LibraryScriptPayloadTests
{
    [Fact]
    public void ExecLibraryScriptPayload_Deserializes_LegacyPayload_WithoutScriptContent()
    {
        const string json = """
            {
              "ScriptId": 2,
              "ScriptType": 1,
              "Preferred": 3,
              "Parameters": {
                "Path": "/"
              }
            }
            """;

        var payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(json);

        payload.Should().NotBeNull();
        payload!.ScriptId.Should().Be(2);
        payload.ScriptContent.Should().BeNull();
        payload.Parameters.Should().Contain("Path", "/");
    }

    [Fact]
    public void ResolveLibraryScriptContent_UsesSnapshotContent_WhenPresent()
    {
        var payload = new ExecLibraryScriptPayload
        {
            ScriptId = 2,
            ScriptType = ScriptType.Bash,
            ScriptContent = "ls /"
        };

        var content = ClientTaskManager.ResolveLibraryScriptContent(payload);

        content.Should().Be("ls /");
    }

    [Fact]
    public void ResolveLibraryScriptContent_UsesSnapshotContent_WhenAgentLibraryIsMissing()
    {
        const string json = """
            {
              "ScriptId": 6,
              "ScriptType": 1,
              "ScriptContent": "#| NetRatel-MANIFEST\n{\"name\":\"Linux Disk Report\"}\n#| END\n\necho from snapshot",
              "Preferred": 3,
              "Parameters": {},
              "WorkingDirectory": "/tmp",
              "TimeoutSeconds": 30
            }
            """;
        var payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(json);

        var content = ClientTaskManager.ResolveLibraryScriptContent(payload!);

        content.Should().Contain("echo from snapshot");
    }

    [Fact]
    public void ResolveLibraryScriptContent_AllowsSnapshotOnlyDispatch()
    {
        var payload = new ExecLibraryScriptPayload
        {
            ScriptId = 0,
            ScriptType = ScriptType.Bash,
            ScriptContent = "echo snapshot-only"
        };

        var content = ClientTaskManager.ResolveLibraryScriptContent(payload);

        content.Should().Be("echo snapshot-only");
    }

    [Fact]
    public void ResolveLibraryScriptContent_RejectsPayloadsWithoutSnapshotContent()
    {
        var payload = new ExecLibraryScriptPayload
        {
            ScriptId = 2,
            ScriptType = ScriptType.Bash
        };

        var content = ClientTaskManager.ResolveLibraryScriptContent(payload);

        content.Should().BeNull();
    }

    [Fact]
    public void ExecLibraryScriptPayload_CarriesSelfContainedDispatchFields()
    {
        var payload = new ExecLibraryScriptPayload
        {
            ScriptId = 2,
            ScriptType = ScriptType.Bash,
            ScriptContent = "ls /",
            Preferred = ShellExecutor.Bash,
            WorkingDirectory = "/tmp",
            TimeoutSeconds = 30,
            Parameters = new Dictionary<string, string>
            {
                ["Path"] = "/"
            }
        };

        var roundTrip = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(JsonSerializer.Serialize(payload));

        roundTrip.Should().NotBeNull();
        roundTrip!.ScriptId.Should().Be(2);
        roundTrip.ScriptType.Should().Be(ScriptType.Bash);
        roundTrip.ScriptContent.Should().Be("ls /");
        roundTrip.Preferred.Should().Be(ShellExecutor.Bash);
        roundTrip.WorkingDirectory.Should().Be("/tmp");
        roundTrip.TimeoutSeconds.Should().Be(30);
        roundTrip.Parameters.Should().Contain("Path", "/");
    }

    [Fact]
    public void Sanitize_RemovesScriptContent_FromLibraryScriptDiagnostics()
    {
        var payload = JsonSerializer.Serialize(new ExecLibraryScriptPayload
        {
            ScriptId = 2,
            ScriptType = ScriptType.Bash,
            ScriptContent = "secret script body",
            Preferred = ShellExecutor.Bash
        });

        var sanitized = ExecLibraryScriptPayloadDiagnostics.Sanitize(TaskKinds.ExecLibraryScript, payload);

        sanitized.Should().NotContain("secret script body");
        sanitized.Should().Contain("[omitted]");
        sanitized.Should().Contain("scriptContentSha256");
        var parsed = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(sanitized!);
        parsed!.ScriptId.Should().Be(2);
        parsed.ScriptContent.Should().Be("[omitted]");
    }

    [Fact]
    public void TaskStatuses_TreatsCancelledAsTerminalAndNotDispatchable()
    {
        TaskStatuses.IsDispatchable(TaskStatuses.Cancelled).Should().BeFalse();
        TaskStatuses.IsTerminal(TaskStatuses.Cancelled).Should().BeTrue();
        TaskStatuses.IsTerminal(TaskStatuses.Processing).Should().BeFalse();
        TaskStatuses.IsDispatchable(TaskStatuses.New).Should().BeTrue();
    }

    [Fact]
    public void BuildLibraryScriptParameters_CarriesJobInputPath_ToPowerShellParameter()
    {
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"c:\"
        };

        var parameters = JobTaskBridge.BuildLibraryScriptParameters(inputs, null, null);

        parameters.Should().ContainKey("Path");
        parameters["Path"].Should().Be(@"c:\");
    }

    [Fact]
    public void BuildLibraryScriptParameters_ExcludesExternalServiceEnvelopeContainers()
    {
        var inputs = JobTemplateHelper.ParseInputs("""
            {
              "meta": {
                "requestId": "req-1",
                "requestTaskId": "task-1"
              },
              "input": {
                "Path": "c:\\"
              }
            }
            """);

        var parameters = JobTaskBridge.BuildLibraryScriptParameters(inputs, null, null);

        parameters.Should().ContainSingle();
        parameters.Should().ContainKey("Path");
        parameters["Path"].Should().Be(@"c:\");
        parameters.Should().NotContainKey("input");
        parameters.Should().NotContainKey("meta");
    }

    [Fact]
    public void BuildLibraryScriptParameters_ExcludesRuntimePolicyMetadata()
    {
        var inputs = JobTemplateHelper.ParseInputs("""
            {
              "meta": {
                "requestId": "req-1"
              },
              "input": {
                "Path": "c:\\"
              },
              "expectedRuntimeSeconds": 1800,
              "graceSeconds": 600,
              "hardTimeoutSeconds": 2400
            }
            """);

        var parameters = JobTaskBridge.BuildLibraryScriptParameters(inputs, null, null);

        parameters.Should().ContainSingle();
        parameters["Path"].Should().Be(@"c:\");
        parameters.Should().NotContainKey("expectedRuntimeSeconds");
        parameters.Should().NotContainKey("graceSeconds");
        parameters.Should().NotContainKey("hardTimeoutSeconds");
    }

    [Fact]
    public void BuildLibraryScriptParameters_AllowsStepPayload_ToOverrideJobInput()
    {
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"c:\"
        };
        var stepParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"d:\"
        };

        var parameters = JobTaskBridge.BuildLibraryScriptParameters(inputs, null, stepParameters);

        parameters["Path"].Should().Be(@"d:\");
    }
}
