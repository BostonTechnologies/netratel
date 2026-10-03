using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Application.Artifacts;
using NetRatel.API.Services;
using NetRatel.Client;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Shared;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

// These execute isolated functions from the rendered installer. They never run
// its entry point, enroll a client, contact an endpoint, or change an OS service.
public sealed class WindowsInstallerTemplateTests
{
    [Fact]
    public async Task RenderedDiagnosticsKeepFixedAuthMessagesAndHideSecretCanaries()
    {
        await WithFixture(async root =>
        {
            var result = await RunFunctions(root, """
                $EnrollmentCode = 'SYNTHETIC-CANARY-GRANT'
                $benign = @(
                    '[Auth] Enrollment is required before starting the service. Run NetRatel.Client --enroll <code> --api <url>. Exiting.',
                    '[Auth] Ignoring netratel.enroll.json: malformed JSON syntax.',
                    '[Auth] Ignoring netratel.enroll.json: invalid field/type (field=tenantId; expected=Int32; actual=String).',
                    '[Auth] Ignoring netratel.enroll.json: unable to read enrollment file (I/O failure).'
                )
                foreach ($message in $benign) {
                    if ((Get-SafeDiagnosticLine $message) -cne $message) { throw 'A fixed Auth diagnostic was hidden.' }
                    if ((Get-SafeDiagnosticLine ('2026-10-02 10:20:30.123 +02:00 - ' + $message)) -cne $message) { throw 'Production timestamp affected safe diagnostics.' }
                }
                $unsafe = @(
                    'Bearer SYNTHETIC-CANARY-AUTH',
                    'refreshToken=SYNTHETIC-CANARY-REFRESH',
                    'password=SYNTHETIC-CANARY-PASSWORD',
                    'grant=SYNTHETIC-CANARY-CAPABILITY',
                    'https://user:SYNTHETIC-CANARY-URL@api.example/path?value=SYNTHETIC-CANARY-QUERY',
                    'plain SYNTHETIC-CANARY-GRANT',
                    '[Auth] Ignoring netratel.enroll.json: invalid field/type (field=SYNTHETIC-CANARY-FIELD; expected=Int32; actual=String).',
                    ($benign[0] + ' SYNTHETIC-CANARY-SUFFIX'),
                    ('SYNTHETIC-CANARY-PREFIX ' + $benign[0])
                )
                foreach ($line in $unsafe) { Write-Output (Get-SafeDiagnosticLine $line) }
                foreach ($message in $benign) { Write-Output (Get-SafeDiagnosticLine $message) }
                """);
            result.ExitCode.Should().Be(0, result.Output);
            result.Output.Should().NotContain("SYNTHETIC-CANARY");
            result.Output.Should().Contain("Enrollment is required before starting the service")
                .And.Contain("field=tenantId; expected=Int32; actual=String")
                .And.Contain("malformed JSON syntax");
        });
    }

    [Fact]
    public async Task RenderedRollbackPreservesOriginalStartupFailureAfterNestedFailures()
    {
        await WithFixture(async root =>
        {
            var result = await RunFunctions(root, """
                $clauses = @($ast.FindAll({ param($node)
                    $node -is [Management.Automation.Language.CatchClauseAst] -and
                    $node.Body.Extent.Text.Contains('$startupFailure = $_')
                }, $true))
                if ($clauses.Count -ne 1) { throw 'The actual startup catch was not selected.' }
                function Write-Diagnostics { try { throw 'synthetic diagnostic failure' } catch {} }
                function Set-ServiceState { return }
                function Invoke-ServiceControl { throw 'synthetic rollback failure' }
                $stopAttempted = $true
                $cutover = $true
                $script:serviceControlExited = $true
                $InstallAsService = $true
                $created = $true
                $serviceName = 'synthetic-offline-service'
                $failure = [InvalidOperationException]::new('synthetic startup exit 78')
                try {
                    & ([scriptblock]::Create('try { throw $failure } ' + $clauses[0].Extent.Text))
                }
                catch {
                    if ($_.Exception -ne $failure -or $_.Exception.Message -ne 'synthetic startup exit 78') { throw 'Startup cause was replaced.' }
                    Write-Output 'Original startup exit 78 preserved.'
                    exit 0
                }
                throw 'Startup failure became success.'
                """);
            result.ExitCode.Should().Be(0, result.Output);
            result.Output.Should().Contain("Original startup exit 78 preserved.");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("category", "hosted")]
    public async Task CleanWindowsPowerShellResolvesProductionServiceControllerTypesWithoutServiceMutation(bool seeded)
    {
        await WithFixture(async root =>
        {
            var result = await RunRenderedFunctions(root, ServiceControllerScript(seeded), """
                if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
                    throw 'This regression requires native Windows PowerShell 5.1.'
                }
                Initialize-ServiceController
                if ([System.ServiceProcess.ServiceController].Assembly.GetName().Name -ne 'System.ServiceProcess') { throw 'Framework assembly not loaded.' }
                if ([System.ServiceProcess.ServiceControllerStatus]::Running -ne 4) { throw 'Status enum unresolved.' }
                """, "powershell.exe");
            result.ExitCode.Should().Be(0, result.Output);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceControllerAssemblyFailurePreservesCauseBeforeServiceMutation(bool seeded)
    {
        await WithFixture(async root =>
        {
            var result = await RunRenderedFunctions(root, ServiceControllerScript(seeded), """
                function Add-Type { throw [IO.FileNotFoundException]::new('synthetic assembly unavailable') }
                function Set-ServiceState { throw 'Service mutation must not run.' }
                try { Initialize-ServiceController; Set-ServiceState 'Stopped' }
                catch {
                    if ($_.Exception.Message -notmatch 'Repair the Windows .NET Framework') { throw }
                    if ($_.Exception.InnerException.Message -ne 'synthetic assembly unavailable') { throw 'Original assembly failure was lost.' }
                    exit 0
                }
                throw 'Assembly initialization unexpectedly succeeded.'
                """);
            result.ExitCode.Should().Be(0, result.Output);
        });
    }

    private static string ServiceControllerScript(bool seeded)
    {
        if (!seeded) return new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            2, "win-x64", "ENR-SYNTHETIC", "https://api.example", DateTimeOffset.UtcNow.AddHours(1), true, true));
        var seed = typeof(AgentUpdateScriptSeedService).GetField("WindowsScript", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var content = (string)seed.GetType().GetProperty("Content")!.GetValue(seed)!;
        var manifestEnd = content.IndexOf("#| END", StringComparison.Ordinal);
        return content[(manifestEnd + "#| END".Length)..].TrimStart('\r', '\n');
    }

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

    [Theory]
    [InlineData(false, false, "")]
    [InlineData(false, true, "")]
    [InlineData(true, false, "")]
    [InlineData(false, false, "https://requested-gateway.example")]
    [InlineData(false, false, "https://new-api.example")]
    public async Task RenderedSettingsUpdatePreservesLegacyShapeAndRuntimeTunables(bool nested, bool emptyClient, string gateway)
    {
        await WithFixture(async root =>
        {
            var client = new Dictionary<string, object>
            {
                ["ApiBaseUrl"] = "https://previous-api.example",
                ["Environment"] = "Prod",
                ["TerminalGracefulExitTimeoutMs"] = 3210,
                ["AutoUpdate"] = new { StateDirectory = "C:\\owned\\state", Channel = "Preview", ActivationTimeoutSeconds = 47 }
            };
            var settings = nested ? new Dictionary<string, object> { ["Client"] = client } : new Dictionary<string, object>(client);
            if (emptyClient) settings["Client"] = new Dictionary<string, object>();
            settings["Gateway"] = new { Endpoint = "https://pinned-gateway.example" };
            var inputPath = Path.Combine(root, "before.json");
            var outputPath = Path.Combine(root, "clientsettings.json");
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(settings));
            var result = await RunFunctions(root, """
                $settings = [IO.File]::ReadAllText($args[1]) | ConvertFrom-Json
                $updated = Update-ClientSettings $settings 'https://new-api.example' $args[3]
                [IO.File]::WriteAllText($args[2], ($updated | ConvertTo-Json -Depth 32))
                """, inputPath, outputPath, gateway);
            result.ExitCode.Should().Be(0, result.Output);
            var options = ClientConfigurationLoader.Load(null, root, []);
            options.ApiBaseUrl.Should().Be("https://new-api.example");
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.TerminalGracefulExitTimeoutMs.Should().Be(3210);
            options.AutoUpdate.StateDirectory.Should().Be("C:\\owned\\state");
            options.AutoUpdate.Channel.Should().Be("Preview");
            options.AutoUpdate.ActivationTimeoutSeconds.Should().Be(47);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            document.RootElement.GetProperty("Gateway").GetProperty("Endpoint").GetString()
                .Should().Be(gateway.Length == 0 ? "https://pinned-gateway.example" : gateway);
            if (!nested && !emptyClient) document.RootElement.TryGetProperty("Client", out _).Should().BeFalse();
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
        return await RunRenderedFunctions(root, ServiceControllerScript(false), probe, arguments: arguments);
    }

    private static async Task<(int ExitCode, string Output)> RunRenderedFunctions(string root, string script, string probe, string? shell = null, params string[] arguments)
    {
        shell ??= Environment.GetEnvironmentVariable("NETRATEL_TEST_POWERSHELL")
                  ?? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var scriptPath = Path.Combine(root, "installer.ps1");
        var probePath = Path.Combine(root, "probe.ps1");
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
