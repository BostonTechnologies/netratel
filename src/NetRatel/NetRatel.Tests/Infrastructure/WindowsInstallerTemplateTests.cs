using System.Diagnostics;
using System.IO.Compression;
using FluentAssertions;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

// These execute isolated functions from the rendered installer. They never run
// its entry point, enroll a client, contact an endpoint, or change an OS service.
public sealed class WindowsInstallerTemplateTests
{
    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:/absolute.dll")]
    [InlineData("sub\\..\\escape.dll")]
    [InlineData("sub/CON.txt")]
    [InlineData("sub/alias.")]
    [InlineData("sub/alias ")]
    [InlineData("sub/file:stream")]
    public async Task RenderedArchiveExtractionRejectsUnsafePaths(string name)
    {
        await WithFixture(async root =>
        {
            CreateArchive(Path.Combine(root, "artifact.zip"), (name, 0));
            var result = await RunFunctions(root, """
                try { Expand-Artifact $args[1] $args[2] }
                catch {
                    if ($_.Exception.Message -notmatch 'unsafe or ambiguous path|escapes staging') { throw }
                    exit 0
                }
                throw 'Unsafe archive was accepted.'
                """, Path.Combine(root, "artifact.zip"), Path.Combine(root, "stage"));
            result.ExitCode.Should().Be(0, result.Output);
            Directory.EnumerateFileSystemEntries(Path.Combine(root, "stage")).Should().BeEmpty();
            File.Exists(Path.Combine(root, "escape.dll")).Should().BeFalse();
        });
    }

    [Fact]
    public async Task RenderedArchiveExtractionRejectsCaseAliasesAndLinks()
    {
        await WithFixture(async root =>
        {
            foreach (var entries in new[]
                     {
                         new[] { ("Client.dll", 0), ("client.dll", 0) },
                         new[] { ("link", unchecked((int)0xA1FF0000)) },
                         new[] { ("junction", 0x400) }
                     })
            {
                var archive = Path.Combine(root, Guid.NewGuid() + ".zip");
                CreateArchive(archive, entries);
                var result = await RunFunctions(root, """
                    try { Expand-Artifact $args[1] $args[2] }
                    catch {
                        if ($_.Exception.Message -notmatch 'unsafe or ambiguous path') { throw }
                        exit 0
                    }
                    throw 'Ambiguous archive was accepted.'
                    """, archive, Path.Combine(root, "stage"));
                result.ExitCode.Should().Be(0, result.Output);
            }
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("netratel-client-win-x64/")]
    public async Task RenderedArchiveExtractionKeepsVerifiedFlatAndWrappedContents(string prefix)
    {
        await WithFixture(async root =>
        {
            var archive = Path.Combine(root, "artifact.zip");
            CreateArchive(archive, (prefix + "NetRatel.Client.exe", 0), (prefix + "updater/netratel-update.ps1", 0));
            var result = await RunFunctions(root, "Expand-Artifact $args[1] $args[2]", archive, Path.Combine(root, "stage"));
            result.ExitCode.Should().Be(0, result.Output);
            File.ReadAllText(Path.Combine(root, "stage", prefix, "NetRatel.Client.exe")).Should().Be("synthetic artifact");
            File.ReadAllText(Path.Combine(root, "stage", prefix, "updater", "netratel-update.ps1")).Should().Be("synthetic artifact");
        });
    }

    [Fact]
    public async Task RenderedOriginsRejectCredentialsAndCapabilityUrls()
    {
        await WithFixture(async root =>
        {
            var result = await RunFunctions(root, """
                if ((Get-Origin 'https://api.example/api/') -ne 'https://api.example') { throw 'API composition changed.' }
                if ((Get-Origin 'http://localhost:8080') -ne 'http://localhost:8080') { throw 'Local REST API origin changed.' }
                if ((Get-Origin 'https://gateway.example' -Gateway) -ne 'https://gateway.example') { throw 'Gateway origin changed.' }
                foreach ($url in @('https://user:secret@api.example', 'https://api.example/install/capability', 'https://api.example?grant=secret', 'https://api.example#secret')) {
                    $rejected = $false
                    try { Get-Origin $url | Out-Null } catch { $rejected = $true }
                    if (-not $rejected) { throw 'An unsafe origin was accepted.' }
                }
                $rejected = $false
                try { Get-Origin 'http://gateway.example' -Gateway | Out-Null } catch { $rejected = $true }
                if (-not $rejected) { throw 'Gateway TLS was weakened.' }
                """);
            result.ExitCode.Should().Be(0, result.Output);
        });
    }

    [Fact]
    public async Task RenderedDeploymentLookupHonorsDirectAliasesAndRejectsAmbiguousAliases()
    {
        await WithFixture(async root =>
        {
            var result = await RunFunctions(root, """
                $previousEnvironment = @('NetRatelCLIENT__Gateway__Endpoint=https://prefixed.example', 'gAtEwAy__EnDpOiNt=https://direct.example')
                if ((Get-ClientSetting 'Gateway__Endpoint') -ne 'https://direct.example') { throw 'The ordinary environment override was ignored.' }
                if ($script:settingSource -ne 'service environment') { throw 'The diagnostic source was incorrect.' }
                $previousEnvironment = @('NetRatelCLIENT__Client__AutoUpdate__StateDirectory=C:\owned\state')
                if ((Get-ClientSetting 'Client__AutoUpdate__StateDirectory') -ne 'C:\owned\state') { throw 'The supported prefixed state path was ignored.' }
                $previousEnvironment = @('Client__AutoUpdate__RequestPath=C:\owned\request.json', 'Client:AutoUpdate:RequestPath=C:\other\request.json')
                $rejected = $false
                try { Get-ClientSetting 'Client__AutoUpdate__RequestPath' | Out-Null } catch { $rejected = $true }
                if (-not $rejected) { throw 'Ambiguous updater environment aliases were accepted.' }
                """);
            result.ExitCode.Should().Be(0, result.Output);
        });
    }

    private static async Task WithFixture(Func<string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-windows-function-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "stage"));
        try { await body(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CreateArchive(string path, params (string Name, int Attributes)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, attributes) in entries)
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = attributes;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("synthetic artifact");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunFunctions(string root, string probe, params string[] arguments)
    {
        var shell = Environment.GetEnvironmentVariable("NETRATEL_TEST_POWERSHELL")
                    ?? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var scriptPath = Path.Combine(root, "installer.ps1");
        var probePath = Path.Combine(root, "probe.ps1");
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            2, "win-x64", "ENR-SYNTHETIC", "https://api.example", DateTimeOffset.UtcNow.AddHours(1), true, true));
        await File.WriteAllTextAsync(scriptPath, script);
        await File.WriteAllTextAsync(probePath, """
            $ErrorActionPreference = 'Stop'
            $source = [IO.File]::ReadAllText($args[0])
            $tokens = $null
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
                Invoke-Expression $function.Extent.Text
            }
            """ + Environment.NewLine + probe);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", probePath, scriptPath }.Concat(arguments))
            start.ArgumentList.Add(value);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("An available PowerShell parser is required for isolated installer-function tests."); throw; }
        using (process)
        {
            process.Should().NotBeNull();
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }
    }
}
