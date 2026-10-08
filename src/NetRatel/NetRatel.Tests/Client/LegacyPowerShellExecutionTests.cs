using System.Collections.Concurrent;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Client.Service.Shells;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Service.Shells;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LegacyPowerShellExecutionTests
{
    [Theory]
    [InlineData(TaskKinds.Legacy_RunPowerShell)]
    [InlineData(TaskKinds.Legacy_ExecPs)]
    public async Task Persisted_legacy_dispatch_preserves_the_property_dictionary_array(string kind)
    {
        const string script = """
            [pscustomobject]@{
                Name = 'Unicode é'
                Count = 3
                Nested = [pscustomobject]@{ Note = 'nested' }
                Duration = [timespan]::FromSeconds(12)
                Timestamp = [datetimeoffset]'2024-01-02T03:04:05+02:00'
            }
            42
            """;
        var result = await ExecuteAsync(kind, script);
        result.Status.Should().Be("completed");
        result.ExitCode.Should().Be(0);
        using var document = JsonDocument.Parse(result.ReturnData!);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        item.GetProperty("Name").GetString().Should().Be("Unicode é");
        item.GetProperty("Count").GetInt32().Should().Be(3);
        item.GetProperty("Nested").GetString().Should().Be("@{Note=nested}");
        item.GetProperty("Duration").GetString().Should().Be("00:00:12");
        item.GetProperty("Timestamp").GetString().Should().Be("2024-01-02T03:04:05+02:00");
    }

    [Theory]
    [InlineData("42", "[\"42\"]")]
    [InlineData("'text'", "[{\"Length\":4}]")]
    [InlineData("$null", "Execution successful")]
    public async Task Legacy_dispatch_preserves_primitive_string_and_empty_result_shapes(string script, string expected)
    {
        var result = await ExecuteAsync(TaskKinds.Legacy_ExecPs, script);
        result.Status.Should().Be("completed");
        result.ReturnData.Should().Be(expected);
    }

    [Theory]
    [InlineData("Write-Error 'legacy nonterminating'; 'output'", 1, "legacy nonterminating")]
    [InlineData("exit 7", 7, "Process exited with code 7 without emitting stdout/stderr")]
    public async Task Legacy_dispatch_fails_on_nonterminating_errors_and_preserves_explicit_exit_codes(string script, int code, string error)
    {
        var result = await ExecuteAsync(TaskKinds.Legacy_ExecPs, script);
        result.Status.Should().Be("failed");
        result.ExitCode.Should().Be(code);
        result.ReturnData.Should().Contain(error);
    }

    [Fact]
    public async Task Missing_interpreter_publishes_one_failure_then_a_valid_command_succeeds_on_the_same_manager()
    {
        var runner = new ExternalShellRunner(null,
            keyword => keyword == "pwsh" ? null : ShellExecutableResolver.Resolve(keyword));
        var statuses = new ConcurrentQueue<(string Id, string Status)>();
        var missing = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var valid = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager(runner, (id, status, result, _) =>
        {
            statuses.Enqueue((id, status));
            if (id == "missing" && status == "failed") missing.TrySetResult(result);
            if (id == "valid" && status == "completed") valid.TrySetResult(result);
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);
        manager.EnqueueGatewayCommand("missing", TaskKinds.ExecLibraryScript,
            JsonSerializer.Serialize(new ExecLibraryScriptPayload
            {
                ScriptType = ScriptType.PowerShell,
                ScriptContent = "Write-Output 'must not run'",
                Preferred = ShellExecutor.Pwsh
            }), 1, 0).Should().BeTrue();
        (await missing.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Contain("pwsh");
        manager.EnqueueGatewayCommand("valid", TaskKinds.ExecShellCommand,
            JsonSerializer.Serialize(new ExecShellCommandPayload
            {
                Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
                Command = OperatingSystem.IsWindows() ? "Write-Output recovered" : "printf recovered",
                TimeoutSeconds = 5
            }), 1, 0).Should().BeTrue();
        (await valid.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Contain("recovered");
        statuses.Where(item => item.Id == "missing").Select(item => item.Status).Should().Equal("started", "failed");
        statuses.Where(item => item.Id == "valid").Select(item => item.Status).Should().Equal("started", "completed");
    }

    private static async Task<(string Status, string? ReturnData, int? ExitCode)> ExecuteAsync(string kind, string script)
    {
        var completion = new TaskCompletionSource<(string, string?, int?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager((_, status, result, code) =>
        {
            if (status is "completed" or "failed" or "cancelled") completion.TrySetResult((status, result, code));
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);
        var payload = kind == TaskKinds.Legacy_ExecPs ? JsonSerializer.Serialize(new { script }) : script;
        manager.EnqueueGatewayCommand("legacy", kind, payload, 1, 0).Should().BeTrue();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
