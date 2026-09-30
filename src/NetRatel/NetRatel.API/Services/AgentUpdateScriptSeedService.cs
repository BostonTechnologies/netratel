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
        const string apiBasePlaceholder = "https://netratel-seed.invalid";
        const string enrollmentCodePlaceholder = "NETRATEL_SEED_ENROLLMENT_CODE_PLACEHOLDER";
        const string validToPlaceholder = "2035-01-01T00:00:00.0000000Z";
        const int tenantPlaceholder = 2_147_483_000;

        var installer = new NetRatel.Infrastructure.Artifacts.ScriptTemplateService().Build(
            new NetRatel.Application.Artifacts.DeploymentScriptTemplateRequest(
                tenantPlaceholder,
                "win-x64",
                enrollmentCodePlaceholder,
                apiBasePlaceholder,
                DateTimeOffset.Parse(validToPlaceholder, System.Globalization.CultureInfo.InvariantCulture),
                InstallAsService: true,
                SilentInstall: false));
        installer = installer.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        installer = ReplaceExactlyOnce(installer,
            $"$ApiBase = \"{apiBasePlaceholder}\"",
            "$ApiBase = Get-NetRatelSeedApiBase $ApiBase");
        installer = ReplaceExactlyOnce(installer,
            $"$GatewayEndpoint = \"\"",
            "$GatewayEndpoint = Get-NetRatelSeedGatewayEndpoint $GatewayEndpoint $ApiBase");
        installer = ReplaceExactlyOnce(installer,
            $"$TenantId = {tenantPlaceholder}",
            "$TenantId = [int]$TenantId");
        installer = ReplaceExactlyOnce(installer,
            $"$EnrollmentCode = \"{enrollmentCodePlaceholder}\"",
            "$EnrollmentCode = [string]$EnrollmentCode");
        installer = ReplaceExactlyOnce(installer,
            "$Runtime = \"win-x64\"",
            "$Runtime = [string]$Runtime");
        installer = ReplaceExactlyOnce(installer,
            "$Version = \"latest\"",
            "$Version = if ([string]::IsNullOrWhiteSpace($Version)) { \"latest\" } else { [string]$Version }");
        installer = ReplaceExactlyOnce(installer,
            "# NetRatel seeded state-path validation extension point.",
            "if ($script:NetRatelSeedHandoffMode) {\n" +
            "    $handoffStatePath = [System.IO.Path]::GetFullPath([string]$NetRatelSeedHandoffStateDirectory).TrimEnd([char[]]@('\\', '/'))\n" +
            "    $effectiveStatePath = [System.IO.Path]::GetFullPath([string]$StateDir).TrimEnd([char[]]@('\\', '/'))\n" +
            "    if (-not [string]::Equals($handoffStatePath, $effectiveStatePath, [StringComparison]::OrdinalIgnoreCase)) {\n" +
            "        throw 'Installer handoff state directory does not match the configured updater state path.'\n" +
            "    }\n" +
            "}");
        installer = ReplaceExactlyOnce(installer,
            $"Write-Host \"Enrollment code valid until {validToPlaceholder}\"",
            "Write-Host \"Enrollment grant validity is enforced by the API.\"");
        installer = ReplaceExactlyOnce(installer,
            $"validToUtc = '{validToPlaceholder}'",
            "validToUtc = [DateTimeOffset]::UtcNow.AddHours(1).ToString('O')");

        installer = AddWindowsHandoffLifecycle(installer);
        return new SeedScript(
            "Update Client To Latest",
            "/Windows/NetRatel",
            "Repair or roll forward a Windows NetRatel service client using a verified staged installer and service readiness handoff.",
            "PowerShell",
            WindowsScriptManifest + Environment.NewLine + WindowsHandoffPreamble + Environment.NewLine + installer);
    }

    private static string ReplaceExactlyOnce(string content, string oldValue, string newValue)
    {
        var first = content.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || content.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("The generated Windows installer template no longer matches the seeded update-script contract.");
        }

        return content[..first] + newValue + content[(first + oldValue.Length)..];
    }

    private static string AddWindowsHandoffLifecycle(string installer)
    {
        const string successMarker = "    Write-Host \"NetRatel service installation complete; authenticated gateway heartbeat readiness was verified.\"\n    $script:InstallerLastCompletedPhase = 'installation-complete'\n}";
        const string handoffStartMarker = "    # Seed handoff integration point.";
        const string preflightFailureMarker = "    # NetRatel installer preflight failure-result extension point.";
        const string failureMarker = "catch {\n    Write-NetRatelInstallerFailureSummary $_\n    throw\n}\nfinally {\n    if ($stageDir -and (Test-Path -LiteralPath $stageDir)) { Remove-Item -LiteralPath $stageDir -Recurse -Force }\n    if (Test-Path $tempDir) {\n        Remove-Item $tempDir -Recurse -Force\n    }\n    if ($null -ne $updateLock) { $updateLock.Dispose() }";

        installer = ReplaceExactlyOnce(installer, handoffStartMarker,
            "    Start-NetRatelSeedHandoff");
        installer = ReplaceExactlyOnce(installer, preflightFailureMarker,
            "    if ($script:NetRatelSeedHandoffMode -and $script:NetRatelSeedHandoffResultPath -and $null -eq $updateLock) {\n" +
            "        try { Set-NetRatelSeedHandoffResult -State 'failed' -FailureCode 'installer_failed' -ExceptionType $_.Exception.GetType().Name }\n" +
            "        catch { Write-Warning 'The installer could not record its preflight failure result.' }\n" +
            "    }");
        installer = ReplaceExactlyOnce(installer, successMarker,
            "    Write-Host \"NetRatel service installation complete; authenticated gateway heartbeat readiness was verified.\"\n" +
            "    $script:InstallerLastCompletedPhase = 'installation-complete'\n" +
            "    Set-NetRatelSeedHandoffResult -State 'heartbeat_ready' -ReadyRecord $readyRecord\n}");
        installer = ReplaceExactlyOnce(installer, failureMarker,
            "catch {\n" +
            "    $installerFailure = $_\n" +
            "    Write-NetRatelInstallerFailureSummary $installerFailure\n" +
            "    if ($script:NetRatelSeedHandoffMode -and $script:NetRatelSeedHandoffResultPath -and $null -eq $updateLock) {\n" +
            "        try { Set-NetRatelSeedHandoffResult -State 'failed' -FailureCode 'installer_failed' -ExceptionType $installerFailure.Exception.GetType().Name }\n" +
            "        catch { Write-Warning 'The installer could not record its preflight failure result.' }\n" +
            "    }\n" +
            "    elseif ($null -ne $updateLock -and $script:NetRatelSeedHandoffResultPath) {\n" +
            "        $failureCode = if ($script:NetRatelSeedHandoffPreflightFailureCode) { $script:NetRatelSeedHandoffPreflightFailureCode } else { 'installer_failed' }\n" +
            "        $failureType = if ($script:NetRatelSeedHandoffPreflightExceptionType) { $script:NetRatelSeedHandoffPreflightExceptionType } else { $installerFailure.Exception.GetType().Name }\n" +
            "        try { Set-NetRatelSeedHandoffResult -State 'failed' -FailureCode $failureCode -ExceptionType $failureType }\n" +
            "        catch { Write-Warning 'The installer could not record its failure result.' }\n" +
            "    }\n" +
            "    throw\n}\n" +
            "finally {\n" +
            "    if ($stageDir -and (Test-Path -LiteralPath $stageDir)) { Remove-Item -LiteralPath $stageDir -Recurse -Force }\n" +
            "    if (Test-Path $tempDir) {\n        Remove-Item $tempDir -Recurse -Force\n    }\n" +
            "    if ($script:NetRatelSeedHandoffMode -and $null -ne $updateLock) {\n" +
            "        Remove-Item -LiteralPath $script:NetRatelSeedHandoffRequestPath -Force -ErrorAction SilentlyContinue\n" +
            "        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue\n" +
            "    }\n" +
            "    if ($null -ne $updateLock) { $updateLock.Dispose() }");
        return installer;
    }

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
    if ([string]::Equals($origin, $ApiOrigin, [StringComparison]::OrdinalIgnoreCase)) { return "" }
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
        [string] $ExceptionType,
        [object] $ReadyRecord
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
    if ($ReadyRecord) {
        $result.agentId = [string]$ReadyRecord.agentId
        $result.tenantId = [int]$ReadyRecord.tenantId
        $result.connectionEpoch = [UInt64]$ReadyRecord.connectionEpoch
        $result.connectionId = [string]$ReadyRecord.connectionId
        $result.processId = [int]$ReadyRecord.processId
        $result.observedAtUtc = [string]$ReadyRecord.observedAtUtc
    }
    $temporaryPath = "$($script:NetRatelSeedHandoffResultPath).$PID.tmp"
    [System.IO.File]::WriteAllText($temporaryPath, ($result | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $script:NetRatelSeedHandoffResultPath -Force
}

function Start-NetRatelSeedHandoff {
if ($script:NetRatelSeedHandoffPreflightFailureCode) {
    throw "Installer handoff validation failed."
}
if ($script:NetRatelSeedHandoffMode) {
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
    $allowLegacyStateAncestors = [bool]$script:NetRatelStateAncestorAllowance
    if (Test-Path -LiteralPath $handoffDirectory) {
        Assert-NetRatelTrustedReadinessPath $handoffDirectory $false $false $true $false $allowLegacyStateAncestors
    }
    else {
        New-NetRatelProtectedDirectory $handoffDirectory (Get-NetRatelTrustedStateSids) $allowLegacyStateAncestors
    }

    $handoffId = [Guid]::NewGuid().ToString("N")
    $handoffNonceBytes = New-Object byte[] 32
    $handoffRandom = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $handoffRandom.GetBytes($handoffNonceBytes) } finally { $handoffRandom.Dispose() }
    $handoffNonce = [Convert]::ToBase64String($handoffNonceBytes)
    [Array]::Clear($handoffNonceBytes, 0, $handoffNonceBytes.Length)
    $handoffRequestPath = Join-Path $handoffDirectory "handoff-$handoffId.json"
    $handoffScriptPath = Join-Path $handoffDirectory "handoff-$handoffId.ps1"
    $handoffResultPath = Join-Path $handoffDirectory "handoff-$handoffId.result.json"
    $originProcess = Get-Process -Id $PID -ErrorAction Stop
    $request = [ordered]@{
        schema = "netratel.seeded-update-handoff.v1"
        handoffId = $handoffId
        nonce = $handoffNonce
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        expiresAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(2).ToString("O")
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
        Copy-Item -LiteralPath $PSCommandPath -Destination $handoffScriptPath -ErrorAction Stop
        [System.IO.File]::WriteAllText($handoffRequestPath, ($request | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
        $powershellPath = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
        if (-not (Test-Path -LiteralPath $powershellPath -PathType Leaf)) { throw "Windows PowerShell could not be located for the detached installer." }
        $arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$handoffScriptPath`" -NetRatelSeedHandoffRequestPath `"$handoffRequestPath`" -NetRatelSeedHandoffStateDirectory `"$stateDirectory`""
        $worker = Start-Process -FilePath $powershellPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -ErrorAction Stop
        if (-not $worker -or $worker.Id -le 0) { throw "The independent installer process could not be started." }
        $script:NetRatelSeedHandoffId = $handoffId
        $script:NetRatelSeedHandoffResultPath = $handoffResultPath
        $script:NetRatelSeedHandoffTenantId = $TenantId
        Set-NetRatelSeedHandoffResult -State "handed_off"
        Write-Host "NetRatel update repair handed off to independent installer process; handoffId=$handoffId. Service readiness will be reported after a fresh SYSTEM gateway heartbeat acknowledgment."
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
    $nonceBytes = [Convert]::FromBase64String([string]$handoff.nonce)
    if ($handoff.schema -ne "netratel.seeded-update-handoff.v1" -or $handoff.handoffId -ne $handoffId -or
        $nonceBytes.Length -ne 32 -or $createdAt -gt $now.AddSeconds(5) -or $expiresAt -le $now -or
        $expiresAt -gt $createdAt.AddMinutes(2) -or $now - $createdAt -gt [TimeSpan]::FromMinutes(2) -or
        -not (Test-Path -LiteralPath $ownedHandoffScriptPath -PathType Leaf) -or
        [int]$handoff.tenantId -le 0 -or [string]::IsNullOrWhiteSpace([string]$handoff.enrollmentCode) -or
        ([string]$handoff.enrollmentCode).Length -gt 512 -or
        [string]$handoff.runtime -notin @("win-x64", "win-arm64") -or
        ([string]$handoff.version -ne "latest" -and [string]$handoff.version -notmatch '^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')) {
        throw "Installer handoff request is invalid, stale, or incomplete."
    }
    [Array]::Clear($nonceBytes, 0, $nonceBytes.Length)
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
        const int tenantPlaceholder = 2_147_483_000;
        const string apiBasePlaceholder = "https://netratel-seed.invalid";
        const string enrollmentCodePlaceholder = "NETRATEL_SEED_ENROLLMENT_CODE_PLACEHOLDER";

        var installer = new NetRatel.Infrastructure.Artifacts.ScriptTemplateService().Build(
            new NetRatel.Application.Artifacts.DeploymentScriptTemplateRequest(
                tenantPlaceholder,
                "linux-x64",
                enrollmentCodePlaceholder,
                apiBasePlaceholder,
                DateTimeOffset.Parse("2035-01-01T00:00:00.0000000Z", System.Globalization.CultureInfo.InvariantCulture),
                InstallAsService: true,
                SilentInstall: false));
        installer = installer.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        installer = ReplaceLinuxExactlyOnce(installer,
            $"API_BASE=\"{apiBasePlaceholder}\"",
            "API_BASE=\"$NETRATEL_SEED_API_BASE\"");
        installer = ReplaceLinuxExactlyOnce(installer,
            "GATEWAY_ENDPOINT=\"\"",
            "GATEWAY_ENDPOINT=\"$NETRATEL_SEED_GATEWAY_ENDPOINT\"");
        installer = ReplaceLinuxExactlyOnce(installer,
            $"TENANT_ID={tenantPlaceholder}",
            "TENANT_ID=\"$NETRATEL_SEED_TENANT_ID\"");
        installer = ReplaceLinuxExactlyOnce(installer,
            $"ENROLLMENT_CODE=\"{enrollmentCodePlaceholder}\"",
            "ENROLLMENT_CODE=\"$NETRATEL_SEED_ENROLLMENT_CODE\"");
        installer = ReplaceLinuxExactlyOnce(installer,
            "RUNTIME=\"linux-x64\"",
            "RUNTIME=\"$NETRATEL_SEED_RUNTIME\"");
        installer = ReplaceLinuxExactlyOnce(installer,
            "VERSION=\"latest\"",
            "VERSION=\"$NETRATEL_SEED_VERSION\"");

        var content = $$"""
#!/usr/bin/env bash
set -euo pipefail
umask 077

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
chmod 0700 "$SEED_TEMP_DIR"
trap 'rm -rf -- "$SEED_TEMP_DIR"' EXIT
NORMAL_INSTALLER="$SEED_TEMP_DIR/netratel-installer.sh"
NORMAL_WORKER="$SEED_TEMP_DIR/netratel-worker.py"
cat > "$NORMAL_INSTALLER" <<'NETRATEL_NORMAL_INSTALLER'
{{installer}}
NETRATEL_NORMAL_INSTALLER
cat > "$NORMAL_WORKER" <<'NETRATEL_NORMAL_WORKER'
{{LinuxSeedWorker}}
NETRATEL_NORMAL_WORKER
chmod 0600 "$NORMAL_INSTALLER" "$NORMAL_WORKER"

LAYOUT_OUTPUT="$(python3 - "$NORMAL_INSTALLER" "$NORMAL_WORKER" <<'NETRATEL_COORDINATOR'
{{LinuxSeedCoordinator}}
NETRATEL_COORDINATOR
)"
{
  IFS= read -r NETRATEL_LAYOUT_ROOT
  IFS= read -r NETRATEL_LAYOUT_STATE
  IFS= read -r NETRATEL_LAYOUT_UNIT_DIR
  IFS= read -r NETRATEL_LAYOUT_BUNDLE_DIR
  IFS= read -r NETRATEL_LAYOUT_MODE
  IFS= read -r NETRATEL_LAYOUT_ID
} <<< "$LAYOUT_OUTPUT"
if [ -z "$NETRATEL_LAYOUT_ROOT" ] || [ -z "$NETRATEL_LAYOUT_STATE" ] ||
   [ -z "$NETRATEL_LAYOUT_UNIT_DIR" ] || [ -z "$NETRATEL_LAYOUT_BUNDLE_DIR" ]; then
  echo "The installed Linux service layout could not be verified." >&2
  exit 1
fi
export NetRatel_ROOT="$NETRATEL_LAYOUT_ROOT"
export NetRatel_STATE="$NETRATEL_LAYOUT_STATE"
export NetRatel_SYSTEMD_UNIT_DIR="$NETRATEL_LAYOUT_UNIT_DIR"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$NETRATEL_LAYOUT_BUNDLE_DIR"
if [ "$NETRATEL_LAYOUT_MODE" = "handoff" ]; then
  echo "NetRatel Linux repair was handed to a detached root worker; the service will stop only after this command exits."
  exit 0
fi
if [ "$NETRATEL_LAYOUT_MODE" != "direct" ]; then
  echo "The installed Linux service layout could not be verified." >&2
  exit 1
fi
/bin/bash "$NORMAL_INSTALLER"
""";

        return new SeedScript(
            "Update Client To Latest",
            "/Linux/NetRatel",
            "Repair or roll forward a Linux NetRatel service client using the normal verified artifact installer and a detached service handoff.",
            "Bash",
            LinuxScriptManifest + Environment.NewLine + content);
    }

    private static string ReplaceLinuxExactlyOnce(string content, string oldValue, string newValue)
    {
        var first = content.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || content.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("The generated Linux installer template no longer matches the seeded update-script contract.");
        }

        return content[..first] + newValue + content[(first + oldValue.Length)..];
    }

    private const string LinuxSeedWorker = """
import hashlib
import json
import os
import stat
import subprocess
import sys
import time
from urllib.parse import urlsplit
import re

REQUEST_PATH = os.path.abspath(sys.argv[1])
TEST_MODE = os.environ.get("NetRatel_TEST_ALLOW_NONROOT") == "true"
EXPECTED_OWNER = os.geteuid() if TEST_MODE else 0
DIR_FLAGS = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)

def reject(message):
    raise RuntimeError(message)

def open_directory(path, private=False):
    path = os.path.abspath(path)
    descriptor = os.open(os.sep, DIR_FLAGS)
    try:
        for index, part in enumerate(part for part in path.split(os.sep) if part):
            child = os.open(part, DIR_FLAGS, dir_fd=descriptor)
            info = os.fstat(child)
            final = index == len([value for value in path.split(os.sep) if value]) - 1
            if not stat.S_ISDIR(info.st_mode):
                os.close(child)
                reject("A protected path component is not a directory.")
            if final and info.st_uid != EXPECTED_OWNER:
                os.close(child)
                reject("A protected directory has an unexpected owner.")
            if not final and info.st_uid not in (0, EXPECTED_OWNER):
                os.close(child)
                reject("A protected path ancestor has an unexpected owner.")
            mode = stat.S_IMODE(info.st_mode)
            if final and mode & 0o022:
                os.close(child)
                reject("A protected directory is group or world writable.")
            if final and private and mode & 0o077:
                os.close(child)
                reject("The protected state directory is not private.")
            if not final and mode & 0o022 and not mode & stat.S_ISVTX:
                os.close(child)
                reject("A protected path ancestor is writable by an untrusted identity.")
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise

def read_file(parent_fd, name, private=False):
    fd = os.open(name, os.O_RDONLY | NOFOLLOW, dir_fd=parent_fd)
    try:
        info = os.fstat(fd)
        mode = stat.S_IMODE(info.st_mode)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != EXPECTED_OWNER or mode & 0o022:
            reject("A protected handoff file has an unexpected type, owner, or mode.")
        if private and mode & 0o077:
            reject("A protected handoff file is not private.")
        with os.fdopen(os.dup(fd), "rb") as stream:
            return stream.read()
    finally:
        os.close(fd)

def process_identity(pid):
    if pid <= 1:
        return None
    try:
        with open("/proc/%d/stat" % pid, encoding="ascii") as stream:
            line = stream.read()
    except FileNotFoundError:
        return None
    fields = line[line.rfind(")") + 2:].split()
    if len(fields) <= 19:
        reject("A process identity record is incomplete.")
    return fields[0], int(fields[1]), int(fields[19])

def systemd_value(name):
    return subprocess.run(
        ["systemctl", "show", "-p", name, "--value", "netratel-client.service"],
        check=True, capture_output=True, text=True, timeout=5).stdout.strip()

def verify_unit(request):
    fragment = systemd_value("FragmentPath")
    if os.path.realpath(fragment) != request["unit_path"] or os.path.islink(fragment):
        reject("The installed service unit identity changed.")
    unit_dir_fd = open_directory(request["unit_dir"])
    try:
        unit_bytes = read_file(unit_dir_fd, os.path.basename(fragment))
    finally:
        os.close(unit_dir_fd)
    import hashlib
    if hashlib.sha256(unit_bytes).hexdigest() != request["unit_sha256"]:
        reject("The installed service unit changed during handoff.")
    lines = unit_bytes.decode("utf-8", errors="strict").splitlines()
    if ("WorkingDirectory=" + request["root_dir"] + "/current" not in lines or
            "ExecStart=" + request["root_dir"] + "/netratel-client-start.sh" not in lines):
        reject("The service no longer targets the owned client layout.")
    main_pid = int(systemd_value("MainPID"))
    identity = process_identity(main_pid)
    if main_pid != request["service_pid"] or identity is None or identity[2] != request["service_start_ticks"]:
        reject("The NetRatel service process identity changed during handoff.")
    active = subprocess.run(
        ["systemctl", "is-active", "--quiet", "netratel-client.service"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
    if active.returncode != 0:
        reject("The NetRatel service is not active after handoff.")

def validate_request(request, handoff_dir):
    if not isinstance(request, dict) or request.get("schema") != "netratel.seeded-linux-update-handoff.v1":
        reject("The Linux handoff request schema is invalid.")
    handoff_id = request.get("handoff_id", "")
    if not re.fullmatch(r"[0-9a-f]{32}", handoff_id):
        reject("The Linux handoff identity is invalid.")
    if time.time() - float(request.get("created_at", 0)) > 120 or float(request.get("created_at", 0)) > time.time() + 5:
        reject("The Linux handoff request has expired.")
    if int(request["tenant_id"]) <= 0 or int(request["tenant_id"]) > 2147483647:
        reject("The Linux handoff tenant is invalid.")
    if not isinstance(request["enrollment_code"], str) or not request["enrollment_code"].strip() or len(request["enrollment_code"]) > 512:
        reject("The Linux handoff enrollment input is invalid.")
    if request["runtime"] not in ("linux-x64", "linux-arm64"):
        reject("The Linux handoff runtime is invalid.")
    version = request["version"]
    if version != "latest" and not __import__("re").fullmatch(
            r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?", version):
        reject("The Linux handoff version is invalid.")
    api = urlsplit(request["api_base"])
    if api.scheme not in ("http", "https") or not api.netloc or api.username or api.password or api.query or api.fragment or api.path:
        reject("The Linux handoff API origin is invalid.")
    gateway = request["gateway_endpoint"]
    if gateway:
        parsed = urlsplit(gateway)
        if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path:
            reject("The Linux handoff gateway origin is invalid.")
    if (request.get("installer_name") != "handoff-" + handoff_id + ".sh" or
            request.get("worker_name") != "handoff-" + handoff_id + ".py"):
        reject("The Linux handoff file identity is invalid.")
    expected_path = os.path.join(handoff_dir, "handoff-" + handoff_id + ".json")
    if os.path.realpath(REQUEST_PATH) != expected_path or request["request_path"] != expected_path:
        reject("The Linux handoff request path changed.")
    if (os.path.realpath(handoff_dir) != handoff_dir or
            os.path.realpath(request["state_dir"]) != request["state_dir"] or
            os.path.dirname(handoff_dir) != request["state_dir"] or
            handoff_dir != os.path.join(request["state_dir"], "install-handoffs") or
            os.path.dirname(request["unit_path"]) != request["unit_dir"] or
            os.path.basename(request["unit_path"]) != "netratel-client.service" or
            request["unit_path"] != os.path.join(request["unit_dir"], "netratel-client.service")):
        reject("The Linux handoff layout changed.")
    if not re.fullmatch(r"[0-9a-f]{64}", request.get("unit_sha256", "")):
        reject("The Linux handoff unit identity is invalid.")
    for name in ("origin_pid", "origin_start_ticks", "service_pid", "service_start_ticks"):
        if int(request.get(name, 0)) <= 1:
            reject("The Linux handoff process identity is invalid.")

def run():
    if len(sys.argv) != 2:
        reject("The Linux handoff worker arguments are invalid.")
    handoff_dir = os.path.dirname(REQUEST_PATH)
    state_dir = os.path.dirname(handoff_dir)
    state_fd = open_directory(state_dir, private=True)
    handoff_fd = None
    request = {}
    try:
        handoff_fd = open_directory(handoff_dir, private=True)
        request_bytes = read_file(handoff_fd, os.path.basename(REQUEST_PATH), private=True)
        request = json.loads(request_bytes)
        validate_request(request, handoff_dir)
        expected_worker_path = os.path.join(handoff_dir, request["worker_name"])
        if (os.path.abspath(sys.argv[0]) != expected_worker_path or
                os.path.realpath(sys.argv[0]) != expected_worker_path or os.path.islink(sys.argv[0])):
            reject("The Linux handoff worker path changed.")
        worker_bytes = read_file(handoff_fd, request["worker_name"], private=True)
        if hashlib.sha256(worker_bytes).hexdigest() != request["worker_sha256"]:
            reject("The Linux handoff worker file changed.")
        installer_bytes = read_file(handoff_fd, request["installer_name"], private=True)
        if hashlib.sha256(installer_bytes).hexdigest() != request["installer_sha256"]:
            reject("The Linux handoff installer file changed.")
        deadline = time.monotonic() + 30
        origin_pid = int(request["origin_pid"])
        expected_origin_ticks = int(request["origin_start_ticks"])
        while True:
            origin = process_identity(origin_pid)
            if origin is None or origin[2] != expected_origin_ticks:
                break
            if time.monotonic() >= deadline:
                reject("The originating shell did not exit before the handoff deadline.")
            time.sleep(0.1)
        verify_unit(request)
        installer_path = os.path.join(handoff_dir, request["installer_name"])
        environment = os.environ.copy()
        environment.update({
            "NetRatel_ROOT": request["root_dir"],
            "NetRatel_STATE": request["state_dir"],
            "NetRatel_SYSTEMD_UNIT_DIR": request["unit_dir"],
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR": request["bundle_extract_dir"],
            "NETRATEL_SEED_API_BASE": request["api_base"],
            "NETRATEL_SEED_GATEWAY_ENDPOINT": request["gateway_endpoint"],
            "NETRATEL_SEED_TENANT_ID": str(request["tenant_id"]),
            "NETRATEL_SEED_ENROLLMENT_CODE": request["enrollment_code"],
            "NETRATEL_SEED_RUNTIME": request["runtime"],
            "NETRATEL_SEED_VERSION": request["version"],
            "NETRATEL_SEED_EXPECTED_SERVICE_PID": str(request["service_pid"]),
            "NETRATEL_SEED_EXPECTED_SERVICE_START_TICKS": str(request["service_start_ticks"]),
            "NETRATEL_SEED_EXPECTED_UNIT_SHA256": request["unit_sha256"],
        })
        return subprocess.run(["/bin/bash", installer_path], cwd=request["root_dir"], env=environment, check=False).returncode
    finally:
        if handoff_fd is not None:
            cleanup_names = [os.path.basename(REQUEST_PATH)]
            handoff_id = request.get("handoff_id", "") if isinstance(request, dict) else ""
            if re.fullmatch(r"[0-9a-f]{32}", handoff_id):
                cleanup_names.extend(("handoff-" + handoff_id + ".sh", "handoff-" + handoff_id + ".py"))
            for filename in cleanup_names:
                try:
                    os.unlink(filename, dir_fd=handoff_fd)
                except FileNotFoundError:
                    pass
            os.close(handoff_fd)
        os.close(state_fd)

try:
    sys.exit(run())
except SystemExit:
    raise
except Exception as error:
    print("Linux update handoff failed: " + type(error).__name__, file=sys.stderr)
    sys.exit(70)
""";

    private const string LinuxSeedCoordinator = """
import hashlib
import json
import os
import re
import secrets
import shlex
import stat
import subprocess
import sys
import time
from urllib.parse import urlsplit

installer_source, worker_source = sys.argv[1:]
test_mode = os.environ.get("NetRatel_TEST_ALLOW_NONROOT") == "true"
if os.geteuid() != 0 and not test_mode:
    raise SystemExit("The seeded Linux service update must run as root.")
expected_owner = 0 if not test_mode else os.geteuid()
directory_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
no_follow = getattr(os, "O_NOFOLLOW", 0)

def reject(message):
    raise SystemExit(message)

def checked_path(value, label):
    if not value or not os.path.isabs(value) or any(character in value for character in "\t\r\n"):
        reject(label + " path is invalid.")
    value = os.path.normpath(value)
    if any(character.isspace() for character in value):
        reject(label + " path may not contain whitespace.")
    return value

def open_directory(path, private=False):
    path = checked_path(os.path.abspath(path), "Managed")
    descriptor = os.open(os.sep, directory_flags)
    try:
        parts = [part for part in path.split(os.sep) if part]
        for index, part in enumerate(parts):
            child = os.open(part, directory_flags, dir_fd=descriptor)
            info = os.fstat(child)
            final = index == len(parts) - 1
            mode = stat.S_IMODE(info.st_mode)
            if not stat.S_ISDIR(info.st_mode):
                os.close(child)
                reject("Managed service path contains a non-directory component.")
            if final and info.st_uid != expected_owner:
                os.close(child)
                reject("Managed service path has an unexpected owner.")
            if not final and info.st_uid not in (0, expected_owner):
                os.close(child)
                reject("Managed service path ancestor has an unexpected owner.")
            if final and mode & 0o022:
                os.close(child)
                reject("Managed service path is group or world writable.")
            if final and private and mode & 0o077:
                os.close(child)
                reject("Managed state path is not private.")
            if not final and mode & 0o022 and not mode & stat.S_ISVTX:
                os.close(child)
                reject("Managed service path has an untrusted writable ancestor.")
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise

def read_file(path, private=False, optional=False):
    parent, name = os.path.split(path)
    try:
        parent_fd = open_directory(parent)
    except FileNotFoundError:
        if optional:
            return None
        raise
    try:
        try:
            fd = os.open(name, os.O_RDONLY | no_follow, dir_fd=parent_fd)
        except FileNotFoundError:
            if optional:
                return None
            raise
        try:
            info = os.fstat(fd)
            mode = stat.S_IMODE(info.st_mode)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != expected_owner or mode & 0o022:
                reject("Managed service file has an unexpected owner, type, or mode.")
            if private and mode & 0o077:
                reject("Managed service file is not private.")
            with os.fdopen(os.dup(fd), "rb") as stream:
                return stream.read()
        finally:
            os.close(fd)
    finally:
        os.close(parent_fd)

def read_file_at(directory_fd, filename, private=False):
    descriptor = os.open(filename, os.O_RDONLY | no_follow, dir_fd=directory_fd)
    try:
        info = os.fstat(descriptor)
        mode = stat.S_IMODE(info.st_mode)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != expected_owner or mode & 0o022:
            reject("Managed service file has an unexpected owner, type, or mode.")
        if private and mode & 0o077:
            reject("Managed service file is not private.")
        with os.fdopen(os.dup(descriptor), "rb") as stream:
            return stream.read()
    finally:
        os.close(descriptor)

def process_identity(pid):
    if pid <= 1:
        return None
    try:
        with open("/proc/%d/stat" % pid, encoding="ascii") as stream:
            line = stream.read()
    except FileNotFoundError:
        return None
    fields = line[line.rfind(")") + 2:].split()
    if len(fields) <= 19:
        reject("The process identity could not be verified.")
    return fields[0], int(fields[1]), int(fields[19])

def systemd_value(name):
    return subprocess.run(
        ["systemctl", "show", "-p", name, "--value", "netratel-client.service"],
        check=True, capture_output=True, text=True, timeout=5).stdout.strip()

def normalize_origin(value, https_only=False):
    try:
        parsed = urlsplit(value.strip())
        scheme = parsed.scheme.lower()
        if scheme not in (("https",) if https_only else ("http", "https")):
            reject("The configured API or gateway origin uses an unsupported scheme.")
        if not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment:
            reject("The configured API or gateway endpoint contains unsupported components.")
        path = parsed.path.rstrip("/")
        if https_only:
            if path:
                reject("The configured gateway endpoint must be an HTTPS origin.")
        elif path.lower() not in ("", "/api"):
            reject("The configured API base must be an origin, optionally followed by /api.")
        host = parsed.hostname
        if not host or any(character.isspace() for character in parsed.netloc):
            reject("The configured API or gateway origin is invalid.")
        if ":" in host:
            host = "[" + host.lower() + "]"
        else:
            host = host.encode("idna").decode("ascii").lower()
        port = parsed.port
        if port is not None and not ((scheme == "https" and port == 443) or (scheme == "http" and port == 80)):
            host += ":" + str(port)
        return scheme + "://" + host
    except ValueError:
        reject("The configured API or gateway origin is invalid.")

def parse_unit(unit_bytes):
    section = ""
    properties = {}
    environment = {}
    for line in unit_bytes.decode("utf-8", errors="strict").splitlines():
        stripped = line.strip()
        if stripped.endswith("\\"):
            reject("The existing systemd service configuration uses a continued directive; repair cannot verify its effective identity.")
        if not stripped or stripped.startswith("#"):
            continue
        if stripped.startswith("[") and stripped.endswith("]"):
            section = stripped[1:-1]
            continue
        if section != "Service" or "=" not in stripped:
            continue
        key, value = stripped.split("=", 1)
        if key in ("WorkingDirectory", "ExecStart"):
            properties[key] = value
        elif key == "Environment":
            if not value.strip():
                environment.clear()
                continue
            for assignment in shlex.split(value):
                name, separator, setting = assignment.partition("=")
                if separator:
                    environment[name] = setting
    return properties, environment

def ancestor_contains(origin_pid, service_pid):
    current = origin_pid
    seen = set()
    for _ in range(128):
        if current == service_pid:
            return True
        if current <= 1 or current in seen:
            return False
        seen.add(current)
        identity = process_identity(current)
        if identity is None:
            return False
        current = identity[1]
    return False

protected_environment = {
    "NETRATEL_ROOT", "NETRATEL_STATE", "NETRATEL_SYSTEMD_UNIT_DIR",
    "NETRATELCLIENT__CLIENT__APIBASEURL", "NETRATELCLIENT__GATEWAY__ENDPOINT",
    "NETRATELCLIENT__CLIENT__AUTOUPDATE__STATEDIRECTORY",
    "NETRATELCLIENT__CLIENT__AUTOUPDATE__REQUESTPATH",
    "NETRATELCLIENT__CLIENT__AUTOUPDATE__READYPATH",
    "NETRATEL_UPDATE_ROOT", "NETRATEL_UPDATE_STATE", "NETRATEL_UPDATE_REQUEST",
    "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
}
protected_dropin_directives = {
    "execstart", "workingdirectory", "rootdirectory", "rootimage", "user", "dynamicuser",
}

def check_environment_file(path, optional):
    if not os.path.isabs(path) or any(character in path for character in "\t\r\n*?%$"):
        reject("The existing service configuration references an unsupported EnvironmentFile path.")
    try:
        contents = read_file(path, optional=optional)
    except FileNotFoundError:
        if optional:
            return
        reject("A required service EnvironmentFile is missing.")
    if contents is None:
        return
    for line in contents.decode("utf-8", errors="strict").splitlines():
        if line.rstrip().endswith("\\"):
            reject("An external EnvironmentFile uses a continued assignment; repair cannot verify its effective NetRatel configuration.")
        match = re.match(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=", line)
        if match and match.group(1).upper() in protected_environment:
            reject("An external EnvironmentFile overrides a NetRatel endpoint or owned service path.")

def inspect_unit_environment(unit_bytes, is_dropin=False):
    _, environment = parse_unit(unit_bytes)
    section = ""
    for line in unit_bytes.decode("utf-8", errors="strict").splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if stripped.startswith("[") and stripped.endswith("]"):
            section = stripped[1:-1]
            continue
        if section != "Service" or "=" not in stripped:
            continue
        name, value = stripped.split("=", 1)
        lower_name = name.lower()
        if lower_name in ("rootdirectory", "rootimage"):
            reject("The systemd service uses a filesystem namespace that repair cannot preserve safely.")
        if is_dropin and lower_name in protected_dropin_directives:
            reject("A systemd drop-in changes the owned service identity; repair requires a directly configured NetRatel service unit.")
        if not is_dropin and lower_name == "user" and value.strip().lower() not in ("", "root", "0"):
            reject("The existing systemd service does not run as root; repair will not change its service identity.")
        if not is_dropin and lower_name == "dynamicuser" and value.strip().lower() not in ("", "no", "false", "off", "0"):
            reject("The existing systemd service uses a dynamic user; repair cannot preserve that identity safely.")
        if is_dropin and lower_name == "environment":
            if not value.strip():
                reject("A systemd drop-in resets the owned service environment; repair cannot safely preserve it.")
            if any(variable.upper() in protected_environment for variable in environment):
                reject("A systemd drop-in overrides a NetRatel endpoint or owned service path.")
        if lower_name == "environmentfile":
            for item in shlex.split(value):
                optional = item.startswith("-")
                check_environment_file(item[1:] if optional else item, optional)
        elif lower_name in ("passenvironment", "unsetenvironment"):
            requested = {item.upper() for item in shlex.split(value)}
            if "*" in requested or requested.intersection(protected_environment):
                reject("The effective systemd service environment changes a protected NetRatel endpoint or path.")
def inspect_effective_unit(unit_name, unit_path):
    if os.path.lexists(unit_path):
        inspect_unit_environment(read_file(unit_path), False)
    result = subprocess.run(
        ["systemctl", "show", "-p", "DropInPaths", "--value", unit_name],
        check=True, capture_output=True, text=True, timeout=5)
    for dropin_path in shlex.split(result.stdout.strip()):
        inspect_unit_environment(read_file(dropin_path), True)

api_base = normalize_origin(os.environ.get("NETRATEL_SEED_API_BASE", ""))
gateway_endpoint = os.environ.get("NETRATEL_SEED_GATEWAY_ENDPOINT", "")
if gateway_endpoint:
    gateway_endpoint = normalize_origin(gateway_endpoint, https_only=True)
tenant_id = os.environ.get("NETRATEL_SEED_TENANT_ID", "")
enrollment_code = os.environ.get("NETRATEL_SEED_ENROLLMENT_CODE", "")
runtime = os.environ.get("NETRATEL_SEED_RUNTIME", "")
version = os.environ.get("NETRATEL_SEED_VERSION", "")
if not re.fullmatch(r"[1-9][0-9]*", tenant_id) or int(tenant_id) > 2147483647:
    reject("The configured tenant identifier is invalid.")
if not enrollment_code.strip() or len(enrollment_code) > 512:
    reject("The enrollment input is invalid.")
if runtime not in ("linux-x64", "linux-arm64"):
    reject("The requested Linux runtime is invalid.")
if version != "latest" and not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?", version):
    reject("The requested version is invalid.")

fragment = os.path.abspath(systemd_value("FragmentPath"))
unit_dir = checked_path(os.path.dirname(fragment), "Systemd unit")
if os.path.basename(fragment) != "netratel-client.service":
    reject("The NetRatel service is not backed by its expected unit name.")
unit_bytes = read_file(fragment)
inspect_effective_unit("netratel-client.service", fragment)
properties, unit_environment = parse_unit(unit_bytes)
working_directory = checked_path(properties.get("WorkingDirectory", ""), "Service working directory")
suffix = "/current"
if not working_directory.endswith(suffix):
    reject("The NetRatel service working directory does not target its current version.")
root_dir = checked_path(working_directory[:-len(suffix)], "Install root")
expected_launcher = root_dir + "/netratel-client-start.sh"
if properties.get("ExecStart") != expected_launcher:
    reject("The service launcher is outside the owned client root.")
state_dir = unit_environment.get("NetRatelCLIENT__Client__AutoUpdate__StateDirectory")
if not state_dir:
    update_unit = os.path.join(unit_dir, "netratel-update.service")
    try:
        update_bytes = read_file(update_unit)
    except OSError:
        update_bytes = b""
    inspect_effective_unit("netratel-update.service", update_unit)
    _, update_environment = parse_unit(update_bytes) if update_bytes else ({}, {})
    state_dir = update_environment.get("NetRatel_UPDATE_STATE")
if not state_dir:
    reject("The update state directory is not configured by the installed service.")
state_dir = checked_path(state_dir, "Update state")
bundle_dir = checked_path(unit_environment.get("DOTNET_BUNDLE_EXTRACT_BASE_DIR", "/var/lib/netratel/bundle"), "Bundle extraction")

root_fd = open_directory(root_dir)
state_fd = open_directory(state_dir, private=True)
try:
    launcher = read_file(expected_launcher)
    launcher_text = launcher.decode("utf-8", errors="strict")
    if 'ROOT_DIR="' + root_dir + '"' not in launcher_text or 'exec "' + root_dir + '/current/NetRatel.Client" --service' not in launcher_text:
        reject("The installed launcher does not target the verified version layout.")
    current_info = os.stat("current", dir_fd=root_fd, follow_symlinks=False)
    if not stat.S_ISLNK(current_info.st_mode):
        reject("The installed client current path is not a symbolic link.")
    current_link = os.readlink("current", dir_fd=root_fd)
    versions_dir = os.path.join(root_dir, "versions")
    if not os.path.isabs(current_link) or os.path.dirname(current_link) != versions_dir:
        reject("The installed client current link does not point to an owned version.")
    version_name = os.path.basename(current_link)
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?", version_name):
        reject("The installed client current link does not point to a versioned package.")
    versions_fd = os.open("versions", directory_flags, dir_fd=root_fd)
    try:
        versions_info = os.fstat(versions_fd)
        if (versions_info.st_uid != expected_owner or stat.S_IMODE(versions_info.st_mode) & 0o022):
            reject("The installed version directory has an unexpected owner or mode.")
        target_fd = os.open(version_name, directory_flags, dir_fd=versions_fd)
    finally:
        os.close(versions_fd)
    try:
        target_info = os.fstat(target_fd)
        if not stat.S_ISDIR(target_info.st_mode) or target_info.st_uid != expected_owner or stat.S_IMODE(target_info.st_mode) & 0o022:
            reject("The installed version directory has an unexpected owner, type, or mode.")
        manifest_bytes = read_file_at(target_fd, "netratel-client-manifest.json")
        executable_info = os.stat("NetRatel.Client", dir_fd=target_fd, follow_symlinks=False)
        if (not stat.S_ISREG(executable_info.st_mode) or executable_info.st_uid != expected_owner or
                stat.S_IMODE(executable_info.st_mode) & 0o022 or not executable_info.st_mode & 0o111):
            reject("The installed executable has an unexpected owner, type, mode, or execution permission.")
        manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
    finally:
        os.close(target_fd)
    if (not isinstance(manifest, dict) or manifest.get("schema") != "netratel.client.manifest.v1" or
            manifest.get("product") != "NetRatel.Client" or
            manifest.get("runtimeId") != runtime or
            manifest.get("version") != version_name or
            manifest.get("executable") != "NetRatel.Client" or
            not re.fullmatch(r"[0-9a-fA-F]{40}", str(manifest.get("commitSha", "")))):
        reject("The installed version manifest is not valid.")
finally:
    os.close(root_fd)
    os.close(state_fd)

service_pid_text = systemd_value("MainPID")
if not re.fullmatch(r"[0-9]+", service_pid_text):
    reject("The NetRatel service process identity could not be verified.")
service_pid = int(service_pid_text)
active = subprocess.run(["systemctl", "is-active", "--quiet", "netratel-client.service"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
if active.returncode not in (0, 3):
    reject("The NetRatel service activity could not be verified.")
service_identity = process_identity(service_pid) if service_pid else None
if active.returncode == 0 and (service_pid <= 1 or service_identity is None):
    reject("The active NetRatel service process identity could not be verified.")
if active.returncode == 3 and service_pid != 0:
    reject("The stopped NetRatel service still reports a live process identity.")
origin_pid = int(os.environ.get("NETRATEL_SEED_ORIGIN_PID", "0"))
origin_identity = process_identity(origin_pid) if origin_pid else None
if active.returncode == 0:
    if origin_identity is None or origin_pid == service_pid or not ancestor_contains(origin_pid, service_pid):
        reject("The seeded command is not a child of the verified NetRatel service.")
    mode = "handoff"
else:
    mode = "direct"

handoff_id = ""
if mode == "handoff":
    state_parent_fd = open_directory(state_dir, private=True)
    try:
        try:
            os.mkdir("install-handoffs", 0o700, dir_fd=state_parent_fd)
        except FileExistsError:
            pass
    finally:
        os.close(state_parent_fd)
    handoff_dir = os.path.join(state_dir, "install-handoffs")
    handoff_fd = open_directory(handoff_dir, private=True)
    handoff_id = secrets.token_hex(16)
    request_name = "handoff-" + handoff_id + ".json"
    worker_name = "handoff-" + handoff_id + ".py"
    installer_name = "handoff-" + handoff_id + ".sh"
    request_path = os.path.join(handoff_dir, request_name)
    try:
        with open(installer_source, "rb") as stream:
            installer_bytes = stream.read()
        with open(worker_source, "rb") as stream:
            worker_bytes = stream.read()
        request = {
            "schema": "netratel.seeded-linux-update-handoff.v1",
            "handoff_id": handoff_id,
            "created_at": time.time(),
            "origin_pid": origin_pid,
            "origin_start_ticks": origin_identity[2],
            "service_pid": service_pid,
            "service_start_ticks": service_identity[2],
            "unit_path": fragment,
            "unit_dir": unit_dir,
            "unit_sha256": hashlib.sha256(unit_bytes).hexdigest(),
            "root_dir": root_dir,
            "state_dir": state_dir,
            "bundle_extract_dir": bundle_dir,
            "api_base": api_base,
            "gateway_endpoint": gateway_endpoint,
            "tenant_id": int(tenant_id),
            "enrollment_code": enrollment_code,
            "runtime": runtime,
            "version": version,
            "request_path": request_path,
            "worker_name": worker_name,
            "worker_sha256": hashlib.sha256(worker_bytes).hexdigest(),
            "installer_name": installer_name,
            "installer_sha256": hashlib.sha256(installer_bytes).hexdigest(),
        }
        def write_private(name, content):
            descriptor = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | no_follow, 0o600, dir_fd=handoff_fd)
            try:
                os.fchmod(descriptor, 0o600)
                with os.fdopen(os.dup(descriptor), "wb") as output:
                    output.write(content)
                    output.flush()
                    os.fsync(output.fileno())
            finally:
                os.close(descriptor)
        write_private(worker_name, worker_bytes)
        write_private(installer_name, installer_bytes)
        write_private(request_name, json.dumps(request, separators=(",", ":")).encode("utf-8"))
        command = ["systemd-run", "--collect", "--no-block", "--unit=netratel-client-repair-" + handoff_id]
        if test_mode:
            command.append("--setenv=NetRatel_TEST_ALLOW_NONROOT=true")
        command.extend(["/usr/bin/python3", os.path.join(handoff_dir, worker_name), request_path])
        dispatched = subprocess.run(command, check=False, capture_output=True, text=True, timeout=10)
        if dispatched.returncode != 0:
            reject("The detached NetRatel repair worker could not be started.")
    except BaseException:
        for name in (request_name, worker_name, installer_name):
            try:
                os.unlink(name, dir_fd=handoff_fd)
            except FileNotFoundError:
                pass
        raise
    finally:
        os.close(handoff_fd)

print(root_dir)
print(state_dir)
print(unit_dir)
print(bundle_dir)
print(mode)
print(handoff_id)
""";

}
