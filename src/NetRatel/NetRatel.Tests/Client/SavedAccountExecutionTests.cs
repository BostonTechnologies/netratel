using System.Text;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Shells;
using NetRatel.Client.Service.Terminal;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Service.Shells;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class SavedAccountExecutionTests(ITestOutputHelper output)
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
            await Assert.ThrowsAsync<WindowsExecutionAccount.LogonRejectedException>(() => runner.RunShellCommandAsync(payload, ct, observer with { Credential = wrongPassword }));
            await VerifyPowerShellAsync(runner, observer, ShellExecutor.WindowsPowerShell, "Desktop", 5, expectedId, ct);
            await VerifyPowerShellAsync(runner, null, ShellExecutor.WindowsPowerShell, "Desktop", 5, "S-1-5-18", ct);
            await VerifyPowerShellAsync(runner, observer, ShellExecutor.Pwsh, "Core", 7, expectedId, ct);
            await VerifyPowerShellAsync(runner, observer, ShellExecutor.Auto, "Core", 7, expectedId, ct);
            var desktopOnly = new ExternalShellRunner(null, keyword => keyword == "pwsh" ? null : ShellExecutableResolver.Resolve(keyword));
            await VerifyPowerShellAsync(desktopOnly, observer, ShellExecutor.Auto, "Desktop", 5, expectedId, ct);
            var missing = new ExecLibraryScriptPayload { ScriptType = ScriptType.PowerShell, Preferred = ShellExecutor.Pwsh };
            await Assert.ThrowsAsync<ExternalShellRunner.RequiredShellUnavailableException>(() =>
                desktopOnly.RunLibraryScriptAsync(missing, "Write-Output 'unexpected'", ct, null, observer));
            await VerifyPowerShellRequirementAsync(runner, observer, ShellExecutor.WindowsPowerShell, "#requires -PSEdition Core", ct);
            await VerifyPowerShellRequirementAsync(runner, observer, ShellExecutor.Pwsh, "#requires -Version 99", ct);
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

    private async Task VerifyPowerShellAsync(ExternalShellRunner runner, ExternalShellRunner.ExecutionObserver? observer,
        ShellExecutor executor, string edition, int major, string accountId, CancellationToken ct)
    {
        const string script = "[System.Security.Principal.WindowsIdentity]::GetCurrent().Name; $PSVersionTable.PSVersion.ToString(); $PSVersionTable.PSEdition; [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value";
        var payload = new ExecLibraryScriptPayload { ScriptType = ScriptType.PowerShell, Preferred = executor, TimeoutSeconds = 15 };
        var result = await runner.RunLibraryScriptAsync(payload, script, ct, null, observer);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Error));
        Assert.Equal(accountId, result.Output[3]);
        if (observer?.Credential is not null)
        {
            Assert.Equal(accountId, result.ObservedIdentity?.AccountId);
            Assert.Equal(result.ObservedIdentity?.Account, result.Output[0], ignoreCase: true);
        }
        var version = Version.Parse(result.Output[1]);
        Assert.Equal(major, version.Major);
        Assert.Equal(edition, result.Output[2]);
        output.WriteLine($"Requested {executor}, observed {edition} {version}, account {result.Output[0]}");
    }

    private static async Task VerifyPowerShellRequirementAsync(ExternalShellRunner runner, ExternalShellRunner.ExecutionObserver observer,
        ShellExecutor executor, string requirement, CancellationToken ct)
    {
        var payload = new ExecLibraryScriptPayload { ScriptType = ScriptType.PowerShell, Preferred = executor, TimeoutSeconds = 15 };
        var result = await runner.RunLibraryScriptAsync(payload, requirement + "\nWrite-Output 'unexpected'", ct, null, observer);
        Assert.True(result.ProcessStarted);
        Assert.Equal(125, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(result.Error, line => line.Contains("does not satisfy the script edition or version requirements", StringComparison.Ordinal));
    }

    private static bool IsObservedId(string line, string id, bool windows) => windows
        ? line.Contains($"\"{id}\"", StringComparison.Ordinal) : line.Trim() == id;
}
