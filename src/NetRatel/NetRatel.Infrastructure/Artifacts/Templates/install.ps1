# NetRatel Windows installer. Windows PowerShell 5.1; no online-readiness gate.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ApiBase = @@API_BASE_URL@@
$GatewayEndpoint = @@GATEWAY_ENDPOINT@@
$TenantId = @@TENANT_ID@@
$EnrollmentCode = @@ENROLLMENT_CODE@@
$Runtime = @@RUNTIME_ID@@
$Version = @@ARTIFACT_VERSION@@
$ExpectedSha256 = @@ARTIFACT_SHA256@@
$ValidToUtc = @@VALID_TO_UTC@@
$InstallAsService = @@INSTALL_AS_SERVICE@@
$SilentInstall = @@SILENT_INSTALL@@
$phase = 'preflight'
$updateLock = $null
$stageDir = $null
$downloadDir = $null
$serviceName = 'NetRatel.Client'
$serviceRegistry = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$serviceControlExited = $true
$trustedSids = @('S-1-5-18', 'S-1-5-32-544')
$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$'

function Get-CanonicalPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]') { throw 'Use an absolute local drive installation path.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Substring(2).Contains(':')) { throw 'Alternate data stream paths are unsupported.' }
    if ($full.Length -eq 3) { return $full }
    return $full.TrimEnd('\', '/')
}

# Inspect ancestors without changing their permissions. At the owned leaf also
# reject write access; ProgramData may legitimately allow creating sibling folders.
function Assert-OwnedPath([string]$Path, [switch]$AllowMissing, [switch]$File) {
    $leaf = Get-CanonicalPath $Path
    $component = $leaf
    while ($component) {
        if (Test-Path -LiteralPath $component) {
            $item = Get-Item -LiteralPath $component -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'An installation path is a reparse point; choose a trusted local path.' }
            if ($component -eq $leaf -and $item.PSIsContainer -eq [bool]$File) { throw 'An owned path has the wrong file type.' }
            $acl = Get-Acl -LiteralPath $component
            if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedSids) { throw 'An installation path has an untrusted owner.' }
            foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
                if ($rule.AccessControlType -ne 'Allow' -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
                $unsafe = 0x000D0040 # Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership.
                if ($component -eq $leaf) { $unsafe = $unsafe -bor 0x116 } # WriteData, AppendData, WriteExtendedAttributes, WriteAttributes.
                if ($rule.IdentityReference.Value -notin $trustedSids -and ([int]$rule.FileSystemRights -band $unsafe)) {
                    throw 'An installation path permits untrusted modification; correct its owned-path permissions first.'
                }
            }
        }
        elseif ($component -eq $leaf -and -not $AllowMissing) { throw 'An expected owned path is missing.' }
        $parent = [IO.Path]::GetDirectoryName($component)
        if ($parent -eq $component) { break }
        $component = $parent
    }
}

function New-OwnedDirectory([string]$Path) {
    Assert-OwnedPath $Path -AllowMissing
    if (Test-Path -LiteralPath $Path) { return }
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) { New-OwnedDirectory $parent }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    [void][IO.Directory]::CreateDirectory($Path, $acl)
    Assert-OwnedPath $Path
}

function Assert-OwnedTree([string]$Path) {
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Path)
    $count = 0
    while ($pending.Count) {
        $directory = $pending.Dequeue()
        Assert-OwnedPath $directory
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (++$count -gt 4096) { throw 'The owned package contains too many entries for safe repair.' }
            Assert-OwnedPath $entry.FullName -File:(-not $entry.PSIsContainer)
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
        }
    }
}

function Write-PrivateFile([string]$Path, [string]$Text) {
    Assert-OwnedPath $Path -AllowMissing -File
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'Allow'))
    }
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::FullControl,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough, $acl)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}

function Get-ServiceEnvironment([string]$Name) {
    foreach ($entry in $previousEnvironment) {
        if ($entry.StartsWith($Name + '=', [StringComparison]::OrdinalIgnoreCase)) { $script:settingSource = 'service environment'; return $entry.Substring($Name.Length + 1) }
    }
    $script:settingSource = 'machine environment'
    return [Environment]::GetEnvironmentVariable($Name, [EnvironmentVariableTarget]::Machine)
}

# The normal .NET environment provider follows the product-prefixed provider.
function Get-ClientSetting([string]$Name) {
    foreach ($prefix in @('', 'NetRatelCLIENT__')) {
        $value = $null
        $source = $null
        foreach ($alias in @(($prefix + $Name), ($prefix + $Name.Replace('__', ':')))) {
            $candidate = Get-ServiceEnvironment $alias
            if ($null -eq $candidate) { continue }
            if ($null -ne $value -and $value -ne $candidate) { throw "Conflicting environment aliases for $Name; correct them before installation." }
            $value = $candidate
            $source = $script:settingSource
        }
        if ($null -ne $value) { $script:settingSource = $source; return $value }
    }
    return $null
}

function Resolve-PathSetting([string]$Requested, [string[]]$Configured, [string]$Default, [string]$Name) {
    $values = @($Configured | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($Requested) { $values = @($Requested) + $values }
    if ($values.Count -eq 0) { return Get-CanonicalPath $Default }
    $resolved = Get-CanonicalPath $values[0]
    foreach ($value in $values) {
        if ((Get-CanonicalPath $value) -ne $resolved) { throw "Conflicting $Name paths; repair the configuration before installation." }
    }
    return $resolved
}

function Get-Origin([string]$Value, [switch]$Gateway) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https') -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath.TrimEnd('/') -notin @('', '/api') -or
        ($Gateway -and ($uri.Scheme -ne 'https' -or $uri.AbsolutePath.TrimEnd('/')))) { throw 'Use a valid API origin or HTTPS gateway origin.' }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Invoke-ServiceControl([string[]]$Arguments) {
    # Windows command-line escaping, including the quotes inside the SCM image path.
    $quoted = foreach ($argument in $Arguments) { '"' + [regex]::Replace([regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $env:SystemRoot 'System32\sc.exe'
    $info.Arguments = $quoted -join ' '
    $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    try {
        $script:serviceControlExited = $process.WaitForExit(15000)
        if (-not $script:serviceControlExited) {
            $process.Kill()
            $script:serviceControlExited = $process.WaitForExit(3000)
            throw 'Service configuration command timed out.'
        }
        if ($process.ExitCode -ne 0) { throw "Service configuration command failed (exit $($process.ExitCode))." }
    }
    finally { $process.Dispose() }
}

function Set-ServiceState([string]$State, [string]$Name = $serviceName) {
    $controller = [ServiceProcess.ServiceController]::new($Name)
    try {
        $controller.Refresh()
        if ([string]$controller.Status -ne $State) {
            if ($State -eq 'Stopped' -and $controller.Status -ne 'StopPending') { $controller.Stop() }
            elseif ($State -eq 'Running' -and $controller.Status -ne 'StartPending') { $controller.Start() }
            $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]([Enum]::Parse([ServiceProcess.ServiceControllerStatus], $State)), [TimeSpan]::FromSeconds(60))
        }
    }
    finally { $controller.Dispose() }
}

function Write-Diagnostics {
    try {
        $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10
        Write-Host "Local service status=$(if ($service) { $service.State } else { 'not-installed' }); account=$(if ($service) { $service.StartName } else { 'n/a' }); logs=$LogDir"
        if (-not $LogDir -or -not (Test-Path -LiteralPath $LogDir)) { return }
        Assert-OwnedPath $LogDir
        $latest = Get-ChildItem -LiteralPath $LogDir -Filter 'netratel-client*.log' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($latest) {
            Assert-OwnedPath $latest.FullName -File
            Write-Host "Client log tail: $($latest.FullName)"
            foreach ($line in Get-Content -LiteralPath $latest.FullName -Tail 40) {
                if ($line -match '(?i)(bearer|token|secret|password|key|enroll|authorization|capability|grant)') { Write-Host '[redacted sensitive log line]'; continue }
                $safe = [regex]::Replace($line, '(https?://[^/\s?#]+)[^\s]*', '$1/[redacted-path]')
                if ($EnrollmentCode) { $safe = $safe.Replace($EnrollmentCode, '[redacted]') }
                Write-Host $safe.Substring(0, [Math]::Min(500, $safe.Length))
            }
        }
    }
    catch { Write-Host 'Local diagnostics could not be read safely.' }
}

function Expand-Artifact([string]$ZipPath, [string]$StageDir) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expanded = 0L
        if ($archive.Entries.Count -gt 4096) { throw 'The archive contains too many entries.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $parts = $name.TrimEnd('/').Split('/')
            $expanded += $entry.Length
            if (-not $name -or $name.StartsWith('/') -or $parts.Count -gt 16 -or $expanded -gt 1GB -or
                ($entry.ExternalAttributes -band 0x400) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                @($parts | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[<>:"|?*]|[ .]$|^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -or
                -not $names.Add($name.TrimEnd('/'))) { throw 'The archive contains an unsafe or ambiguous path.' }
            $destination = [IO.Path]::GetFullPath((Join-Path $stageDir $name))
            if (-not $destination.StartsWith($stageDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'An archive entry escapes staging.' }
            if ($name.EndsWith('/')) { [void][IO.Directory]::CreateDirectory($destination); continue }
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
            $input = $entry.Open()
            $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

try {
    @@SEED_INITIALIZE@@
    if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -lt 5) { throw 'Run this installer using Windows PowerShell 5.1 or later on Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run Windows PowerShell as Administrator.' }
    $trustedSids += $identity.User.Value
    $trustedSids += ([Security.Principal.NTAccount]::new('NT SERVICE', 'TrustedInstaller')).Translate([Security.Principal.SecurityIdentifier]).Value
    $administrators = Get-CimInstance Win32_Group -Filter "SID='S-1-5-32-544'" -OperationTimeoutSec 10
    $trustedSids += @(Get-CimAssociatedInstance -InputObject $administrators -Association Win32_GroupUser -ResultClassName Win32_UserAccount -OperationTimeoutSec 10 | Select-Object -ExpandProperty SID)
    $ApiBase = Get-Origin $ApiBase
    if ($GatewayEndpoint) { $GatewayEndpoint = Get-Origin $GatewayEndpoint -Gateway }
    if ($Runtime -notin @('win-x64', 'win-arm64') -or ($Version -ne 'latest' -and $Version -notmatch $versionPattern) -or
        $TenantId -le 0 -or $EnrollmentCode -match '[\r\n]' -or [string]::IsNullOrWhiteSpace($EnrollmentCode) -or
        ($ExpectedSha256 -and $ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$')) { throw 'The installer request has an invalid runtime, version, tenant, grant, or checksum.' }
    $existingService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10
    $previousEnvironment = @()
    $previousDir = $null
    $registeredRoot = $null
    if ($existingService) {
        if ($existingService.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) { throw 'The existing service uses another account; preserve its credential identity with a supported manual repair.' }
        $match = [regex]::Match($existingService.PathName, '^\s*(?:"(?<exe>[^"]+\.exe)"|(?<exe>\S+\.exe))\s+--service\s*$')
        if (-not $match.Success) { throw 'The existing service image is unsupported; refusing to modify it.' }
        $previousExe = Get-CanonicalPath $match.Groups['exe'].Value
        $previousDir = Split-Path -Parent $previousExe
        Assert-OwnedPath $previousDir
        if ((Split-Path -Leaf $previousExe) -ne 'NetRatel.Client.exe') { throw 'The existing service executable is not NetRatel.Client.' }
        $registeredRoot = $previousDir
        if ((Split-Path -Leaf $previousDir) -match $versionPattern -and (Split-Path -Leaf (Split-Path -Parent $previousDir)) -eq 'versions') { $registeredRoot = Split-Path -Parent (Split-Path -Parent $previousDir) }
        $previousEnvironment = @((Get-ItemProperty -LiteralPath $serviceRegistry).Environment | Where-Object { $_ -is [string] })
        Assert-OwnedPath $previousExe -File
        Assert-OwnedPath (Join-Path $previousDir 'netratel-client-manifest.json') -File
        $oldManifest = Get-Content -LiteralPath (Join-Path $previousDir 'netratel-client-manifest.json') -Raw | ConvertFrom-Json
        if ($oldManifest.schema -cne 'netratel.client.manifest.v1' -or $oldManifest.product -cne 'NetRatel.Client' -or
            $oldManifest.runtimeId -cne $Runtime -or $oldManifest.executable -cne 'NetRatel.Client.exe' -or $oldManifest.commitSha -notmatch '^[a-fA-F0-9]{40}$' -or
            $oldManifest.version -notmatch $versionPattern -or ($registeredRoot -ne $previousDir -and $oldManifest.version -cne (Split-Path -Leaf $previousDir))) { throw 'The registered package manifest does not identify the requested NetRatel client.' }
    }
    $RootDir = Resolve-PathSetting $env:NetRatel_ROOT @((Get-ServiceEnvironment 'NetRatel_UPDATE_ROOT'), $registeredRoot) (Join-Path $env:ProgramFiles 'NetRatel\Client') 'package root'
    $installedSettings = $null
    $installedDefaults = $null
    if ($previousDir) {
        foreach ($file in @('clientsettings.json', 'appsettings.json')) {
            $path = Join-Path $previousDir $file
            if (Test-Path -LiteralPath $path) {
                Assert-OwnedPath $path -File
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                if ($file -eq 'clientsettings.json') { $installedSettings = $value } else { $installedDefaults = $value }
            }
        }
    }
    $configuredState = Get-ClientSetting 'Client__AutoUpdate__StateDirectory'
    if (-not $configuredState) { $configuredState = $installedSettings.Client.AutoUpdate.StateDirectory }
    if (-not $configuredState) { $configuredState = $installedDefaults.Client.AutoUpdate.StateDirectory }
    $StateDir = Resolve-PathSetting $env:NetRatel_STATE @((Get-ServiceEnvironment 'NetRatel_UPDATE_STATE'), $configuredState) (Join-Path $env:ProgramData 'NetRatel\update') 'updater state'
    $clientRequest = Get-ClientSetting 'Client__AutoUpdate__RequestPath'
    if (-not $clientRequest) { $clientRequest = $installedSettings.Client.AutoUpdate.RequestPath }
    if (-not $clientRequest) { $clientRequest = $installedDefaults.Client.AutoUpdate.RequestPath }
    $updateRequest = Resolve-PathSetting '' @((Get-ServiceEnvironment 'NetRatel_UPDATE_REQUEST'), $clientRequest) (Join-Path $StateDir 'request.json') 'updater request'
    $LogDir = Resolve-PathSetting $env:NetRatel_LOG_DIR @((Get-ServiceEnvironment 'NetRatel_CLIENT_LOG_DIR')) (Join-Path $env:ProgramData 'NetRatel\logs') 'client log'
    $VersionsDir = Join-Path $RootDir 'versions'
    $StagingDir = Join-Path $RootDir 'staging'
    $FailedDir = Join-Path $RootDir 'failed'
    $credentialDir = Join-Path $env:ProgramData 'NetRatel'
    New-OwnedDirectory $credentialDir
    foreach ($path in @($RootDir, $StateDir, $LogDir)) {
        if ($path -in @($env:ProgramFiles, $env:ProgramData, $env:SystemRoot, $credentialDir, [IO.Path]::GetPathRoot($path))) { throw 'Choose a product subdirectory, not a shared system directory.' }
        New-OwnedDirectory $path
    }
    foreach ($credentialFile in @('agent.dat', '.netratel-credential-machine-id')) { Assert-OwnedPath (Join-Path $credentialDir $credentialFile) -AllowMissing -File }
    Assert-OwnedPath $updateRequest -AllowMissing -File
    $phase = 'updater lock'
    $lockPath = Join-Path $StateDir 'update.lock'
    Assert-OwnedPath $lockPath -AllowMissing -File
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while (-not $updateLock) {
        try { $updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'The updater is busy; try again after it completes.' }; Start-Sleep -Seconds 1 }
    }
    $lockedService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10
    $lockedEnvironment = if ($lockedService) { @((Get-ItemProperty -LiteralPath $serviceRegistry).Environment | Where-Object { $_ -is [string] }) } else { @() }
    if ([bool]$lockedService -ne [bool]$existingService -or ($existingService -and ($lockedService.PathName -ne $existingService.PathName -or $lockedService.StartName -ne $existingService.StartName -or $lockedService.StartMode -ne $existingService.StartMode -or ($lockedEnvironment -join "`n") -ne ($previousEnvironment -join "`n")))) { throw 'The service changed while waiting for the updater; generate a fresh repair attempt.' }
    if ($existingService) { $existingService = $lockedService }
    @@SEED_BEFORE_MUTATION@@
    $UpdaterDir = Join-Path $RootDir 'updater'
    foreach ($path in @($VersionsDir, $StagingDir, $FailedDir, $UpdaterDir)) { New-OwnedDirectory $path }
    $attempt = [Guid]::NewGuid().ToString('N')
    $downloadDir = Join-Path $StagingDir "download-$attempt"
    $stageDir = Join-Path $StagingDir "install-$attempt"
    New-OwnedDirectory $downloadDir
    New-OwnedDirectory $stageDir
    $zipPath = Join-Path $downloadDir 'client.zip'
    $phase = 'artifact verification'
    Write-Host "Downloading requested version=$Version; API=$ApiBase (installer request)."
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $request = [Net.HttpWebRequest]::Create("$ApiBase/api/v1/client-artifacts/$Runtime/$Version/onboarding-download")
    $request.AllowAutoRedirect = $false
    $request.Timeout = 120000
    $request.ReadWriteTimeout = 120000
    $request.Headers['X-NetRatel-Tenant-Id'] = [string]$TenantId
    $request.Headers['X-NetRatel-Enrollment-Code'] = $EnrollmentCode
    try { $response = [Net.HttpWebResponse]$request.GetResponse() } catch { throw 'Authorized artifact download failed; check the API origin and grant validity.' }
    try {
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw 'The artifact response was not HTTP 200.' }
        $resolvedVersion = $response.Headers['X-NetRatel-Artifact-Version']
        $sha = $response.Headers['X-NetRatel-Artifact-Sha256']
        $size = 0L
        if ($response.Headers['X-NetRatel-Artifact-Rid'] -cne $Runtime -or $resolvedVersion -notmatch $versionPattern -or
            ($Version -ne 'latest' -and $resolvedVersion -cne $Version) -or $sha -notmatch '^[a-fA-F0-9]{64}$' -or
            ($ExpectedSha256 -and $sha -ne $ExpectedSha256) -or -not [long]::TryParse($response.Headers['X-NetRatel-Artifact-Size'], [ref]$size) -or $size -le 0 -or $size -gt 1GB) { throw 'Authorized artifact metadata does not match this installer.' }
        $input = $response.GetResponseStream()
        $output = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
        try {
            $buffer = New-Object byte[] 65536
            $received = 0L
            while (($count = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $received += $count
                if ($received -gt $size) { throw 'The artifact exceeds its authorized size.' }
                $output.Write($buffer, 0, $count)
            }
        }
        finally { $output.Dispose(); $input.Dispose() }
    }
    finally { $response.Dispose() }
    if ((Get-Item -LiteralPath $zipPath).Length -ne $size -or (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $sha) { throw 'Artifact size or SHA-256 verification failed.' }
    Expand-Artifact $zipPath $stageDir
    $manifestName = 'netratel-client-manifest.json'
    $entries = @(Get-ChildItem -LiteralPath $stageDir -Force)
    $wrapper = "netratel-client-$Runtime"
    if ($entries.Count -eq 1 -and $entries[0].PSIsContainer -and $entries[0].Name -ceq $wrapper) {
        foreach ($entry in Get-ChildItem -LiteralPath $entries[0].FullName -Force) { Move-Item -LiteralPath $entry.FullName -Destination $stageDir }
        Remove-Item -LiteralPath (Join-Path $stageDir $wrapper)
    }
    elseif (Test-Path -LiteralPath (Join-Path $stageDir $wrapper)) { throw 'Mixed flat and wrapped archive layouts are unsupported.' }
    Assert-OwnedPath (Join-Path $stageDir $manifestName) -File
    Assert-OwnedPath (Join-Path $stageDir 'NetRatel.Client.exe') -File
    $manifest = Get-Content -LiteralPath (Join-Path $stageDir $manifestName) -Raw | ConvertFrom-Json
    if ($manifest.schema -cne 'netratel.client.manifest.v1' -or $manifest.product -cne 'NetRatel.Client' -or $manifest.runtimeId -cne $Runtime -or
        $manifest.version -cne $resolvedVersion -or $manifest.executable -cne 'NetRatel.Client.exe' -or $manifest.commitSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'The package manifest does not match its authorized artifact.' }
    $target = Join-Path $VersionsDir $resolvedVersion
    $backup = Join-Path $FailedDir "$resolvedVersion-replaced-$attempt"
    Assert-OwnedPath $target -AllowMissing
    if (Test-Path -LiteralPath $target) {
        Assert-OwnedTree $target
        $replacedManifest = Get-Content -LiteralPath (Join-Path $target $manifestName) -Raw | ConvertFrom-Json
        if ($replacedManifest.schema -cne 'netratel.client.manifest.v1' -or $replacedManifest.product -cne 'NetRatel.Client' -or
            $replacedManifest.runtimeId -cne $Runtime -or $replacedManifest.version -cne $resolvedVersion -or $replacedManifest.executable -cne 'NetRatel.Client.exe' -or
            $replacedManifest.commitSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'The existing version directory is not an owned NetRatel package.' }
        if (-not $previousDir) { $previousDir = $target }
    }
    $settingsPath = Join-Path $stageDir 'clientsettings.json'
    if ($previousDir -and (Test-Path -LiteralPath (Join-Path $previousDir 'clientsettings.json'))) {
        Assert-OwnedPath (Join-Path $previousDir 'clientsettings.json') -File
        Copy-Item -LiteralPath (Join-Path $previousDir 'clientsettings.json') -Destination $settingsPath -Force
    }
    $settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $settings.Client) { $settings | Add-Member -NotePropertyName Client -NotePropertyValue ([pscustomobject]@{}) -Force }
    $oldApi = [string]$settings.Client.ApiBaseUrl
    if (-not $oldApi) { $oldApi = [string]$settings.ApiBaseUrl }
    if ($oldApi -and $settings.Gateway.Endpoint -and (Get-Origin $settings.Gateway.Endpoint -Gateway) -eq (Get-Origin $oldApi)) { $settings.Gateway.PSObject.Properties.Remove('Endpoint') }
    $settings.Client | Add-Member -NotePropertyName ApiBaseUrl -NotePropertyValue $ApiBase -Force
    if ($GatewayEndpoint) {
        if (-not $settings.Gateway) { $settings | Add-Member -NotePropertyName Gateway -NotePropertyValue ([pscustomobject]@{}) -Force }
        $settings.Gateway | Add-Member -NotePropertyName Endpoint -NotePropertyValue $GatewayEndpoint -Force
    }
    $settings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $gatewaySource = 'API default'
    $effectiveGateway = $ApiBase
    $preservedGateway = Get-ClientSetting 'Gateway__Endpoint'
    $preservedGatewaySource = $script:settingSource
    if ($settings.Gateway.Endpoint) { $effectiveGateway = Get-Origin $settings.Gateway.Endpoint -Gateway; $gatewaySource = 'installed settings' }
    if ($null -ne $preservedGateway) { $effectiveGateway = if ($preservedGateway) { Get-Origin $preservedGateway -Gateway } else { $ApiBase }; $gatewaySource = $preservedGatewaySource }
    if ($GatewayEndpoint) { $effectiveGateway = $GatewayEndpoint; $gatewaySource = 'installer request' }
    Write-Host "Resolved version=$resolvedVersion; gateway=$effectiveGateway ($gatewaySource); logs=$LogDir."
    $created = $false
    $activated = $false
    $backedUp = $false
    $updaterChanged = $false
    $updaterPath = Join-Path $UpdaterDir 'netratel-update.ps1'
    $updaterBackup = Join-Path $FailedDir "updater-replaced-$attempt.ps1"
    $cutover = $false
    $stopAttempted = $false
    $previousRunning = $existingService -and $existingService.State -eq 'Running'
    $phase = 'local activation'
    try {
        if ($existingService -and -not $InstallAsService) { throw 'A registered service owns this installation; use its service repair installer.' }
        if ($existingService) { $stopAttempted = $true; Set-ServiceState 'Stopped' }
        $cutover = $true
        if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup; $backedUp = $true }
        Move-Item -LiteralPath $stageDir -Destination $target
        $activated = $true
        $exe = Join-Path $target 'NetRatel.Client.exe'
        if ($InstallAsService) {
            $enrollment = @{ schema = 'netratel.enroll.v1'; tenantId = $TenantId; enrollmentCode = $EnrollmentCode; issuer = $ApiBase; createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); validToUtc = $ValidToUtc } | ConvertTo-Json
            Write-PrivateFile (Join-Path $target 'netratel.enroll.json') $enrollment
            $updaterSource = Join-Path $target 'updater\netratel-update.ps1'
            Assert-OwnedPath $updaterSource -File
            Assert-OwnedPath $updaterPath -AllowMissing -File
            if (Test-Path -LiteralPath $updaterPath) { Move-Item -LiteralPath $updaterPath -Destination $updaterBackup }
            $updaterChanged = $true
            Write-PrivateFile $updaterPath ([IO.File]::ReadAllText($updaterSource))
            $image = '"' + $exe + '" --service'
            if (-not $existingService) { Invoke-ServiceControl @('create', $serviceName, 'binPath=', $image, 'obj=', 'LocalSystem', 'start=', 'auto'); $created = $true }
            else { Invoke-ServiceControl @('config', $serviceName, 'binPath=', $image, 'obj=', 'LocalSystem', 'start=', 'auto') }
            $overwritten = @('NetRatelCLIENT__Client__ApiBaseUrl', 'NetRatel_UPDATE_ROOT', 'NetRatel_UPDATE_STATE', 'NetRatel_UPDATE_REQUEST', 'NetRatelCLIENT__Client__AutoUpdate__StateDirectory', 'NetRatelCLIENT__Client__AutoUpdate__RequestPath', 'NetRatel_CLIENT_LOG_DIR')
            if ($GatewayEndpoint) { $overwritten += 'NetRatelCLIENT__Gateway__Endpoint' }
            $managed = @('Client__ApiBaseUrl', 'Client__AutoUpdate__StateDirectory', 'Client__AutoUpdate__RequestPath')
            if ($GatewayEndpoint) { $managed += 'Gateway__Endpoint' }
            foreach ($name in $managed) { $overwritten += @($name, $name.Replace('__', ':'), ('NetRatelCLIENT__' + $name.Replace('__', ':'))) }
            $environment = @($previousEnvironment | Where-Object { ($_ -split '=', 2)[0] -notin $overwritten -and $_ -notmatch '^NetRatelCLIENT__Client__ServiceReadiness__' })
            $environment += @("NetRatelCLIENT__Client__ApiBaseUrl=$ApiBase", "NetRatel_UPDATE_ROOT=$RootDir", "NetRatel_UPDATE_STATE=$StateDir", "NetRatel_UPDATE_REQUEST=$updateRequest", "NetRatelCLIENT__Client__AutoUpdate__StateDirectory=$StateDir", "NetRatelCLIENT__Client__AutoUpdate__RequestPath=$updateRequest", "NetRatel_CLIENT_LOG_DIR=$LogDir")
            $environment += @("Client__ApiBaseUrl=$ApiBase", "Client__AutoUpdate__StateDirectory=$StateDir", "Client__AutoUpdate__RequestPath=$updateRequest")
            $environment += @("Client:ApiBaseUrl=$ApiBase", "Client:AutoUpdate:StateDirectory=$StateDir", "Client:AutoUpdate:RequestPath=$updateRequest", "NetRatelCLIENT__Client:ApiBaseUrl=$ApiBase", "NetRatelCLIENT__Client:AutoUpdate:StateDirectory=$StateDir", "NetRatelCLIENT__Client:AutoUpdate:RequestPath=$updateRequest")
            if ($GatewayEndpoint) { $environment += @("NetRatelCLIENT__Gateway__Endpoint=$GatewayEndpoint", "Gateway__Endpoint=$GatewayEndpoint", "Gateway:Endpoint=$GatewayEndpoint", "NetRatelCLIENT__Gateway:Endpoint=$GatewayEndpoint") }
            New-ItemProperty -LiteralPath $serviceRegistry -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null
            $configured = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10
            if ($configured.PathName -ne $image -or $configured.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM') -or $configured.StartMode -ne 'Auto') { throw 'The service configuration did not verify.' }
            Set-ServiceState 'Running'
            Start-Sleep -Seconds 3
            if ((Get-Service -Name $serviceName).Status -ne 'Running') { throw 'The client service exited during local startup.' }
            Write-Host "Installed and started: executable=$exe; account=LocalSystem. Enrollment and gateway online status remain unverified."
            # Retire only the obsolete, owned PowerShell updater registration.
            try {
                $retired = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Update'" -OperationTimeoutSec 10
                $hostPattern = '(?:powershell\.exe|"?' + [regex]::Escape((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')) + '"?)'
                $retiredPattern = '^\s*' + $hostPattern + '\s+(?:-NoProfile\s+|-ExecutionPolicy\s+Bypass\s+)*-File\s+"' + [regex]::Escape($updaterPath) + '"\s*$'
                if ($retired -and $retired.PathName -match $retiredPattern) {
                    Assert-OwnedPath $updaterPath -File
                    Set-ServiceState 'Stopped' 'NetRatel.Update'
                    Invoke-ServiceControl @('delete', 'NetRatel.Update')
                }
            }
            catch { Write-Host 'The owned legacy updater service could not be retired; inspect its registration manually.' }
        }
        else {
            $arguments = @('--enroll', $EnrollmentCode, '--api', $ApiBase)
            if ($SilentInstall) { $arguments += '--silent' }
            & $exe @arguments
            if ($LASTEXITCODE -ne 0) { throw "Client enrollment failed (exit $LASTEXITCODE)." }
            Write-Host "Installed: executable=$exe. Enrollment command completed; gateway online status remains unverified."
        }
    }
    catch {
        Write-Diagnostics
        if ($stopAttempted -and -not $cutover -and $previousRunning) {
            try { Set-ServiceState 'Stopped'; Set-ServiceState 'Running'; Write-Host 'The previous service was restarted; package files were untouched.' }
            catch { Write-Host 'The previous service could not be restarted within the bounded recovery; package files were untouched.' }
        }
        if ($cutover -and $script:serviceControlExited) {
            try {
                if ($InstallAsService -and ($existingService -or $created)) { Set-ServiceState 'Stopped' }
                if ($created) { Invoke-ServiceControl @('delete', $serviceName) }
                elseif ($existingService) {
                    $startMode = @{ Auto = 'auto'; Manual = 'demand'; Disabled = 'disabled' }[[string]$existingService.StartMode]
                    if (-not $startMode) { throw 'The previous service start mode is unsupported.' }
                    Invoke-ServiceControl @('config', $serviceName, 'binPath=', [string]$existingService.PathName, 'obj=', 'LocalSystem', 'start=', $startMode)
                    if ($previousEnvironment.Count) { New-ItemProperty -LiteralPath $serviceRegistry -Name Environment -PropertyType MultiString -Value $previousEnvironment -Force | Out-Null }
                    else { Remove-ItemProperty -LiteralPath $serviceRegistry -Name Environment -ErrorAction SilentlyContinue }
                }
                if ($activated) {
                    $bootstrap = Join-Path $target 'netratel.enroll.json'
                    if ($InstallAsService -and (Test-Path -LiteralPath $bootstrap)) { Assert-OwnedPath $bootstrap -File; Remove-Item -LiteralPath $bootstrap }
                    Move-Item -LiteralPath $target -Destination (Join-Path $FailedDir "$resolvedVersion-failed-$attempt")
                }
                if ($backedUp) { Move-Item -LiteralPath $backup -Destination $target }
                if ($updaterChanged) {
                    if (Test-Path -LiteralPath $updaterPath) { Remove-Item -LiteralPath $updaterPath }
                    if (Test-Path -LiteralPath $updaterBackup) { Move-Item -LiteralPath $updaterBackup -Destination $updaterPath }
                }
                if ($previousRunning) { Set-ServiceState 'Running' }
                Write-Host 'Previous binaries and service configuration restored; credentials were preserved.'
            }
            catch { Write-Host 'Rollback could not complete safely; retained packages require manual inspection.' }
        }
        else { Write-Host 'No safe cutover rollback was available; retained packages require manual inspection.' }
        throw
    }
    @@SEED_SUCCESS@@
}
catch {
    Write-Host "Installer stopped during $phase ($($_.Exception.GetType().Name))."
    if ($_.Exception.Message -notmatch '(?i)(https?://|bearer|token|secret|password|enrollment.?code)') { Write-Host $_.Exception.Message }
    Write-Diagnostics
    @@SEED_FAILURE@@
    throw "NetRatel installation failed during $phase; inspect the safe diagnostics above."
}
finally {
    foreach ($temporary in @($stageDir, $downloadDir)) {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) {
            try { Assert-OwnedPath $temporary; Remove-Item -LiteralPath $temporary -Recurse -Force } catch { Write-Host 'Attempt cleanup requires manual inspection.' }
        }
    }
    @@SEED_CLEANUP@@
    if ($updateLock) { $updateLock.Dispose() }
}
