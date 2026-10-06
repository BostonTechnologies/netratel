using NetRatel.Application.Scripts;
using NetRatel.Application.Artifacts;

namespace NetRatel.API.Services;

public sealed class AgentUpdateScriptSeedService(IServiceScopeFactory scopeFactory, ILogger<AgentUpdateScriptSeedService> logger) : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<AgentUpdateScriptSeedService> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var scripts = scope.ServiceProvider.GetRequiredService<IScriptService>();
        await UpsertAsync(scripts, WindowsScript, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(scripts, LinuxScript, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task UpsertAsync(IScriptService scripts, SeedScript seed, CancellationToken ct)
    {
        var existing = (await scripts.ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.FolderPath, seed.FolderPath, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(x.Name, seed.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            await scripts.CreateAsync(new CreateScriptCommand(seed.Name, seed.FolderPath, seed.Description, seed.Content, seed.ScriptType, ManifestRaw: null), ct).ConfigureAwait(false);
            _logger.LogInformation("Seeded NetRatel agent update script {Folder}/{Name}.", seed.FolderPath, seed.Name);
            return;
        }

        if (!string.Equals(existing.Content, seed.Content, StringComparison.Ordinal) ||
            !string.Equals(existing.ScriptType, seed.ScriptType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Description, seed.Description, StringComparison.Ordinal))
        {
            await scripts.UpdateAsync(new UpdateScriptCommand(existing.Id, seed.Name, seed.FolderPath, seed.Description, seed.Content, seed.ScriptType, ManifestRaw: null), ct).ConfigureAwait(false);
            _logger.LogInformation("Updated seeded NetRatel agent update script {Folder}/{Name}.", seed.FolderPath, seed.Name);
        }
    }

    private sealed record SeedScript(string Name, string FolderPath, string Description, string ScriptType, string Content);

    private const string WindowsScriptManifest = """
#| NetRatel-MANIFEST
{
  "params": [
    { "name": "ApiBase", "type": "string", "required": true },
    { "name": "TenantId", "type": "int", "required": true },
    { "name": "EnrollmentCode", "type": "string", "required": true },
    { "name": "Runtime", "type": "string", "required": false, "default": "win-x64" },
    { "name": "Version", "type": "string", "required": false, "default": "latest" },
    { "name": "GatewayEndpoint", "type": "string", "required": false, "default": "" }
  ]
}
#| END
""";

    private static readonly SeedScript WindowsScript = BuildWindowsScript();

    private static SeedScript BuildWindowsScript()
    {
        var installer = BuildSeedInstaller("win-x64");
        return new SeedScript(
            "Update Client To Latest",
            "/Windows/NetRatel",
            "Repair or roll forward a Windows NetRatel service client using a verified staged installer and detached service handoff; enrollment and gateway status remain unverified.",
            "PowerShell",
            WindowsScriptManifest + Environment.NewLine + WindowsHandoffPreamble + Environment.NewLine + installer);
    }

    private static string BuildSeedInstaller(string runtime) =>
        new NetRatel.Infrastructure.Artifacts.ScriptTemplateService().Build(
            new DeploymentScriptTemplateRequest(
                TenantId: 1,
                RuntimeId: runtime,
                EnrollmentCode: "synthetic-seed-input",
                ApiBaseUrl: "https://netratel-seed.invalid",
                ValidToUtc: DateTimeOffset.MaxValue,
                InstallAsService: true,
                SilentInstall: false,
                IsUpdateSeed: true))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private const string WindowsHandoffPreamble = """
param(
    [string] $ApiBase,
    [int] $TenantId,
    [string] $EnrollmentCode,
    [string] $Runtime = "win-x64",
    [string] $Version = "latest",
    [string] $GatewayEndpoint = "",
    [string] $NetRatelSeedHandoffRequestPath,
    [string] $NetRatelSeedHandoffStateDirectory
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$rawNetRatelSeedHandoffRequestPath = $NetRatelSeedHandoffRequestPath
$script:NetRatelSeedHandoffMode = $false
$script:NetRatelSeedHandoffRequestPath = $null
$script:NetRatelSeedHandoffResultPath = $null
$script:NetRatelSeedHandoffId = $null
$script:NetRatelSeedHandoffTenantId = $TenantId
$script:NetRatelSeedHandoffPreflightFailureCode = $null
$script:NetRatelSeedHandoffPreflightExceptionType = $null

function Get-NetRatelSeedApiBase([string] $Value) {
    try { $uri = [Uri]::new($Value.Trim(), [UriKind]::Absolute) }
    catch { throw "ApiBase must be an absolute HTTP or HTTPS origin, optionally followed by /api." }
    if ($uri.Scheme -notin @("http", "https") -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.AbsolutePath.TrimEnd("/") -notin @("", "/api")) {
        throw "ApiBase must be an HTTP or HTTPS origin without credentials, query, fragment, or an arbitrary path base."
    }
    return $uri.GetLeftPart([System.UriPartial]::Authority)
}

function Get-NetRatelSeedGatewayEndpoint([string] $Value, [string] $ApiOrigin) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    try { $uri = [Uri]::new($Value.Trim(), [UriKind]::Absolute) }
    catch { throw "GatewayEndpoint must be an HTTPS origin." }
    if ($uri.Scheme -ne "https" -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath.TrimEnd("/") -ne "") {
        throw "GatewayEndpoint must be an HTTPS origin without credentials, a path, a query, or a fragment."
    }
    $origin = $uri.GetLeftPart([System.UriPartial]::Authority)
    return $origin
}

function Wait-NetRatelSeedOriginExit([int] $ProcessId, [string] $ExpectedStartedAtUtc) {
    $expectedStartedAt = [DateTimeOffset]::Parse($ExpectedStartedAtUtc).ToUniversalTime()
    $origin = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
    if (-not $origin) { return }

    $actualStartedAt = Convert-NetRatelSeedProcessCreationTime $origin.CreationDate
    # A different creation time proves this PID has been reused after the recorded origin exited.
    if ([Math]::Abs(($actualStartedAt - $expectedStartedAt).TotalSeconds) -gt 2) { return }

    $process = $null
    try { $process = [System.Diagnostics.Process]::GetProcessById($ProcessId) }
    catch [ArgumentException] {
        $gone = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
        if (-not $gone -or [Math]::Abs(((Convert-NetRatelSeedProcessCreationTime $gone.CreationDate) - $expectedStartedAt).TotalSeconds) -gt 2) { return }
        throw "Installer handoff origin process identity could not be opened for exit verification."
    }
    try {
        if (-not $process.WaitForExit(30000)) {
            throw "Installer handoff origin did not exit before its bounded wait expired."
        }
    }
    finally {
        $process.Dispose()
    }

    $remaining = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop
    if ($remaining) {
        $remainingStartedAt = Convert-NetRatelSeedProcessCreationTime $remaining.CreationDate
        if ([Math]::Abs(($remainingStartedAt - $expectedStartedAt).TotalSeconds) -le 2) {
            throw "Installer handoff origin remained alive after its bounded wait."
        }
    }
}

function Assert-NetRatelSeedHandoffNoReparse([string] $Path, [bool] $LeafFile) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) { throw 'Installer handoff path has no filesystem root.' }
    $currentPath = $pathRoot
    foreach ($component in $fullPath.Substring($pathRoot.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $component
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ([string]::Equals($currentPath, $fullPath, [StringComparison]::OrdinalIgnoreCase) -and ($item.PSIsContainer -eq $LeafFile)) -or
            (-not [string]::Equals($currentPath, $fullPath, [StringComparison]::OrdinalIgnoreCase) -and -not $item.PSIsContainer)) {
            throw 'Installer handoff path contains a reparse point or unexpected object type.'
        }
    }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $acl = Get-Acl -LiteralPath $fullPath -ErrorAction Stop
        $trusted = @('S-1-5-18', 'S-1-5-32-544')
        $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $trusted) { throw 'Installer handoff path has an untrusted owner.' }
        foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                $rule.IdentityReference.Value -notin $trusted -and [int]$rule.FileSystemRights -ne 0) {
                throw 'Installer handoff path is accessible to an untrusted principal.'
            }
        }
    }
}

function Convert-NetRatelSeedProcessCreationTime([object] $Value) {
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) { return [DateTimeOffset]$Value.ToUniversalTime() }
    return [DateTimeOffset]([System.Management.ManagementDateTimeConverter]::ToDateTime([string]$Value).ToUniversalTime())
}

function Validate-NetRatelSeedHandoffParameters([string] $RequestRuntime, [string] $RequestVersion, [string] $RequestEnrollmentCode) {
    if ([string]::IsNullOrWhiteSpace($RequestEnrollmentCode) -or $RequestEnrollmentCode.Length -gt 512) {
        throw "Installer handoff enrollment input is invalid."
    }
    if ($RequestRuntime -notin @("win-x64", "win-arm64")) {
        throw "Installer handoff runtime is invalid."
    }
    if ($RequestVersion -ne "latest" -and $RequestVersion -notmatch '^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Installer handoff version is invalid."
    }
}

function Set-NetRatelSeedHandoffResult {
    param(
        [Parameter(Mandatory = $true)] [string] $State,
        [string] $FailureCode,
        [string] $ExceptionType
    )
    if ([string]::IsNullOrWhiteSpace($script:NetRatelSeedHandoffResultPath)) { return }
    $result = [ordered]@{
        schema = "netratel.seeded-update-result.v1"
        handoffId = $script:NetRatelSeedHandoffId
        state = $State
        tenantId = $script:NetRatelSeedHandoffTenantId
        observedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    if ($FailureCode) { $result.failureCode = $FailureCode }
    if ($ExceptionType) { $result.exceptionType = $ExceptionType }
    $temporaryPath = "$($script:NetRatelSeedHandoffResultPath).$PID.tmp"
    [System.IO.File]::WriteAllText($temporaryPath, ($result | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $script:NetRatelSeedHandoffResultPath -Force
}

function Complete-NetRatelSeedFailure([object] $Failure) {
    if (-not $script:NetRatelSeedHandoffMode) { return }
    try { Set-NetRatelSeedHandoffResult -State 'failed' -FailureCode 'installer_failed' -ExceptionType $Failure.Exception.GetType().Name }
    catch { Write-Warning 'The detached installer could not record its failure result.' }
}

function Remove-NetRatelSeedHandoffFiles {
    if ($script:NetRatelSeedHandoffMode) {
        Remove-Item -LiteralPath $script:NetRatelSeedHandoffRequestPath, $PSCommandPath -Force -ErrorAction SilentlyContinue
    }
}

function Start-NetRatelSeedHandoff {
if ($script:NetRatelSeedHandoffPreflightFailureCode) {
    throw "Installer handoff validation failed."
}
if ($script:NetRatelSeedHandoffMode) {
    $expectedState = [System.IO.Path]::GetFullPath($NetRatelSeedHandoffStateDirectory).TrimEnd([char[]]@('\', '/'))
    $actualState = [System.IO.Path]::GetFullPath($StateDir).TrimEnd([char[]]@('\', '/'))
    if (-not [string]::Equals($expectedState, $actualState, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installer handoff state directory does not match the configured updater state path.'
    }
    Assert-OwnedPath $script:NetRatelSeedHandoffRequestPath -File
    Assert-OwnedPath $PSCommandPath -File
    Set-NetRatelSeedHandoffResult -State 'processing'
    return
}

if ([string]::IsNullOrWhiteSpace($rawNetRatelSeedHandoffRequestPath)) {
    $script:ApiBase = Get-NetRatelSeedApiBase $ApiBase
    if ($TenantId -le 0) { throw "TenantId must be positive." }
    if ([string]::IsNullOrWhiteSpace($EnrollmentCode) -or $EnrollmentCode.Length -gt 512) { throw "EnrollmentCode is required." }
    if ($Runtime -notin @("win-x64", "win-arm64")) { throw "Runtime is not a supported Windows client runtime." }
    if ($Version -ne "latest" -and $Version -notmatch '^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Version must be latest or a canonical semantic version."
    }
    $script:GatewayEndpoint = Get-NetRatelSeedGatewayEndpoint $GatewayEndpoint $script:ApiBase

    $stateDirectory = [System.IO.Path]::GetFullPath($StateDir)
    $handoffDirectory = Join-Path $stateDirectory "install-handoffs"
    New-OwnedDirectory $handoffDirectory

    $handoffId = [Guid]::NewGuid().ToString("N")
    $handoffRequestPath = Join-Path $handoffDirectory "handoff-$handoffId.json"
    $handoffScriptPath = Join-Path $handoffDirectory "handoff-$handoffId.ps1"
    $handoffResultPath = Join-Path $handoffDirectory "handoff-$handoffId.result.json"
    $originProcess = Get-Process -Id $PID -ErrorAction Stop
    $handoffCreatedAtUtc = [DateTimeOffset]::UtcNow
    $request = [ordered]@{
        schema = "netratel.seeded-update-handoff.v1"
        handoffId = $handoffId
        createdAtUtc = $handoffCreatedAtUtc.ToString("O")
        expiresAtUtc = $handoffCreatedAtUtc.AddMinutes(2).ToString("O")
        sourceProcessId = [int]$PID
        sourceProcessStartedAtUtc = ([DateTimeOffset]$originProcess.StartTime.ToUniversalTime()).ToString("O")
        apiBase = $script:ApiBase
        tenantId = $TenantId
        enrollmentCode = $EnrollmentCode
        runtime = $Runtime
        version = $Version
        gatewayEndpoint = $script:GatewayEndpoint
    }

    try {
        Write-PrivateFile $handoffScriptPath ([System.IO.File]::ReadAllText($PSCommandPath))
        Write-PrivateFile $handoffRequestPath ($request | ConvertTo-Json -Depth 4)
        $powershellPath = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
        if (-not (Test-Path -LiteralPath $powershellPath -PathType Leaf)) { throw "Windows PowerShell could not be located for the detached installer." }
        $arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$handoffScriptPath`" -NetRatelSeedHandoffRequestPath `"$handoffRequestPath`" -NetRatelSeedHandoffStateDirectory `"$stateDirectory`""
        $worker = Start-Process -FilePath $powershellPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -ErrorAction Stop
        if (-not $worker -or $worker.Id -le 0) { throw "The independent installer process could not be started." }
        $script:NetRatelSeedHandoffId = $handoffId
        $script:NetRatelSeedHandoffResultPath = $handoffResultPath
        $script:NetRatelSeedHandoffTenantId = $TenantId
        Set-NetRatelSeedHandoffResult -State "handed_off"
        Write-Host "NetRatel update repair handed off to independent installer process; handoffId=$handoffId. Local activation status will be recorded; enrollment and gateway status remain unverified."
        exit 0
    }
    catch {
        Remove-Item -LiteralPath $handoffRequestPath, $handoffScriptPath -Force -ErrorAction SilentlyContinue
        throw
    }
}

$ownedHandoffRequestPath = $null
$ownedHandoffScriptPath = $null
try {
    if ([string]::IsNullOrWhiteSpace($NetRatelSeedHandoffStateDirectory) -or
        -not [System.IO.Path]::IsPathRooted($NetRatelSeedHandoffStateDirectory)) {
        throw "Installer handoff state directory is invalid."
    }
    $stateDirectory = [System.IO.Path]::GetFullPath($NetRatelSeedHandoffStateDirectory)
    $expectedHandoffDirectory = [System.IO.Path]::GetFullPath((Join-Path $stateDirectory "install-handoffs"))
    $requestPath = [System.IO.Path]::GetFullPath($rawNetRatelSeedHandoffRequestPath)
    if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($requestPath), $expectedHandoffDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($requestPath) -notmatch '^handoff-(?<id>[a-f0-9]{32})\.json$') {
        throw "Installer handoff request is outside its protected directory or has an invalid name."
    }
    Assert-NetRatelSeedHandoffNoReparse $stateDirectory $false
    Assert-NetRatelSeedHandoffNoReparse $expectedHandoffDirectory $false
    Assert-NetRatelSeedHandoffNoReparse $requestPath $true
    $ownedHandoffRequestPath = $requestPath
    $script:NetRatelSeedHandoffRequestPath = $ownedHandoffRequestPath
    $handoffId = $Matches.id
    $ownedHandoffScriptPath = Join-Path $expectedHandoffDirectory "handoff-$handoffId.ps1"
    Assert-NetRatelSeedHandoffNoReparse $ownedHandoffScriptPath $true
    if ((Get-Item -LiteralPath $ownedHandoffRequestPath -Force -ErrorAction Stop).Length -gt 65536 -or
        (Get-Item -LiteralPath $ownedHandoffScriptPath -Force -ErrorAction Stop).Length -gt 1048576) {
        throw 'Installer handoff files exceed their bounded size limits.'
    }
    $script:NetRatelSeedHandoffMode = $true
    $script:NetRatelSeedHandoffId = $handoffId
    $script:NetRatelSeedHandoffResultPath = Join-Path $expectedHandoffDirectory "handoff-$handoffId.result.json"
    $handoff = Get-Content -LiteralPath $ownedHandoffRequestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ([int]$handoff.tenantId -gt 0) { $script:NetRatelSeedHandoffTenantId = [int]$handoff.tenantId }
    $now = [DateTimeOffset]::UtcNow
    $createdAt = [DateTimeOffset]::Parse([string]$handoff.createdAtUtc).ToUniversalTime()
    $expiresAt = [DateTimeOffset]::Parse([string]$handoff.expiresAtUtc).ToUniversalTime()
    if ($handoff.schema -ne "netratel.seeded-update-handoff.v1" -or $handoff.handoffId -ne $handoffId -or
        $createdAt -gt $now.AddSeconds(5) -or $expiresAt -le $now -or
        $expiresAt -gt $createdAt.AddMinutes(2) -or $now - $createdAt -gt [TimeSpan]::FromMinutes(2) -or
        -not (Test-Path -LiteralPath $ownedHandoffScriptPath -PathType Leaf) -or
        [int]$handoff.tenantId -le 0 -or [string]::IsNullOrWhiteSpace([string]$handoff.enrollmentCode) -or
        ([string]$handoff.enrollmentCode).Length -gt 512 -or
        [string]$handoff.runtime -notin @("win-x64", "win-arm64") -or
        ([string]$handoff.version -ne "latest" -and [string]$handoff.version -notmatch '^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')) {
        throw "Installer handoff request is invalid, stale, or incomplete."
    }
    $script:ApiBase = Get-NetRatelSeedApiBase ([string]$handoff.apiBase)
    $script:TenantId = [int]$handoff.tenantId
    $script:EnrollmentCode = [string]$handoff.enrollmentCode
    $script:Runtime = [string]$handoff.runtime
    $script:Version = [string]$handoff.version
    $script:GatewayEndpoint = Get-NetRatelSeedGatewayEndpoint ([string]$handoff.gatewayEndpoint) $script:ApiBase
    Validate-NetRatelSeedHandoffParameters $script:Runtime $script:Version $script:EnrollmentCode
    $script:NetRatelSeedHandoffTenantId = $TenantId

    $sourceProcessId = [int]$handoff.sourceProcessId
    if ($sourceProcessId -le 0) { throw "Installer handoff origin process identity is invalid." }
    Wait-NetRatelSeedOriginExit $sourceProcessId ([string]$handoff.sourceProcessStartedAtUtc)
}
catch {
    $script:NetRatelSeedHandoffPreflightFailureCode = 'handoff_rejected'
    $script:NetRatelSeedHandoffPreflightExceptionType = $_.Exception.GetType().Name
    if ($ownedHandoffRequestPath) {
        # Only these validated, uniquely named handoff files may be completed before the updater lock.
        $script:NetRatelSeedHandoffId = [System.IO.Path]::GetFileNameWithoutExtension($ownedHandoffRequestPath).Substring(8)
        $script:NetRatelSeedHandoffTenantId = $TenantId
        $script:NetRatelSeedHandoffResultPath = [System.IO.Path]::ChangeExtension($ownedHandoffRequestPath, ".result.json")
        try {
            Set-NetRatelSeedHandoffResult -State 'failed' -FailureCode 'handoff_rejected' -ExceptionType $script:NetRatelSeedHandoffPreflightExceptionType
        }
        finally {
            Remove-Item -LiteralPath $ownedHandoffRequestPath, $ownedHandoffScriptPath -Force -ErrorAction SilentlyContinue
        }
    }
    throw
}
}

# Validate detached child input before the installer performs service or path preflight.
# The ordinary parent handoff remains at the post-lock integration point.
if (-not [string]::IsNullOrWhiteSpace($rawNetRatelSeedHandoffRequestPath)) {
    Start-NetRatelSeedHandoff
}
""";


    private const string LinuxScriptManifest = """
#| NetRatel-MANIFEST
{
  "params": [
    { "name": "ApiBase", "type": "string", "required": true, "default": "https://netratel.example.invalid" },
    { "name": "TenantId", "type": "int", "required": true },
    { "name": "EnrollmentCode", "type": "string", "required": true },
    { "name": "Runtime", "type": "string", "required": false, "default": "linux-x64" },
    { "name": "Version", "type": "string", "required": false, "default": "latest" },
    { "name": "GatewayEndpoint", "type": "string", "required": false, "default": "" }
  ]
}
#| END
""";

    private static readonly SeedScript LinuxScript = BuildLinuxScript();

    private static SeedScript BuildLinuxScript()
    {
        var installer = BuildSeedInstaller("linux-x64");

        var content = $$"""
#!/usr/bin/env bash
set -euo pipefail
umask 077
for required in python3 systemctl systemd-run; do
  command -v "$required" >/dev/null || { echo "Required command is unavailable: $required" >&2; exit 1; }
done
NETRATEL_SEED_API_BASE="$(printenv ApiBase)"
NETRATEL_SEED_TENANT_ID="$(printenv TenantId)"
NETRATEL_SEED_ENROLLMENT_CODE="$(printenv EnrollmentCode)"
NETRATEL_SEED_RUNTIME="$(printenv Runtime 2>/dev/null || true)"
NETRATEL_SEED_VERSION="$(printenv Version 2>/dev/null || true)"
NETRATEL_SEED_GATEWAY_ENDPOINT="$(printenv GatewayEndpoint 2>/dev/null || true)"
if [ -z "$NETRATEL_SEED_RUNTIME" ]; then NETRATEL_SEED_RUNTIME="linux-x64"; fi
if [ -z "$NETRATEL_SEED_VERSION" ]; then NETRATEL_SEED_VERSION="latest"; fi
export NETRATEL_SEED_API_BASE NETRATEL_SEED_TENANT_ID NETRATEL_SEED_ENROLLMENT_CODE
export NETRATEL_SEED_RUNTIME NETRATEL_SEED_VERSION NETRATEL_SEED_GATEWAY_ENDPOINT
NETRATEL_SEED_ORIGIN_PID="$BASHPID"
export NETRATEL_SEED_ORIGIN_PID
SEED_TEMP_DIR="$(mktemp -d)"
trap 'rm -rf -- "$SEED_TEMP_DIR"' EXIT
NORMAL_INSTALLER="$SEED_TEMP_DIR/netratel-installer.sh"
HANDOFF_HELPER="$SEED_TEMP_DIR/netratel-handoff.py"
cat > "$NORMAL_INSTALLER" <<'NETRATEL_NORMAL_INSTALLER'
{{installer}}
NETRATEL_NORMAL_INSTALLER
cat > "$HANDOFF_HELPER" <<'NETRATEL_HANDOFF_HELPER'
{{LinuxSeedHandoff}}
NETRATEL_HANDOFF_HELPER
chmod 0600 "$NORMAL_INSTALLER" "$HANDOFF_HELPER"
python3 "$HANDOFF_HELPER" dispatch "$NORMAL_INSTALLER" "$HANDOFF_HELPER"
echo "NetRatel Linux repair was handed to a detached root worker; local activation will run after this command exits. Enrollment and gateway status remain unverified."
""";

        return new SeedScript(
            "Update Client To Latest",
            "/Linux/NetRatel",
            "Repair or roll forward a Linux NetRatel service client using the normal verified artifact installer and a detached service handoff.",
            "Bash",
            LinuxScriptManifest + Environment.NewLine + content);
    }

    private const string LinuxSeedHandoff = """
import hashlib, json, os, re, secrets, shlex, stat, subprocess, sys, time
from urllib.parse import urlsplit

TEST_MODE = os.environ.get("NetRatel_TEST_ALLOW_NONROOT") == "true"
OWNER = os.geteuid() if TEST_MODE else 0
DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
UNIT = "netratel-client.service"
SEMVER = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?"

class HandoffRejected(RuntimeError):
    pass

def reject(message):
    raise HandoffRejected(message)

def safe_path(path):
    if not path or not os.path.isabs(path) or any(c in path for c in "\r\n\t\x00%"):
        reject("A managed handoff path must be absolute and contain no control characters.")
    return os.path.normpath(path)

def directory(path, private=False):
    parts = safe_path(path).split(os.sep)[1:]
    fd = os.open(os.sep, DIR_FLAGS)
    try:
        for index, name in enumerate(parts):
            child = os.open(name, DIR_FLAGS, dir_fd=fd)
            info = os.fstat(child)
            mode = stat.S_IMODE(info.st_mode)
            final = index == len(parts) - 1
            if (info.st_uid not in (0, OWNER) or (final and info.st_uid != OWNER) or
                (mode & 0o022 and (final or not mode & stat.S_ISVTX)) or
                (final and private and mode & 0o077)):
                os.close(child)
                reject("A managed handoff directory has an untrusted owner or mode.")
            os.close(fd)
            fd = child
        return fd
    except BaseException:
        os.close(fd)
        raise

def read(path, private=False, limit=1048576):
    parent = directory(os.path.dirname(safe_path(path)))
    try:
        fd = os.open(os.path.basename(path), os.O_RDONLY | os.O_NOFOLLOW, dir_fd=parent)
        try:
            info = os.fstat(fd)
            mode = stat.S_IMODE(info.st_mode)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != OWNER or mode & 0o022 or
                (private and mode & 0o077) or info.st_size > limit):
                reject("A protected handoff file has an untrusted type, owner, mode or size.")
            with os.fdopen(os.dup(fd), "rb") as stream:
                return stream.read(limit + 1)
        finally:
            os.close(fd)
    finally:
        os.close(parent)

def identity(pid):
    if pid <= 1:
        return None
    try:
        with open("/proc/%d/stat" % pid, encoding="ascii") as stream:
            record = stream.read()
    except FileNotFoundError:
        return None
    fields = record[record.rfind(")") + 2:].split()
    return int(fields[1]), int(fields[19])

def show(name):
    return subprocess.run(["systemctl", "show", "-p", name, "--value", UNIT],
                          check=True, capture_output=True, text=True, timeout=5).stdout.strip()

def snapshot():
    fragment = safe_path(show("FragmentPath"))
    if os.path.basename(fragment) != UNIT:
        reject("The installed service fragment has an unsupported name.")
    paths = [fragment] + shlex.split(show("DropInPaths"))
    return {path: hashlib.sha256(read(path)).hexdigest() for path in paths}

def configuration(paths):
    properties, environment = {}, {}
    for path in list(paths):
        section = ""
        for line in read(path).decode("utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith(("#", ";")):
                continue
            if line.startswith("[") and line.endswith("]"):
                section = line[1:-1]
            elif section == "Service" and "=" in line:
                key, value = line.split("=", 1)
                if line.endswith("\\"):
                    reject("A continued systemd directive is unsupported for seeded repair.")
                if key == "Environment":
                    if not value:
                        environment.clear()
                    for entry in shlex.split(value):
                        name, separator, setting = entry.partition("=")
                        if separator:
                            environment[name] = setting
                else:
                    properties[key] = value
                if key == "EnvironmentFile":
                    for entry in shlex.split(value):
                        optional, external = entry.startswith("-"), entry.lstrip("-")
                        if any(c in external for c in "*?%$"):
                            reject("The seeded repair cannot preserve a dynamic EnvironmentFile path.")
                        try:
                            paths[external] = hashlib.sha256(read(external)).hexdigest()
                        except FileNotFoundError:
                            if not optional:
                                raise
    return properties, environment

def origin(value, gateway=False):
    if any(c.isspace() for c in value):
        reject("The configured seed origin contains whitespace.")
    parsed = urlsplit(value.strip())
    _ = parsed.port
    if (parsed.scheme not in (("https",) if gateway else ("http", "https")) or
        not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or
        parsed.path.rstrip("/") not in (("",) if gateway else ("", "/api"))):
        reject("The configured seed API or gateway origin is invalid.")
    return parsed.scheme + "://" + parsed.netloc

def parameters(values):
    values["api_base"] = origin(values["api_base"])
    if values["gateway_endpoint"]:
        values["gateway_endpoint"] = origin(values["gateway_endpoint"], True)
    if (not isinstance(values["tenant_id"], int) or not 0 < values["tenant_id"] <= 2147483647 or
        not values["enrollment_code"].strip() or len(values["enrollment_code"]) > 512 or
        values["runtime"] not in ("linux-x64", "linux-arm64") or
        (values["version"] != "latest" and not re.fullmatch(SEMVER, values["version"]))):
        reject("The seeded update parameters are invalid.")

def installer_environment(request):
    env = os.environ.copy()
    for name, field in {"API_BASE": "api_base", "GATEWAY_ENDPOINT": "gateway_endpoint",
                        "TENANT_ID": "tenant_id", "ENROLLMENT_CODE": "enrollment_code",
                        "RUNTIME": "runtime", "VERSION": "version"}.items():
        env["NETRATEL_SEED_" + name] = str(request[field])
    env["NetRatel_ROOT"] = request["root_dir"]
    env["NetRatel_STATE"] = request["state_dir"]
    env["NetRatel_SYSTEMD_UNIT_DIR"] = request["unit_dir"]
    env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = request["bundle_extract_dir"]
    env["NETRATEL_SEED_VALID_TO_UTC"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 3600))
    return env

def dispatch(installer, helper):
    if os.geteuid() != 0 and not TEST_MODE:
        reject("The seeded Linux service repair must run as root.")
    paths = snapshot()
    unit_paths = list(paths)
    properties, environment = configuration(paths)
    working = properties.get("WorkingDirectory", "")
    # WorkingDirectory is a literal scalar path. Read historical quoted units
    # only to repair them; ExecStart and Environment retain their list grammar.
    if working.startswith('"') and working.endswith('"'):
        legacy = shlex.split(working)
        working = legacy[0] if len(legacy) == 1 else ""
    if not working.endswith("/current"):
        reject("The installed service WorkingDirectory must target the owned current version.")
    root = safe_path(working[:-len("/current")])
    if "\\" in root:
        reject("Systemd executable roots cannot contain a backslash; reconcile the installed service root.")
    command = shlex.split(properties.get("ExecStart", ""))
    if command not in ([root + "/netratel-client-start.sh"], [root + "/current/NetRatel.Client", "--service"]):
        reject("The installed service ExecStart is unsupported for seeded repair.")
    if properties.get("User", "root") not in ("", "root", "0") or properties.get("DynamicUser", "no").lower() not in ("no", "false", "0", ""):
        reject("Seeded service repair cannot change an existing systemd service identity.")
    state = environment.get("NetRatelCLIENT__Client__AutoUpdate__StateDirectory")
    if not state:
        updater_unit = os.path.join(os.path.dirname(unit_paths[0]), "netratel-update.service")
        try:
            updater_paths = {updater_unit: hashlib.sha256(read(updater_unit)).hexdigest()}
            _, updater_environment = configuration(updater_paths)
            state = updater_environment.get("NetRatel_UPDATE_STATE")
            paths.update(updater_paths)
        except FileNotFoundError:
            pass
    if not state:
        reject("The installed service must configure its updater state directory for seeded repair.")
    request = dict(schema="netratel.seeded-linux-update-handoff.v1", created_at=time.time(),
                   root_dir=root, state_dir=safe_path(state), unit_dir=os.path.dirname(next(iter(paths))),
                   bundle_extract_dir=environment.get("DOTNET_BUNDLE_EXTRACT_BASE_DIR", "/var/lib/netratel/bundle"),
                   api_base=os.environ["NETRATEL_SEED_API_BASE"], gateway_endpoint=os.environ["NETRATEL_SEED_GATEWAY_ENDPOINT"],
                   tenant_id=int(os.environ["NETRATEL_SEED_TENANT_ID"]), enrollment_code=os.environ["NETRATEL_SEED_ENROLLMENT_CODE"],
                   runtime=os.environ["NETRATEL_SEED_RUNTIME"], version=os.environ["NETRATEL_SEED_VERSION"], unit_hashes=paths, unit_paths=unit_paths)
    parameters(request)
    root_fd = directory(root)
    os.close(root_fd)
    state_fd = directory(request["state_dir"], True)
    try:
        try:
            os.mkdir("install-handoffs", 0o700, dir_fd=state_fd)
        except FileExistsError:
            pass
    finally:
        os.close(state_fd)
    handoff_dir = os.path.join(request["state_dir"], "install-handoffs")
    handoff_fd = directory(handoff_dir, True)
    handoff_id = secrets.token_hex(16)
    request["handoff_id"] = handoff_id
    request["origin_pid"] = int(os.environ["NETRATEL_SEED_ORIGIN_PID"])
    request["origin_start_ticks"] = identity(request["origin_pid"])[1]
    request["service_pid"] = int(show("MainPID"))
    service = identity(request["service_pid"])
    request["service_start_ticks"] = service[1] if service else 0
    names = ["handoff-" + handoff_id + suffix for suffix in (".json", ".sh", ".py")]
    payloads = [None, read(installer, True), read(helper, True)]
    request["installer_sha256"] = hashlib.sha256(payloads[1]).hexdigest()
    request["worker_sha256"] = hashlib.sha256(payloads[2]).hexdigest()
    payloads[0] = json.dumps(request).encode("utf-8")
    try:
        for name, payload in zip(names, payloads):
            fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=handoff_fd)
            with os.fdopen(fd, "wb") as stream:
                stream.write(payload)
        argv = ["systemd-run", "--collect", "--no-block", "--unit=netratel-client-repair-" + handoff_id]
        if TEST_MODE:
            argv.append("--setenv=NetRatel_TEST_ALLOW_NONROOT=true")
        argv.extend(["/usr/bin/python3", os.path.join(handoff_dir, names[2]), "worker", os.path.join(handoff_dir, names[0])])
        subprocess.run(argv, check=True, capture_output=True, timeout=10)
    except BaseException:
        for name in names:
            try:
                os.unlink(name, dir_fd=handoff_fd)
            except FileNotFoundError:
                pass
        raise
    finally:
        os.close(handoff_fd)

def worker(request_path):
    handoff_dir = os.path.dirname(safe_path(request_path))
    fd = directory(handoff_dir, True)
    names = []
    try:
        if not re.fullmatch(r"handoff-[a-f0-9]{32}\.json", os.path.basename(request_path)):
            reject("The handoff request name is invalid.")
        names = [os.path.basename(request_path)[:-5] + suffix for suffix in (".json", ".sh", ".py")]
        request = json.loads(read(request_path, True, 65536))
        if (request.get("schema") != "netratel.seeded-linux-update-handoff.v1" or
            request.get("handoff_id") != names[0][8:-5] or not -5 <= time.time() - request["created_at"] <= 120 or
            handoff_dir != os.path.join(safe_path(request["state_dir"]), "install-handoffs") or
            os.path.abspath(__file__) != os.path.join(handoff_dir, names[2])):
            reject("The handoff request identity, lifetime or layout is invalid.")
        parameters(request)
        for name, field in ((names[1], "installer_sha256"), (names[2], "worker_sha256")):
            if hashlib.sha256(read(os.path.join(handoff_dir, name), True)).hexdigest() != request[field]:
                reject("A protected installer or worker changed during handoff.")
        deadline = time.monotonic() + 30
        while True:
            origin_process = identity(request["origin_pid"])
            if origin_process is None or origin_process[1] != request["origin_start_ticks"]:
                break
            if time.monotonic() >= deadline:
                reject("The originating shell did not exit before the handoff deadline.")
            time.sleep(0.1)
        if snapshot() != {path: request["unit_hashes"][path] for path in request["unit_paths"]}:
            reject("The installed service unit changed during handoff.")
        for path, digest in request["unit_hashes"].items():
            if hashlib.sha256(read(path)).hexdigest() != digest:
                reject("The installed service configuration changed during handoff.")
        pid = int(show("MainPID"))
        current = identity(pid)
        if pid != request["service_pid"] or (current[1] if current else 0) != request["service_start_ticks"]:
            reject("The installed service process identity changed during handoff.")
        return subprocess.run(["/bin/bash", os.path.join(handoff_dir, names[1])],
                              env=installer_environment(request), timeout=600).returncode
    finally:
        for name in names:
            try:
                os.unlink(name, dir_fd=fd)
            except FileNotFoundError:
                pass
        os.close(fd)

try:
    if sys.argv[1] == "dispatch":
        dispatch(sys.argv[2], sys.argv[3])
    elif sys.argv[1] == "worker":
        sys.exit(worker(sys.argv[2]))
    else:
        reject("The handoff mode is invalid.")
except Exception as error:
    print("Linux update handoff failed: " + (str(error) if isinstance(error, HandoffRejected) else type(error).__name__), file=sys.stderr)
    sys.exit(70)
""";
}
