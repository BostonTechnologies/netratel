using NetRatel.Client.Service.Shells;
using NetRatel.Client.Service.Terminal;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Service.ClientEnvironment;
using NetRatel.Shared.Service.Shells;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ShellResolutionTests
{
    [Theory]
    [InlineData(".ps1", true, false)]
    [InlineData(".ps1", true, true)]
    [InlineData(".json", false, false)]
    public void Private_powershell_scripts_preserve_unicode_with_one_utf8_bom(string extension, bool expectedBom, bool inputBom)
    {
        const string contents = "'Unicode: λ 日本語'";
        var path = ExternalShellRunner.WritePrivateTemp(extension, inputBom ? "\uFEFF" + contents : contents);
        try
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(expectedBom, bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            Assert.False(bytes.AsSpan(3).StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
            Assert.Equal(contents, File.ReadAllText(path));
        }
        finally { ExternalShellRunner.TryDeletePrivateTemp(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_known_interpreters_resolve_with_empty_or_partially_populated_service_path(bool partialPath)
    {
        var windows = Path.GetFullPath("test-windows");
        var programs = Path.GetFullPath("test-program-files");
        var restrictedPath = Path.GetFullPath("test-restricted-path");
        var pwsh = Path.Combine(programs, "PowerShell", "7", "pwsh.exe");
        var powershell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var bash = Path.Combine(restrictedPath, "bash.exe");
        var installed = new HashSet<string> { pwsh, powershell, bash };
        var environment = new ShellExecutableResolver.SearchEnvironment(
            IsWindows: true, partialPath ? [restrictedPath] : [], windows, programs);

        Assert.Equal(pwsh, ShellExecutableResolver.Resolve("pwsh", environment, installed.Contains));
        Assert.Equal(powershell, ShellExecutableResolver.Resolve("powershell", environment, installed.Contains));
        Assert.Equal(partialPath ? bash : null, ShellExecutableResolver.Resolve("bash", environment, installed.Contains));
    }

    [Fact]
    public void Unix_pwsh_on_path_resolves_without_accepting_a_windows_powershell_substitute()
    {
        var directory = Path.GetFullPath("test-unix-service-path");
        var pwsh = Path.Combine(directory, "pwsh");
        var environment = new ShellExecutableResolver.SearchEnvironment(false, [directory]);
        var installed = new HashSet<string> { pwsh, Path.Combine(directory, "powershell.exe") };

        Assert.Equal(pwsh, ShellExecutableResolver.Resolve("pwsh", environment, installed.Contains));
        Assert.Null(ShellExecutableResolver.Resolve("powershell", environment, installed.Contains));
    }

    [Theory]
    [InlineData(ShellExecutor.Pwsh, "powershell")]
    [InlineData(ShellExecutor.WindowsPowerShell, "pwsh")]
    public void Explicit_missing_engine_never_substitutes_the_other_engine(ShellExecutor requested, string installed)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ExternalShellRunner.ResolvePowerShellHost(
            requested, "Write-Output 'marker'", keyword => keyword == installed ? "/installed/" + keyword : null, isWindows: true));
        Assert.Contains("Required shell not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_powershell_is_rejected_on_unix_even_if_an_executable_has_that_name()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ExternalShellRunner.ResolvePowerShellHost(
            ShellExecutor.WindowsPowerShell, "", keyword => "/installed/" + keyword, isWindows: false));
        Assert.Contains("requires Windows", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#requires -PSEdition Desktop\r\nWrite-Output 'marker'", "powershell")]
    [InlineData("\uFEFF#requires -PSEdition Core\nWrite-Output 'marker'", "pwsh")]
    [InlineData("#requires -Version 7.0\nWrite-Output 'marker'", "pwsh")]
    [InlineData("Write-Output 'marker'\n#requires -PSEdition Desktop", "powershell")]
    [InlineData("<#\n#requires -PSEdition Desktop\n#>\nWrite-Output 'marker'", "pwsh")]
    [InlineData("<# nested <#\n#requires -PSEdition Desktop\n#> outer #>\nWrite-Output 'marker'", "pwsh")]
    [InlineData("@'\n#requires -PSEdition Desktop\n'@", "pwsh")]
    [InlineData("Write-Output '\n#requires -PSEdition Desktop\n'", "pwsh")]
    public void PowerShell_auto_honors_requirements_and_ignores_comment_or_string_contents(string script, string expected)
    {
        Assert.Equal(expected, ExternalShellRunner.ResolvePowerShellHost(
            ShellExecutor.Auto, script, keyword => keyword, isWindows: true));
    }

    [Theory]
    [InlineData("Write-Output 'marker'", "powershell")]
    [InlineData("#requires -Version 5.1.100\nWrite-Output 'marker'", "powershell")]
    [InlineData("#requires -PSEdition Core\nWrite-Output 'marker'", null)]
    [InlineData("#requires -Version 7.0\nWrite-Output 'marker'", null)]
    public void PowerShell_auto_windows_fallback_requires_compatible_engine_requirements(string script, string? expected)
    {
        string? Resolve(string keyword) => keyword == "powershell" ? keyword : null;
        if (expected is null)
            Assert.Throws<InvalidOperationException>(() => ExternalShellRunner.ResolvePowerShellHost(ShellExecutor.Auto, script, Resolve, isWindows: true));
        else
            Assert.Equal(expected, ExternalShellRunner.ResolvePowerShellHost(ShellExecutor.Auto, script, Resolve, isWindows: true));
    }

    [Fact]
    public void Reported_shells_inventory_and_terminal_launches_resolve_the_same_executables()
    {
        var inventory = new ShellInventory();
        foreach (var shell in ShellInventoryDetector.Detect())
        {
            Assert.Equal(shell.Path, ShellExecutableResolver.Resolve(shell.Keyword));
            Assert.Equal(shell.Path, inventory.Find(shell.Keyword)?.Path);
            Assert.Equal(shell.Path, TerminalHostFactory.ResolveExecutable(shell.Keyword));
        }
    }
}
