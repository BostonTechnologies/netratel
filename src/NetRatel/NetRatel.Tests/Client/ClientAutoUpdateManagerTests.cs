using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using AwesomeAssertions;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Updates;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientUpdateVersioningTests
{
    [Theory]
    [InlineData("0.4.10", "0.4.9", true)]
    [InlineData("0.4.10+build.2", "0.4.9", true)]
    [InlineData("0.4.10", "0.4.10", false)]
    [InlineData("0.4.9", "0.4.10", false)]
    [InlineData("0.4.102", "0.4.102-rc.1", true)]
    [InlineData("0.4.102-rc.3", "0.4.102-rc.1", true)]
    [InlineData("0.4.102-rc.1", "0.4.102", false)]
    [InlineData("0.1.0-rc.1", "0.5.6-rc.5", false)]
    [InlineData("not-a-version", "0.4.10", false)]
    public void IsNewerVersion_UsesSemanticCore(string candidate, string current, bool expected)
    {
        ClientUpdateVersioning.IsNewerVersion(candidate, current).Should().Be(expected);
    }

    [Fact]
    public void ResolveRuntimeId_UsesConfiguredValue_WhenPresent()
    {
        ClientUpdateVersioning.ResolveRuntimeId("LINUX-X64").Should().Be("linux-x64");
    }

    [Fact]
    public void AkkaCoordinator_IsTheOnlyClientUpdateTransport()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var coordinator = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Updates/AkkaClientAutoUpdateCoordinator.cs"));

        File.Exists(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Updates/ClientAutoUpdateManager.cs")).Should().BeFalse();
        coordinator.Should().Contain("StartUpdater");
        coordinator.Should().NotContain("Spacetime");
    }

    [Theory]
    [InlineData("0.4.121-rc.1+9a5b48a39f279ae20e320e2d7f847a057ef28d3c", "0.4.121-rc.1")]
    [InlineData("0.4.121-rc.1", "0.4.121-rc.1")]
    public void PublishedPrereleaseVersion_NormalizesWithoutUsingAssemblyVersion(string informationalVersion, string expected)
    {
        ClientUpdateVersioning.NormalizePublishedVersion(informationalVersion).Should().Be(expected);
    }

    [Fact]
    public async Task PresenceClient_SendsImmediateActivationHeartbeatBeforeExtensions()
    {
        var clock = new GatewayPresenceTestClock();
        var fixture = new ActivationPresenceFixture(clock);
        using var stopping = new CancellationTokenSource();
        var extensionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentGatewayPresenceClient(new GatewayClientOptions { Endpoint = "https://gateway.test" },
            fixture, 7, Guid.NewGuid(), "activation-test", [], _ => { },
            runForPresenceSession: (session, _, token) =>
            {
                fixture.Events.Should().ContainInOrder("accepted", "confirmation");
                session.ConnectionId.Should().Be(fixture.ConnectionId);
                session.ConnectionEpoch.Should().Be(fixture.ConnectionEpoch);
                fixture.Events.Enqueue("extension");
                extensionStarted.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }, updateHandler: fixture, timeProvider: clock, createCall: fixture.Open);

        var run = agent.RunAsync(stopping.Token);
        try
        {
            var heartbeat = await fixture.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3));
            clock.GetTimestamp().Should().Be(0, "activation must not wait for the periodic heartbeat interval");
            heartbeat.Sequence.Should().Be(1);
            fixture.ActivationHello!.AttemptId.Should().Be(fixture.AttemptId);
            fixture.Events.Should().Equal("connected", "sent", "heartbeat-written");
            extensionStarted.Task.IsCompleted.Should().BeFalse("admission alone does not prove activation readiness");

            fixture.AcceptFirstHeartbeat(heartbeat);
            await extensionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Events.Should().Equal("connected", "sent", "heartbeat-written", "accepted", "confirmation", "extension");
            fixture.AcknowledgementFailures.Should().BeEmpty();
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void AkkaCoordinator_Closes_The_Staged_Package_Before_It_Is_Renamed()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var coordinator = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Updates/AkkaClientAutoUpdateCoordinator.cs"));

        coordinator.Should().Contain(
            "await using (var destination = new FileStream(temporaryPackagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))");
        coordinator.Should().Contain("await CopyOfferedArtifactAsync(source, destination, exactSizeBytes, cancellationToken)");
        coordinator.Should().Contain("ValidateManifest(temporaryPackagePath, version, runtimeId);");
        var manifestVerification = coordinator.IndexOf("ValidateManifest(temporaryPackagePath, version, runtimeId);", StringComparison.Ordinal);
        var replacement = coordinator.IndexOf("File.Move(temporaryPackagePath, packagePath, true);", StringComparison.Ordinal);
        manifestVerification.Should().BeGreaterThanOrEqualTo(0);
        replacement.Should().BeGreaterThan(manifestVerification);
    }

    [Fact]
    public void WindowsUpdater_Sets_And_Verifies_The_Service_ImagePath_Without_ScExe_Quoting()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var updater = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"));

        updater.Should().Contain("function Set-NetRatelServiceImagePath");
        updater.Should().Contain("Set-ItemProperty -Path $serviceKey -Name ImagePath -Value $ImagePath");
        updater.Should().Contain("Windows service ImagePath verification failed");
        updater.Should().Contain("Set-NetRatelServiceImagePath $script:ActivePath");
        updater.Should().Contain("Set-NetRatelServiceImagePath $script:PreviousPath");
        updater.Should().NotContain("sc.exe config");
    }

    [Fact]
    public void WindowsUpdaterLauncher_UsesTheResolvedRootStateAndRequestPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-custom-root", "client");
        var state = Path.Combine(Path.GetTempPath(), "netratel-custom-state");
        var request = Path.Combine(state, "update-offer.json");
        var powerShell = Path.Combine(Path.GetTempPath(), "WindowsPowerShell", "powershell.exe");

        var startInfo = AkkaClientAutoUpdateCoordinator.CreateWindowsUpdaterStartInfo(powerShell, root, state, request);

        Assert.Equal(powerShell, startInfo.FileName);
        Assert.Equal(
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "updater", "netratel-update.ps1") },
            startInfo.ArgumentList.ToArray());
        Assert.Equal(root, startInfo.Environment["NetRatel_UPDATE_ROOT"]);
        Assert.Equal(state, startInfo.Environment["NetRatel_UPDATE_STATE"]);
        Assert.Equal(request, startInfo.Environment["NetRatel_UPDATE_REQUEST"]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public async Task WindowsUpdater_RequiresAnExactRegisteredClientExecutableInItsOwnedPackageLayout()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var updater = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"));
        var start = updater.IndexOf("function Assert-NetRatelOwnedServiceImage", StringComparison.Ordinal);
        var end = start < 0 ? -1 : updater.IndexOf("function Get-ActivationFailureCode", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The updater must keep its service ownership check isolated for behavior testing.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-updater-owned-image-{Guid.NewGuid():N}");
        var version = "0.4.131-rc.1";
        var versionDirectory = Path.Combine(root, "versions", version);
        var nestedVersionDirectory = Path.Combine(root, "versions", "foreign", version);
        var trustedExecutable = Path.Combine(versionDirectory, "NetRatel.Client.exe");
        var nestedExecutable = Path.Combine(nestedVersionDirectory, "NetRatel.Client.exe");
        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(nestedVersionDirectory);
        await File.WriteAllTextAsync(trustedExecutable, "owned executable placeholder");
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, "netratel-client-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "netratel.client.manifest.v1",
            product = "NetRatel.Client",
            version,
            runtimeId = "win-x64",
            commitSha = new string('a', 40),
            executable = "NetRatel.Client.exe"
        }));
        await File.WriteAllTextAsync(nestedExecutable, "foreign executable placeholder");

        var validQuoted = $"\"{trustedExecutable}\" --service";
        var validPlain = trustedExecutable;
        var cases = new (string Name, string Image, bool Expected)[]
        {
            ("quoted owned package", validQuoted, true),
            ("plain owned package", validPlain, true),
            ("extra command argument", $"\"{trustedExecutable}\" --service -Command Write-Output", false),
            ("executable prefix lookalike", $"\"{trustedExecutable} -foreign.exe\" --service", false),
            ("nested version lookalike", $"\"{nestedExecutable}\" --service", false)
        };
        var testCases = string.Join(",\n", cases.Select(testCase =>
            $"[pscustomobject]@{{ Name={PowerShellLiteral(testCase.Name)}; Image={PowerShellLiteral(testCase.Image)}; Expected=${testCase.Expected.ToString().ToLowerInvariant()} }}"));
        var harnessPath = Path.Combine(root, "verify.ps1");
        var harness = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "$ClientService = 'NetRatel.Client'",
            "$RootDir = " + PowerShellLiteral(root),
            "$script:RuntimeId = 'win-x64'",
            updater[start..end],
            "$cases = @(" + testCases + ")",
            "foreach ($testCase in $cases) {",
            "    $accepted = $false",
            "    try { [void](Assert-NetRatelOwnedServiceImage -ImagePath $testCase.Image); $accepted = $true } catch { }",
            "    if ($accepted -ne $testCase.Expected) { throw ('Ownership mismatch for ' + $testCase.Name + ': accepted=' + $accepted) }",
            "}");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("PowerShell is required to execute the updater ownership parser.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw new TimeoutException("PowerShell updater ownership check did not finish within 20 seconds.");
            }

            Assert.True(process.ExitCode == 0,
                "Updater ownership cases failed: " + await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsUpdater_HardensConfiguredStateBelowVerifiedAdminAncestorBeforeOpeningLock()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var updater = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"));
        var helperStart = updater.IndexOf("function Assert-NetRatelTrustedPath", StringComparison.Ordinal);
        var helperEnd = helperStart < 0 ? -1 : updater.IndexOf("function Protect-NetRatelOwnedStateTree", helperStart, StringComparison.Ordinal);
        Assert.True(helperStart >= 0 && helperEnd > helperStart, "The updater path verifier must remain isolated for behavior testing.");

        var mainStart = updater.IndexOf("$request = Initialize-NetRatelUpdaterPreflight", StringComparison.Ordinal);
        var stateHardening = updater.IndexOf("Protect-NetRatelOwnedStateTree -Path $StateDir", mainStart, StringComparison.Ordinal);
        var statePathVerification = updater.IndexOf("$allowStatePathAncestors = Test-NetRatelPathWithin $path $StateDir", stateHardening, StringComparison.Ordinal);
        var lockAcquisition = updater.IndexOf("[System.IO.File]::Open($LockPath", stateHardening, StringComparison.Ordinal);
        Assert.True(mainStart >= 0 && stateHardening > mainStart && statePathVerification > stateHardening && lockAcquisition > statePathVerification,
            "The updater must harden validated state, revalidate strict leaves with ancestor-only allowance, then acquire the lock.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-updater-state-ancestor-{Guid.NewGuid():N}");
        var adminAncestor = Path.Combine(root, "administrator-owned-parent");
        var stateDirectory = Path.Combine(adminAncestor, "NetRatel", "update");
        var requestPath = Path.Combine(stateDirectory, "request.json");
        var lockPath = Path.Combine(stateDirectory, "update.lock");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(requestPath, "{}");

        var harnessPath = Path.Combine(root, "verify.ps1");
        var harness = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "$adminAncestor = " + PowerShellLiteral(adminAncestor),
            "$stateDirectory = " + PowerShellLiteral(stateDirectory),
            "$requestPath = " + PowerShellLiteral(requestPath),
            "$script:hardened = $false",
            "$script:untrustedRequestWrite = $false",
            "function Get-NetRatelCanonicalPath([string]$Path,[string]$Description) { [System.IO.Path]::GetFullPath($Path) }",
            "function Get-NetRatelTrustedPathSids { return ,([string[]]@('S-1-5-18','S-1-5-32-544')) }",
            "function Get-NetRatelLocalAdministratorMemberSids { return ,([string[]]@('S-1-5-21-local-admin')) }",
            updater[helperStart..helperEnd],
            "function Get-Acl { [CmdletBinding()] param([string]$LiteralPath)",
            "    $insideState = [string]::Equals($LiteralPath, $stateDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or $LiteralPath.StartsWith($stateDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)",
            "    $isAdminAncestor = [string]::Equals($LiteralPath, $adminAncestor, [System.StringComparison]::OrdinalIgnoreCase)",
            "    $ownerSid = if ($script:hardened -and $insideState) { 'S-1-5-32-544' } elseif ($insideState -or $isAdminAncestor) { 'S-1-5-21-local-admin' } else { 'S-1-5-18' }",
            "    $rules = @()",
            "    if ($script:hardened -and $insideState) { $rules += [pscustomobject]@{ AccessControlType=[System.Security.AccessControl.AccessControlType]::Allow; PropagationFlags=[System.Security.AccessControl.PropagationFlags]::None; IdentityReference=[pscustomobject]@{ Value='S-1-5-32-544' }; FileSystemRights=[System.Security.AccessControl.FileSystemRights]::FullControl; IsInherited=$false } }",
            "    if ((-not $script:hardened -and $insideState) -or $isAdminAncestor -or ($script:untrustedRequestWrite -and [string]::Equals($LiteralPath, $requestPath, [System.StringComparison]::OrdinalIgnoreCase))) { $rules += [pscustomobject]@{ AccessControlType=[System.Security.AccessControl.AccessControlType]::Allow; PropagationFlags=[System.Security.AccessControl.PropagationFlags]::None; IdentityReference=[pscustomobject]@{ Value='S-1-5-21-local-admin' }; FileSystemRights=[System.Security.AccessControl.FileSystemRights]::WriteData; IsInherited=$false } }",
            "    $acl = [pscustomobject]@{ OwnerSid=$ownerSid; Rules=$rules }",
            "    $acl | Add-Member ScriptMethod GetOwner { param($type) return [pscustomobject]@{ Value=$this.OwnerSid } }",
            "    $acl | Add-Member ScriptMethod GetAccessRules { param($includeExplicit,$includeInherited,$sidType) return ,$this.Rules }",
            "    return $acl",
            "}",
            "[void](Assert-NetRatelTrustedPath -Path $stateDirectory -LeafIsDirectory:$true -AllowLocalAdministrator:$true)",
            "[void](Assert-NetRatelTrustedPath -Path $requestPath -LeafIsDirectory:$false -AllowLocalAdministrator:$true)",
            "$script:hardened = $true",
            "[void](Assert-NetRatelTrustedPath -Path $stateDirectory -LeafIsDirectory:$true -AllowLegacyAdministratorAncestors:$true)",
            "[void](Assert-NetRatelTrustedPath -Path $requestPath -LeafIsDirectory:$false -AllowLegacyAdministratorAncestors:$true)",
            "$unscopedRejected = $false",
            "try { [void](Assert-NetRatelTrustedPath -Path $requestPath -LeafIsDirectory:$false) } catch { $unscopedRejected = $true }",
            "if (-not $unscopedRejected) { throw 'The updater accepted the administrator ancestor without explicit configured-state provenance.' }",
            "$script:untrustedRequestWrite = $true",
            "$untrustedLeafRejected = $false",
            "try { [void](Assert-NetRatelTrustedPath -Path $requestPath -LeafIsDirectory:$false -AllowLegacyAdministratorAncestors:$true) } catch { $untrustedLeafRejected = $true }",
            "if (-not $untrustedLeafRejected) { throw 'Ancestor allowance made the hardened request leaf writable by an individual administrator.' }",
            "$script:untrustedRequestWrite = $false",
            "[void](Assert-NetRatelTrustedPath -Path " + PowerShellLiteral(lockPath) + " -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLegacyAdministratorAncestors:$true)",
            "$lock = [System.IO.File]::Open(" + PowerShellLiteral(lockPath) + ", [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)",
            "$lock.Dispose()",
            "if (-not (Test-Path -LiteralPath " + PowerShellLiteral(lockPath) + " -PathType Leaf)) { throw 'The updater lock was not acquired after hardened-state verification.' }");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("PowerShell is required to execute the updater state-path behavior test.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw new TimeoutException("PowerShell updater state-path check did not finish within 20 seconds.");
            }

            Assert.True(process.ExitCode == 0,
                "Updater state-path cases failed: " + await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateArtifactDownload_RejectsNetworkPathAndRedirectWithoutReplacingExistingPackage()
    {
        var apiOrigin = new Uri("https://api.example.invalid");
        var root = Path.Combine(Path.GetTempPath(), $"netratel-update-download-origin-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(root, "staging", "linux-x64-1.2.4.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var previousPackage = Encoding.UTF8.GetBytes("previous valid package");
        await File.WriteAllBytesAsync(packagePath, previousPackage);

        using var handler = new RecordingHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://untrusted.example.invalid/package.zip");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AkkaClientAutoUpdateCoordinator.DownloadAndVerifyOfferedArtifactAsync(
                    client, apiOrigin, "//untrusted.example.invalid/package.zip", "test-token", packagePath,
                    "1.2.4", "linux-x64", 1, 1024, new string('0', 64), CancellationToken.None));
            Assert.Empty(handler.RequestUris);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AkkaClientAutoUpdateCoordinator.DownloadAndVerifyOfferedArtifactAsync(
                    client, apiOrigin, "/api/v1/client-artifacts/release/download", "test-token", packagePath,
                    "1.2.4", "linux-x64", 1, 1024, new string('0', 64), CancellationToken.None));
            Assert.Equal(new[] { new Uri(apiOrigin, "/api/v1/client-artifacts/release/download") }, handler.RequestUris);
            Assert.Equal(previousPackage, await File.ReadAllBytesAsync(packagePath));
            Assert.Equal(new[] { packagePath }, Directory.EnumerateFiles(Path.GetDirectoryName(packagePath)!).ToArray());

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var program = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Program.cs"));
            Assert.Contains("ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })", program, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateArtifactDownload_RejectsOversizedUnknownLengthBodyBeforeReplacingPackage()
    {
        var apiOrigin = new Uri("https://api.example.invalid");
        var root = Path.Combine(Path.GetTempPath(), $"netratel-update-download-size-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(root, "staging", "linux-x64-1.2.4.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var previousPackage = Encoding.UTF8.GetBytes("previous valid package");
        await File.WriteAllBytesAsync(packagePath, previousPackage);
        var content = new StreamContent(new UnknownLengthMemoryStream(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(content.Headers.ContentLength);

        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        }));
        using var client = new HttpClient(handler);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AkkaClientAutoUpdateCoordinator.DownloadAndVerifyOfferedArtifactAsync(
                    client, apiOrigin, "/api/v1/client-artifacts/release/download", "test-token", packagePath,
                    "1.2.4", "linux-x64", 3, 1024, new string('0', 64), CancellationToken.None));

            Assert.Single(handler.RequestUris);
            Assert.Equal(new Uri(apiOrigin, "/api/v1/client-artifacts/release/download"), handler.RequestUris[0]);
            Assert.Equal(previousPackage, await File.ReadAllBytesAsync(packagePath));
            Assert.Equal(new[] { packagePath }, Directory.EnumerateFiles(Path.GetDirectoryName(packagePath)!).ToArray());
            Assert.False(File.Exists(Path.Combine(root, "request.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateArtifactDownload_ReplacesExistingPackageOnlyAfterExactVerifiedResponse()
    {
        var apiOrigin = new Uri("https://api.example.invalid");
        var root = Path.Combine(Path.GetTempPath(), $"netratel-update-download-valid-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(root, "staging", "linux-x64-1.2.4.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        await File.WriteAllTextAsync(packagePath, "previous package");
        var package = CreateClientPackage("1.2.4", "linux-x64");
        var sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        using var handler = new RecordingHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(package)
        }));
        using var client = new HttpClient(handler);

        try
        {
            await AkkaClientAutoUpdateCoordinator.DownloadAndVerifyOfferedArtifactAsync(
                client, apiOrigin, "/api/v1/client-artifacts/release/download", "test-token", packagePath,
                "1.2.4", "linux-x64", package.Length, package.Length + 100, sha256, CancellationToken.None);

            Assert.Equal(package, await File.ReadAllBytesAsync(packagePath));
            Assert.Single(handler.RequestUris);
            Assert.Equal(new Uri(apiOrigin, "/api/v1/client-artifacts/release/download"), handler.RequestUris[0]);
            Assert.Equal(new[] { packagePath }, Directory.EnumerateFiles(Path.GetDirectoryName(packagePath)!).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://old-api.example.invalid/api/", "https://old-api.example.invalid", false)]
    [InlineData("https://old-api.example.invalid", "https://split-gateway.example.invalid", true)]
    public async Task WindowsUpdater_SettingsMigration_UsesThePerVersionApiGatewayPair(
        string settingsApiBase,
        string settingsGateway,
        bool expectGatewayEndpoint)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var updater = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"));
        var start = updater.IndexOf("function Get-NetRatelPublicOrigin", StringComparison.Ordinal);
        var end = start < 0 ? -1 : updater.IndexOf("function Expand-NetRatelZip", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The updater settings migration helpers must remain in the isolated function section.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-updater-settings-{Guid.NewGuid():N}");
        var previousDirectory = Path.Combine(root, "versions", "0.4.130");
        var destinationDirectory = Path.Combine(root, "versions", "0.4.131");
        Directory.CreateDirectory(previousDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var previousImagePath = Path.Combine(previousDirectory, "NetRatel.Client.exe");
        await File.WriteAllTextAsync(Path.Combine(previousDirectory, "clientsettings.json"), $$"""
            {
              "Client": { "ApiBaseUrl": "{{settingsApiBase}}" },
              "Gateway": { "Endpoint": "{{settingsGateway}}" }
            }
            """);

        var harnessPath = Path.Combine(root, "verify.ps1");
        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            $ClientService = 'NetRatel.Client'
            $script:LogPath = $null
            {{updater[start..end]}}
            function Write-UpdateLog([string] $message) { }
            function Get-ItemProperty {
                [CmdletBinding()]
                param([string] $Path, [string[]] $Name)
                [pscustomobject]@{ Environment = @('NetRatelCLIENT__Client__ApiBaseUrl=https://new-service-api.example.invalid') }
            }
            Copy-NetRatelInstalledClientSettings -PreviousImagePath {{PowerShellLiteral(previousImagePath)}} -DestinationDirectory {{PowerShellLiteral(destinationDirectory)}}
            """;
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("PowerShell is required to execute the updater settings migration helper.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }
            var stdout = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
            Assert.True(process.ExitCode == 0, $"Updater settings migration failed: {stderr}{Environment.NewLine}{stdout}");

            using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destinationDirectory, "clientsettings.json")));
            var gateway = migrated.RootElement.GetProperty("Gateway");
            Assert.Equal(expectGatewayEndpoint, gateway.TryGetProperty("Endpoint", out _));
            if (expectGatewayEndpoint) Assert.Equal(settingsGateway, gateway.GetProperty("Endpoint").GetString());
            Assert.Equal(settingsApiBase, migrated.RootElement.GetProperty("Client").GetProperty("ApiBaseUrl").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string PowerShellLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static byte[] CreateClientPackage(string version, string runtimeId)
    {
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("netratel-client-manifest.json");
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(JsonSerializer.Serialize(new
                {
                    schema = "netratel.client.manifest.v1",
                    product = "NetRatel.Client",
                    version,
                    runtimeId,
                    commitSha = new string('a', 40),
                    executable = "NetRatel.Client.exe"
                }));
            }

            var executable = archive.CreateEntry("NetRatel.Client.exe");
            using var stream = executable.Open();
            stream.WriteByte(1);
        }

        return package.ToArray();
    }

    private sealed class ActivationPresenceFixture(TimeProvider clock) : IAgentTokenService,
        IAgentGatewayUpdateHandler, IClientStreamWriter<AgentFrame>, IAsyncStreamReader<GatewayFrame>
    {
        private readonly Channel<GatewayFrame> _responses = Channel.CreateUnbounded<GatewayFrame>();
        private CancellationToken _callToken;
        internal Guid ConnectionId { get; } = Guid.NewGuid();
        internal ulong ConnectionEpoch => 42;
        internal string AttemptId { get; } = Guid.NewGuid().ToString("D");
        internal string ReleaseId { get; } = Guid.NewGuid().ToString("D");
        internal UpdateActivationContext? ActivationHello { get; private set; }
        internal TaskCompletionSource<AgentFrame> FirstHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Events { get; } = new();
        internal ConcurrentQueue<Exception> AcknowledgementFailures { get; } = new();
        public WriteOptions? WriteOptions { get; set; }
        public GatewayFrame Current { get; private set; } = new();

        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(("activation-test-token", clock.GetUtcNow().AddHours(1)));
        }

        internal AsyncDuplexStreamingCall<AgentFrame, GatewayFrame> Open(Metadata _, CancellationToken token)
        {
            _callToken = token;
            return new AsyncDuplexStreamingCall<AgentFrame, GatewayFrame>(this, this, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), Dispose);
        }

        public Task WriteAsync(AgentFrame frame) => WriteAsync(frame, _callToken);
        public Task WriteAsync(AgentFrame frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (frame.Hello is not null)
            {
                ActivationHello = frame.Hello.UpdateActivation?.Clone();
                var response = Reply(frame);
                response.Connected = new ConnectAccepted
                {
                    HeartbeatIntervalSeconds = 15, HeartbeatTimeoutSeconds = 50, PresenceAuthority = "akka"
                };
                _responses.Writer.TryWrite(response).Should().BeTrue();
            }
            else if (frame.Heartbeat is not null)
            {
                Events.Enqueue("heartbeat-written");
                FirstHeartbeat.TrySetResult(frame.Clone()).Should().BeTrue("only the immediate heartbeat is sent before time advances");
            }
            else throw new InvalidOperationException("Unexpected activation test frame.");
            return Task.CompletedTask;
        }

        internal void AcceptFirstHeartbeat(AgentFrame heartbeat)
        {
            var response = Reply(heartbeat);
            response.HeartbeatAccepted = new HeartbeatAccepted
            {
                PresenceAuthority = "akka",
                UpdateConfirmation = new UpdateActivationConfirmation
                {
                    AttemptId = AttemptId, ReleaseId = ReleaseId, ConfirmationId = Guid.NewGuid().ToString("D")
                }
            };
            _responses.Writer.TryWrite(response).Should().BeTrue();
        }

        private GatewayFrame Reply(AgentFrame frame) => new()
        {
            ProtocolVersion = frame.ProtocolVersion, TenantId = frame.TenantId, ClientId = frame.ClientId,
            ConnectionId = ConnectionId.ToString("D"), ConnectionEpoch = ConnectionEpoch,
            OperationId = frame.OperationId, Sequence = frame.Sequence
        };

        public async Task<bool> MoveNext(CancellationToken token)
        {
            Current = await _responses.Reader.ReadAsync(token);
            return true;
        }
        public Task CompleteAsync() => Task.CompletedTask;
        public void Dispose() => _responses.Writer.TryComplete();
        public void PopulateHello(ConnectHello hello) => hello.UpdateActivation = new UpdateActivationContext
        {
            AttemptId = AttemptId, ReleaseId = ReleaseId, AdmissionNonce = "activation-test-nonce"
        };
        public void OnPresenceConnected(ulong epoch)
        {
            epoch.Should().Be(ConnectionEpoch);
            Events.Enqueue("connected");
        }
        public void OnActivationHeartbeatSent(ulong epoch)
        {
            epoch.Should().Be(ConnectionEpoch);
            Events.Enqueue("sent");
        }
        public void OnActivationHeartbeatAccepted(ulong epoch)
        {
            epoch.Should().Be(ConnectionEpoch);
            Events.Enqueue("accepted");
        }
        public void OnAcknowledgement(ClientUpdateOffer? offer, ClientUpdatePolicy? policy, UpdateActivationConfirmation? confirmation)
        {
            if (confirmation is null) return;
            confirmation.AttemptId.Should().Be(AttemptId);
            confirmation.ReleaseId.Should().Be(ReleaseId);
            Events.Enqueue("confirmation");
        }
        public void RecordAcknowledgementFailure(Exception exception) => AcknowledgementFailures.Enqueue(exception);
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("Test request has no URI."));
            return await responseFactory(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class UnknownLengthMemoryStream(byte[] contents) : MemoryStream(contents)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
