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
$script:TargetBackupPath = ""
$script:TargetMovedToBackup = $false
$script:CandidateInstalled = $false
$script:ServiceStopRequested = $false
$script:RollbackInProgress = $false
$script:PresencePath = ""
$script:ActivationPath = Join-Path $StateDir "activation.json"
$script:CutoverStarted = $false
$script:TrustedOutputsReady = $false
$script:UpdateLockAcquired = $false
$script:ServiceEnvironment = @()
$script:LocalAdministratorMemberSids = $null

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
    if ($script:TrustedOutputsReady -and -not [string]::IsNullOrWhiteSpace($script:LogPath)) {
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
    if (-not $script:TrustedOutputsReady -or -not $script:UpdateLockAcquired) {
        Write-Host "[UpdateService] Result not written because updater output paths have not passed trust validation. status=$status"
        return
    }
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

function Assert-NetRatelOwnedServiceImage {
    param([Parameter(Mandatory = $true)][string] $ImagePath, [switch] $SkipManifest)

    $imageMatch = [regex]::Match(
        $ImagePath,
        '^\s*(?:"(?<path>[^"]+\.exe)"|(?<path>\S+\.exe))(?:\s+--service)?\s*$',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $imageMatch.Success -or -not [System.IO.Path]::IsPathRooted($imageMatch.Groups['path'].Value)) {
        throw "$ClientService executable could not be resolved from its complete registered image command."
    }
    try { $executablePath = [System.IO.Path]::GetFullPath($imageMatch.Groups['path'].Value) }
    catch { throw "$ClientService executable path is invalid; refusing to stop or replace an unowned service." }
    $rootPath = [System.IO.Path]::GetFullPath($RootDir).TrimEnd('\', '/')
    $legacyRootExecutable = [string]::Equals(
        $executablePath,
        [System.IO.Path]::GetFullPath((Join-Path $rootPath 'NetRatel.Client.exe')),
        [System.StringComparison]::OrdinalIgnoreCase)
    $registeredVersion = Split-Path -Leaf (Split-Path -Parent $executablePath)
    $ownedVersionExecutable = $registeredVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -and
        [string]::Equals(
            $executablePath,
            [System.IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path $rootPath 'versions') $registeredVersion) 'NetRatel.Client.exe')),
            [System.StringComparison]::OrdinalIgnoreCase)
    if (-not ($legacyRootExecutable -or $ownedVersionExecutable)) {
        throw "$ClientService image is outside the configured NetRatel package layout; refusing to stop or replace an unowned service."
    }

    $manifestPath = Join-Path (Split-Path -Parent $executablePath) 'netratel-client-manifest.json'
    if (-not $SkipManifest -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ($manifest.schema -ne 'netratel.client.manifest.v1' -or $manifest.product -ne 'NetRatel.Client' -or
            $manifest.executable -ne 'NetRatel.Client.exe' -or $manifest.runtimeId -ne $script:RuntimeId -or
            $manifest.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
            ($ownedVersionExecutable -and $manifest.version -ne $registeredVersion)) {
            throw "$ClientService image manifest does not identify NetRatel.Client; refusing to stop or replace an unowned service."
        }
    }

    return $executablePath
}

function Get-NetRatelServiceEnvironment {
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ClientService"
    $key = Get-Item -LiteralPath $serviceKey -ErrorAction Stop
    $entries = @($key.GetValue('Environment', $null))
    if ($entries.Count -eq 1 -and $null -eq $entries[0]) { return @() }
    return ,([string[]]$entries)
}

function Get-NetRatelServiceEnvironmentValue([string] $Name, [string[]] $Entries = $script:ServiceEnvironment) {
    $value = $null
    foreach ($entry in $Entries) {
        if ($entry -isnot [string]) { continue }
        $separator = $entry.IndexOf('=')
        if ($separator -gt 0 -and $entry.Substring(0, $separator).Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            $value = $entry.Substring($separator + 1)
        }
    }
    return $value
}

function Get-NetRatelTrustedPathSids {
    $trusted = [System.Collections.Generic.List[string]]::new()
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        if (-not $trusted.Contains($sid)) { $trusted.Add($sid) }
    }
    $trustedInstallerSid = [System.Security.Principal.NTAccount]::new('NT SERVICE', 'TrustedInstaller').Translate([System.Security.Principal.SecurityIdentifier]).Value
    if (-not $trusted.Contains($trustedInstallerSid)) { $trusted.Add($trustedInstallerSid) }
    if (-not [string]::IsNullOrWhiteSpace([string]$script:RegisteredService.StartName) -and
        -not [string]::Equals([string]$script:RegisteredService.StartName, 'LocalSystem', [System.StringComparison]::OrdinalIgnoreCase)) {
        $serviceAccount = [string]$script:RegisteredService.StartName
        $serviceSid = if ($serviceAccount -match '^S-1-') {
            $serviceAccount
        } else {
            [System.Security.Principal.NTAccount]::new($serviceAccount).Translate([System.Security.Principal.SecurityIdentifier]).Value
        }
        if (-not $trusted.Contains($serviceSid)) { $trusted.Add($serviceSid) }
    }
    try {
        $serviceSid = [System.Security.Principal.NTAccount]::new('NT SERVICE', $ClientService).Translate([System.Security.Principal.SecurityIdentifier]).Value
        if (-not $trusted.Contains($serviceSid)) { $trusted.Add($serviceSid) }
    }
    catch {
        # Service SID is optional for an existing service; its configured logon SID above remains authoritative.
    }
    return ,([string[]]$trusted)
}

function Get-NetRatelLocalAdministratorMemberSids {
    if ($null -eq $script:LocalAdministratorMemberSids) {
        $administratorGroup = Get-CimInstance Win32_Group -Filter "SID='S-1-5-32-544'" -ErrorAction Stop
        if (-not $administratorGroup) { throw 'The local Administrators group could not be identified.' }
        $members = @(Get-CimAssociatedInstance -InputObject $administratorGroup -Association 'Win32_GroupUser' -ErrorAction Stop)
        $script:LocalAdministratorMemberSids = @($members | ForEach-Object { if ($_.SID) { [string]$_.SID } })
    }
    return ,([string[]]$script:LocalAdministratorMemberSids)
}

function Test-NetRatelAdministratorMemberSid([string] $Sid) {
    if ([string]::IsNullOrWhiteSpace($Sid)) { return $false }
    return $Sid -in (Get-NetRatelLocalAdministratorMemberSids)
}

function Get-NetRatelCanonicalPath([string] $Path, [string] $SettingName) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$SettingName must be an absolute path."
    }
    $canonical = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($canonical)
    if ($canonical.Length -gt $root.Length) { $canonical = $canonical.TrimEnd([char[]]@('\', '/')) }
    return $canonical
}

function Resolve-NetRatelRegisteredRoot([string] $ExecutablePath, [string] $ConfiguredRoot) {
    $root = Get-NetRatelCanonicalPath $ConfiguredRoot 'registered package root'
    $executable = Get-NetRatelCanonicalPath $ExecutablePath 'registered client executable'
    $legacyExecutable = [System.IO.Path]::GetFullPath((Join-Path $root 'NetRatel.Client.exe'))
    if ([string]::Equals($executable, $legacyExecutable, [System.StringComparison]::OrdinalIgnoreCase)) { return $root }

    $versionDirectory = Split-Path -Parent $executable
    $version = Split-Path -Leaf $versionDirectory
    $versionsDirectory = Split-Path -Parent $versionDirectory
    $expectedVersionsDirectory = [System.IO.Path]::GetFullPath((Join-Path $root 'versions'))
    $expectedVersionExecutable = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $expectedVersionsDirectory $version) 'NetRatel.Client.exe'))
    if ($version -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -and
        [string]::Equals($versionsDirectory, $expectedVersionsDirectory, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($executable, $expectedVersionExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $root
    }
    throw 'The registered NetRatel executable does not identify an exact supported install root.'
}

function Test-NetRatelPathWithin([string] $Path, [string] $Directory) {
    $canonicalPath = Get-NetRatelCanonicalPath $Path 'Configured output path'
    $canonicalDirectory = Get-NetRatelCanonicalPath $Directory 'Configured output directory'
    return [string]::Equals($canonicalPath, $canonicalDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
        $canonicalPath.StartsWith($canonicalDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-NetRatelTrustedPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][bool] $LeafIsDirectory,
        [bool] $AllowMissingLeaf = $false,
        [bool] $AllowLegacyDirectoryWrites = $false,
        [bool] $AllowLocalAdministrator = $false,
        [bool] $AllowLegacyAdministratorAncestors = $false
    )

    $fullPath = Get-NetRatelCanonicalPath $Path 'NetRatel updater path'
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) { throw 'The updater path has no filesystem root.' }
    $trustedSids = Get-NetRatelTrustedPathSids
    $localAdministratorSids = if ($AllowLocalAdministrator -or $AllowLegacyAdministratorAncestors) { Get-NetRatelLocalAdministratorMemberSids } else { @() }
    $dangerousRights = [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    $leafWriteRights = [System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes

    $components = [System.Collections.Generic.List[string]]::new()
    $components.Add($pathRoot)
    $current = $pathRoot
    foreach ($component in $fullPath.Substring($pathRoot.Length).Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $component
        $components.Add($current)
    }

    for ($index = 0; $index -lt $components.Count; $index++) {
        $componentPath = $components[$index]
        $isLeaf = $index -eq ($components.Count - 1)
        $allowLegacyAdministratorForComponent = $AllowLocalAdministrator -or
            (-not $isLeaf -and $AllowLegacyAdministratorAncestors)
        if (-not (Test-Path -LiteralPath $componentPath)) {
            if ($AllowMissingLeaf -and $isLeaf) { return $fullPath }
            throw 'A required updater path component does not exist.'
        }

        $item = Get-Item -LiteralPath $componentPath -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($isLeaf -and ($item.PSIsContainer -eq (-not $LeafIsDirectory))) -or
            (-not $isLeaf -and -not $item.PSIsContainer)) {
            throw 'The updater path contains a reparse point or unexpected file type.'
        }

        $acl = Get-Acl -LiteralPath $componentPath -ErrorAction Stop
        $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        $trustedOwner = $ownerSid -in $trustedSids -or
            ($allowLegacyAdministratorForComponent -and $ownerSid -in $localAdministratorSids)
        if (-not $trustedOwner) { throw 'The updater path has an untrusted owner.' }
        $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
        foreach ($rule in $rules) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
                ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
            $ruleSid = $rule.IdentityReference.Value
            $trustedPrincipal = $ruleSid -in $trustedSids -or
                ($allowLegacyAdministratorForComponent -and $ruleSid -in $localAdministratorSids)
            if (-not $trustedPrincipal -and ($rule.FileSystemRights -band $dangerousRights) -ne 0) {
                throw 'The updater path grants an untrusted principal replacement access.'
            }
            if ($isLeaf -and -not $trustedPrincipal -and ($rule.FileSystemRights -band $leafWriteRights) -ne 0 -and
                -not ($AllowLegacyDirectoryWrites -and $LeafIsDirectory -and $rule.IsInherited)) {
                throw 'The updater path grants an untrusted principal write access.'
            }
        }
    }
    return $fullPath
}

function Protect-NetRatelOwnedStateTree {
    param([Parameter(Mandatory = $true)][string] $Path, [switch] $ValidateOnly)

    $trustedSids = Get-NetRatelTrustedPathSids
    $items = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.Stack[object]]::new()
    $pending.Push((Get-Item -LiteralPath $Path -Force -ErrorAction Stop))
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        $isDirectory = [bool]$item.PSIsContainer
        [void](Assert-NetRatelTrustedPath -Path $item.FullName -LeafIsDirectory:$isDirectory -AllowLegacyDirectoryWrites:$isDirectory -AllowLocalAdministrator:$true)
        $items.Add($item)
        if ($isDirectory) {
            foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)) {
                if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'The existing update state contains a reparse point.'
                }
                $pending.Push($child)
            }
        }
    }

    if ($ValidateOnly) { return }

    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName -ErrorAction Stop
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($existingRule in @($acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))) {
            [void]$acl.RemoveAccessRuleSpecific($existingRule)
        }
        $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
        $inheritance = if ($item.PSIsContainer) {
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
        } else { [System.Security.AccessControl.InheritanceFlags]::None }
        foreach ($sidValue in $trustedSids) {
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                [System.Security.Principal.SecurityIdentifier]::new($sidValue), $rights, $inheritance,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $item.FullName -AclObject $acl -ErrorAction Stop
    }
}

function Get-NetRatelConfiguredClientOption([string] $Name) {
    $environmentValue = Get-NetRatelServiceEnvironmentValue "NetRatelCLIENT__Client__AutoUpdate__$Name"
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) { return $environmentValue }

    $executableDirectory = Split-Path -Parent $script:RegisteredExecutablePath
    foreach ($fileName in @('clientsettings.json', 'appsettings.json')) {
        $settingsPath = Join-Path $executableDirectory $fileName
        if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { continue }
        [void](Assert-NetRatelTrustedPath -Path $settingsPath -LeafIsDirectory:$false -AllowLocalAdministrator:$true)
        $settings = Get-Content -LiteralPath $settingsPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        $client = if ($settings.PSObject.Properties['Client']) { $settings.Client } else { $settings }
        $autoUpdate = if ($client -and $client.PSObject.Properties['AutoUpdate']) { $client.AutoUpdate } else { $null }
        if ($autoUpdate -and $autoUpdate.PSObject.Properties[$Name]) {
            $value = [string]$autoUpdate.PSObject.Properties[$Name].Value
            if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
        }
    }
    return $null
}

function Assert-NetRatelConfiguredPath([string] $Actual, [string] $Expected, [string] $Name) {
    $actualPath = Get-NetRatelCanonicalPath $Actual $Name
    $expectedPath = Get-NetRatelCanonicalPath $Expected $Name
    if (-not [string]::Equals($actualPath, $expectedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The updater $Name does not match the registered NetRatel service configuration."
    }
    return $actualPath
}

function Initialize-NetRatelUpdaterPreflight {
    $service = Get-CimInstance Win32_Service -Filter "Name='$ClientService'" -ErrorAction Stop
    if (-not $service -or [string]::IsNullOrWhiteSpace([string]$service.PathName)) {
        throw "$ClientService service registration was not found; updater refuses to write outside an owned installation."
    }
    $script:RegisteredService = $service
    $script:ServiceEnvironment = Get-NetRatelServiceEnvironment

    $registeredExecutable = Assert-NetRatelOwnedServiceImage -ImagePath ([string]$service.PathName) -SkipManifest
    $resolvedRoot = Assert-NetRatelConfiguredPath $RootDir $RootDir 'package root'
    $registeredRoot = Resolve-NetRatelRegisteredRoot $registeredExecutable $resolvedRoot
    $resolvedRoot = Assert-NetRatelConfiguredPath $resolvedRoot $registeredRoot 'package root'
    $registeredRootSetting = Get-NetRatelServiceEnvironmentValue 'NetRatel_UPDATE_ROOT'
    if ($registeredRootSetting) { [void](Assert-NetRatelConfiguredPath $resolvedRoot $registeredRootSetting 'package root') }
    if ($env:NetRatel_UPDATE_ROOT) { [void](Assert-NetRatelConfiguredPath $resolvedRoot $env:NetRatel_UPDATE_ROOT 'package root') }

    [void](Assert-NetRatelTrustedPath -Path $resolvedRoot -LeafIsDirectory:$true -AllowLocalAdministrator:$true)
    [void](Assert-NetRatelTrustedPath -Path $registeredExecutable -LeafIsDirectory:$false -AllowLocalAdministrator:$true)
    $script:RegisteredExecutablePath = $registeredExecutable

    $installedManifestPath = Join-Path (Split-Path -Parent $registeredExecutable) 'netratel-client-manifest.json'
    if (Test-Path -LiteralPath $installedManifestPath -PathType Leaf) {
        [void](Assert-NetRatelTrustedPath -Path $installedManifestPath -LeafIsDirectory:$false -AllowLocalAdministrator:$true)
        $installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ([string]$installedManifest.schema -ne 'netratel.client.manifest.v1' -or
            [string]$installedManifest.product -ne 'NetRatel.Client' -or
            [string]$installedManifest.executable -ne 'NetRatel.Client.exe' -or
            [string]$installedManifest.runtimeId -notmatch '^win-[A-Za-z0-9][A-Za-z0-9._-]*$' -or
            [string]$installedManifest.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
            [string]$installedManifest.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
            throw 'The installed client manifest cannot establish a trusted update runtime.'
        }
        $script:RuntimeId = [string]$installedManifest.runtimeId
        $script:InstalledVersion = [string]$installedManifest.version
    } else {
        $script:RuntimeId = Get-NetRatelConfiguredClientOption 'RuntimeId'
        if ([string]$script:RuntimeId -notmatch '^win-[A-Za-z0-9][A-Za-z0-9._-]*$') {
            throw 'The installed client has no trusted runtime identifier; refusing to use a request-selected runtime.'
        }
    }
    [void](Assert-NetRatelOwnedServiceImage -ImagePath ([string]$service.PathName))

    $configuredClientState = Get-NetRatelConfiguredClientOption 'StateDirectory'
    $configuredUpdaterState = Get-NetRatelServiceEnvironmentValue 'NetRatel_UPDATE_STATE'
    if ($configuredClientState -and $configuredUpdaterState) {
        [void](Assert-NetRatelConfiguredPath $configuredClientState $configuredUpdaterState 'update state directory')
    }
    $expectedState = if ($configuredUpdaterState) { $configuredUpdaterState } elseif ($configuredClientState) { $configuredClientState } else { Join-Path $env:ProgramData 'NetRatel\update' }
    $resolvedState = Assert-NetRatelConfiguredPath $StateDir $expectedState 'update state directory'
    if ($env:NetRatel_UPDATE_STATE) { [void](Assert-NetRatelConfiguredPath $resolvedState $env:NetRatel_UPDATE_STATE 'update state directory') }

    $configuredClientRequest = Get-NetRatelConfiguredClientOption 'RequestPath'
    $configuredUpdaterRequest = Get-NetRatelServiceEnvironmentValue 'NetRatel_UPDATE_REQUEST'
    if ($configuredClientRequest) { $configuredClientRequest = [System.IO.Path]::GetFullPath($configuredClientRequest) }
    if ($configuredUpdaterRequest) { $configuredUpdaterRequest = [System.IO.Path]::GetFullPath($configuredUpdaterRequest) }
    if ($configuredClientRequest -and $configuredUpdaterRequest) {
        [void](Assert-NetRatelConfiguredPath $configuredClientRequest $configuredUpdaterRequest 'update request path')
    }
    $expectedRequest = if ($configuredUpdaterRequest) { $configuredUpdaterRequest } elseif ($configuredClientRequest) { $configuredClientRequest } else { Join-Path $StateDir 'request.json' }
    $resolvedRequest = Assert-NetRatelConfiguredPath $RequestPath $expectedRequest 'update request path'
    if ($env:NetRatel_UPDATE_REQUEST) { [void](Assert-NetRatelConfiguredPath $resolvedRequest $env:NetRatel_UPDATE_REQUEST 'update request path') }

    $configuredReady = Get-NetRatelConfiguredClientOption 'ReadyPath'
    $expectedReady = if ($configuredReady) { [System.IO.Path]::GetFullPath($configuredReady) } else { Join-Path $StateDir 'ready.json' }
    $script:RootDir = $resolvedRoot
    $script:VersionsDir = Join-Path $resolvedRoot 'versions'
    $script:StagingDir = Join-Path $resolvedRoot 'staging'
    $script:FailedDir = Join-Path $resolvedRoot 'failed'
    $script:StateDir = $resolvedState
    $script:RequestPath = $resolvedRequest
    $script:StatePath = Join-Path $resolvedState 'state.json'
    $script:LockPath = Join-Path $resolvedState 'update.lock'
    $script:ResultPath = Join-Path $resolvedState 'result.json'
    $script:PresencePath = Join-Path $resolvedState 'presence.json'
    $script:ActivationPath = Join-Path $resolvedState 'activation.json'
    $RootDir = $resolvedRoot
    $VersionsDir = $script:VersionsDir
    $StagingDir = $script:StagingDir
    $FailedDir = $script:FailedDir
    $StateDir = $resolvedState
    $RequestPath = $resolvedRequest
    $StatePath = $script:StatePath
    $LockPath = $script:LockPath
    $script:LogPath = $null

    $canonicalState = $resolvedState
    if (-not (Test-Path -LiteralPath $canonicalState -PathType Container)) { return $null }
    [void](Assert-NetRatelTrustedPath -Path $canonicalState -LeafIsDirectory:$true -AllowLegacyDirectoryWrites:$true -AllowLocalAdministrator:$true)
    [void](Assert-NetRatelTrustedPath -Path $RequestPath -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLocalAdministrator:$true)
    if (-not (Test-Path -LiteralPath $RequestPath -PathType Leaf)) { return $null }
    $request = Get-Content -LiteralPath $RequestPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ([string]$request.schema -ne 'netratel.update.request.v2') { throw 'The update request schema is not supported.' }
    $attemptGuid = [Guid]::Empty
    $releaseGuid = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$request.attemptId, [ref]$attemptGuid) -or $attemptGuid -eq [Guid]::Empty -or
        -not [Guid]::TryParse([string]$request.releaseId, [ref]$releaseGuid) -or $releaseGuid -eq [Guid]::Empty -or
        [string]::IsNullOrWhiteSpace([string]$request.admissionNonce)) {
        throw 'The update request is missing valid attempt, release, or admission context.'
    }
    $version = [string]$request.toVersion
    if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$request.version }
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw 'The update request version is invalid.'
    }
    $runtimeId = [string]$request.runtimeId
    if ($runtimeId -notmatch '^win-[A-Za-z0-9][A-Za-z0-9._-]*$' -or
        -not [string]::Equals($runtimeId, $script:RuntimeId, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The update request runtime identifier does not match the installed client.'
    }
    if ([string]$request.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'The update request checksum is invalid.' }
    $expectedPackage = Join-Path (Join-Path $StateDir 'staging') "$runtimeId-$version.zip"
    $packagePath = Assert-NetRatelConfiguredPath ([string]$request.packagePath) $expectedPackage 'staged package path'
    $requestReadyPath = Assert-NetRatelConfiguredPath ([string]$request.readyPath) $expectedReady 'readiness path'
    $requestResultPath = Assert-NetRatelConfiguredPath ([string]$request.resultPath) (Join-Path $StateDir 'result.json') 'result path'
    $requestPresencePath = Assert-NetRatelConfiguredPath ([string]$request.presencePath) (Join-Path $StateDir 'presence.json') 'presence path'

    $configuredLogDirectory = Get-NetRatelServiceEnvironmentValue 'NetRatel_CLIENT_LOG_DIR'
    if (-not $configuredLogDirectory) { $configuredLogDirectory = Get-NetRatelServiceEnvironmentValue 'NetRatelCLIENT__Client__LogDirectory' }
    if (-not $configuredLogDirectory) { $configuredLogDirectory = Get-NetRatelServiceEnvironmentValue 'NetRatelCLIENT__LogDirectory' }
    if (-not $configuredLogDirectory) { $configuredLogDirectory = Join-Path $env:ProgramData 'NetRatel\Client\logs\service' }
    $configuredLogDirectory = Get-NetRatelCanonicalPath $configuredLogDirectory 'service log directory'
    $localAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
    $fallbackLogDirectory = if ([string]::IsNullOrWhiteSpace($localAppData)) { $null } else {
        Get-NetRatelCanonicalPath (Join-Path $localAppData 'NetRatel\Client\logs\fallback') 'fallback service log directory'
    }
    $requestLogPath = [string]$request.logPath
    if (-not [string]::IsNullOrWhiteSpace($requestLogPath)) {
        $requestLogPath = Get-NetRatelCanonicalPath $requestLogPath 'service log file'
        $insideConfiguredLog = Test-NetRatelPathWithin $requestLogPath $configuredLogDirectory
        $insideFallbackLog = $fallbackLogDirectory -and (Test-NetRatelPathWithin $requestLogPath $fallbackLogDirectory)
        if (-not ($insideConfiguredLog -or $insideFallbackLog) -or
            (Split-Path -Leaf $requestLogPath) -notmatch '^netratel-client-service-.+-[0-9]+\.log$') {
            throw 'The update request log path is outside the configured NetRatel service log directory.'
        }
    }

    if (-not (Test-Path -LiteralPath $RequestPath -PathType Leaf)) { throw 'The protected update request disappeared during preflight.' }
    foreach ($path in @($StatePath, $LockPath, $script:ResultPath, $script:PresencePath, $script:ActivationPath,
        (Join-Path $StateDir 'suspension.json'))) {
        [void](Assert-NetRatelTrustedPath -Path $path -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLocalAdministrator:$true)
    }
    [void](Assert-NetRatelTrustedPath -Path $requestReadyPath -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLocalAdministrator:$true)
    if ($requestLogPath) { [void](Assert-NetRatelTrustedPath -Path $requestLogPath -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLocalAdministrator:$true) }
    [void](Assert-NetRatelTrustedPath -Path $packagePath -LeafIsDirectory:$false -AllowLocalAdministrator:$true)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'The staged update package is missing.' }
    foreach ($path in @((Join-Path $RootDir 'versions'), (Join-Path $RootDir 'staging'), (Join-Path $RootDir 'failed'))) {
        [void](Assert-NetRatelTrustedPath -Path $path -LeafIsDirectory:$true -AllowMissingLeaf:$true -AllowLocalAdministrator:$true)
    }

    # Existing state directories can carry harmless inherited write ACEs from an older
    # installation. Validate every file strictly before taking the lock or hardening it.
    Protect-NetRatelOwnedStateTree -Path $canonicalState -ValidateOnly
    if ($requestLogPath -and (Test-Path -LiteralPath (Split-Path -Parent $requestLogPath) -PathType Container)) {
        $script:LogPath = $requestLogPath
    }
    $script:ValidatedLogPath = $script:LogPath
    $script:LogPath = $null
    $script:AttemptId = [string]$request.attemptId
    $script:ReleaseId = [string]$request.releaseId
    $script:RuntimeId = $runtimeId
    $script:FromVersion = [string]$request.fromVersion
    if ([string]::IsNullOrWhiteSpace($script:FromVersion)) { $script:FromVersion = [string]$request.currentVersion }
    if ($script:InstalledVersion -and
        -not [string]::Equals($script:FromVersion, $script:InstalledVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The update request current version does not match the registered client package.'
    }
    $script:ToVersion = $version
    return $request
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

function Get-NetRatelPublicOrigin([string] $Value) {
    try {
        if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
        $candidate = [Uri]($Value.Trim().TrimEnd('/'))
        if (-not $candidate.IsAbsoluteUri -or $candidate.Query -or $candidate.Fragment -or
            $candidate.Scheme -notin @('https', 'http') -or $candidate.AbsolutePath -notin @('', '/', '/api')) { return $null }
        return $candidate.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/').ToLowerInvariant()
    }
    catch { return $null }
}

function Remove-NetRatelRetiredClientSettings($Settings) {
    if (-not $Settings) { return }
    $gateway = $Settings.Gateway
    if ($gateway) {
        foreach ($property in @($gateway.PSObject.Properties)) {
            $name = $property.Name.ToLowerInvariant()
            if ($name -in @(
                'requiredpresenceauthority', 'telemetryshadowenabled', 'telemetryauthorityenabled',
                'controlauthorityenabled', 'commandauthorityenabled', 'fileauthorityenabled',
                'jobauthorityenabled', 'logauthorityenabled', 'remotesupportauthorityenabled',
                'terminalauthorityenabled', 'remotesupportv1enabled', 'controlgatewayenabled',
                'filegatewayenabled', 'loggatewayenabled', 'remotesupportgatewayenabled',
                'terminalgatewayenabled', 'remotesupportv2inventoryenabled', 'remotesupportv2mediaenabled')) {
                $gateway.PSObject.Properties.Remove($property.Name)
            }
        }
    }
    if ($Settings.Transport) { $Settings.Transport.PSObject.Properties.Remove('Mode') }
}

function Copy-NetRatelInstalledClientSettings {
    param(
        [Parameter(Mandatory = $true)][string] $PreviousImagePath,
        [Parameter(Mandatory = $true)][string] $DestinationDirectory
    )

    $imageMatch = [regex]::Match($PreviousImagePath, '^\s*"(?<path>[^\"]+\.exe)"|^\s*(?<path>\S+\.exe)(?:\s|$)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $previousDirectory = if ($imageMatch.Success) { Split-Path -Parent $imageMatch.Groups['path'].Value } else { $null }
    $previousSettingsPath = if ($previousDirectory) { Join-Path $previousDirectory 'clientsettings.json' } else { $null }
    $destinationSettingsPath = Join-Path $DestinationDirectory 'clientsettings.json'
    $settings = $null
    $settingsSource = 'package defaults'
    if ($previousSettingsPath -and (Test-Path -LiteralPath $previousSettingsPath -PathType Leaf)) {
        $settings = Get-Content -LiteralPath $previousSettingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $settingsSource = 'installed clientsettings.json'
    }
    elseif (Test-Path -LiteralPath $destinationSettingsPath -PathType Leaf) {
        $settings = Get-Content -LiteralPath $destinationSettingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
    }

    if (-not $settings) {
        Write-UpdateLog "Client settings source=$settingsSource; no per-version settings file was present."
        return
    }

    $previousApiBase = $null
    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ClientService"
    $serviceEnvironment = @((Get-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue).Environment)
    foreach ($entry in $serviceEnvironment) {
        if ($entry -isnot [string]) { continue }
        $separator = $entry.IndexOf('=')
        if ($separator -gt 0 -and $entry.Substring(0, $separator).Equals('NetRatelCLIENT__Client__ApiBaseUrl', [System.StringComparison]::OrdinalIgnoreCase)) {
            $previousApiBase = $entry.Substring($separator + 1)
            break
        }
    }
    $apiSource = if ($previousApiBase) { 'service environment' } else { $null }
    if ([string]::IsNullOrWhiteSpace($previousApiBase) -and $previousDirectory -and $settingsSource -eq 'installed clientsettings.json') {
        $clientSettings = if ($settings.PSObject.Properties['Client']) { $settings.Client } else { $settings }
        $previousApiBase = [string]$clientSettings.ApiBaseUrl
        if ($previousApiBase) { $apiSource = 'installed clientsettings.json' }
    }
    if ([string]::IsNullOrWhiteSpace($previousApiBase) -and $previousDirectory) {
        $defaultsPath = Join-Path $previousDirectory 'appsettings.json'
        if (Test-Path -LiteralPath $defaultsPath -PathType Leaf) {
            $defaults = Get-Content -LiteralPath $defaultsPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $previousApiBase = [string]$defaults.Client.ApiBaseUrl
            if ($previousApiBase) { $apiSource = 'installed appsettings.json' }
        }
    }

    $settingsClient = if ($settings.PSObject.Properties['Client']) { $settings.Client } else { $settings }
    $settingsApiOrigin = Get-NetRatelPublicOrigin ([string]$settingsClient.ApiBaseUrl)
    $RemoveEndpoint = $false
    if ($settings.Gateway -and $settingsApiOrigin) {
        $gatewayOrigin = Get-NetRatelPublicOrigin ([string]$settings.Gateway.Endpoint)
        $RemoveEndpoint = $gatewayOrigin -and $gatewayOrigin.Equals($settingsApiOrigin, [System.StringComparison]::OrdinalIgnoreCase)
    }
    Remove-NetRatelRetiredClientSettings $settings
    if ($RemoveEndpoint) { $settings.Gateway.PSObject.Properties.Remove('Endpoint') }
    $settings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $destinationSettingsPath -Encoding UTF8
    if (-not $apiSource) { $apiSource = 'not configured in the prior service' }
    Write-UpdateLog "Preserved per-version client settings from $settingsSource; prior API source=$apiSource; redundant same-origin gateway endpoint removed=$RemoveEndpoint."
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
    $script:RollbackInProgress = $true
    Write-UpdateLog "Rolling back NetRatel client update $($script:ToVersion). reason=$reason"
    Archive-ActivationEvidence
    $rollbackStopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    try {
        $rollbackService = Get-CimInstance Win32_Service -Filter "Name='$ClientService'" -ErrorAction Stop
        if (-not $rollbackService) {
            throw "$ClientService registration disappeared; refusing to move package files without confirming service ownership and stopped state."
        }
        [void](Assert-NetRatelOwnedServiceImage -ImagePath ([string]$rollbackService.PathName))
        if ($rollbackService.State -ne 'Stopped') {
            Stop-Service -Name $ClientService -Force -ErrorAction Stop
            do {
                Start-Sleep -Milliseconds 250
                $rollbackService = Get-CimInstance Win32_Service -Filter "Name='$ClientService'" -ErrorAction Stop
                if (-not $rollbackService) {
                    throw "$ClientService registration disappeared while waiting to stop; refusing to move package files."
                }
                [void](Assert-NetRatelOwnedServiceImage -ImagePath ([string]$rollbackService.PathName))
                if ($rollbackService.State -eq 'Stopped') { break }
            } while ([DateTimeOffset]::UtcNow -lt $rollbackStopDeadline)
        }
        if ($rollbackService.State -ne 'Stopped') {
            throw "$ClientService did not stop; rollback left the running executable and package files untouched."
        }

        New-Item -ItemType Directory -Path $FailedDir -Force | Out-Null
        if ($script:CandidateInstalled -and -not [string]::IsNullOrWhiteSpace($script:TargetDir) -and (Test-Path $script:TargetDir)) {
            $failedTarget = Join-Path $FailedDir "$($script:ToVersion)-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))-$($script:AttemptId)"
            Move-Item -Path $script:TargetDir -Destination $failedTarget -Force -ErrorAction Stop
        }
        if ($script:TargetMovedToBackup -and -not [string]::IsNullOrWhiteSpace($script:TargetBackupPath) -and (Test-Path $script:TargetBackupPath)) {
            Move-Item -Path $script:TargetBackupPath -Destination $script:TargetDir -Force -ErrorAction Stop
        }
        if (-not [string]::IsNullOrWhiteSpace($script:PreviousPath) -and $script:CandidateInstalled) {
            Set-NetRatelServiceImagePath $script:PreviousPath
            $script:ActivePath = $script:PreviousPath
        }
    }
    catch {
        $failureType = $_.Exception.GetType().Name
        Write-UpdateLog "Rollback could not safely restore the client files: $failureType"
        Write-State "rollback_failed" $script:ToVersion
        Write-Result "RollbackUnverified" "Rollback could not safely restore the previous client." "rollback_file_restore_failed"
        throw "NetRatel updater rollback stopped safely; executable files were not moved unless the owning service was confirmed stopped ($failureType)."
    }
    if (-not [string]::IsNullOrWhiteSpace($script:PresencePath) -and (Test-Path $script:PresencePath)) {
        Remove-Item $script:PresencePath -Force
    }
    try { Start-Service -Name $ClientService -ErrorAction Stop }
    catch {
        Write-UpdateLog "Rollback service start failed: $($_.Exception.GetType().Name)"
        Write-State "rollback_failed" $script:ToVersion
        Write-Result "RollbackUnverified" "Rollback files were restored, but the previous client service did not start." "rollback_service_start_failed"
        throw "The previous NetRatel client service did not start after rollback."
    }

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

$lock = $null
try {
    if (-not [string]::Equals($ClientService, 'NetRatel.Client', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The updater service name is not the supported NetRatel.Client service.'
    }
    $request = Initialize-NetRatelUpdaterPreflight
    if ($null -eq $request) { return }
    $version = [string]$request.toVersion
    if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$request.version }
    $packagePath = [System.IO.Path]::GetFullPath([string]$request.packagePath)
    $expectedSha = ([string]$request.sha256).ToLowerInvariant()
    $readyPath = [string]$request.readyPath
    $script:LogPath = if ($script:LogPath) { $script:LogPath } else { $null }

    $zipPath = $packagePath
    $extractDir = Join-Path $StagingDir "update-$($script:AttemptId)"
    $targetDir = Join-Path $VersionsDir $version
    $script:TargetDir = $targetDir
    $previousPath = [string]$script:RegisteredService.PathName
    if ([string]::IsNullOrWhiteSpace([string]$previousPath)) { throw "$ClientService service executable could not be resolved." }
    [void](Assert-NetRatelOwnedServiceImage -ImagePath ([string]$previousPath))
    $script:PreviousPath = $previousPath

    Protect-NetRatelOwnedStateTree -Path $StateDir
    foreach ($path in @($RequestPath, $StatePath, $LockPath, $script:ResultPath, $script:PresencePath, $script:ActivationPath)) {
        $allowStatePathAncestors = Test-NetRatelPathWithin $path $StateDir
        [void](Assert-NetRatelTrustedPath -Path $path -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLegacyAdministratorAncestors:$allowStatePathAncestors)
    }
    $allowReadyPathAncestors = Test-NetRatelPathWithin $readyPath $StateDir
    [void](Assert-NetRatelTrustedPath -Path $readyPath -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLegacyAdministratorAncestors:$allowReadyPathAncestors)
    if ($script:ValidatedLogPath) {
        [void](Assert-NetRatelTrustedPath -Path $script:ValidatedLogPath -LeafIsDirectory:$false -AllowMissingLeaf:$true -AllowLocalAdministrator:$true)
    }
    $script:TrustedOutputsReady = $true
    $script:LogPath = $script:ValidatedLogPath
    $lock = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $script:UpdateLockAcquired = $true
    New-Item -ItemType Directory -Path $VersionsDir, $StagingDir, $FailedDir -Force | Out-Null

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
        [string]$manifest.executable -ne 'NetRatel.Client.exe' -or
        -not (Test-Path (Join-Path $extractDir ([string]$manifest.executable)))) {
        throw "Update manifest does not match the request."
    }
    $exe = Join-Path $extractDir "NetRatel.Client.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Client executable was not found in staged artifact." }
    Copy-NetRatelInstalledClientSettings -PreviousImagePath ([string]$previousPath) -DestinationDirectory $extractDir

    Write-State "applying" $version
    Write-UpdateLog "Stopping $ClientService for NetRatel client update $version."
    $script:ServiceStopRequested = $true
    Stop-Service -Name $ClientService -Force -ErrorAction Stop
    $stopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 250
        $stoppedService = Get-CimInstance Win32_Service -Filter "Name='$ClientService'" -ErrorAction Stop
        if (-not $stoppedService) { throw "$ClientService registration disappeared while waiting to stop; refusing to replace package files." }
        [void](Assert-NetRatelOwnedServiceImage -ImagePath ([string]$stoppedService.PathName))
        if ($stoppedService.State -eq 'Stopped') { break }
    } while ([DateTimeOffset]::UtcNow -lt $stopDeadline)
    if ($stoppedService.State -ne 'Stopped') { throw "$ClientService did not stop before client files were replaced." }
    $script:CutoverStarted = $true
    if (Test-Path -LiteralPath $targetDir) {
        $script:TargetBackupPath = Join-Path $FailedDir "$version-before-update-$($script:AttemptId)"
        Move-Item -LiteralPath $targetDir -Destination $script:TargetBackupPath -ErrorAction Stop
        $script:TargetMovedToBackup = $true
    }
    Move-Item -Path $extractDir -Destination $targetDir -ErrorAction Stop
    $script:CandidateInstalled = $true
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
                $script:ServiceStopRequested = $false
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
    if ($script:RollbackInProgress) {
        throw
    }
    elseif ($script:CutoverStarted -or $script:ServiceStopRequested -or $script:TargetMovedToBackup -or $script:CandidateInstalled) {
        Invoke-NetRatelRollback "post_cutover_failure"
    }
    else {
        Write-Result "FailedPreActivation" "NetRatel client update failed." $_.Exception.Message
    }
    throw
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    $script:UpdateLockAcquired = $false
}
