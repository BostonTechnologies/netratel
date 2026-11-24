using System.Text;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Shells;
using NetRatel.Client.Service.Terminal;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class SavedAccountExecutionTests
{
    [Fact]
    [Trait("category", "manual-integration")]
    public async Task Job_and_terminal_verify_the_selected_native_identity()
    {
        var username = Environment.GetEnvironmentVariable("NETRATEL_TEST_EXECUTION_USER");
        var expectedId = Environment.GetEnvironmentVariable("NETRATEL_TEST_EXECUTION_ACCOUNT");
        Assert.SkipUnless(username is not null && expectedId is not null, "Run in the privileged native account fixture.");
        var windows = OperatingSystem.IsWindows();
        var credential = new RunAsCredential
        {
            ProfileId = Guid.NewGuid().ToString("D"),
            Username = username!,
            Mode = windows ? RunAsCredential.Types.Mode.WindowsPassword : RunAsCredential.Types.Mode.UnixPrivilegedLocal,
            Password = Environment.GetEnvironmentVariable("NETRATEL_TEST_EXECUTION_PASSWORD") ?? ""
        };
        Assert.True(NativeExecutionAccounts.IsValid(credential));
        var ct = TestContext.Current.CancellationToken;
        var command = windows ? "whoami /user /fo csv /nh" : "id -u";
        var runner = new ExternalShellRunner();
        var observer = new ExternalShellRunner.ExecutionObserver(() => Task.CompletedTask, (_, _) => Task.CompletedTask)
        { Credential = credential };
        var payload = new ExecShellCommandPayload { Preferred = windows ? ShellExecutor.Cmd : ShellExecutor.Bash, Command = command, TimeoutSeconds = 15 };
        var result = await runner.RunShellCommandAsync(payload, ct, observer);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Error));
        Assert.Equal(expectedId, result.ObservedIdentity?.AccountId);
        Assert.Contains(result.Output, line => IsObservedId(line, expectedId!, windows));

        var wrongPin = credential.Clone();
        wrongPin.ExpectedAccountId = windows ? "S-1-5-18" : "0";
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunShellCommandAsync(payload, ct, observer with { Credential = wrongPin }));
        if (windows)
        {
            var wrongPassword = credential.Clone();
            wrongPassword.Password += "A!";
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunShellCommandAsync(payload, ct, observer with { Credential = wrongPassword }));
        }

        var factory = new TerminalHostFactory(new());
        using var host = Assert.Single(factory.CreateCandidates(new NetRatel.Client.Service.Terminal.Terminal.TerminalOpenPayload
            { ShellType = windows ? "cmd" : "bash" }, credential));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var identityPrinted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new StringBuilder();
        var localWindowsAccount = $"{Environment.MachineName}\\{username}";
        await host.StartAsync(chunk =>
        {
            lock (recorded)
            {
                var transcript = recorded.Append(chunk.Data).ToString();
                if (windows ? transcript.Contains(localWindowsAccount, StringComparison.OrdinalIgnoreCase) :
                    transcript.Contains($"netratel-id={expectedId}\r\n", StringComparison.Ordinal))
                    identityPrinted.TrySetResult();
            }
            return Task.CompletedTask;
        }, deadline.Token);
        try
        {
            Assert.Equal(expectedId, host.ObservedIdentity?.AccountId);
            // The native observation verifies the SID. The short whoami output also verifies
            // that the child inherits the same account and sends its output through ConPTY.
            await host.WriteInputAsync(windows ? "whoami\r" : "printf 'netratel-id=%s\\n' \"$(id -u)\"\n", deadline.Token);
            try { await identityPrinted.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token); }
            catch (TimeoutException)
            {
                lock (recorded) Assert.Fail($"The selected identity did not appear in the terminal output: {recorded}");
            }
        }
        finally { await host.RequestCloseAsync("test complete", ct); }

        var unchanged = await runner.RunShellCommandAsync(payload, ct);
        Assert.True(unchanged.Success);
        Assert.Contains(unchanged.Output, line => IsObservedId(line, windows ? "S-1-5-18" : "0", windows));
    }

    private static bool IsObservedId(string line, string id, bool windows) => windows
        ? line.Contains($"\"{id}\"", StringComparison.Ordinal) : line.Trim() == id;
}
