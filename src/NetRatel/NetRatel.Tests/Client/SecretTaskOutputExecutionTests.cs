using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Protected_library_binding_uses_private_transport_or_fails_before_start(bool expired, bool wrongScript)
    {
        const string secret = "synthetic-protected-909\n◆multiline-edge\n";
        var largeSecret = new string('q', 48 * 1024);
        var valueHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var largeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(largeSecret)));
        var windows = OperatingSystem.IsWindows();
        var hashProgram = OperatingSystem.IsMacOS() ? "/usr/bin/shasum -a 256" : "sha256sum";
        var script = windows
            ? $$"""
                param([string]$Canary, [string]$LargeSecret)
                $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($Canary))).Replace('-', '')
                if ($hash -ne '{{valueHash}}' -or $env:NETRATEL_SECRET_Canary) { exit 41 }
                $largeHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($LargeSecret))).Replace('-', '')
                if ($largeHash -ne '{{largeHash}}' -or [Console]::In.Read() -ne -1) { exit 42 }
                [Console]::Out.Write($Canary)
                [Console]::Error.Write($Canary)
                [Console]::Out.WriteLine('private-input-intact')
                exit 0
                """
            : $$"""
                actual=$(printf '%s' "$NETRATEL_SECRET_Canary" | {{hashProgram}})
                [[ ${actual%% *} == '{{valueHash.ToLowerInvariant()}}' ]] || exit 41
                [[ $(command env) != *NETRATEL_SECRET_Canary=* ]] || exit 42
                actual_large=$(printf '%s' "$NETRATEL_SECRET_LargeSecret" | {{hashProgram}})
                [[ ${actual_large%% *} == '{{largeHash.ToLowerInvariant()}}' ]] || exit 43
                if IFS= read -r -n 1; then exit 44; fi
                printf '%s' "$NETRATEL_SECRET_Canary"
                printf '%s' "$NETRATEL_SECRET_Canary" >&2
                printf 'private-input-intact\n'
                """;
        var scriptHash = wrongScript ? new string('f', 64) : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        var policy = TaskOutputPolicy.Create([("Canary", "secret"), ("LargeSecret", "secret")], 19, 3, scriptHash);
        var updates = new ConcurrentQueue<(string Status, string? Result, int? ExitCode)>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager((_, status, result, exitCode) =>
        {
            updates.Enqueue((status, result, exitCode));
            if (status is "completed" or "failed" or "cancelled") completion.TrySetResult();
            return Task.CompletedTask;
        }, message => throw new InvalidOperationException(message));
        manager.Start(7, 0);
        var payload = JsonSerializer.Serialize(new ExecLibraryScriptPayload
        {
            ScriptId = 19, ScriptType = windows ? ScriptType.PowerShell : ScriptType.Bash,
            Preferred = windows ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
            ScriptContent = script, OutputPolicy = policy, TimeoutSeconds = 10
        });
        Assert.True(manager.EnqueueProtectedJobStep("protected-library-binding", TaskKinds.ExecLibraryScript, payload, 7, 0,
            new Dictionary<string, string> { ["Canary"] = secret, ["LargeSecret"] = largeSecret }, DateTimeOffset.UtcNow.AddSeconds(expired ? -1 : 30)));
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        foreach (var update in updates)
        {
            Assert.DoesNotContain("synthetic-protected-909", update.Result ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("multiline-edge", update.Result ?? "", StringComparison.Ordinal);
        }
        if (expired || wrongScript)
        {
            Assert.Equal("failed", updates.Last().Status);
            Assert.DoesNotContain(updates, update => update.Status == "progress");
        }
        else
        {
            Assert.Equal("completed", updates.Last().Status);
            Assert.Equal(0, updates.Last().ExitCode);
            Assert.Contains("private-input-intact", updates.Last().Result, StringComparison.Ordinal);
            Assert.Contains(TaskOutputRedactor.Mask, updates.Last().Result, StringComparison.Ordinal);
        }
    }

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
