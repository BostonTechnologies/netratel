$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RootDir = $env:NetRatel_UPDATE_ROOT
if ([string]::IsNullOrWhiteSpace($RootDir)) { $RootDir = Join-Path $env:ProgramFiles "NetRatel\Client" }
$StateDir = $env:NetRatel_UPDATE_STATE
if ([string]::IsNullOrWhiteSpace($StateDir)) { $StateDir = Join-Path $env:ProgramData "NetRatel\update" }
$ClientService = $env:NetRatel_CLIENT_SERVICE
if ([string]::IsNullOrWhiteSpace($ClientService)) { $ClientService = "NetRatel.Client" }
$RequestPath = $env:NetRatel_UPDATE_REQUEST
if ([string]::IsNullOrWhiteSpace($RequestPath)) { $RequestPath = Join-Path $StateDir "request.json" }

$VersionsDir = Join-Path $RootDir "versions"
$StagingDir = Join-Path $RootDir "staging"
$FailedDir = Join-Path $RootDir "failed"
$StatePath = Join-Path $StateDir "state.json"
$LockPath = Join-Path $StateDir "update.lock"
$script:LogPath = $null
$script:ResultPath = Join-Path $StateDir "result.json"
$script:ReleaseId = ""
$script:AttemptId = ""
$script:RuntimeId = ""
$script:FromVersion = ""
$script:ToVersion = ""
$script:PreviousPath = ""
$script:ActivePath = ""
$script:TargetDir = ""
$script:PresencePath = ""
$script:ActivationPath = Join-Path $StateDir "activation.json"
$script:CutoverStarted = $false

try {
    $tls12 = [System.Enum]::Parse([System.Net.SecurityProtocolType], "Tls12")
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor $tls12
}
catch {
    # Older frameworks may not expose Tls12; continue and let network calls report the real error.
}

function Write-State($state, $version) {
    $temporary = "$StatePath.tmp"
    @{ state = $state; version = $version; updatedAtUtc = (Get-Date).ToUniversalTime().ToString("O") } |
        ConvertTo-Json -Depth 4 | Set-Content -Path $temporary -Encoding UTF8
    Move-Item -Path $temporary -Destination $StatePath -Force
}

function Write-UpdateLog($message) {
    $line = "$((Get-Date).ToUniversalTime().ToString("O")) [UpdateService] $message"
    Write-Host $line
    if (-not [string]::IsNullOrWhiteSpace($script:LogPath)) {
        try {
            $dir = Split-Path -Parent $script:LogPath
            if (-not [string]::IsNullOrWhiteSpace($dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            Add-Content -Path $script:LogPath -Value $line -Encoding UTF8
        }
        catch {
        }
    }
}

function Write-Result($status, $message, $errorText = $null) {
    $payload = @{
        schema = "netratel.update.result.v2"
        attemptId = $script:AttemptId
        releaseId = $script:ReleaseId
        runtimeId = $script:RuntimeId
        fromVersion = $script:FromVersion
        toVersion = $script:ToVersion
        version = $script:ToVersion
        state = $status
        failureCode = $errorText
        message = $message
        previousTarget = $script:PreviousPath
        activeTarget = $script:ActivePath
        error = $errorText
        completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    }
    $dir = Split-Path -Parent $script:ResultPath
    if (-not [string]::IsNullOrWhiteSpace($dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $temporary = "$($script:ResultPath).tmp"
    $payload | ConvertTo-Json -Depth 5 | Set-Content -Path $temporary -Encoding UTF8
    Move-Item -Path $temporary -Destination $script:ResultPath -Force
}

function Set-NetRatelServiceImagePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ImagePath
    )

    # Windows PowerShell can silently alter native-process argument quoting.
    # Set the registry-backed service setting directly and verify it before a
    # candidate or rollback process is started.
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ClientService"
    if (-not (Test-Path $serviceKey)) {
        throw "Windows service registry key was not found: $serviceKey"
    }

    Set-ItemProperty -Path $serviceKey -Name ImagePath -Value $ImagePath
    $configured = (Get-CimInstance Win32_Service -Filter "Name='$ClientService'" -ErrorAction Stop).PathName
    if (-not [string]::Equals([string]$configured, $ImagePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Windows service ImagePath verification failed. expected=$ImagePath actual=$configured"
    }

    Write-UpdateLog "Configured $ClientService ImagePath=$ImagePath"
}

function Get-ActivationFailureCode {
    if (-not (Test-Path $script:ActivationPath)) { return "activation_request_not_loaded" }
    try {
        $activation = Get-Content -Raw -Path $script:ActivationPath | ConvertFrom-Json
        switch ([string]$activation.stage) {
            "request_loaded" { return "activation_context_missing" }
            "activation_context_rejected" { return "activation_context_missing" }
            "activation_context_attached" { return "activation_presence_not_admitted" }
            "presence_connected" { return "activation_heartbeat_timeout" }
            "first_heartbeat_sent" { return "activation_heartbeat_timeout" }
            "heartbeat_accepted" { return "activation_confirmation_timeout" }
            "confirmation_received" { return "activation_ready_write_failed" }
            "ready_marker_written" { return "activation_ready_write_failed" }
            default { return "activation_unknown_timeout" }
        }
    }
    catch {
        return "activation_unknown_timeout"
    }
}

function Archive-ActivationEvidence {
    $attemptDirectory = Join-Path (Join-Path $StateDir "attempts") $script:AttemptId
    try {
        New-Item -ItemType Directory -Path $attemptDirectory -Force | Out-Null
        if (Test-Path $RequestPath) {
            $request = Get-Content -Raw -Path $RequestPath | ConvertFrom-Json
            @{
                schema = [string]$request.schema; attemptId = [string]$request.attemptId; releaseId = [string]$request.releaseId
                runtimeId = [string]$request.runtimeId; fromVersion = [string]$request.fromVersion; toVersion = [string]$request.toVersion
                packagePath = [string]$request.packagePath; readyPath = [string]$request.readyPath; presencePath = [string]$request.presencePath
                resultPath = [string]$request.resultPath; requestedAtUtc = [string]$request.requestedAtUtc; admissionNonce = "REDACTED"
            } | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $attemptDirectory "request.redacted.json") -Encoding UTF8
        }
        foreach ($source in @($script:ActivationPath, $StatePath, $script:ResultPath)) {
            if (Test-Path $source) { Copy-Item -Path $source -Destination (Join-Path $attemptDirectory (Split-Path -Leaf $source)) -Force }
        }
        @{
            attemptId = $script:AttemptId; releaseId = $script:ReleaseId; currentVersion = $script:FromVersion; targetVersion = $script:ToVersion
            serviceExecutablePath = $script:ActivePath; serviceState = $(if ($service = Get-Service -Name $ClientService -ErrorAction SilentlyContinue) { $service.Status.ToString() } else { "Unknown" })
            serviceProcessId = (Get-CimInstance Win32_Service -Filter "Name='$ClientService'" -ErrorAction SilentlyContinue).ProcessId
            failedVersionPath = $script:TargetDir; latestClientLogPath = $script:LogPath; archivedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
        } | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $attemptDirectory "rollback-context.json") -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($script:LogPath) -and (Test-Path $script:LogPath)) {
            Get-Content -Path $script:LogPath -Tail 200 | Where-Object { $_ -notmatch '(?i)bearer|token|nonce|authorization|credential' } |
                Set-Content -Path (Join-Path $attemptDirectory "client-log.safe.tail.txt") -Encoding UTF8
        }
    }
    catch {
        Write-UpdateLog "Activation evidence archive failed: $($_.Exception.GetType().Name)"
    }
}

function Get-NetRatelSha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $getFileHash = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($getFileHash) {
        return ((Get-FileHash -Path $Path -Algorithm SHA256).Hash).ToLowerInvariant()
    }

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read
    )

    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
        }
        finally {
            if ($sha -is [System.IDisposable]) {
                $sha.Dispose()
            }
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Expand-NetRatelZip {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ZipPath,

        [Parameter(Mandatory = $true)]
        [string] $DestinationPath
    )

    $expandArchive = Get-Command Expand-Archive -ErrorAction SilentlyContinue
    if ($expandArchive) {
        Expand-Archive -Path $ZipPath -DestinationPath $DestinationPath -Force
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $DestinationPath)
}

function Invoke-NetRatelRollback($reason) {
    $script:CutoverStarted = $false
    Write-UpdateLog "Rolling back NetRatel client update $($script:ToVersion). reason=$reason"
    Archive-ActivationEvidence
    Stop-Service -Name $ClientService -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $FailedDir -Force | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($script:TargetDir) -and (Test-Path $script:TargetDir)) {
        $failedTarget = Join-Path $FailedDir "$($script:ToVersion)-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"
        Move-Item -Path $script:TargetDir -Destination $failedTarget -Force
    }
    if (-not [string]::IsNullOrWhiteSpace($script:PreviousPath)) {
        Set-NetRatelServiceImagePath $script:PreviousPath
        $script:ActivePath = $script:PreviousPath
    }
    if (-not [string]::IsNullOrWhiteSpace($script:PresencePath) -and (Test-Path $script:PresencePath)) {
        Remove-Item $script:PresencePath -Force
    }
    try { Start-Service -Name $ClientService } catch { Write-UpdateLog "Rollback service start failed: $($_.Exception.Message)" }

    $rollbackVerified = $false
    $rollbackDeadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $rollbackDeadline) {
        if (-not [string]::IsNullOrWhiteSpace($script:PresencePath) -and (Test-Path $script:PresencePath)) {
            try {
                $presence = Get-Content -Raw -Path $script:PresencePath | ConvertFrom-Json
                if ([string]$presence.version -eq $script:FromVersion) { $rollbackVerified = $true; break }
            }
            catch { }
        }
        Start-Sleep -Seconds 5
    }

    $suspensionPath = Join-Path $StateDir "suspension.json"
    $suspensionTemporary = "$suspensionPath.tmp"
    @{ schema = "netratel.update.suspension.v1"; attemptId = $script:AttemptId; releaseId = $script:ReleaseId;
       reason = $reason; suspendedAtUtc = (Get-Date).ToUniversalTime().ToString("O") } |
        ConvertTo-Json -Depth 4 | Set-Content -Path $suspensionTemporary -Encoding UTF8
    Move-Item -Path $suspensionTemporary -Destination $suspensionPath -Force
    Write-State "rolled_back" $script:ToVersion
    Write-UpdateLog "Rollback completed for NetRatel client update $($script:ToVersion)."
    if ($rollbackVerified) {
        Write-Result "RolledBack" "Update activation failed; rollback reconnected." $reason
    }
    else {
        Write-Result "RollbackUnverified" "Rollback was applied but gateway check-in was not observed." "rollback_checkin_timeout"
    }
}

New-Item -ItemType Directory -Path $StateDir, $VersionsDir, $StagingDir, $FailedDir -Force | Out-Null
$lock = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    if (-not (Test-Path $RequestPath)) { return }
    $request = Get-Content -Raw -Path $RequestPath | ConvertFrom-Json
    $version = [string]$request.toVersion
    if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$request.version }
    $packagePath = [string]$request.packagePath
    $expectedSha = ([string]$request.sha256).ToLowerInvariant()
    $readyPath = [string]$request.readyPath
    $script:LogPath = [string]$request.logPath
    $script:ResultPath = [string]$request.resultPath
    if ([string]::IsNullOrWhiteSpace($script:ResultPath)) { $script:ResultPath = Join-Path $StateDir "result.json" }
    $script:ReleaseId = [string]$request.releaseId
    $script:AttemptId = [string]$request.attemptId
    $script:RuntimeId = [string]$request.runtimeId
    $script:FromVersion = [string]$request.fromVersion
    $presencePath = [string]$request.presencePath
    $script:PresencePath = $presencePath
    if ([string]::IsNullOrWhiteSpace($script:FromVersion)) { $script:FromVersion = [string]$request.currentVersion }
    $script:ToVersion = $version
    if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($packagePath) -or
        [string]::IsNullOrWhiteSpace($expectedSha) -or [string]::IsNullOrWhiteSpace($script:AttemptId) -or
        [string]::IsNullOrWhiteSpace($script:ReleaseId)) {
        throw "Update request is missing version, packagePath, or sha256."
    }
    if (-not (Test-Path $packagePath)) {
        throw "Staged package was not found at $packagePath."
    }

    $zipPath = $packagePath
    $extractDir = Join-Path $StagingDir $version
    $targetDir = Join-Path $VersionsDir $version
    $script:TargetDir = $targetDir
    $service = Get-CimInstance Win32_Service -Filter "Name='$ClientService'"
    $previousPath = $service.PathName
    $script:PreviousPath = $previousPath

    Write-UpdateLog "Cutover starting for NetRatel client update $version. previous=$previousPath"
    Write-State "verifying_package" $version
    $actualSha = Get-NetRatelSha256Hex -Path $zipPath
    if ($actualSha -ne $expectedSha) { throw "Checksum mismatch for $version." }
    Write-UpdateLog "Checksum verified for NetRatel client update $version."

    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $extractDir
    $manifestPath = Join-Path $extractDir "netratel-client-manifest.json"
    if (-not (Test-Path $manifestPath)) { throw "Update manifest is missing." }
    $manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
    if ([string]$manifest.schema -ne "netratel.client.manifest.v1" -or
        [string]$manifest.product -ne "NetRatel.Client" -or
        [string]$manifest.version -ne $version -or
        [string]$manifest.runtimeId -ne $script:RuntimeId -or
        [string]$manifest.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
        -not (Test-Path (Join-Path $extractDir ([string]$manifest.executable)))) {
        throw "Update manifest does not match the request."
    }
    $exe = @(Join-Path $extractDir "NetRatel.Client.exe"; Join-Path $extractDir "NetRatel.Client.exe") |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $exe) { throw "Client executable was not found in staged artifact." }

    Write-State "applying" $version
    Write-UpdateLog "Stopping $ClientService for NetRatel client update $version."
    $script:CutoverStarted = $true
    Stop-Service -Name $ClientService -Force -ErrorAction SilentlyContinue
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    Move-Item -Path $extractDir -Destination $targetDir
    $activeExe = Join-Path $targetDir (Split-Path $exe -Leaf)
    $script:ActivePath = "`"$activeExe`" --service"
    Set-NetRatelServiceImagePath $script:ActivePath
    Start-Service -Name $ClientService
    Write-UpdateLog "Started $ClientService with NetRatel client update $version; waiting for readiness marker."

    Write-State "verifying" $version
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $readyPath) {
            $ready = Get-Content -Raw -Path $readyPath | ConvertFrom-Json
            if ([string]$ready.schema -eq "netratel.update.ready.v2" -and
                [string]$ready.attemptId -eq $script:AttemptId -and
                [string]$ready.releaseId -eq $script:ReleaseId -and
                [string]$ready.version -eq $version -and
                -not [string]::IsNullOrWhiteSpace([string]$ready.confirmationId)) {
                Write-State "accepted" $version
                $script:CutoverStarted = $false
                Get-ChildItem -Path $VersionsDir -Directory |
                    Sort-Object LastWriteTimeUtc -Descending |
                    Select-Object -Skip 3 |
                    Remove-Item -Recurse -Force
                Write-UpdateLog "NetRatel client update $version accepted."
                Write-Result "Accepted" "NetRatel client update $version accepted."
                return
            }
        }
        Start-Sleep -Seconds 5
    }

    Invoke-NetRatelRollback (Get-ActivationFailureCode)
    exit 2
}
catch {
    Write-UpdateLog "NetRatel client update failed: $($_.Exception.Message)"
    if ($script:CutoverStarted) {
        Invoke-NetRatelRollback "post_cutover_failure"
    }
    else {
        Write-Result "FailedPreActivation" "NetRatel client update failed." $_.Exception.Message
    }
    throw
}
finally {
    $lock.Dispose()
}
