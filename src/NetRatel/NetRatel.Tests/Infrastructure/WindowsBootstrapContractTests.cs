using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using NetRatel.API.Services;
using NetRatel.Application.Artifacts;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service.Auth;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class WindowsBootstrapContractTests
{
    private const string ApiBase = "https://bootstrap.example.invalid";
    private const string EnrollmentCode = "synthetic-bootstrap-'quoted'-${no-expansion}";

    // The native Windows package lane selects this generic test explicitly.
    // The probe executes only four actual initializers and the actual serializer;
    // installer entry points, service control, downloads and HTTP never run.
    [Theory]
    [InlineData(false, 1, "en-US")]
    [InlineData(false, 2, "de-DE")]
    [InlineData(false, int.MaxValue, "ar-SA")]
    [InlineData(true, 1, "en-US")]
    [InlineData(true, 2, "de-DE")]
    [InlineData(true, int.MaxValue, "ar-SA")]
    [Trait("category", "hosted")]
    public async Task RenderedWindowsBootstrapEnrollsThroughStrictConsumer(bool seeded, int tenantId, string culture)
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Render(seeded, tenantId, culture);
            Console.WriteLine("Rendered producer ({0}; culture={1}): {2} lines, {3} UTF-8 bytes, {4} functions.",
                seeded ? "seeded" : "normal", culture, script.Count(character => character == '\n') + 1,
                Encoding.UTF8.GetByteCount(script), Regex.Matches(script, @"(?m)^function\s").Count);
            var outputPath = Path.Combine(root, "netratel.enroll.json");
            var shell = OperatingSystem.IsWindows() ? "powershell.exe"
                : Environment.GetEnvironmentVariable("NETRATEL_TEST_POWERSHELL") ?? "pwsh";
            var result = await SerializeBootstrap(root, script, seeded, tenantId, culture, shell);
            result.ExitCode.Should().Be(0, result.Output);
            var json = await File.ReadAllTextAsync(outputPath);
            using var document = JsonDocument.Parse(json);
            var tenant = document.RootElement.GetProperty("tenantId");
            var events = new List<string>();
            var fileSystem = new RecordingFileSystem(events);
            var enrollment = new RecordingEnrollment(events, outputPath);
            var store = new RecordingCredentialStore(events, outputPath);
            var diagnostics = new List<string>();
            var bootstrap = new InjectedEnrollmentBootstrap(fileSystem, () => root, diagnostics.Add);

            var credentials = await bootstrap.TryEnrollAsync(
                new ClientOptions { ApiBaseUrl = ApiBase }, enrollment, store, CancellationToken.None);
            Console.WriteLine("{0}; tenantId JSON kind={1}; expected Int32={2}; strict consumer accepted={3}; calls={4}.",
                result.Output.Trim(), tenant.ValueKind, tenantId, credentials is not null, string.Join(",", events));

            using (new AssertionScope())
            {
                tenant.ValueKind.Should().Be(JsonValueKind.Number, "the actual installer must produce the strict consumer's integer contract");
                (tenant.ValueKind == JsonValueKind.Number && tenant.TryGetInt32(out var value) && value == tenantId)
                    .Should().BeTrue("tenantId must preserve the intended positive Int32 under {0}", culture);
                credentials.Should().Be(("synthetic-agent", "synthetic-refresh"));
                enrollment.Code.Should().Be(EnrollmentCode);
                store.Saved.Should().Be(credentials);
                events.Should().Equal("read", "enroll", "persist", "delete");
                File.Exists(outputPath).Should().BeFalse();
                diagnostics.Should().BeEmpty();
            }

            await AssertStrictRejections(root, json);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Render(bool seeded, int tenantId, string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                tenantId, "win-x64", EnrollmentCode, ApiBase, DateTimeOffset.UtcNow.AddHours(1),
                InstallAsService: true, SilentInstall: true, IsUpdateSeed: seeded));
            if (!seeded) return script;
            // The actual seed parameter binding and origin helper are part of the
            // producer seam; no hand-written replacements for seed expressions.
            var preamble = (string)typeof(AgentUpdateScriptSeedService)
                .GetField("WindowsHandoffPreamble", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue()!;
            return preamble + Environment.NewLine + script;
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static async Task<(int ExitCode, string Output)> SerializeBootstrap(
        string root, string script, bool seeded, int tenantId, string culture, string shell)
    {
        var scriptPath = Path.Combine(root, "rendered-installer.ps1");
        var inputPath = Path.Combine(root, "synthetic-input.json");
        var probePath = Path.Combine(root, "serialize-probe.ps1");
        var outputPath = Path.Combine(root, "netratel.enroll.json");
        await File.WriteAllTextAsync(scriptPath, script);
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new { seeded, tenantId, culture, apiBase = ApiBase, enrollmentCode = EnrollmentCode }));
        await File.WriteAllTextAsync(probePath, """
            $ErrorActionPreference = 'Stop'
            if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and
                ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1)) {
                throw 'Native Windows PowerShell 5.1 is required on Windows.'
            }
            $inputData = [IO.File]::ReadAllText($args[1]) | ConvertFrom-Json
            [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($inputData.culture)
            $tokens = $null
            $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($args[0]), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            $initializers = @($ast.EndBlock.Statements | Where-Object {
                if ($_ -isnot [Management.Automation.Language.AssignmentStatementAst]) { return $false }
                $left = $_.Left
                while ($left -is [Management.Automation.Language.ConvertExpressionAst]) { $left = $left.Child }
                return $left -is [Management.Automation.Language.VariableExpressionAst] -and
                    $left.VariablePath.UserPath -in @('ApiBase', 'TenantId', 'EnrollmentCode', 'ValidToUtc')
            })
            if ($initializers.Count -ne 4) { throw 'Expected exactly four actual bootstrap initializers.' }
            $serializers = @($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq 'enrollment'
            }, $true))
            if ($serializers.Count -ne 1) { throw 'Expected exactly one actual bootstrap serialization assignment.' }
            $selected = ''
            if ($inputData.seeded) {
                if (-not $ast.ParamBlock) { throw 'The actual seed parameter block is missing.' }
                $selected += $ast.ParamBlock.Extent.Text + [Environment]::NewLine
                $helper = @($ast.FindAll({ param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-NetRatelSeedApiBase'
                }, $false))
                if ($helper.Count -ne 1) { throw 'The actual seed API origin helper is missing.' }
                $selected += $helper[0].Extent.Text + [Environment]::NewLine
            }
            foreach ($initializer in $initializers) { $selected += $initializer.Extent.Text + [Environment]::NewLine }
            $selected += $serializers[0].Extent.Text + [Environment]::NewLine + '$enrollment'
            $serialization = [scriptblock]::Create($selected)
            if ($inputData.seeded) {
                $json = & $serialization -ApiBase $inputData.apiBase -TenantId $inputData.tenantId -EnrollmentCode $inputData.enrollmentCode
            } else { $json = & $serialization }
            [IO.File]::WriteAllText($args[2], [string]$json)
            Write-Output ('Serialization runtime: ' + $PSVersionTable.PSEdition + ' ' + $PSVersionTable.PSVersion)
            """);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", probePath, scriptPath, inputPath, outputPath })
            start.ArgumentList.Add(value);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("An available PowerShell parser is required for the isolated bootstrap contract test."); throw; }
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

    private static async Task AssertStrictRejections(string root, string generatedJson)
    {
        var invalid = new (string Name, Action<JsonObject> Change)[]
        {
            ("quoted tenantId", node => node["tenantId"] = "2"),
            ("invalid tenantId", node => node["tenantId"] = "synthetic-invalid"),
            ("overflow tenantId", node => node["tenantId"] = (long)int.MaxValue + 1),
            ("fractional tenantId", node => node["tenantId"] = 1.5),
            ("boolean tenantId", node => node["tenantId"] = true),
            ("null tenantId", node => node["tenantId"] = null),
            ("missing tenantId", node => node.Remove("tenantId")),
            ("zero tenantId", node => node["tenantId"] = 0),
            ("negative tenantId", node => node["tenantId"] = -1),
            ("issuer mismatch", node => node["issuer"] = "https://other.example.invalid"),
            ("expired payload", node => node["validToUtc"] = "2000-01-01T00:00:00Z"),
            ("missing expiry", node => node.Remove("validToUtc"))
        };
        foreach (var (name, change) in invalid)
        {
            var payload = JsonNode.Parse(generatedJson)!.AsObject();
            change(payload);
            await AssertRejected(root, payload.ToJsonString(), name);
        }
        await AssertRejected(root, "{", "malformed syntax");
    }

    private static async Task AssertRejected(string root, string json, string reason)
    {
        var path = Path.Combine(root, "netratel.enroll.json");
        await File.WriteAllTextAsync(path, json);
        var events = new List<string>();
        var enrollment = new RecordingEnrollment(events, path);
        var store = new RecordingCredentialStore(events, path);
        var bootstrap = new InjectedEnrollmentBootstrap(new RecordingFileSystem(events), () => root, _ => { });
        var result = await bootstrap.TryEnrollAsync(new ClientOptions { ApiBaseUrl = ApiBase }, enrollment, store, CancellationToken.None);
        using (new AssertionScope())
        {
            result.Should().BeNull(reason);
            enrollment.Code.Should().BeNull(reason);
            store.Saved.Should().BeNull(reason);
            events.Should().Equal(new[] { "read" }, reason);
            File.Exists(path).Should().BeTrue(reason);
        }
    }

    private sealed class RecordingFileSystem(List<string> events) : IInjectedEnrollmentFileSystem
    {
        public bool Exists(string path) => File.Exists(path);
        public Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
            events.Add("read");
            return File.ReadAllTextAsync(path, ct);
        }
        public void Delete(string path) { events.Add("delete"); File.Delete(path); }
    }

    private sealed class RecordingEnrollment(List<string> events, string path) : IAgentEnrollmentService
    {
        public string? Code { get; private set; }
        public Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct)
        {
            File.Exists(path).Should().BeTrue("bootstrap is retained until credentials have persisted");
            Code = enrollmentCode;
            events.Add("enroll");
            return Task.FromResult(("synthetic-agent", "synthetic-refresh"));
        }
    }

    private sealed class RecordingCredentialStore(List<string> events, string path) : IAgentCredentialStore
    {
        public (string AgentId, string RefreshToken)? Saved { get; private set; }
        public Task SaveAsync(string agentId, string refreshToken)
        {
            File.Exists(path).Should().BeTrue("bootstrap deletion follows successful credential persistence");
            events.Add("persist");
            Saved = (agentId, refreshToken);
            return Task.CompletedTask;
        }
        public Task<(string AgentId, string RefreshToken)?> LoadAsync() => Task.FromResult<(string AgentId, string RefreshToken)?>(null);
        public Task ClearRefreshCredentialsAsync() => Task.CompletedTask;
        public Task ResetInstallationIdentityAsync() => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
