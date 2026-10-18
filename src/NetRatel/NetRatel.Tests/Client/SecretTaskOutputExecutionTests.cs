using System.Collections.Concurrent;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class SecretTaskOutputExecutionTests
{
    [Fact]
    public async Task Library_reference_is_available_to_execution_but_redacted_before_stream_capture_and_truncation()
    {
        const string secret = "synthetic-garden-908";
        var reference = "NETRATEL_OUTPUT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(reference, secret);
        try
        {
            var updates = new ConcurrentQueue<(string Status, string? Result, int? ExitCode)>();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var manager = new ClientTaskManager((_, status, result, exitCode) =>
            {
                updates.Enqueue((status, result, exitCode));
                if (status is "completed" or "failed" or "cancelled") completion.TrySetResult();
                return Task.CompletedTask;
            }, message => throw new InvalidOperationException(message));
            var policy = TaskOutputPolicy.Create([("vault", "secret_reference")], 19, 3, new string('a', 64));
            var script = OperatingSystem.IsWindows()
                ? "param([string]$vault)\n$value = [Environment]::GetEnvironmentVariable($vault); if ($value -ne '" + secret + "') { exit 12 }; " +
                  "[Console]::Out.WriteLine(('x' * 4090) + $value); [Console]::Error.WriteLine($value); [Console]::Out.WriteLine('execution-value-intact'); exit 0"
                : "value=\"${!vault}\"; [ \"$value\" = '" + secret + "' ] || exit 12; " +
                  "printf '%4090s%s\\n' '' \"$value\"; printf '%s\\n' \"$value\" >&2; printf 'execution-value-intact\\n'";
            manager.Start(7, 0);
            manager.EnqueueGatewayCommand("owned-secret-output", TaskKinds.ExecLibraryScript,
                JsonSerializer.Serialize(new ExecLibraryScriptPayload
                {
                    ScriptId = 19,
                    ScriptType = OperatingSystem.IsWindows() ? ScriptType.PowerShell : ScriptType.Bash,
                    Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
                    ScriptContent = script,
                    Parameters = new() { ["vault"] = reference },
                    TimeoutSeconds = 10,
                    OutputPolicy = policy
                }), 7, 0).Should().BeTrue();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));

            updates.Last().Status.Should().Be("completed");
            updates.Last().ExitCode.Should().Be(0, "the executable parameter and its locally resolved value remain unchanged");
            var progress = updates.Where(x => x.Status == "progress").ToArray();
            progress.Should().NotBeEmpty();
            foreach (var update in updates)
            {
                update.Result.Should().NotContain("synthe", "a full secret or a prefix truncated at the old capture boundary must never escape");
                TaskOutputRedactor.ReadBinding(update.Result).Should().Be(policy.Binding);
            }
            updates.Last().Result.Should().Contain("execution-value-intact");
            using var terminal = JsonDocument.Parse(updates.Last().Result!);
            terminal.RootElement.GetProperty("outputTruncated").GetBoolean().Should().BeTrue();
            terminal.RootElement.GetProperty("stderr")[0].GetString().Should().Be(TaskOutputRedactor.Mask);
        }
        finally
        {
            Environment.SetEnvironmentVariable(reference, null);
        }
    }
}
