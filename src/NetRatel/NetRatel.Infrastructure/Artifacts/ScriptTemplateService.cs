using NetRatel.Application.Artifacts;
using NetRatel.Shared.Client;

namespace NetRatel.Infrastructure.Artifacts;

public sealed class ScriptTemplateService : IScriptTemplateService
{
    public string Build(DeploymentScriptTemplateRequest request)
    {
        if (request.ReadinessTimeoutSeconds is < 5 or > 600)
            throw new ArgumentOutOfRangeException(
                nameof(DeploymentScriptTemplateRequest.ReadinessTimeoutSeconds),
                "Readiness timeout must be between 5 and 600 seconds.");

        request = request with
        {
            ApiBaseUrl = ClientEndpointAddress.NormalizeApiBase(request.ApiBaseUrl),
            GatewayEndpoint = string.IsNullOrWhiteSpace(request.GatewayEndpoint)
                ? null
                : ClientEndpointAddress.NormalizeGatewayBase(request.GatewayEndpoint)
        };

        if (request.RuntimeId.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
            return BuildBash(request);
        if (request.RuntimeId.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
            return BuildMacBash(request);
        return BuildPowerShell(request);
    }

    public string GetFileExtension(string runtimeId)
        => runtimeId.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ||
           runtimeId.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? "sh" : "ps1";

    private static string BuildPowerShell(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var enrollmentBlock = request.InstallAsService
            ? string.Empty
            : $$"""

    & $exe --enroll $EnrollmentCode --api $ApiBase {{silentArg}}
    if ($LASTEXITCODE -ne 0) {
        throw "Enrollment failed with exit code $LASTEXITCODE."
    }
""";

        var updateLockBlock = """
            $updateLockPath = Join-Path $StateDir "update.lock"
            $updateLockDeadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
            while ($null -eq $updateLock) {
                try {
                    $updateLock = [System.IO.File]::Open($updateLockPath, [System.IO.FileMode]::OpenOrCreate,
                        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                }
                catch [System.IO.IOException] {
                    if ([DateTimeOffset]::UtcNow -ge $updateLockDeadline) { throw "Timed out waiting for the NetRatel updater lock." }
                    Start-Sleep -Seconds 1
                }
            }
            """;

        var userInstallBlock = request.InstallAsService
            ? string.Empty
            : $$"""
            $stagedTargetDir = $targetDir
            $versionTargetDir = Join-Path $VersionsDir $resolvedVersion
            $versionBackupDir = Join-Path $FailedDir "$resolvedVersion-replaced-$([Guid]::NewGuid().ToString('N'))"
            $registeredClientService = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'" -ErrorAction Stop
            if ($registeredClientService) {
                $registeredImage = [regex]::Match([string]$registeredClientService.PathName,
                    '^\s*(?:"(?<path>[^"]+\.exe)"|(?<path>\S+\.exe))(?:\s+--service)?\s*$',
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if (-not $registeredImage.Success) { throw 'The registered NetRatel.Client executable could not be resolved; refusing a non-service package replacement.' }
                if (-not [System.IO.Path]::IsPathRooted($registeredImage.Groups['path'].Value)) { throw 'The registered NetRatel.Client executable path is not absolute; refusing a non-service package replacement.' }
                try { $registeredExecutablePath = [System.IO.Path]::GetFullPath($registeredImage.Groups['path'].Value) }
                catch { throw 'The registered NetRatel.Client executable path is invalid; refusing a non-service package replacement.' }
                $versionExecutablePath = [System.IO.Path]::GetFullPath((Join-Path $versionTargetDir 'NetRatel.Client.exe'))
                if ([string]::Equals($registeredExecutablePath, $versionExecutablePath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'This package is owned by the registered NetRatel.Client service; use the service repair path so the service can be stopped and rolled back safely.'
                }
            }
            if (Test-Path -LiteralPath $versionTargetDir) {
                Move-Item -LiteralPath $versionTargetDir -Destination $versionBackupDir -ErrorAction Stop
            }
            try {
                Move-Item -LiteralPath $targetDir -Destination $versionTargetDir -ErrorAction Stop
                $targetDir = $versionTargetDir
                $exe = Join-Path $targetDir "NetRatel.Client.exe"
            }
            catch {
                if (Test-Path -LiteralPath $versionTargetDir) { Move-Item -LiteralPath $versionTargetDir -Destination $stagedTargetDir -Force }
                if (Test-Path -LiteralPath $versionBackupDir) { Move-Item -LiteralPath $versionBackupDir -Destination $versionTargetDir -Force }
                throw
            }
            """;

        var servicePreflightBlock = request.InstallAsService
            ? """
$serviceName = 'NetRatel.Client'
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$existingService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
$existingServiceEnvironment = @()
if ($existingService) {
    $existingEnvironmentValue = (Get-ItemProperty -Path $serviceRegistryPath -ErrorAction Stop).Environment
    if ($null -ne $existingEnvironmentValue) { $existingServiceEnvironment = @($existingEnvironmentValue) }
}

function Get-NetRatelServiceEnvironmentValue([string] $name) {
    foreach ($entry in $existingServiceEnvironment) {
        if ($entry -isnot [string]) { continue }
        $separator = $entry.IndexOf('=')
        if ($separator -le 0) { continue }
        if ($entry.Substring(0, $separator).Equals($name, [StringComparison]::OrdinalIgnoreCase)) {
            return $entry.Substring($separator + 1)
        }
    }
    return $null
}

function Get-NetRatelCanonicalPath([string] $value, [string] $settingName) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    if (-not [System.IO.Path]::IsPathRooted($value)) { throw "$settingName must be an absolute path." }
    $fullPath = [System.IO.Path]::GetFullPath($value)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.Length -gt $pathRoot.Length) { $fullPath = $fullPath.TrimEnd([char[]]@('\', '/')) }
    return $fullPath
}

function Test-NetRatelSameOrAncestorPath([string] $candidate, [string] $target) {
    $candidatePath = Get-NetRatelCanonicalPath $candidate 'candidate path'
    $targetPath = Get-NetRatelCanonicalPath $target 'target path'
    if ([string]::Equals($candidatePath, $targetPath, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    if (-not $candidatePath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $candidatePath += [System.IO.Path]::DirectorySeparatorChar
    }
    return $targetPath.StartsWith($candidatePath, [StringComparison]::OrdinalIgnoreCase)
}

function Get-NetRatelClientServiceExecutablePath([string] $pathName) {
    if ([string]::IsNullOrWhiteSpace($pathName)) { return $null }
    $imageMatch = [regex]::Match($pathName,
        '^\s*(?:"(?<path>[^"]+\.exe)"|(?<path>\S+\.exe))(?:\s+--service)?\s*$',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $imageMatch.Success -or -not [System.IO.Path]::IsPathRooted($imageMatch.Groups['path'].Value)) { return $null }
    try { return [System.IO.Path]::GetFullPath($imageMatch.Groups['path'].Value) }
    catch { return $null }
}

function Get-NetRatelOwnedClientExecutablePath([string] $pathName, [string] $rootDir) {
    $executablePath = Get-NetRatelClientServiceExecutablePath $pathName
    if (-not $executablePath -or [string]::IsNullOrWhiteSpace($rootDir)) { return $null }
    $canonicalRoot = [System.IO.Path]::GetFullPath($rootDir).TrimEnd([char[]]@('\', '/'))
    $rootExecutable = [System.IO.Path]::GetFullPath((Join-Path $canonicalRoot 'NetRatel.Client.exe'))
    if ([string]::Equals($executablePath, $rootExecutable, [StringComparison]::OrdinalIgnoreCase)) { return $executablePath }
    $version = Split-Path -Leaf (Split-Path -Parent $executablePath)
    if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { return $null }
    $expectedVersionExecutable = [System.IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path $canonicalRoot 'versions') $version) 'NetRatel.Client.exe'))
    if ([string]::Equals($executablePath, $expectedVersionExecutable, [StringComparison]::OrdinalIgnoreCase)) { return $executablePath }
    return $null
}

function Assert-NetRatelPathsMatch([string] $left, [string] $right, [string] $settingName) {
    $leftPath = Get-NetRatelCanonicalPath $left $settingName
    $rightPath = Get-NetRatelCanonicalPath $right $settingName
    if ($leftPath -and $rightPath -and -not [string]::Equals($leftPath, $rightPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The existing $settingName does not match the requested NetRatel installation path; refusing to split the installer and updater state."
    }
}

function Resolve-NetRatelInstallRoot([string] $requestedRoot, [string] $serviceRoot, [string] $registeredRoot, [bool] $requestedExplicit) {
    if ($serviceRoot -and $registeredRoot) { Assert-NetRatelPathsMatch $serviceRoot $registeredRoot 'registered NetRatel package root' }
    if ($requestedExplicit) {
        if ($serviceRoot) { Assert-NetRatelPathsMatch $requestedRoot $serviceRoot 'updater root' }
        if ($registeredRoot) { Assert-NetRatelPathsMatch $requestedRoot $registeredRoot 'registered NetRatel package root' }
        return $requestedRoot
    }
    if ($serviceRoot) { return $serviceRoot }
    if ($registeredRoot) { return $registeredRoot }
    return $requestedRoot
}

function Resolve-NetRatelStateDirectory([string] $requestedState, [string] $clientState, [string] $updaterState, [bool] $requestedExplicit) {
    Assert-NetRatelPathsMatch $clientState $updaterState 'automatic update state directory'
    $configuredState = if ($clientState) { $clientState } else { $updaterState }
    if (-not $configuredState) { return $requestedState }
    if ($requestedExplicit) {
        Assert-NetRatelPathsMatch $requestedState $configuredState 'NetRatel_STATE'
        return $requestedState
    }
    return $configuredState
}

function Resolve-NetRatelUpdateRequestPath([string] $stateDirectory, [string] $clientRequest, [string] $settingsRequest, [string] $defaultsRequest, [string] $updaterRequest) {
    $configuredClientRequest = if ($clientRequest) { $clientRequest } elseif ($settingsRequest) { $settingsRequest } else { $defaultsRequest }
    Assert-NetRatelPathsMatch $configuredClientRequest $updaterRequest 'automatic update request path'
    if ($configuredClientRequest) { return $configuredClientRequest }
    if ($updaterRequest) { return $updaterRequest }
    return Join-Path $stateDirectory 'request.json'
}

$rootWasExplicit = -not [string]::IsNullOrWhiteSpace($env:NetRatel_ROOT)
$stateWasExplicit = -not [string]::IsNullOrWhiteSpace($env:NetRatel_STATE)
$previousExecutableDir = $null
$registeredRootDir = $null
if ($existingService) {
    if ([string]::IsNullOrWhiteSpace([string]$existingService.PathName)) { throw 'The registered NetRatel.Client executable could not be resolved; refusing to stop or rewrite an unowned service.' }
    $registeredExecutablePath = Get-NetRatelClientServiceExecutablePath ([string]$existingService.PathName)
    if (-not $registeredExecutablePath) { throw 'The registered NetRatel.Client executable could not be resolved; refusing to stop or rewrite an unowned service.' }
    $previousExecutableDir = Split-Path -Parent $registeredExecutablePath
    if ([string]::Equals((Split-Path -Leaf $registeredExecutablePath), 'NetRatel.Client.exe', [StringComparison]::OrdinalIgnoreCase)) {
        $registeredVersion = Split-Path -Leaf $previousExecutableDir
        if ($registeredVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -and
            [string]::Equals((Split-Path -Leaf (Split-Path -Parent $previousExecutableDir)), 'versions', [StringComparison]::OrdinalIgnoreCase)) {
            $registeredRootDir = Split-Path -Parent (Split-Path -Parent $previousExecutableDir)
        }
        elseif (Test-Path -LiteralPath (Join-Path $previousExecutableDir 'netratel-client-manifest.json') -PathType Leaf) {
            $registeredRootDir = $previousExecutableDir
        }
    }
}
$configuredUpdateRoot = Get-NetRatelServiceEnvironmentValue 'NetRatel_UPDATE_ROOT'
$RootDir = Resolve-NetRatelInstallRoot $RootDir $configuredUpdateRoot $registeredRootDir $rootWasExplicit
$RootDir = Get-NetRatelCanonicalPath $RootDir 'NetRatel_ROOT'

$serviceExecutablePath = if ($existingService) { Get-NetRatelClientServiceExecutablePath ([string]$existingService.PathName) } else { $null }
if ($existingService) {
    $rootFullPath = [System.IO.Path]::GetFullPath($RootDir).TrimEnd('\', '/')
    $legacyRootExecutable = [string]::Equals($serviceExecutablePath, [System.IO.Path]::GetFullPath((Join-Path $rootFullPath 'NetRatel.Client.exe')), [StringComparison]::OrdinalIgnoreCase)
    $registeredVersion = Split-Path -Leaf (Split-Path -Parent $serviceExecutablePath)
    $expectedVersionExecutable = [System.IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path $rootFullPath 'versions') $registeredVersion) 'NetRatel.Client.exe'))
    $ownedVersionExecutable = $registeredVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -and
        [string]::Equals($serviceExecutablePath, $expectedVersionExecutable, [StringComparison]::OrdinalIgnoreCase)
    if (-not ($legacyRootExecutable -or $ownedVersionExecutable)) {
        throw 'The registered NetRatel.Client image is outside the configured NetRatel package layout; refusing to stop or rewrite it.'
    }
    $previousManifestPath = Join-Path (Split-Path -Parent $serviceExecutablePath) 'netratel-client-manifest.json'
    if (Test-Path -LiteralPath $previousManifestPath -PathType Leaf) {
        $previousManifest = Get-Content -LiteralPath $previousManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        if ($previousManifest.schema -ne 'netratel.client.manifest.v1' -or $previousManifest.product -ne 'NetRatel.Client' -or
            $previousManifest.executable -ne 'NetRatel.Client.exe' -or $previousManifest.runtimeId -ne $Runtime -or
            $previousManifest.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
            ($ownedVersionExecutable -and $previousManifest.version -ne (Split-Path -Leaf (Split-Path -Parent $serviceExecutablePath)))) {
            throw 'The registered package manifest does not identify NetRatel.Client; refusing to stop or rewrite an unowned service.'
        }
    }
}

$settingsPath = if ($previousExecutableDir) { Join-Path $previousExecutableDir 'clientsettings.json' } else { $null }
$installedSettings = $null
if ($settingsPath -and (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    $installedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
}
$previousDefaultsPath = if ($previousExecutableDir) { Join-Path $previousExecutableDir 'appsettings.json' } else { $null }
$previousDefaults = $null
if ($previousDefaultsPath -and (Test-Path -LiteralPath $previousDefaultsPath -PathType Leaf)) {
    $previousDefaults = Get-Content -LiteralPath $previousDefaultsPath -Raw | ConvertFrom-Json -ErrorAction Stop
}
$settingsClientForPaths = if ($installedSettings -and $installedSettings.PSObject.Properties['Client']) { $installedSettings.Client } else { $installedSettings }
$settingsAutoUpdate = if ($settingsClientForPaths) { $settingsClientForPaths.AutoUpdate } else { $null }
$defaultsAutoUpdate = if ($previousDefaults -and $previousDefaults.Client) { $previousDefaults.Client.AutoUpdate } else { $null }
$configuredAutoUpdateState = Get-NetRatelServiceEnvironmentValue 'NetRatelCLIENT__Client__AutoUpdate__StateDirectory'
if (-not $configuredAutoUpdateState -and $settingsAutoUpdate) { $configuredAutoUpdateState = [string]$settingsAutoUpdate.StateDirectory }
if (-not $configuredAutoUpdateState -and $defaultsAutoUpdate) { $configuredAutoUpdateState = [string]$defaultsAutoUpdate.StateDirectory }
$configuredUpdaterState = Get-NetRatelServiceEnvironmentValue 'NetRatel_UPDATE_STATE'
$StateDir = Resolve-NetRatelStateDirectory $StateDir $configuredAutoUpdateState $configuredUpdaterState $stateWasExplicit
$StateDir = Get-NetRatelCanonicalPath $StateDir 'NetRatel_STATE'

$configuredAutoUpdateRequest = Get-NetRatelServiceEnvironmentValue 'NetRatelCLIENT__Client__AutoUpdate__RequestPath'
$settingsAutoUpdateRequest = if ($settingsAutoUpdate) { [string]$settingsAutoUpdate.RequestPath } else { $null }
$defaultsAutoUpdateRequest = if ($defaultsAutoUpdate) { [string]$defaultsAutoUpdate.RequestPath } else { $null }
$configuredUpdaterRequest = Get-NetRatelServiceEnvironmentValue 'NetRatel_UPDATE_REQUEST'
$autoUpdateRequestPath = Resolve-NetRatelUpdateRequestPath $StateDir $configuredAutoUpdateRequest $settingsAutoUpdateRequest $defaultsAutoUpdateRequest $configuredUpdaterRequest
$autoUpdateRequestPath = Get-NetRatelCanonicalPath $autoUpdateRequestPath 'automatic update request path'
$configuredAutoUpdateReady = Get-NetRatelServiceEnvironmentValue 'NetRatelCLIENT__Client__AutoUpdate__ReadyPath'
if (-not $configuredAutoUpdateReady -and $settingsAutoUpdate) { $configuredAutoUpdateReady = [string]$settingsAutoUpdate.ReadyPath }
if (-not $configuredAutoUpdateReady -and $defaultsAutoUpdate) { $configuredAutoUpdateReady = [string]$defaultsAutoUpdate.ReadyPath }
$autoUpdateReadyPath = if ($configuredAutoUpdateReady) { $configuredAutoUpdateReady } else { Join-Path $StateDir 'ready.json' }
$autoUpdateReadyPath = Get-NetRatelCanonicalPath $autoUpdateReadyPath 'automatic update ready path'

function Get-NetRatelLocalAdministratorMemberSids {
    if ($null -eq $script:netRatelLocalAdministratorMemberSids) {
        $administratorGroup = Get-CimInstance Win32_Group -Filter "SID='S-1-5-32-544'" -ErrorAction Stop
        if (-not $administratorGroup) { throw 'The local Administrators group could not be identified.' }
        $members = @(Get-CimAssociatedInstance -InputObject $administratorGroup -Association 'Win32_GroupUser' -ErrorAction Stop)
        $script:netRatelLocalAdministratorMemberSids = @($members | ForEach-Object { if ($_.SID) { [string]$_.SID } })
    }
    return ,([string[]]$script:netRatelLocalAdministratorMemberSids)
}

function Test-NetRatelLocalAdministratorMemberSid([string] $sid) {
    if ([string]::IsNullOrWhiteSpace($sid)) { return $false }
    return $sid -in (Get-NetRatelLocalAdministratorMemberSids)
}

function Get-NetRatelTrustedStateSids {
    $trusted = [System.Collections.Generic.List[string]]::new()
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        if (-not $trusted.Contains($sid)) { $trusted.Add($sid) }
    }
    try {
        $trustedInstallerSid = [System.Security.Principal.NTAccount]::new('NT SERVICE', 'TrustedInstaller').Translate([System.Security.Principal.SecurityIdentifier]).Value
        if (-not $trusted.Contains($trustedInstallerSid)) { $trusted.Add($trustedInstallerSid) }
    }
    catch { throw 'The trusted Windows installer principal could not be resolved.' }
    # New LocalSystem-owned paths never inherit trust from a prior service logon account.
    return ,([string[]]$trusted)
}

function Get-NetRatelProtectedDirectoryAcl([string[]] $trustedSids) {
    $acl = [System.Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
    $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sidValue in $trustedSids) {
        $sid = [System.Security.Principal.SecurityIdentifier]::new($sidValue)
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid, $rights, $inheritance, [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function New-NetRatelProtectedDirectory([string] $path, [string[]] $trustedSids, [bool] $allowLegacyAdministratorsOnParent = $false) {
    if (Test-Path -LiteralPath $path) { throw 'A protected updater state directory appeared during creation.' }
    $parentPath = Split-Path -Parent $path
    Assert-NetRatelTrustedReadinessPath $parentPath $false $false $false $allowLegacyAdministratorsOnParent
    $acl = Get-NetRatelProtectedDirectoryAcl $trustedSids
    [void][System.IO.Directory]::CreateDirectory($path, $acl)
    Assert-NetRatelTrustedReadinessPath $path $false $false $true $false $allowLegacyAdministratorsOnParent
}

function Assert-NetRatelTrustedReadinessPath([string] $path, [bool] $leafFile, [bool] $allowInheritedStateWrites = $false, [bool] $checkLeafWrite = $true, [bool] $allowLegacyAdministrators = $false, [bool] $allowLegacyAdministratorAncestors = $false) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) { throw 'The service readiness path has no filesystem root.' }
    if ($fullPath.Length -gt $pathRoot.Length) { $fullPath = $fullPath.TrimEnd([char[]]@('\', '/')) }
    $trustedSids = Get-NetRatelTrustedStateSids
    $pathComponents = [System.Collections.Generic.List[string]]::new()
    $pathComponents.Add($pathRoot)
    $currentPath = $pathRoot
    $remainingPath = $fullPath.Substring($pathRoot.Length)
    foreach ($component in $remainingPath.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $currentPath = Join-Path $currentPath $component
        $pathComponents.Add($currentPath)
    }

    $dangerousRights = [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    $leafWriteRights = [System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes
    foreach ($componentPath in $pathComponents) {
        $isLeaf = [string]::Equals($componentPath, $fullPath, [StringComparison]::OrdinalIgnoreCase)
        $allowLegacyAdministratorForComponent = $allowLegacyAdministrators -or
            (-not $isLeaf -and $allowLegacyAdministratorAncestors)
        try {
            $item = Get-Item -LiteralPath $componentPath -Force -ErrorAction Stop
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                ($isLeaf -and ($item.PSIsContainer -eq $leafFile)) -or
                (-not $isLeaf -and -not $item.PSIsContainer)) {
                throw 'The service readiness path contains an unexpected file or reparse point.'
            }
            $acl = Get-Acl -LiteralPath $componentPath -ErrorAction Stop
            $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
            if ($ownerSid -notin $trustedSids -and
                -not ($allowLegacyAdministratorForComponent -and (Test-NetRatelLocalAdministratorMemberSid $ownerSid))) {
                throw 'The service readiness path has an untrusted owner.'
            }
            $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
            foreach ($rule in $rules) {
                if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
                    ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
                $ruleSid = $rule.IdentityReference.Value
                $untrusted = $ruleSid -notin $trustedSids
                if ($untrusted -and $allowLegacyAdministratorForComponent -and (Test-NetRatelLocalAdministratorMemberSid $ruleSid)) {
                    $untrusted = $false
                }
                if ($untrusted -and ($rule.FileSystemRights -band $dangerousRights) -ne 0) {
                    throw 'The service readiness path grants an untrusted principal replacement access.'
                }
                if ($isLeaf -and $checkLeafWrite -and $untrusted -and ($rule.FileSystemRights -band $leafWriteRights) -ne 0 -and
                    -not ($allowInheritedStateWrites -and $rule.IsInherited)) {
                    throw 'The service readiness path grants an untrusted principal write access.'
                }
            }
        }
        catch {
            throw "The service readiness path could not be securely verified ($($_.Exception.GetType().Name))."
        }
    }
}

function Protect-NetRatelOwnedStateTree([string] $path, [string[]] $trustedSids) {
    $items = [System.Collections.Generic.List[object]]::new()
    $pending = [System.Collections.Generic.Stack[object]]::new()
    $pending.Push((Get-Item -LiteralPath $path -Force -ErrorAction Stop))
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        $isFile = -not $item.PSIsContainer
        Assert-NetRatelTrustedReadinessPath $item.FullName $isFile (-not $isFile) $true $true
        $items.Add($item)
        if ($item.PSIsContainer) {
            foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force -ErrorAction Stop)) {
                if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'The existing updater state contains a reparse point.'
                }
                $pending.Push($child)
            }
        }
    }

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

function Initialize-NetRatelProtectedStateDirectory {
    $canonicalState = [System.IO.Path]::GetFullPath($StateDir)
    $trustedSids = Get-NetRatelTrustedStateSids
    $configuredPaths = @($configuredAutoUpdateState, $configuredUpdaterState) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath([string]$_) }
    $ownsConfiguredState = $false
    foreach ($configuredPath in $configuredPaths) {
        if ([string]::Equals($configuredPath.TrimEnd('\', '/'), $canonicalState.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
            $ownsConfiguredState = $true
        }
    }
    $legacyDefaultState = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramData 'NetRatel\update'))
    $ownsConfiguredState = $ownsConfiguredState -or ($existingService -and $configuredPaths.Count -eq 0 -and
        [string]::Equals($legacyDefaultState.TrimEnd('\', '/'), $canonicalState.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase))
    $trustedServiceState = [bool]($existingService -and $ownsConfiguredState)
    $script:NetRatelStateAncestorAllowance = [bool]($stateWasExplicit -or $trustedServiceState)

    if (Test-Path -LiteralPath $canonicalState) {
        Assert-NetRatelTrustedReadinessPath $canonicalState $false $trustedServiceState $true $trustedServiceState ($stateWasExplicit -or $trustedServiceState)
        if ($trustedServiceState) { Protect-NetRatelOwnedStateTree $canonicalState $trustedSids }
    }
    else {
        $components = [System.Collections.Generic.List[string]]::new()
        $rootPath = [System.IO.Path]::GetPathRoot($canonicalState)
        if ([string]::IsNullOrWhiteSpace($rootPath)) { throw 'The update state path has no filesystem root.' }
        $components.Add($rootPath)
        $current = $rootPath
        foreach ($component in $canonicalState.Substring($rootPath.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
            $current = Join-Path $current $component
            $components.Add($current)
        }
        foreach ($componentPath in $components) {
            if (Test-Path -LiteralPath $componentPath) {
                $isStateLeaf = [string]::Equals($componentPath, $canonicalState, [StringComparison]::OrdinalIgnoreCase)
                $allowLegacyAdministratorsOnPath = -not $isStateLeaf -and ($stateWasExplicit -or $trustedServiceState)
                Assert-NetRatelTrustedReadinessPath $componentPath $false $false $isStateLeaf $allowLegacyAdministratorsOnPath ($stateWasExplicit -or $trustedServiceState)
                continue
            }
            New-NetRatelProtectedDirectory $componentPath $trustedSids ($stateWasExplicit -or $trustedServiceState)
        }
    }
    Assert-NetRatelTrustedReadinessPath $canonicalState $false $false $true $false $script:NetRatelStateAncestorAllowance
}

function Set-NetRatelProtectedInstallDirectoryAcl([string] $path, [string[]] $trustedSids) {
    $acl = Get-Acl -LiteralPath $path -ErrorAction Stop
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($existingRule in @($acl.GetAccessRules($true, $false, [System.Security.Principal.SecurityIdentifier]))) {
        [void]$acl.RemoveAccessRuleSpecific($existingRule)
    }
    $rights = [System.Security.AccessControl.FileSystemRights]::FullControl
    $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sidValue in $trustedSids) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new($sidValue), $rights, $inheritance,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $path -AclObject $acl -ErrorAction Stop
}

function Initialize-NetRatelProtectedInstallDirectories {
    $trustedSids = Get-NetRatelTrustedStateSids
    $canonicalRoot = [System.IO.Path]::GetFullPath($RootDir).TrimEnd('\', '/')
    $ownsRegisteredRoot = $existingService -and $registeredRootDir -and
        [string]::Equals([System.IO.Path]::GetFullPath($registeredRootDir).TrimEnd('\', '/'), $canonicalRoot, [StringComparison]::OrdinalIgnoreCase)
    $configuredServiceLogDir = Get-NetRatelServiceEnvironmentValue 'NetRatel_CLIENT_LOG_DIR'
    $explicitLogDir = -not [string]::IsNullOrWhiteSpace($env:NetRatel_LOG_DIR)
    foreach ($path in @($RootDir, $UpdaterDir, $VersionsDir, $StagingDir, $FailedDir, $LogDir)) {
        $canonicalPath = [System.IO.Path]::GetFullPath($path)
        $insideOwnedRoot = $ownsRegisteredRoot -and
            ($canonicalPath.Equals($canonicalRoot, [StringComparison]::OrdinalIgnoreCase) -or
             $canonicalPath.StartsWith($canonicalRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
        $ownedServiceLogPath = $existingService -and $configuredServiceLogDir -and
            [string]::Equals([System.IO.Path]::GetFullPath([string]$configuredServiceLogDir).TrimEnd('\', '/'), $canonicalPath.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
        $requestedLogPath = $explicitLogDir -and
            [string]::Equals([System.IO.Path]::GetFullPath([string]$env:NetRatel_LOG_DIR).TrimEnd('\', '/'), $canonicalPath.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)
        $explicitRootAncestor = $rootWasExplicit -and (Test-NetRatelSameOrAncestorPath $canonicalPath $canonicalRoot)
        $explicitRootChild = $rootWasExplicit -and (Test-NetRatelSameOrAncestorPath $canonicalRoot $canonicalPath)
        $explicitLogAncestor = $explicitLogDir -and (Test-NetRatelSameOrAncestorPath $canonicalPath ([System.IO.Path]::GetFullPath([string]$env:NetRatel_LOG_DIR)))
        $explicitLogChild = $explicitLogDir -and (Test-NetRatelSameOrAncestorPath ([System.IO.Path]::GetFullPath([string]$env:NetRatel_LOG_DIR)) $canonicalPath)
        $allowLegacyAdministrators = [bool]($insideOwnedRoot -or $ownedServiceLogPath -or $requestedLogPath)
        $allowLegacyAdministratorAncestors = [bool]($explicitRootAncestor -or $explicitRootChild -or $explicitLogAncestor -or $explicitLogChild)
        if (Test-Path -LiteralPath $canonicalPath) {
            Assert-NetRatelTrustedReadinessPath $canonicalPath $false $false $true $allowLegacyAdministrators $allowLegacyAdministratorAncestors
            if ($insideOwnedRoot -or $ownedServiceLogPath -or $requestedLogPath) {
                Set-NetRatelProtectedInstallDirectoryAcl $canonicalPath $trustedSids
            }
            continue
        }

        $pathRoot = [System.IO.Path]::GetPathRoot($canonicalPath)
        if ([string]::IsNullOrWhiteSpace($pathRoot)) { throw 'The package installation path has no filesystem root.' }
        $currentPath = $pathRoot
        foreach ($component in $canonicalPath.Substring($pathRoot.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
            $currentPath = Join-Path $currentPath $component
            if (Test-Path -LiteralPath $currentPath) {
                $isLeaf = [string]::Equals($currentPath, $canonicalPath, [StringComparison]::OrdinalIgnoreCase)
                $allowLegacyAdministratorsOnPath = [bool]($allowLegacyAdministrators -and -not $isLeaf)
                $allowLegacyAdministratorAncestorsOnPath = [bool](-not $isLeaf -and $allowLegacyAdministratorAncestors)
                Assert-NetRatelTrustedReadinessPath $currentPath $false $false $isLeaf $allowLegacyAdministratorsOnPath $allowLegacyAdministratorAncestorsOnPath
                continue
            }
            New-NetRatelProtectedDirectory $currentPath $trustedSids ($allowLegacyAdministrators -or $allowLegacyAdministratorAncestors)
        }
    }
}
"""
            : string.Empty;

        var serviceDirectoryInitialization = request.InstallAsService
            ? "    Initialize-NetRatelProtectedInstallDirectories" + Environment.NewLine + "    Initialize-NetRatelProtectedStateDirectory"
            : "    New-Item -ItemType Directory -Path $RootDir, $UpdaterDir, $VersionsDir, $StagingDir, $FailedDir, $StateDir -Force | Out-Null";

        var serviceBlock = request.InstallAsService
            ? $$"""
            function Get-NetRatelOrigin([string] $value) {
                try {
                    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
                    $candidate = [Uri]($value.Trim().TrimEnd('/'))
                    if (-not $candidate.IsAbsoluteUri -or $candidate.Scheme -notin @('https', 'http') -or
                        $candidate.Query -or $candidate.Fragment -or $candidate.AbsolutePath -notin @('', '/', '/api')) { return $null }
                    return $candidate.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/')
                }
                catch { return $null }
            }

            function Test-NetRatelOwnedUpdaterImage([string] $pathName) {
                if ([string]::IsNullOrWhiteSpace($pathName)) { return $false }
                $legacyCommandMatch = [regex]::Match($pathName,
                    '^\s*powershell\.exe\s+-NoProfile\s+-ExecutionPolicy\s+Bypass\s+-File\s+"(?<script>[^\"]+\.ps1)"\s*$',
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($legacyCommandMatch.Success) {
                    try {
                        $legacyScriptPath = [System.IO.Path]::GetFullPath($legacyCommandMatch.Groups['script'].Value)
                        $expectedLegacyScriptPath = [System.IO.Path]::GetFullPath((Join-Path $UpdaterDir 'netratel-update.ps1'))
                        if ([string]::Equals($legacyScriptPath, $expectedLegacyScriptPath, [StringComparison]::OrdinalIgnoreCase) -and
                            (Test-Path -LiteralPath $expectedLegacyScriptPath -PathType Leaf)) { return $true }
                    }
                    catch { return $false }
                    return $false
                }
                $commandMatch = [regex]::Match($pathName,
                    '^\s*(?:"(?<host>[^"]+)"|(?<host>\S+))\s+-NoProfile\s+-ExecutionPolicy\s+Bypass\s+-File\s+(?:"(?<script>[^"]+\.ps1)"|(?<script>\S+\.ps1))\s*$',
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if (-not $commandMatch.Success -or -not (Test-NetRatelTrustedWindowsPowerShell $commandMatch.Groups['host'].Value) -or
                    -not [System.IO.Path]::IsPathRooted($commandMatch.Groups['script'].Value)) { return $false }
                try { $actualScriptPath = [System.IO.Path]::GetFullPath($commandMatch.Groups['script'].Value) }
                catch { return $false }
                $expectedScriptPath = [System.IO.Path]::GetFullPath((Join-Path $UpdaterDir 'netratel-update.ps1'))
                return [string]::Equals($actualScriptPath, $expectedScriptPath, [StringComparison]::OrdinalIgnoreCase) -and
                    (Test-Path -LiteralPath $expectedScriptPath -PathType Leaf)
            }

            function Test-NetRatelTrustedWindowsPowerShell([string] $executablePath) {
                if ([string]::IsNullOrWhiteSpace($executablePath) -or -not [System.IO.Path]::IsPathRooted($executablePath)) { return $false }
                try { $actualPath = [System.IO.Path]::GetFullPath($executablePath) }
                catch { return $false }
                $windowsRoot = [Environment]::GetEnvironmentVariable('SystemRoot')
                if ([string]::IsNullOrWhiteSpace($windowsRoot)) { return $false }
                $trustedPaths = @(
                    [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($windowsRoot, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')),
                    [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($windowsRoot, 'SysWOW64', 'WindowsPowerShell', 'v1.0', 'powershell.exe')))
                if (-not ($trustedPaths | Where-Object { [string]::Equals($_, $actualPath, [StringComparison]::OrdinalIgnoreCase) })) { return $false }
                return Test-Path -LiteralPath $actualPath -PathType Leaf
            }

            $previousApiBase = $null
            $previousApiSource = 'package default or unset'
            foreach ($entry in $existingServiceEnvironment) {
                if ($entry -isnot [string]) { continue }
                $separator = $entry.IndexOf('=')
                if ($separator -le 0) { continue }
                $name = $entry.Substring(0, $separator)
                if ($name.Equals('NetRatelCLIENT__Client__ApiBaseUrl', [StringComparison]::OrdinalIgnoreCase)) {
                    $previousApiBase = $entry.Substring($separator + 1)
                    $previousApiSource = 'service environment'
                    break
                }
            }
            $previousExecutableDir = $null
            $serviceExecutablePath = $null
            if ($existingService -and -not [string]::IsNullOrWhiteSpace([string]$existingService.PathName)) {
                $serviceExecutablePath = Get-NetRatelClientServiceExecutablePath ([string]$existingService.PathName)
                if ($serviceExecutablePath) { $previousExecutableDir = Split-Path -Parent $serviceExecutablePath }
            }
            if ($existingService) {
                $ownedServiceExecutablePath = Get-NetRatelOwnedClientExecutablePath ([string]$existingService.PathName) $RootDir
                if (-not $ownedServiceExecutablePath) {
                    throw 'The registered NetRatel.Client image is outside the configured NetRatel package layout; refusing to stop or rewrite it.'
                }
                $serviceExecutablePath = $ownedServiceExecutablePath
                $registeredVersion = Split-Path -Leaf (Split-Path -Parent $serviceExecutablePath)
                $ownedVersionExecutable = $registeredVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$'
                $previousManifestPath = Join-Path (Split-Path -Parent $serviceExecutablePath) 'netratel-client-manifest.json'
                if (Test-Path -LiteralPath $previousManifestPath -PathType Leaf) {
                    $previousManifest = Get-Content -LiteralPath $previousManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
                    if ($previousManifest.schema -ne 'netratel.client.manifest.v1' -or $previousManifest.product -ne 'NetRatel.Client' -or
                        $previousManifest.executable -ne 'NetRatel.Client.exe' -or $previousManifest.runtimeId -ne $Runtime -or
                        $previousManifest.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
                        ($ownedVersionExecutable -and $previousManifest.version -ne $registeredVersion)) {
                        throw 'The registered package manifest does not identify NetRatel.Client; refusing to stop or rewrite an unowned service.'
                    }
                }
            }

            $settingsPath = if ($previousExecutableDir) { Join-Path $previousExecutableDir 'clientsettings.json' } else { $null }
            $installedSettings = $null
            if ($settingsPath -and (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
                $installedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
            }
            if ([string]::IsNullOrWhiteSpace($previousApiBase) -and $installedSettings) {
                $previousClientSettings = if ($installedSettings.PSObject.Properties['Client']) { $installedSettings.Client } else { $installedSettings }
                $previousApiBase = [string]$previousClientSettings.ApiBaseUrl
                if (-not [string]::IsNullOrWhiteSpace($previousApiBase)) { $previousApiSource = 'installed clientsettings.json' }
            }
            if ([string]::IsNullOrWhiteSpace($previousApiBase) -and $previousExecutableDir) {
                $previousDefaultsPath = Join-Path $previousExecutableDir 'appsettings.json'
                if (Test-Path -LiteralPath $previousDefaultsPath -PathType Leaf) {
                    $previousDefaults = Get-Content -LiteralPath $previousDefaultsPath -Raw | ConvertFrom-Json -ErrorAction Stop
                    $previousApiBase = [string]$previousDefaults.Client.ApiBaseUrl
                    if (-not [string]::IsNullOrWhiteSpace($previousApiBase)) { $previousApiSource = 'installed appsettings.json' }
                }
            }
            $previousApiOrigin = Get-NetRatelOrigin $previousApiBase
            if ($existingService) {
                Write-Host "Existing service API source=$previousApiSource; per-version clientsettings.json=$(if ($installedSettings) { 'present' } else { 'absent' })."
            }

            function Remove-RetiredClientSettings($settings) {
                if (-not $settings) { return }
                $gatewaySettings = $settings.Gateway
                if ($gatewaySettings) {
                    foreach ($property in @($gatewaySettings.PSObject.Properties)) {
                        $name = $property.Name.ToLowerInvariant()
                        if ($name -in @(
                            'requiredpresenceauthority', 'telemetryshadowenabled', 'telemetryauthorityenabled',
                            'controlauthorityenabled', 'commandauthorityenabled', 'fileauthorityenabled',
                            'jobauthorityenabled', 'logauthorityenabled', 'remotesupportauthorityenabled',
                            'terminalauthorityenabled', 'remotesupportv1enabled', 'controlgatewayenabled',
                            'filegatewayenabled', 'loggatewayenabled', 'remotesupportgatewayenabled',
                            'terminalgatewayenabled', 'remotesupportv2inventoryenabled', 'remotesupportv2mediaenabled')) {
                            $gatewaySettings.PSObject.Properties.Remove($property.Name)
                        }
                    }
                    $settingsClient = if ($settings.PSObject.Properties['Client']) { $settings.Client } else { $settings }
                    $settingsApiOrigin = Get-NetRatelOrigin ([string]$settingsClient.ApiBaseUrl)
                    $oldGatewayOrigin = Get-NetRatelOrigin ([string]$gatewaySettings.Endpoint)
                    if ($settingsApiOrigin -and $oldGatewayOrigin -and
                        $oldGatewayOrigin.Equals($settingsApiOrigin, [StringComparison]::OrdinalIgnoreCase)) {
                        $gatewaySettings.PSObject.Properties.Remove('Endpoint')
                    }
                }
                if ($settings.Transport) {
                    $settings.Transport.PSObject.Properties.Remove('Mode')
                    if ($settings.Transport.PSObject.Properties.Count -eq 0) {
                        $settings.PSObject.Properties.Remove('Transport')
                    }
                }
            }

            $stageClientSettingsPath = Join-Path $targetDir 'clientsettings.json'
            if ($installedSettings) {
                Remove-RetiredClientSettings $installedSettings
                $installedSettings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $stageClientSettingsPath -Encoding UTF8
            }
            elseif (Test-Path -LiteralPath $stageClientSettingsPath -PathType Leaf) {
                $stageSettings = Get-Content -LiteralPath $stageClientSettingsPath -Raw | ConvertFrom-Json -ErrorAction Stop
                Remove-RetiredClientSettings $stageSettings
                $stageSettings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $stageClientSettingsPath -Encoding UTF8
            }

            $preservedClientEnvironment = @()
            $retiredClientSettings = @(
                'Transport__Mode', 'Gateway__RequiredPresenceAuthority',
                'Gateway__TelemetryShadowEnabled', 'Gateway__TelemetryAuthorityEnabled',
                'Gateway__ControlAuthorityEnabled', 'Gateway__CommandAuthorityEnabled',
                'Gateway__FileAuthorityEnabled', 'Gateway__JobAuthorityEnabled',
                'Gateway__LogAuthorityEnabled', 'Gateway__RemoteSupportAuthorityEnabled',
                'Gateway__TerminalAuthorityEnabled', 'Gateway__RemoteSupportV1Enabled',
                'Gateway__ControlGatewayEnabled', 'Gateway__FileGatewayEnabled',
                'Gateway__LogGatewayEnabled', 'Gateway__RemoteSupportGatewayEnabled',
                'Gateway__RemoteSupportV2InventoryEnabled', 'Gateway__RemoteSupportV2MediaEnabled',
                'Gateway__TerminalGatewayEnabled')
            foreach ($entry in $existingServiceEnvironment) {
                if ($entry -isnot [string]) { continue }
                $separator = $entry.IndexOf('=')
                if ($separator -le 0) { $preservedClientEnvironment += $entry; continue }
                $name = $entry.Substring(0, $separator)
                if ($name.Equals('NetRatel_UPDATE_ROOT', [StringComparison]::OrdinalIgnoreCase) -or
                    $name.Equals('NetRatel_UPDATE_STATE', [StringComparison]::OrdinalIgnoreCase) -or
                    $name.Equals('NetRatel_UPDATE_REQUEST', [StringComparison]::OrdinalIgnoreCase)) { continue }
                if ($name.StartsWith('NetRatelCLIENT__', [StringComparison]::OrdinalIgnoreCase)) {
                    $setting = $name.Substring('NetRatelCLIENT__'.Length)
                    if ($setting.Equals('Client__ApiBaseUrl', [StringComparison]::OrdinalIgnoreCase) -or
                        $setting.Equals('Client__ServiceReadiness__RequestPath', [StringComparison]::OrdinalIgnoreCase) -or
                        $setting.Equals('Client__ServiceReadiness__ReadyPath', [StringComparison]::OrdinalIgnoreCase) -or
                        $setting.Equals('Client__AutoUpdate__StateDirectory', [StringComparison]::OrdinalIgnoreCase) -or
                        $setting.Equals('Client__AutoUpdate__RequestPath', [StringComparison]::OrdinalIgnoreCase) -or
                        $setting.Equals('Client__AutoUpdate__ReadyPath', [StringComparison]::OrdinalIgnoreCase) -or
                        $retiredClientSettings -contains $setting) { continue }
                    if ($setting.Equals('Gateway__Endpoint', [StringComparison]::OrdinalIgnoreCase)) {
                        if (-not [string]::IsNullOrWhiteSpace($GatewayEndpoint)) { continue }
                        $oldGatewayOrigin = Get-NetRatelOrigin ($entry.Substring($separator + 1))
                        if ($previousApiOrigin -and $oldGatewayOrigin -and
                            $oldGatewayOrigin.Equals($previousApiOrigin, [StringComparison]::OrdinalIgnoreCase)) { continue }
                    }
                }
                $preservedClientEnvironment += $entry
            }

            $attemptId = [Guid]::NewGuid().ToString('D')
            $nonceBytes = New-Object byte[] 32
            $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            try { $random.GetBytes($nonceBytes) } finally { $random.Dispose() }
            $nonce = [Convert]::ToBase64String($nonceBytes)
            [Array]::Clear($nonceBytes, 0, $nonceBytes.Length)
            $readinessDir = Join-Path $StateDir 'install-readiness'
            if (Test-Path -LiteralPath $readinessDir) {
                Assert-NetRatelTrustedReadinessPath $readinessDir $false $false $true $false $script:NetRatelStateAncestorAllowance
            }
            else {
                New-NetRatelProtectedDirectory $readinessDir (Get-NetRatelTrustedStateSids) $script:NetRatelStateAncestorAllowance
            }
            Assert-NetRatelTrustedReadinessPath $readinessDir $false $false $true $false $script:NetRatelStateAncestorAllowance
            $requestPath = Join-Path $readinessDir 'request.json'
            $readyPath = Join-Path $readinessDir 'ready.json'
            foreach ($readinessFile in @($requestPath, $readyPath)) {
                $existingReadinessFile = $null
                try { $existingReadinessFile = Get-Item -LiteralPath $readinessFile -Force -ErrorAction Stop }
                catch [System.Management.Automation.ItemNotFoundException] { }
                if ($existingReadinessFile) {
                    Assert-NetRatelTrustedReadinessPath $readinessFile $true $false $true $false $script:NetRatelStateAncestorAllowance
                    Remove-Item -LiteralPath $readinessFile -Force -ErrorAction Stop
                }
            }
            $previousPathName = if ($existingService) { [string]$existingService.PathName } else { $null }
            $previousStartName = if ($existingService) { [string]$existingService.StartName } else { 'LocalSystem' }
            $previousStartMode = if ($existingService) {
                switch ([string]$existingService.StartMode) {
                    'Auto' { 'auto' }
                    'Manual' { 'demand' }
                    'Disabled' { 'disabled' }
                    'Boot' { 'boot' }
                    'System' { 'system' }
                    default { 'auto' }
                }
            } else { 'auto' }
            $previousEnvironment = @($existingServiceEnvironment)
            $previousWasRunning = $existingService -and $existingService.State -eq 'Running'
            $versionTargetDir = Join-Path $VersionsDir $resolvedVersion
            $versionBackupDir = Join-Path $FailedDir "$resolvedVersion-replaced-$attemptId"
            $serviceCreated = $false
            $targetMovedToBackup = $false
            $newTargetInstalled = $false
            $cutoverPrepared = $false
            $serviceConfigurationChanged = $false
            $stagedTargetDir = $targetDir
            $retiredUpdaterService = $null
            try {
                $retiredUpdaterService = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Update'" -ErrorAction Stop
                if ($retiredUpdaterService -and -not (Test-NetRatelOwnedUpdaterImage ([string]$retiredUpdaterService.PathName))) {
                    $retiredUpdaterService = $null
                }
                if ($existingService -and $existingService.State -ne 'Stopped') {
                    Stop-Service -Name $serviceName -Force -ErrorAction Stop
                    $stopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
                    do {
                        Start-Sleep -Milliseconds 250
                        $existingService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                        if ($existingService.State -eq 'Stopped') { break }
                    } while ([DateTimeOffset]::UtcNow -lt $stopDeadline)
                    if ($existingService.State -ne 'Stopped') { throw 'The existing NetRatel.Client service did not stop.' }
                }
                $cutoverPrepared = $true

                if (Test-Path -LiteralPath $versionTargetDir) {
                    Move-Item -LiteralPath $versionTargetDir -Destination $versionBackupDir -ErrorAction Stop
                    $targetMovedToBackup = $true
                }
                Move-Item -LiteralPath $targetDir -Destination $versionTargetDir -ErrorAction Stop
                $newTargetInstalled = $true
                $targetDir = $versionTargetDir
                $exe = Join-Path $targetDir 'NetRatel.Client.exe'

                $enrollmentPayload = @{
                    schema = 'netratel.enroll.v1'
                    tenantId = $TenantId
                    enrollmentCode = $EnrollmentCode
                    issuer = $ApiBase
                    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
                    validToUtc = '{{request.ValidToUtc.UtcDateTime:O}}'
                } | ConvertTo-Json -Depth 4
                $enrollmentPath = Join-Path $targetDir 'netratel.enroll.json'
                Set-Content -LiteralPath $enrollmentPath -Value $enrollmentPayload -Encoding UTF8
                & icacls.exe $enrollmentPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not protect the one-time enrollment instruction.' }

                $updaterSource = Join-Path $targetDir 'updater\netratel-update.ps1'
                if (Test-Path -LiteralPath $updaterSource) {
                    Copy-Item -LiteralPath $updaterSource -Destination (Join-Path $UpdaterDir 'netratel-update.ps1') -Force
                }

                if (-not $existingService) {
                    New-Service -Name $serviceName -BinaryPathName "`"$exe`" --service" -DisplayName 'NetRatel Client' -StartupType Automatic | Out-Null
                    $serviceCreated = $true
                }
                $scResult = & sc.exe config $serviceName "binPath= `"$exe`" --service" 'obj= LocalSystem' 'start= auto' 2>&1
                $serviceConfigurationChanged = $true
                if ($LASTEXITCODE -ne 0) { throw 'Could not configure the NetRatel.Client service executable and LocalSystem identity.' }

                $hasPreservedLogDir = @($preservedClientEnvironment | Where-Object { $_ -match '^NetRatel_CLIENT_LOG_DIR=' }).Count -gt 0
                $clientEnvironment = @()
                if (-not $hasPreservedLogDir) { $clientEnvironment += "NetRatel_CLIENT_LOG_DIR=$LogDir" }
                $clientEnvironment += $preservedClientEnvironment
                $clientEnvironment += "NetRatelCLIENT__Client__ApiBaseUrl=$ApiBase"
                $clientEnvironment += "NetRatelCLIENT__Client__ServiceReadiness__RequestPath=$requestPath"
                $clientEnvironment += "NetRatelCLIENT__Client__ServiceReadiness__ReadyPath=$readyPath"
                $clientEnvironment += "NetRatel_UPDATE_ROOT=$RootDir"
                $clientEnvironment += "NetRatel_UPDATE_STATE=$StateDir"
                $clientEnvironment += "NetRatel_UPDATE_REQUEST=$autoUpdateRequestPath"
                $clientEnvironment += "NetRatelCLIENT__Client__AutoUpdate__StateDirectory=$StateDir"
                $clientEnvironment += "NetRatelCLIENT__Client__AutoUpdate__RequestPath=$autoUpdateRequestPath"
                $clientEnvironment += "NetRatelCLIENT__Client__AutoUpdate__ReadyPath=$autoUpdateReadyPath"
                if (-not [string]::IsNullOrWhiteSpace($GatewayEndpoint)) {
                    $clientEnvironment += "NetRatelCLIENT__Gateway__Endpoint=$GatewayEndpoint"
                }
                New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $clientEnvironment -Force | Out-Null
                $requestedAt = [DateTimeOffset]::UtcNow
                $challenge = @{
                    schema = 'netratel.install-readiness.request.v1'
                    attemptId = $attemptId
                    nonce = $nonce
                    requestedAtUtc = $requestedAt.ToString('O')
                    expiresAtUtc = $requestedAt.AddSeconds({{request.ReadinessTimeoutSeconds}}).ToString('O')
                } | ConvertTo-Json -Depth 4
                Set-Content -LiteralPath $requestPath -Value $challenge -Encoding UTF8
                Assert-NetRatelTrustedReadinessPath $requestPath $true $false $true $false $script:NetRatelStateAncestorAllowance
                Start-Service -Name $serviceName -ErrorAction Stop
            }
            catch {
                $activationFailure = $_
                $rollbackFailures = @()
                $rollbackSafe = -not $cutoverPrepared
                if ($cutoverPrepared) {
                $rollbackSafe = $true
                    $rollbackService = $null
                    try {
                        $rollbackService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                        if (($existingService -or $serviceCreated) -and -not $rollbackService) {
                            throw 'The expected NetRatel.Client service registration could not be confirmed.'
                        }
                        if ($rollbackService -and $rollbackService.State -ne 'Stopped') {
                            Stop-Service -Name $serviceName -Force -ErrorAction Stop
                            $rollbackStopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
                            do {
                                Start-Sleep -Milliseconds 250
                                $rollbackService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                                if ($rollbackService.State -eq 'Stopped') { break }
                            } while ([DateTimeOffset]::UtcNow -lt $rollbackStopDeadline)
                            if ($rollbackService.State -ne 'Stopped') { throw 'The replacement service did not stop; its executable files were left untouched.' }
                        }
                    }
                    catch {
                        $rollbackSafe = $false
                        $rollbackFailures += "service stop/query: $($_.Exception.GetType().Name)"
                    }
                }
                if ($rollbackSafe -and $cutoverPrepared) {
                    try {
                        if ($newTargetInstalled -and (Test-Path -LiteralPath $versionTargetDir)) {
                            $failedCandidateDir = Join-Path $FailedDir "$resolvedVersion-installer-failed-$attemptId"
                            Move-Item -LiteralPath $versionTargetDir -Destination $failedCandidateDir -ErrorAction Stop
                        }
                        if ($targetMovedToBackup -and (Test-Path -LiteralPath $versionBackupDir)) {
                            Move-Item -LiteralPath $versionBackupDir -Destination $versionTargetDir -ErrorAction Stop
                        }
                    }
                    catch {
                        $rollbackFailures += "file restore: $($_.Exception.GetType().Name)"
                    }
                    try {
                        if ($serviceCreated) {
                            & sc.exe delete $serviceName | Out-Null
                            if ($LASTEXITCODE -ne 0) { throw 'Could not remove the new service registration.' }
                        }
                        elseif ($existingService -and $serviceConfigurationChanged) {
                            if ($previousPathName) {
                                & sc.exe config $serviceName "binPath= $previousPathName" "obj= $previousStartName" "start= $previousStartMode" | Out-Null
                                if ($LASTEXITCODE -ne 0) { throw 'Could not restore the previous service configuration.' }
                                $restoredService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                                if (-not [string]::Equals([string]$restoredService.PathName, $previousPathName, [StringComparison]::OrdinalIgnoreCase) -or
                                    -not [string]::Equals([string]$restoredService.StartName, $previousStartName, [StringComparison]::OrdinalIgnoreCase)) {
                                    throw 'The previous service executable or identity did not verify after rollback.'
                                }
                            }
                            if ($previousEnvironment.Count -gt 0) {
                                New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $previousEnvironment -Force | Out-Null
                            }
                            else {
                                Remove-ItemProperty -Path $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
                            }
                            $restoredEnvironment = @((Get-ItemProperty -Path $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue).Environment)
                            if (($restoredEnvironment -join "`n") -ne ($previousEnvironment -join "`n")) { throw 'The previous service environment did not verify after rollback.' }
                            if ($previousWasRunning) {
                                Start-Service -Name $serviceName -ErrorAction Stop
                                $restoreStartDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
                                do {
                                    Start-Sleep -Milliseconds 250
                                    $restoredService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                                    if ($restoredService.State -eq 'Running') { break }
                                } while ([DateTimeOffset]::UtcNow -lt $restoreStartDeadline)
                                if ($restoredService.State -ne 'Running') { throw 'The previous service did not return to Running after rollback.' }
                            }
                        }
                    }
                    catch {
                        $rollbackFailures += "service restore: $($_.Exception.GetType().Name)"
                    }
                }
                Remove-Item -LiteralPath $requestPath, $readyPath -Force -ErrorAction SilentlyContinue
                if ($rollbackFailures.Count -gt 0) {
                    throw "Activation failed and rollback was incomplete ($($rollbackFailures -join ', ')); candidate identity and package files were retained for repair."
                }
                throw $activationFailure
            }

            Write-Host 'Waiting for the LocalSystem service to enroll, authenticate, gain gateway admission, and acknowledge a heartbeat...'
            $readinessDeadline = $requestedAt.AddSeconds({{request.ReadinessTimeoutSeconds}})
            $lastStage = 'service-started'
            $readyRecord = $null
            $expectedServiceExecutablePath = [System.IO.Path]::GetFullPath($exe)
            while ([DateTimeOffset]::UtcNow -lt $readinessDeadline) {
                Start-Sleep -Seconds 2
                $currentService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                $serviceState = if ($currentService) { [string]$currentService.State } else { 'Missing' }
                $currentServiceExecutablePath = if ($currentService) { Get-NetRatelClientServiceExecutablePath ([string]$currentService.PathName) } else { $null }
                if ($serviceState -ne 'Running' -or [int]$currentService.ProcessId -le 0 -or
                    -not [string]::Equals([string]$currentService.StartName, 'LocalSystem', [StringComparison]::OrdinalIgnoreCase) -or
                    -not $currentServiceExecutablePath -or
                    -not [string]::Equals($currentServiceExecutablePath, $expectedServiceExecutablePath, [StringComparison]::OrdinalIgnoreCase)) { continue }
                if (-not (Test-Path -LiteralPath $readyPath)) { continue }
                try { $candidate = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json -ErrorAction Stop }
                catch { continue }
                $lastStage = [string]$candidate.stage
                $parsedAgentId = [Guid]::Empty
                $parsedConnectionId = [Guid]::Empty
                if ($candidate.schema -ne 'netratel.install-readiness.ready.v1' -or
                    $candidate.attemptId -ne $attemptId -or $candidate.nonce -ne $nonce -or
                    $candidate.stage -ne 'heartbeat_ready' -or
                    [int]$candidate.processId -ne [int]$currentService.ProcessId -or
                    [int]$candidate.sessionId -ne 0 -or $candidate.userSid -ne 'S-1-5-18' -or
                    -not [Guid]::TryParse([string]$candidate.agentId, [ref]$parsedAgentId) -or $parsedAgentId -eq [Guid]::Empty -or
                    [int]$candidate.tenantId -ne $TenantId -or [UInt64]$candidate.connectionEpoch -eq 0 -or
                    -not [Guid]::TryParse([string]$candidate.connectionId, [ref]$parsedConnectionId) -or $parsedConnectionId -eq [Guid]::Empty) { continue }
                try {
                    $reportedRequestedAt = [DateTimeOffset]::Parse([string]$candidate.requestedAtUtc).ToUniversalTime()
                    $observedAt = [DateTimeOffset]::Parse([string]$candidate.observedAtUtc).ToUniversalTime()
                    $processStartedAt = [DateTimeOffset]::Parse([string]$candidate.processStartedAtUtc).ToUniversalTime()
                    $actualProcess = Get-Process -Id ([int]$currentService.ProcessId) -ErrorAction Stop
                    $actualStartedAt = [DateTimeOffset]$actualProcess.StartTime.ToUniversalTime()
                    $actualSessionId = [int]$actualProcess.SessionId
                    $now = [DateTimeOffset]::UtcNow
                    if ($reportedRequestedAt -ne $requestedAt -or $processStartedAt -le $requestedAt -or
                        [Math]::Abs(($actualStartedAt - $processStartedAt).TotalSeconds) -gt 2 -or
                        $actualSessionId -ne 0 -or $observedAt -lt $requestedAt -or
                        $observedAt -gt $now.AddSeconds(5) -or $now - $observedAt -gt [TimeSpan]::FromSeconds(30)) { continue }
                }
                catch { continue }
                $readyRecord = $candidate
                break
            }
            Remove-Item -LiteralPath $requestPath -Force -ErrorAction SilentlyContinue
            if (-not $readyRecord) {
                $currentService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop
                $serviceState = if ($currentService) { [string]$currentService.State } else { 'Missing' }
                Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
                throw "NetRatel.Client did not reach gateway heartbeat readiness within the bounded wait. Last stage='$lastStage'; service state='$serviceState'. The installed service identity is retained and a running service may still be retrying."
            }
            Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $targetDir 'netratel.enroll.json') -Force -ErrorAction SilentlyContinue
            if ($retiredUpdaterService) {
                try {
                    if ($retiredUpdaterService.State -ne 'Stopped') {
                        Stop-Service -Name 'NetRatel.Update' -Force -ErrorAction Stop
                        $updaterStopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
                        do {
                            Start-Sleep -Milliseconds 250
                            $retiredUpdaterService = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Update'" -ErrorAction Stop
                            if ($retiredUpdaterService.State -eq 'Stopped') { break }
                        } while ([DateTimeOffset]::UtcNow -lt $updaterStopDeadline)
                        if ($retiredUpdaterService.State -ne 'Stopped') { throw 'The obsolete updater service did not stop.' }
                    }
                    & sc.exe delete 'NetRatel.Update' | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'Could not delete the obsolete updater service registration.' }
                }
                catch {
                    Write-Warning "The client is ready, but the obsolete updater service could not be retired ($($_.Exception.GetType().Name))."
                }
            }
            Write-Host "Gateway heartbeat ready: agentId=$($readyRecord.agentId), tenantId=$($readyRecord.tenantId), connectionEpoch=$($readyRecord.connectionEpoch)."
            """
            : string.Empty;

        return $$"""
# ================================
# NetRatel Automated Deployment Script
# ================================

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$ApiBase = "{{request.ApiBaseUrl}}"
$GatewayEndpoint = "{{request.GatewayEndpoint ?? string.Empty}}"
$TenantId = {{request.TenantId}}
$EnrollmentCode = "{{request.EnrollmentCode}}"
$Runtime = "{{request.RuntimeId}}"
$Version = "{{(string.IsNullOrWhiteSpace(request.ArtifactVersion) ? "latest" : request.ArtifactVersion)}}"
$ExpectedSha256 = "{{(string.IsNullOrWhiteSpace(request.ArtifactSha256) ? string.Empty : request.ArtifactSha256)}}"
$RootDir = if ($env:NetRatel_ROOT) { $env:NetRatel_ROOT } else { Join-Path $env:ProgramFiles "NetRatel\Client" }
$StateDir = if ($env:NetRatel_STATE) { $env:NetRatel_STATE } else { Join-Path $env:ProgramData "NetRatel\update" }
$configuredServiceLogDir = Get-NetRatelServiceEnvironmentValue 'NetRatel_CLIENT_LOG_DIR'
$logDirWasExplicit = -not [string]::IsNullOrWhiteSpace($env:NetRatel_LOG_DIR)
$LogDir = if ($logDirWasExplicit) { $env:NetRatel_LOG_DIR } elseif ($configuredServiceLogDir) { $configuredServiceLogDir } else { Join-Path $env:ProgramData "NetRatel\logs" }
{{servicePreflightBlock}}
$UpdaterDir = Join-Path $RootDir "updater"
$VersionsDir = Join-Path $RootDir "versions"
$StagingDir = Join-Path $RootDir "staging"
$FailedDir = Join-Path $RootDir "failed"
$updateLock = $null
$stageDir = $null

Write-Host "Starting NetRatel Client deployment..."
Write-Host "Enrollment code valid until {{request.ValidToUtc.UtcDateTime:O}}"
try {
    Write-Host "PowerShell version: $($PSVersionTable.PSVersion)"
    Write-Host "PowerShell edition: $($PSVersionTable.PSEdition)"
}
catch {
}

try {
    $tls12 = [System.Enum]::Parse([System.Net.SecurityProtocolType], "Tls12")
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor $tls12
}
catch {
    # Older frameworks may not expose Tls12; continue and let download report the real error.
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

function Get-NetRatelFinalResponseHeaders {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $StatusDescription
    )

    $statusCode = $null
    $headers = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -match '^HTTP/[^ ]+\s+([0-9]{3})(?:\s|$)') {
            $statusCode = [int]$Matches[1]
            $headers = @{}
            continue
        }

        $separator = $line.IndexOf(':')
        if ($statusCode -and $separator -gt 0) {
            $name = $line.Substring(0, $separator).Trim()
            $headers[$name] = $line.Substring($separator + 1).Trim()
        }
    }

    if ($statusCode -ne 200) {
        throw "$StatusDescription returned an unexpected HTTP status."
    }

    return $headers
}

function Receive-NetRatelArtifactWithoutCurl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Uri,
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $Headers,
        [Parameter(Mandatory = $true)]
        [string] $DestinationPath
    )

    $request = [System.Net.HttpWebRequest]::Create($Uri)
    $request.Method = "GET"
    $request.AllowAutoRedirect = $false
    $request.Timeout = 120000
    $request.ReadWriteTimeout = 120000
    foreach ($name in $Headers.Keys) {
        $request.Headers[[string]$name] = [string]$Headers[$name]
    }

    $response = $null
    $responseStream = $null
    $fileStream = $null
    try {
        $response = [System.Net.HttpWebResponse]$request.GetResponse()
        if ($response.StatusCode -ne [System.Net.HttpStatusCode]::OK) {
            throw "The authorized artifact download returned an unexpected HTTP status."
        }

        $responseStream = $response.GetResponseStream()
        $fileStream = [System.IO.File]::Create($DestinationPath)
        $responseStream.CopyTo($fileStream)

        return @{
            "X-NetRatel-Artifact-Rid" = $response.Headers["X-NetRatel-Artifact-Rid"]
            "X-NetRatel-Artifact-Version" = $response.Headers["X-NetRatel-Artifact-Version"]
            "X-NetRatel-Artifact-Sha256" = $response.Headers["X-NetRatel-Artifact-Sha256"]
            "X-NetRatel-Artifact-Size" = $response.Headers["X-NetRatel-Artifact-Size"]
        }
    }
    catch [System.Net.WebException] {
        throw "The authorized artifact download request failed."
    }
    finally {
        if ($null -ne $fileStream) { $fileStream.Dispose() }
        if ($null -ne $responseStream) { $responseStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
    }
}

$tempDir = Join-Path $env:TEMP "netratel_install_$([Guid]::NewGuid())"
try {
    {{serviceDirectoryInitialization}}
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    {{updateLockBlock}}
    $resolvedVersion = $Version
    $zipPath = Join-Path $tempDir "netratel.zip"
    $responseHeadersPath = Join-Path $tempDir "netratel-response-headers.txt"
    $downloadUri = "$ApiBase/api/v1/client-artifacts/$Runtime/$resolvedVersion/onboarding-download"
    $downloadHeaders = @{
        "X-NetRatel-Tenant-Id" = "$TenantId"
        "X-NetRatel-Enrollment-Code" = $EnrollmentCode
    }

    Write-Host "Downloading NetRatel Client package $resolvedVersion..."
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -f --max-redirs 0 --connect-timeout 15 --max-time 120 `
            -H "X-NetRatel-Tenant-Id: $TenantId" `
            -H "X-NetRatel-Enrollment-Code: $EnrollmentCode" `
            -D $responseHeadersPath `
            -o $zipPath $downloadUri
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed with exit code $LASTEXITCODE."
        }

        $responseMetadata = Get-NetRatelFinalResponseHeaders -Path $responseHeadersPath -StatusDescription "Artifact download"
    }
    else {
        $responseMetadata = Receive-NetRatelArtifactWithoutCurl -Uri $downloadUri -Headers $downloadHeaders -DestinationPath $zipPath
    }
    $reportedRid = [string]$responseMetadata["X-NetRatel-Artifact-Rid"]
    $reportedVersion = [string]$responseMetadata["X-NetRatel-Artifact-Version"]
    $reportedSha256 = [string]$responseMetadata["X-NetRatel-Artifact-Sha256"]
    $reportedSize = [string]$responseMetadata["X-NetRatel-Artifact-Size"]
    if ([string]::IsNullOrWhiteSpace($reportedRid) -or [string]::IsNullOrWhiteSpace($reportedVersion) -or
        [string]::IsNullOrWhiteSpace($reportedSha256) -or [string]::IsNullOrWhiteSpace($reportedSize)) {
        throw "The authorized artifact response did not include integrity metadata."
    }

    $reportedSizeBytes = 0L
    if ($reportedRid -ne $Runtime -or
        $reportedVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
        ($Version -ne 'latest' -and $reportedVersion -ne $Version) -or
        $reportedSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        -not [long]::TryParse($reportedSize, [ref]$reportedSizeBytes) -or
        $reportedSizeBytes -le 0) {
        throw "The authorized artifact response metadata did not match the requested package."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        $reportedSha256 -ne $ExpectedSha256) {
        throw "The authorized artifact response did not match the generated package snapshot."
    }
    $resolvedVersion = $reportedVersion
    $ExpectedSha256 = $reportedSha256.ToLowerInvariant()
    if (-not (Test-Path $zipPath) -or ((Get-Item $zipPath).Length -le 0)) {
        throw "Downloaded client package was empty."
    }
    if ((Get-Item $zipPath).Length -ne $reportedSizeBytes) {
        throw "Downloaded client package size did not match its authorized metadata."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $actualSha = Get-NetRatelSha256Hex -Path $zipPath
        if ($actualSha -ne $ExpectedSha256.ToLowerInvariant()) {
            throw "Downloaded client package failed SHA-256 verification."
        }
    }

    $stageDir = Join-Path $StagingDir "install-$([Guid]::NewGuid().ToString('N'))"
    $targetDir = $stageDir
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    Write-Host "Extracting NetRatel Client package..."
    Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $targetDir
    $manifestPath = Join-Path $targetDir "netratel-client-manifest.json"
    $exe = Join-Path $targetDir "NetRatel.Client.exe"
    $wrapperName = "netratel-client-$Runtime"
    $wrapperPath = Join-Path $targetDir $wrapperName
    $rootEntries = @(Get-ChildItem -LiteralPath $targetDir -Force)
    $hasFlatPackage = (Test-Path -LiteralPath $manifestPath -PathType Leaf) -and
        (Test-Path -LiteralPath $exe -PathType Leaf)

    if ($hasFlatPackage) {
        if (Test-Path -LiteralPath $wrapperPath) {
            throw "Client package has an unexpected mixed archive layout."
        }
    }
    elseif ($rootEntries.Count -eq 1 -and
        $rootEntries[0].PSIsContainer -and
        $rootEntries[0].Name -ceq $wrapperName -and
        (($rootEntries[0].Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0)) {
        $wrappedManifestPath = Join-Path $wrapperPath "netratel-client-manifest.json"
        $wrappedExe = Join-Path $wrapperPath "NetRatel.Client.exe"
        if (-not (Test-Path -LiteralPath $wrappedManifestPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $wrappedExe -PathType Leaf)) {
            throw "Wrapped Client package is missing its manifest or executable."
        }

        $wrappedEntries = @(Get-ChildItem -LiteralPath $wrapperPath -Force)
        foreach ($entry in $wrappedEntries) {
            $destinationPath = Join-Path $targetDir $entry.Name
            if (Test-Path -LiteralPath $destinationPath) {
                throw "Wrapped Client package contains conflicting root entries."
            }
            Move-Item -LiteralPath $entry.FullName -Destination $targetDir -ErrorAction Stop
        }
        Remove-Item -LiteralPath $wrapperPath -Force -ErrorAction Stop
    }
    else {
        throw "Client package has an unexpected archive layout."
    }

    $exe = Join-Path $targetDir "NetRatel.Client.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Client executable was not found in extracted package."
    }
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $manifestVersion = [string]$manifest.version
            if ($manifest.schema -ne "netratel.client.manifest.v1" -or
                $manifest.product -ne "NetRatel.Client" -or
                $manifest.runtimeId -ne $Runtime -or
                $manifest.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
                $manifest.executable -ne "NetRatel.Client.exe" -or
                -not (Test-Path -LiteralPath (Join-Path $targetDir ([string]$manifest.executable)) -PathType Leaf) -or
                $manifestVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
                $manifestVersion -ne $resolvedVersion) {
                throw "Client package manifest does not match the requested runtime and version."
            }
            $resolvedVersion = $manifestVersion

    {{enrollmentBlock}}
    {{userInstallBlock}}
    {{serviceBlock}}

    Write-Host "{{(request.InstallAsService ? "NetRatel service installation complete; authenticated gateway heartbeat readiness was verified." : "NetRatel client installed and enrolled; no service readiness was requested.")}}"
}
finally {
    if ($null -ne $updateLock) { $updateLock.Dispose() }
    if ($stageDir -and (Test-Path -LiteralPath $stageDir)) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
    if (Test-Path $tempDir) {
        Remove-Item $tempDir -Recurse -Force
    }
}
""";
    }

    private static string BuildBash(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var enrollCommand = $$"""
"${CLIENT_EXE}" --enroll "${ENROLLMENT_CODE}" --api "${API_BASE}" {{silentArg}}
""";

        var preparationBlock = request.InstallAsService
            ? """
            preserved_client_environment="$(python3 - "${GATEWAY_ENDPOINT}" <<'PY'
            import os
            import re
            import subprocess
            import sys
            from urllib.parse import urlsplit

            prefix = "NetRatelCLIENT__"
            gateway_endpoint = sys.argv[1]
            retired = {
                "transport__mode",
                "gateway__requiredpresenceauthority",
                "gateway__telemetryshadowenabled",
                "gateway__telemetryauthorityenabled",
                "gateway__commandauthorityenabled",
                "gateway__jobauthorityenabled",
                "gateway__terminalauthorityenabled",
                "gateway__fileauthorityenabled",
                "gateway__logauthorityenabled",
                "gateway__controlauthorityenabled",
                "gateway__remotesupportauthorityenabled",
                "gateway__remotesupportv1enabled",
                "gateway__remotesupportv2inventoryenabled",
                "gateway__remotesupportv2mediaenabled",
                "gateway__controlgatewayenabled",
                "gateway__filegatewayenabled",
                "gateway__loggatewayenabled",
                "gateway__remotesupportgatewayenabled",
                "gateway__terminalgatewayenabled",
            }
            retired_json_gateway = {
                "requiredpresenceauthority", "telemetryshadowenabled", "telemetryauthorityenabled",
                "controlauthorityenabled", "commandauthorityenabled", "fileauthorityenabled",
                "jobauthorityenabled", "logauthorityenabled", "remotesupportauthorityenabled",
                "terminalauthorityenabled", "remotesupportv1enabled", "controlgatewayenabled",
                "filegatewayenabled", "loggatewayenabled", "remotesupportgatewayenabled",
                "terminalgatewayenabled", "remotesupportv2inventoryenabled", "remotesupportv2mediaenabled",
            }

            def words(value):
                index = 0
                while index < len(value):
                    while index < len(value) and value[index].isspace():
                        index += 1
                    if index == len(value):
                        break
                    raw = []
                    decoded = []
                    quote = None
                    while index < len(value):
                        char = value[index]
                        if quote is None and char.isspace():
                            break
                        if char == "\\" and quote != "'" and index + 1 < len(value):
                            raw.append(char)
                            escaped = value[index + 1]
                            raw.append(escaped)
                            decoded.append(escaped)
                            index += 2
                            continue
                        if char in "'\"" and (quote is None or quote == char):
                            raw.append(char)
                            quote = None if quote is not None else char
                            index += 1
                            continue
                        raw.append(char)
                        decoded.append(char)
                        index += 1
                    yield "".join(raw), "".join(decoded)

            def public_origin(value):
                try:
                    parsed = urlsplit(value.strip())
                    if not parsed.scheme or not parsed.netloc or parsed.query or parsed.fragment:
                        return None
                    if parsed.path.rstrip("/").lower() not in ("", "/api"):
                        return None
                    return f"{parsed.scheme.lower()}://{parsed.netloc.lower()}"
                except ValueError:
                    return None

            fragment = subprocess.run(["systemctl", "show", "-p", "FragmentPath", "--value", "netratel-client.service"], capture_output=True, text=True).stdout.strip()
            if not fragment or not os.path.isfile(fragment):
                raise SystemExit(0)
            with open(fragment, encoding="utf-8", errors="replace") as unit:
                lines = unit.readlines()
            parsed_environment = []
            previous_api_base = None
            for line in lines:
                match = re.match(r"^\s*Environment=(.*)$", line)
                if not match:
                    continue
                for raw, assignment in words(match.group(1)):
                    name, separator, value = assignment.partition("=")
                    parsed_environment.append((name, separator, value))
                    if separator and name.lower() == (prefix + "Client__ApiBaseUrl").lower():
                        previous_api_base = value
            previous_api_origin = public_origin(previous_api_base or "")
            previous_gateway_is_redundant = any(
                separator and name.lower() == (prefix + "Gateway__Endpoint").lower() and
                previous_api_origin and public_origin(value) == previous_api_origin
                for name, separator, value in parsed_environment)
            for line in lines:
                if line.lstrip().startswith("EnvironmentFile="):
                    print(line)
                    continue
                match = re.match(r"^\s*Environment=(.*)$", line)
                if not match:
                    continue
                for raw, assignment in words(match.group(1)):
                    name, separator, _ = assignment.partition("=")
                    if separator and name.lower().startswith(prefix.lower()):
                        setting = name[len(prefix):].lower()
                        if (setting == "client__apibaseurl" or setting in retired or
                            (setting == "gateway__endpoint" and (gateway_endpoint or previous_gateway_is_redundant))):
                            continue
                    print("Environment=" + raw)
            PY
            )"
            settings_migration="$(python3 - "${PREVIOUS_TARGET}" "${STAGE_DIR}" "${GATEWAY_ENDPOINT}" <<'PY'
            import json
            import os
            import re
            import shlex
            import subprocess
            import sys
            from urllib.parse import urlsplit

            previous_dir, target_dir, new_gateway = sys.argv[1:]
            previous_settings = os.path.join(previous_dir, "clientsettings.json") if previous_dir else ""
            target_settings = os.path.join(target_dir, "clientsettings.json")
            source = previous_settings if previous_settings and os.path.isfile(previous_settings) else target_settings
            if not os.path.isfile(source):
                print("No per-version clientsettings.json was present.")
                raise SystemExit(0)

            with open(source, encoding="utf-8-sig") as stream:
                settings = json.load(stream)

            environment = {}
            try:
                fragment = subprocess.run(["systemctl", "show", "-p", "FragmentPath", "--value", "netratel-client.service"], capture_output=True, text=True, check=True).stdout.strip()
                if fragment and os.path.isfile(fragment):
                    with open(fragment, encoding="utf-8", errors="replace") as stream:
                        for line in stream:
                            match = re.match(r"^\s*Environment=(.*)$", line)
                            if match:
                                for assignment in shlex.split(match.group(1)):
                                    name, separator, value = assignment.partition("=")
                                    if separator:
                                        environment[name] = value
            except (OSError, subprocess.SubprocessError, ValueError):
                pass

            def get_case_insensitive(mapping, name, fallback=None):
                return next((value for key, value in mapping.items() if key.lower() == name.lower()), fallback)

            client = get_case_insensitive(settings, "Client", settings)
            api_base = environment.get("NetRatelCLIENT__Client__ApiBaseUrl", "") or str(client.get("ApiBaseUrl", ""))
            if not api_base and previous_dir:
                try:
                    with open(os.path.join(previous_dir, "appsettings.json"), encoding="utf-8-sig") as stream:
                        api_base = str(json.load(stream).get("Client", {}).get("ApiBaseUrl", ""))
                except (OSError, ValueError, AttributeError):
                    pass

            def origin(value):
                try:
                    parsed = urlsplit(value.strip())
                    if parsed.scheme.lower() not in ("http", "https") or not parsed.netloc or parsed.query or parsed.fragment:
                        return None
                    if parsed.path.rstrip("/").lower() not in ("", "/api"):
                        return None
                    return f"{parsed.scheme.lower()}://{parsed.netloc.lower()}"
                except ValueError:
                    return None

            settings_client = get_case_insensitive(settings, "Client", settings)
            settings_api_origin = origin(str(get_case_insensitive(settings_client, "ApiBaseUrl", "") or ""))
            retired_json_gateway = {
                "requiredpresenceauthority", "telemetryshadowenabled", "telemetryauthorityenabled",
                "controlauthorityenabled", "commandauthorityenabled", "fileauthorityenabled",
                "jobauthorityenabled", "logauthorityenabled", "remotesupportauthorityenabled",
                "terminalauthorityenabled", "remotesupportv1enabled", "controlgatewayenabled",
                "filegatewayenabled", "loggatewayenabled", "remotesupportgatewayenabled",
                "terminalgatewayenabled", "remotesupportv2inventoryenabled", "remotesupportv2mediaenabled",
            }
            gateway = get_case_insensitive(settings, "Gateway")
            if isinstance(gateway, dict):
                retired = {"requiredpresenceauthority"}
                for name in list(gateway):
                    lowered = name.lower()
                    if lowered in retired_json_gateway:
                        gateway.pop(name, None)
                endpoint_key = next((key for key in gateway if key.lower() == "endpoint"), None)
                old_gateway = gateway.get(endpoint_key) if endpoint_key else None
                old_origin = origin(str(old_gateway or ""))
                if old_gateway and (new_gateway or (settings_api_origin and old_origin == settings_api_origin)):
                    gateway.pop(endpoint_key, None)
            transport_key = next((key for key in settings if key.lower() == "transport"), None)
            transport = settings.get(transport_key) if transport_key else None
            if isinstance(transport, dict):
                mode_key = next((key for key in transport if key.lower() == "mode"), None)
                if mode_key: transport.pop(mode_key, None)
                if not transport: settings.pop(transport_key, None)

            os.makedirs(target_dir, exist_ok=True)
            with open(target_settings, "w", encoding="utf-8") as stream:
                json.dump(settings, stream, indent=2)
                stream.write("\n")
            source_label = "installed per-version file" if source == previous_settings else "package file"
            api_label = "service environment" if environment.get("NetRatelCLIENT__Client__ApiBaseUrl") else ("per-version file or package defaults" if api_base else "not configured")
            print(f"Preserved {source_label}; prior API source={api_label}.")
            PY
            )"
            if [ -n "${settings_migration}" ]; then echo "${settings_migration}"; fi

            """
            : string.Empty;
        var serviceBlock = request.InstallAsService
            ? """
            FILES_MUTATED=true
            if [ -f "${TARGET_DIR}/updater/netratel-update.sh" ]; then
              install -m 0755 "${TARGET_DIR}/updater/netratel-update.sh" "${UPDATER_DIR}/.netratel-update.sh.$$"
              mv -f "${UPDATER_DIR}/.netratel-update.sh.$$" "${UPDATER_DIR}/netratel-update.sh"
            fi
            cat > "${ROOT_DIR}/.netratel-client-start.sh.$$" <<SH
            #!/usr/bin/env bash
            set -euo pipefail
            ROOT_DIR="${ROOT_DIR}"
            if [ -x "${ROOT_DIR}/current/NetRatel.Client" ]; then
              exec "${ROOT_DIR}/current/NetRatel.Client" --service
            fi
            echo "No NetRatel client executable found in ${ROOT_DIR}/current" >&2
            exit 78
            SH
            chmod 0755 "${ROOT_DIR}/.netratel-client-start.sh.$$"
            mv -f "${ROOT_DIR}/.netratel-client-start.sh.$$" "${ROOT_DIR}/netratel-client-start.sh"
            cat > "${SYSTEMD_UNIT_DIR}/.netratel-client.service.$$" <<UNIT
            [Unit]
            Description=NetRatel Client
            After=network-online.target
            Wants=network-online.target
            [Service]
            WorkingDirectory=${ROOT_DIR}/current
            ExecStart=${ROOT_DIR}/netratel-client-start.sh
            Restart=always
            RestartPreventExitStatus=78
            Environment=NetRatel_CLIENT_LOG_DIR=/var/lib/netratel/logs
            Environment=NetRatelCLIENT__Client__AutoUpdate__Mode=Service
            Environment=NetRatelCLIENT__Client__AutoUpdate__StateDirectory=${STATE_DIR}
            Environment=NetRatelCLIENT__Client__AutoUpdate__RequestPath=${STATE_DIR}/request.json
            Environment=NetRatelCLIENT__Client__AutoUpdate__ReadyPath=${STATE_DIR}/ready.json
            UNIT
            if [ -n "${preserved_client_environment}" ]; then
              printf '%s\n' "${preserved_client_environment}" >> "${SYSTEMD_UNIT_DIR}/.netratel-client.service.$$"
            fi
            cat >> "${SYSTEMD_UNIT_DIR}/.netratel-client.service.$$" <<UNIT
            Environment=NetRatelCLIENT__Client__ApiBaseUrl=${API_BASE}
            UNIT
            if [ -n "${GATEWAY_ENDPOINT}" ]; then
              printf 'Environment=NetRatelCLIENT__Gateway__Endpoint=%s\n' "${GATEWAY_ENDPOINT}" >> "${SYSTEMD_UNIT_DIR}/.netratel-client.service.$$"
            fi
            cat >> "${SYSTEMD_UNIT_DIR}/.netratel-client.service.$$" <<UNIT
            [Install]
            WantedBy=multi-user.target
            UNIT
            mv -f "${SYSTEMD_UNIT_DIR}/.netratel-client.service.$$" "${SYSTEMD_UNIT_DIR}/netratel-client.service"
            cat > "${SYSTEMD_UNIT_DIR}/.netratel-update.service.$$" <<UNIT
            [Unit]
            Description=NetRatel Client Updater
            After=network-online.target
            [Service]
            Type=oneshot
            ExecStart=${UPDATER_DIR}/netratel-update.sh
            Environment=NetRatel_UPDATE_ROOT=${ROOT_DIR}
            Environment=NetRatel_UPDATE_STATE=${STATE_DIR}
            Environment=NetRatel_UPDATE_REQUEST=${STATE_DIR}/request.json
            Environment=NetRatel_CLIENT_SERVICE=netratel-client.service
            [Install]
            WantedBy=multi-user.target
            UNIT
            mv -f "${SYSTEMD_UNIT_DIR}/.netratel-update.service.$$" "${SYSTEMD_UNIT_DIR}/netratel-update.service"
            systemctl daemon-reload
            if systemctl cat sto-client.service >/dev/null 2>&1; then
              systemctl disable --now sto-client.service
              if systemctl is-active --quiet sto-client.service; then
                echo "Legacy sto-client.service remains active; refusing a competing NetRatel service." >&2
                exit 1
              fi
            fi
            systemctl enable netratel-client.service
            if ! timeout --foreground 60s systemctl start netratel-client.service; then
              echo "NetRatel client service start did not complete successfully. Recent journal output:" >&2
              journalctl -u netratel-client.service -n 80 --no-pager >&2 || true
              exit 1
            fi
            if ! wait_for_service_active; then
              echo "NetRatel client service failed to start. Recent journal output:" >&2
              journalctl -u netratel-client.service -n 80 --no-pager >&2 || true
              exit 1
            fi
            """
            : string.Empty;

        var requiredCommands = request.InstallAsService
            ? "curl sha256sum systemctl flock python3 install timeout realpath readlink grep awk wc tr"
            : "curl sha256sum flock python3 realpath readlink grep awk wc tr";
        var privilegeCheck = request.InstallAsService ? """
            if [ "${EUID}" -ne 0 ] && [ "${NetRatel_TEST_ALLOW_NONROOT:-false}" != "true" ]; then
              echo "This NetRatel systemd installer must be run as root." >&2
              exit 1
            fi
            """ : string.Empty;
        var seededServiceFence = request.InstallAsService ? """
if [ -n "${NETRATEL_SEED_EXPECTED_SERVICE_PID:-}" ] ||
   [ -n "${NETRATEL_SEED_EXPECTED_SERVICE_START_TICKS:-}" ] ||
   [ -n "${NETRATEL_SEED_EXPECTED_UNIT_SHA256:-}" ]; then
  python3 - "${CLIENT_UNIT_PATH}" "${NETRATEL_SEED_EXPECTED_SERVICE_PID:-}" "${NETRATEL_SEED_EXPECTED_SERVICE_START_TICKS:-}" "${NETRATEL_SEED_EXPECTED_UNIT_SHA256:-}" <<'PY'
import hashlib
import os
import stat
import subprocess
import sys

unit_path, expected_pid, expected_ticks, expected_sha = sys.argv[1:]
if not expected_pid.isdigit() or not expected_ticks.isdigit() or not expected_sha:
    raise SystemExit("The seeded service identity fence is incomplete; no installed files were changed.")
fragment = subprocess.run(["systemctl", "show", "-p", "FragmentPath", "--value", "netratel-client.service"],
                         check=True, capture_output=True, text=True, timeout=5).stdout.strip()
if fragment != unit_path or os.path.islink(unit_path):
    raise SystemExit("The seeded service unit identity changed; no installed files were changed.")
active = subprocess.run(["systemctl", "is-active", "--quiet", "netratel-client.service"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
if active.returncode != 0:
    raise SystemExit("The seeded service is no longer active; no installed files were changed.")
main_pid = subprocess.run(["systemctl", "show", "-p", "MainPID", "--value", "netratel-client.service"],
                          check=True, capture_output=True, text=True, timeout=5).stdout.strip()
if main_pid != expected_pid:
    raise SystemExit("The seeded service process changed; no installed files were changed.")
with open("/proc/%s/stat" % expected_pid, encoding="ascii") as stream:
    process_stat = stream.read()
fields = process_stat[process_stat.rfind(")") + 2:].split()
if len(fields) <= 19 or fields[19] != expected_ticks:
    raise SystemExit("The seeded service process identity changed; no installed files were changed.")
descriptor = os.open(unit_path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
try:
    info = os.fstat(descriptor)
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) & 0o022:
        raise SystemExit("The seeded service unit is no longer trusted; no installed files were changed.")
    digest = hashlib.sha256()
    while True:
        chunk = os.read(descriptor, 65536)
        if not chunk:
            break
        digest.update(chunk)
finally:
    os.close(descriptor)
if digest.hexdigest() != expected_sha:
    raise SystemExit("The seeded service unit changed; no installed files were changed.")
PY
fi
""" : string.Empty;
        var activationBlock = request.InstallAsService ? $$"""
            {{seededServiceFence}}
            if service_is_running; then
              SERVICE_WAS_ACTIVE=true
              if ! timeout --foreground 60s systemctl stop netratel-client.service; then
                echo "NetRatel client service did not stop; no installed files were changed." >&2
                exit 1
              fi
              if ! wait_for_service_stopped; then
                echo "NetRatel client service could not be proven stopped; no installed files were changed." >&2
                exit 1
              fi
            else
              service_state=$?
              if [ "${service_state}" -ne 1 ]; then
                SERVICE_STATE_UNKNOWN=true
                echo "The NetRatel service state could not be verified; no installed files were changed." >&2
                exit 1
              fi
            fi
            TRANSACTION_MUTATED=true
            if [ -e "${TARGET_DIR}" ] || [ -L "${TARGET_DIR}" ]; then
              mv "${TARGET_DIR}" "${TARGET_BACKUP}"
              TARGET_BACKED_UP=true
            fi
            mv "${STAGE_DIR}" "${TARGET_DIR}"
            STAGE_DIR=""
            TARGET_INSTALLED=true
            CURRENT_UPDATED=true
            ln -s -- "${TARGET_DIR}" "${ROOT_DIR}/.current.$$"
            mv -Tf "${ROOT_DIR}/.current.$$" "${ROOT_DIR}/current"
            """ : """
            TRANSACTION_MUTATED=true
            if [ -e "${TARGET_DIR}" ] || [ -L "${TARGET_DIR}" ]; then
              mv "${TARGET_DIR}" "${TARGET_BACKUP}"
              TARGET_BACKED_UP=true
            fi
            mv "${STAGE_DIR}" "${TARGET_DIR}"
            STAGE_DIR=""
            TARGET_INSTALLED=true
            """;
        var defaultRoot = request.InstallAsService ? "/opt/netratel/client" : "${HOME}/.local/share/netratel/client";
        var defaultState = request.InstallAsService ? "/var/lib/netratel/update" : "${HOME}/.local/state/netratel/update";

        var directorySetup = $$"""
python3 - "${ROOT_DIR}" "${STATE_DIR}" "${SYSTEMD_UNIT_DIR}" "{{(request.InstallAsService ? "true" : "false")}}" <<'PY'
import os
import stat
import sys

root_dir, state_dir, unit_dir, service_mode = sys.argv[1:]
uid = os.geteuid()
service_mode = service_mode == "true"
flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)

def ensure_directory(path, mode, expected_owner):
    path = os.path.abspath(path)
    fd = os.open("/", flags)
    try:
        parts = [part for part in path.split(os.sep) if part]
        for index, part in enumerate(parts):
            final = index == len(parts) - 1
            try:
                os.mkdir(part, mode if final else 0o755, dir_fd=fd)
            except FileExistsError:
                pass
            child = os.open(part, flags, dir_fd=fd)
            info = os.fstat(child)
            if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode):
                raise SystemExit("Managed install path contains a non-directory or symlink component.")
            if info.st_uid not in (0, uid):
                raise SystemExit("Managed install path has an unexpected owner.")
            if index < len(parts) - 1 and info.st_mode & 0o022 and not info.st_mode & stat.S_ISVTX:
                raise SystemExit("Managed install path has an untrusted writable ancestor.")
            if final:
                if info.st_uid != expected_owner:
                    raise SystemExit("Managed install directory is not owned by the installation identity.")
                os.fchmod(child, mode)
            os.close(fd)
            fd = child
    finally:
        os.close(fd)

owner = 0 if service_mode and uid == 0 else uid
ensure_directory(root_dir, 0o755, owner)
ensure_directory(os.path.join(root_dir, "updater"), 0o755, owner)
ensure_directory(os.path.join(root_dir, "versions"), 0o755, owner)
ensure_directory(os.path.join(root_dir, "staging"), 0o755, owner)
ensure_directory(os.path.join(root_dir, "failed"), 0o755, owner)
ensure_directory(state_dir, 0o700, owner)
if service_mode:
    ensure_directory(unit_dir, 0o755, 0 if uid == 0 else uid)
PY
""";

        var ownedLayoutPreflight = request.InstallAsService ? """
CURRENT_LINK_VALUE=""
PREVIOUS_TARGET=""
if [ -L "${ROOT_DIR}/current" ]; then
  CURRENT_LINK_PRESENT=true
  CURRENT_LINK_VALUE=$(readlink -- "${ROOT_DIR}/current")
  if ! PREVIOUS_TARGET=$(realpath -e -- "${ROOT_DIR}/current"); then
    echo "The managed current link does not resolve to an installed version." >&2
    exit 1
  fi
elif [ -e "${ROOT_DIR}/current" ] || [ -L "${ROOT_DIR}/current" ]; then
  echo "The managed current path exists but is not a symbolic link." >&2
  exit 1
fi
SERVICE_FRAGMENT=$(systemctl show -p FragmentPath --value netratel-client.service 2>/dev/null || true)
CLIENT_UNIT_PATH="${SYSTEMD_UNIT_DIR}/netratel-client.service"
UPDATE_UNIT_PATH="${SYSTEMD_UNIT_DIR}/netratel-update.service"
LAUNCHER_PATH="${ROOT_DIR}/netratel-client-start.sh"
if [ -n "${SERVICE_FRAGMENT}" ]; then
  expected_fragment=$(realpath -m -- "${CLIENT_UNIT_PATH}")
  actual_fragment=$(realpath -e -- "${SERVICE_FRAGMENT}" 2>/dev/null || true)
  if [ -z "${actual_fragment}" ] || [ "${actual_fragment}" != "${expected_fragment}" ] || [ -L "${SERVICE_FRAGMENT}" ]; then
    echo "The existing NetRatel service is not backed by its owned unit path." >&2
    exit 1
  fi
  if ! grep -Fqx "WorkingDirectory=${ROOT_DIR}/current" "${CLIENT_UNIT_PATH}" ||
     ! grep -Fqx "ExecStart=${LAUNCHER_PATH}" "${CLIENT_UNIT_PATH}"; then
    echo "The existing NetRatel service unit does not target the owned current layout." >&2
    exit 1
  fi
elif [ -e "${CLIENT_UNIT_PATH}" ] || systemctl is-active --quiet netratel-client.service; then
  echo "The NetRatel service path exists without a verified systemd unit identity." >&2
  exit 1
fi
if [ -e "${LAUNCHER_PATH}" ]; then
  if [ ! -f "${LAUNCHER_PATH}" ] || [ -L "${LAUNCHER_PATH}" ] ||
     ! grep -Fqx "ROOT_DIR=\"${ROOT_DIR}\"" "${LAUNCHER_PATH}" ||
     ! grep -Fqx "exec \"${ROOT_DIR}/current/NetRatel.Client\" --service" "${LAUNCHER_PATH}"; then
    echo "The existing NetRatel launcher is not owned by this install layout." >&2
    exit 1
  fi
fi
if [ -e "${UPDATER_DIR}/netratel-update.sh" ] &&
   { [ ! -f "${UPDATER_DIR}/netratel-update.sh" ] || [ -L "${UPDATER_DIR}/netratel-update.sh" ]; }; then
  echo "The existing NetRatel updater path is not a regular owned file." >&2
  exit 1
fi
if [ -e "${CLIENT_UNIT_PATH}" ] && { [ ! -f "${CLIENT_UNIT_PATH}" ] || [ -L "${CLIENT_UNIT_PATH}" ]; }; then
  echo "The existing NetRatel service unit path is not a regular owned file." >&2
  exit 1
fi
if [ -e "${UPDATE_UNIT_PATH}" ] && { [ ! -f "${UPDATE_UNIT_PATH}" ] || [ -L "${UPDATE_UNIT_PATH}" ]; }; then
  echo "The existing NetRatel updater unit path is not a regular owned file." >&2
  exit 1
fi
if [ -n "${PREVIOUS_TARGET}" ]; then
  python3 - "${PREVIOUS_TARGET}" "${VERSIONS_DIR}" "${RUNTIME}" <<'PY'
import json
import os
import re
import stat
import sys

target, versions, runtime_id = sys.argv[1:]
if os.path.dirname(target) != os.path.realpath(versions) or os.path.islink(target):
    raise SystemExit("The current link does not point directly to an owned version directory.")
manifest_path = os.path.join(target, "netratel-client-manifest.json")
executable_path = os.path.join(target, "NetRatel.Client")
try:
    if not stat.S_ISDIR(os.lstat(target).st_mode): raise ValueError()
    if not stat.S_ISREG(os.lstat(manifest_path).st_mode): raise ValueError()
    if not stat.S_ISREG(os.lstat(executable_path).st_mode): raise ValueError()
    with open(manifest_path, encoding="utf-8-sig") as stream: manifest = json.load(stream)
except (OSError, ValueError, json.JSONDecodeError):
    raise SystemExit("The current version is not a verified NetRatel package.")
if (not isinstance(manifest, dict) or
    manifest.get("schema") != "netratel.client.manifest.v1" or
    manifest.get("product") != "NetRatel.Client" or
    manifest.get("version") != os.path.basename(target) or
    manifest.get("runtimeId") != runtime_id or
    manifest.get("executable") != "NetRatel.Client" or
    not isinstance(manifest.get("commitSha"), str) or
    not re.fullmatch(r"[0-9a-fA-F]{40}", manifest["commitSha"])):
    raise SystemExit("The current version manifest is not a verified NetRatel package.")
PY
fi
if [ -n "${PREVIOUS_TARGET}" ] || [ -e "${TARGET_DIR}" ] || [ -L "${TARGET_DIR}" ]; then
  python3 - "${PREVIOUS_TARGET}" "${TARGET_DIR}" <<'PY'
import os
import stat
import sys

expected_owner = os.geteuid()
for target in dict.fromkeys(path for path in sys.argv[1:] if path):
    directory = os.lstat(target)
    if (not stat.S_ISDIR(directory.st_mode) or stat.S_ISLNK(directory.st_mode) or
            directory.st_uid != expected_owner or stat.S_IMODE(directory.st_mode) & 0o022):
        raise SystemExit("An installed version directory has an unexpected owner, type, or mode.")
    for name in ("netratel-client-manifest.json", "NetRatel.Client", "clientsettings.json", "appsettings.json"):
        path = os.path.join(target, name)
        if not os.path.lexists(path):
            continue
        info = os.lstat(path)
        if (not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or
                info.st_uid != expected_owner or stat.S_IMODE(info.st_mode) & 0o022):
            raise SystemExit("An installed version file has an unexpected owner, type, or mode.")
PY
fi
if [ -e "${TARGET_DIR}" ] || [ -L "${TARGET_DIR}" ]; then
  python3 - "${TARGET_DIR}" "${RESOLVED_VERSION}" "${RUNTIME}" <<'PY'
import json
import os
import re
import stat
import sys

target, version, runtime_id = sys.argv[1:]
manifest_path = os.path.join(target, "netratel-client-manifest.json")
executable_path = os.path.join(target, "NetRatel.Client")
try:
    if os.path.islink(target) or not stat.S_ISDIR(os.lstat(target).st_mode): raise ValueError()
    if not stat.S_ISREG(os.lstat(manifest_path).st_mode): raise ValueError()
    if not stat.S_ISREG(os.lstat(executable_path).st_mode): raise ValueError()
    with open(manifest_path, encoding="utf-8-sig") as stream: manifest = json.load(stream)
except (OSError, ValueError, json.JSONDecodeError):
    raise SystemExit("The existing target version is not a verified NetRatel package.")
if (not isinstance(manifest, dict) or
    manifest.get("schema") != "netratel.client.manifest.v1" or
    manifest.get("product") != "NetRatel.Client" or
    manifest.get("version") != version or
    manifest.get("runtimeId") != runtime_id or
    manifest.get("executable") != "NetRatel.Client" or
    not isinstance(manifest.get("commitSha"), str) or
    not re.fullmatch(r"[0-9a-fA-F]{40}", manifest["commitSha"])):
    raise SystemExit("The existing target version is not a verified NetRatel package.")
PY
fi
""" : string.Empty;

        var effectiveServiceConfigPreflight = request.InstallAsService ? """
python3 - "${CLIENT_UNIT_PATH}" "${UPDATE_UNIT_PATH}" <<'PY'
import os
import re
import shlex
import stat
import subprocess
import sys

client_unit, update_unit = map(os.path.abspath, sys.argv[1:])
expected_owner = os.geteuid()
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
directory_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
no_follow = getattr(os, "O_NOFOLLOW", 0)

def reject(message):
    raise SystemExit(message)

def open_directory(path):
    if not os.path.isabs(path) or any(character.isspace() for character in path):
        reject("The existing service configuration has an unsupported path.")
    descriptor = os.open(os.sep, directory_flags)
    try:
        for part in (piece for piece in path.split(os.sep) if piece):
            child = os.open(part, directory_flags, dir_fd=descriptor)
            info = os.fstat(child)
            mode = stat.S_IMODE(info.st_mode)
            if not stat.S_ISDIR(info.st_mode) or info.st_uid not in (0, expected_owner):
                os.close(child)
                reject("The existing service configuration path is not trusted.")
            if mode & 0o022 and not mode & stat.S_ISVTX:
                os.close(child)
                reject("The existing service configuration path is writable by an untrusted identity.")
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise

def read_trusted_file(path, optional=False):
    if not os.path.isabs(path) or any(character in path for character in "\t\r\n*?%$"):
        reject("The existing service configuration references an unsupported file path.")
    parent, name = os.path.split(path)
    try:
        parent_fd = open_directory(parent)
    except FileNotFoundError:
        if optional:
            return None
        raise
    try:
        try:
            descriptor = os.open(name, os.O_RDONLY | no_follow, dir_fd=parent_fd)
        except FileNotFoundError:
            if optional:
                return None
            reject("A required service EnvironmentFile is missing.")
        try:
            info = os.fstat(descriptor)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid not in (0, expected_owner) or
                    stat.S_IMODE(info.st_mode) & 0o022):
                reject("The existing service configuration file is not trusted.")
            with os.fdopen(os.dup(descriptor), "r", encoding="utf-8", errors="strict") as stream:
                return stream.read()
        finally:
            os.close(descriptor)
    finally:
        os.close(parent_fd)

def check_environment_file(path, optional):
    contents = read_trusted_file(path, optional)
    if contents is None:
        return
    for line in contents.splitlines():
        if line.rstrip().endswith("\\"):
            reject("An external EnvironmentFile uses a continued assignment; repair cannot verify its effective NetRatel configuration.")
        match = re.match(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=", line)
        if match and match.group(1).upper() in protected_environment:
            reject("An external EnvironmentFile overrides a NetRatel endpoint or owned service path; repair requires that override to be removed.")

def inspect_unit(path, unit_name, dropin=False):
    contents = read_trusted_file(path)
    section = ""
    for line in contents.splitlines():
        if line.rstrip().endswith("\\"):
            reject("The existing systemd service configuration uses a continued directive; repair cannot verify its effective identity.")
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
        if dropin and lower_name in protected_dropin_directives:
            reject("A systemd drop-in changes the owned service identity; repair requires a directly configured NetRatel service unit.")
        if not dropin and lower_name == "user" and value.strip().lower() not in ("", "root", "0"):
            reject("The existing systemd service does not run as root; repair will not change its service identity.")
        if not dropin and lower_name == "dynamicuser" and value.strip().lower() not in ("", "no", "false", "off", "0"):
            reject("The existing systemd service uses a dynamic user; repair cannot preserve that identity safely.")
        if lower_name == "environmentfile":
            for item in shlex.split(value):
                optional = item.startswith("-")
                filename = item[1:] if optional else item
                check_environment_file(filename, optional)
        elif dropin and lower_name == "environment":
            if not value.strip():
                reject("A systemd drop-in resets the owned service environment; repair cannot safely preserve it.")
            for assignment in shlex.split(value):
                variable, separator, _ = assignment.partition("=")
                if separator and variable.upper() in protected_environment:
                    reject("A systemd drop-in overrides a NetRatel endpoint or owned service path.")
        elif dropin and lower_name in ("passenvironment", "unsetenvironment"):
            if any(item == "*" or item.upper() in protected_environment for item in shlex.split(value)):
                reject("A systemd drop-in changes the effective NetRatel service environment.")

for unit_name, unit_path in (("netratel-client.service", client_unit), ("netratel-update.service", update_unit)):
    result = subprocess.run(
        ["systemctl", "show", "-p", "DropInPaths", "--value", unit_name],
        check=True, capture_output=True, text=True, timeout=5)
    if os.path.lexists(unit_path):
        inspect_unit(unit_path, unit_name)
    for dropin_path in shlex.split(result.stdout.strip()):
        inspect_unit(dropin_path, unit_name, True)
PY
""" : string.Empty;

        var managedPathPreflight = $$"""
python3 - "${ROOT_DIR}" "${STATE_DIR}" "${VERSIONS_DIR}" "${STAGING_DIR}" "${FAILED_DIR}" "${UPDATER_DIR}" "${SYSTEMD_UNIT_DIR}" "${TARGET_DIR}" "${STATE_DIR}/update.lock" "{{(request.InstallAsService ? "true" : "false")}}" <<'PY'
import os
import stat
import sys

root, state, versions, staging, failed, updater, unit_dir, target, lock_path, service_mode = sys.argv[1:]
service_mode = service_mode == "true"
expected_owner = os.geteuid()

def check_directory(path, private=False):
    info = os.lstat(path)
    if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != expected_owner:
        raise SystemExit("Managed install directory has an unexpected type or owner.")
    if stat.S_IMODE(info.st_mode) & 0o022:
        raise SystemExit("Managed install directory is group or world writable.")
    if private and stat.S_IMODE(info.st_mode) & 0o077:
        raise SystemExit("Managed state directory permissions are not private.")

def check_file(path):
    if not os.path.lexists(path):
        return
    info = os.lstat(path)
    if not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != expected_owner:
        raise SystemExit("Managed install file has an unexpected type or owner.")
    if stat.S_IMODE(info.st_mode) & 0o022:
        raise SystemExit("Managed install file is group or world writable.")

for path in (root, state, versions, staging, failed, updater):
    check_directory(path, path == state)
if service_mode:
    check_directory(unit_dir)
if os.path.lexists(target):
    check_directory(target)
    for name in ("netratel-client-manifest.json", "NetRatel.Client", "clientsettings.json", "appsettings.json"):
        check_file(os.path.join(target, name))
for path in (lock_path, os.path.join(updater, "netratel-update.sh"),
             os.path.join(unit_dir, "netratel-client.service"),
             os.path.join(unit_dir, "netratel-update.service"),
             os.path.join(root, "netratel-client-start.sh")):
    check_file(path)
PY
""";

        var serviceStoppedCheck = request.InstallAsService ? """
service_is_running() {
  local active_status=0
  if systemctl is-active --quiet netratel-client.service; then return 0; else active_status=$?; fi
  if [ "${active_status}" -ne 3 ] && [ "${active_status}" -ne 4 ]; then return 2; fi
  local load_state
  if ! load_state=$(systemctl show -p LoadState --value netratel-client.service 2>/dev/null); then return 2; fi
  case "${load_state}" in
    not-found) return 1 ;;
    loaded) ;;
    *) return 2 ;;
  esac
  local main_pid
  if ! main_pid=$(systemctl show -p MainPID --value netratel-client.service 2>/dev/null); then return 2; fi
  if [[ "${main_pid}" =~ ^0+$ ]]; then return 1; fi
  if [[ "${main_pid}" =~ ^[1-9][0-9]*$ ]]; then return 0; fi
  return 2
}

wait_for_service_stopped() {
  local deadline=$((SECONDS + 30))
  while true; do
    if service_is_running; then
      :
    else
      local service_status=$?
      if [ "${service_status}" -eq 1 ]; then return 0; fi
      SERVICE_STATE_UNKNOWN=true
      return 1
    fi
    if [ "${SECONDS}" -ge "${deadline}" ]; then return 1; fi
    sleep 1
  done
}
wait_for_service_active() {
  local deadline=$((SECONDS + 30))
  while true; do
    if service_is_running; then return 0; else
      local service_status=$?
      if [ "${service_status}" -eq 2 ]; then SERVICE_STATE_UNKNOWN=true; return 1; fi
    fi
    if [ "${SECONDS}" -ge "${deadline}" ]; then return 1; fi
    sleep 1
  done
}
""" : string.Empty;

        var lockFilePreflight = """
python3 - "${STATE_DIR}/update.lock" <<'PY'
import os
import stat
import sys

path = sys.argv[1]
if os.path.lexists(path):
    info = os.lstat(path)
    if not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != os.geteuid():
        raise SystemExit("The installer lock path has an unexpected type or owner.")
    if stat.S_IMODE(info.st_mode) & 0o022:
        raise SystemExit("The installer lock path is group or world writable.")
PY
""";

var transactionSnapshot = request.InstallAsService ? """
SERVICE_WAS_ACTIVE=false
SERVICE_WAS_ENABLED=false
SERVICE_STATE_UNKNOWN=false
if service_is_running; then
  SERVICE_WAS_ACTIVE=true
else
  service_status=$?
  if [ "${service_status}" -ne 1 ]; then
    echo "The current NetRatel service state could not be verified; no files were changed." >&2
    exit 1
  fi
fi
if systemctl is-enabled netratel-client.service >/dev/null 2>&1; then SERVICE_WAS_ENABLED=true; fi
TXN_DIR=$(mktemp -d "${STATE_DIR}/.installer-transaction.XXXXXX")
chmod 0700 "${TXN_DIR}"
TARGET_BACKUP="${FAILED_DIR}/${RESOLVED_VERSION}-replaced-$(basename "${TXN_DIR}")"
snapshot_file() {
  local source="$1" name="$2"
  if [ -e "${source}" ]; then cp -a -- "${source}" "${TXN_DIR}/${name}"; fi
}
snapshot_file "${CLIENT_UNIT_PATH}" client-unit
snapshot_file "${UPDATE_UNIT_PATH}" update-unit
snapshot_file "${LAUNCHER_PATH}" launcher
snapshot_file "${UPDATER_DIR}/netratel-update.sh" updater
""" : """
SERVICE_WAS_ACTIVE=false
SERVICE_WAS_ENABLED=false
TXN_DIR=$(mktemp -d "${STATE_DIR}/.installer-transaction.XXXXXX")
chmod 0700 "${TXN_DIR}"
TARGET_BACKUP="${FAILED_DIR}/${RESOLVED_VERSION}-replaced-$(basename "${TXN_DIR}")"
""";

        var rollbackService = request.InstallAsService ? """
    local rollback_safe=true
    if service_is_running; then
      if ! timeout --foreground 60s systemctl stop netratel-client.service; then rollback_safe=false; fi
      if [ "${rollback_safe}" = true ] && ! wait_for_service_stopped; then rollback_safe=false; fi
    else
      local service_status=$?
      if [ "${service_status}" -ne 1 ]; then rollback_safe=false; fi
    fi
    if [ "${rollback_safe}" = false ]; then
      rollback_failed=true
      echo "Rollback retained the replacement and recovery snapshot because the owned service state could not be proven safe." >&2
    fi
""" : string.Empty;

        var restoreUnitFiles = request.InstallAsService ? """
    if [ "${SERVICE_WAS_ENABLED}" = true ]; then
      if ! restore_file "${CLIENT_UNIT_PATH}" client-unit; then rollback_failed=true; fi
      if ! restore_file "${UPDATE_UNIT_PATH}" update-unit; then rollback_failed=true; fi
      if ! restore_file "${LAUNCHER_PATH}" launcher; then rollback_failed=true; fi
      if ! restore_file "${UPDATER_DIR}/netratel-update.sh" updater; then rollback_failed=true; fi
      if ! systemctl daemon-reload; then rollback_failed=true; fi
      if ! systemctl enable netratel-client.service; then rollback_failed=true; fi
    else
      if systemctl is-enabled netratel-client.service >/dev/null 2>&1 &&
         ! systemctl disable netratel-client.service >/dev/null 2>&1; then rollback_failed=true; fi
      if ! restore_file "${CLIENT_UNIT_PATH}" client-unit; then rollback_failed=true; fi
      if ! restore_file "${UPDATE_UNIT_PATH}" update-unit; then rollback_failed=true; fi
      if ! restore_file "${LAUNCHER_PATH}" launcher; then rollback_failed=true; fi
      if ! restore_file "${UPDATER_DIR}/netratel-update.sh" updater; then rollback_failed=true; fi
      if ! systemctl daemon-reload; then rollback_failed=true; fi
    fi
    if [ "${SERVICE_WAS_ACTIVE}" = true ]; then
      if ! timeout --foreground 60s systemctl start netratel-client.service || ! wait_for_service_active; then
        rollback_failed=true
      fi
    fi
""" : ":";

        var archivePreflight = """
python3 - "${TMP_DIR}/netratel.zip" "${RESOLVED_VERSION}" "${RUNTIME}" <<'PY'
import json
import re
import stat
import sys
import zipfile

archive_path, requested_version, runtime_id = sys.argv[1:]
manifest_name = "netratel-client-manifest.json"
executable_name = "NetRatel.Client"
names = set()
try:
    archive = zipfile.ZipFile(archive_path, "r")
    with archive:
        manifest_info = None
        executable_info = None
        for info in archive.infolist():
            name = info.filename
            if (not name or name.startswith("/") or "\\" in name or
                re.match(r"^[A-Za-z]:", name) or "\x00" in name):
                raise ValueError("unsafe ZIP path")
            path_name = name[:-1] if info.is_dir() and name.endswith("/") else name
            parts = path_name.split("/")
            if any(part in ("", ".", "..") for part in parts):
                raise ValueError("unsafe ZIP path component")
            normalized = "/".join(parts)
            key = normalized.casefold()
            if key in names:
                raise ValueError("duplicate ZIP path")
            names.add(key)
            unix_mode = (info.external_attr >> 16) & 0xFFFF if info.create_system == 3 else 0
            file_kind = stat.S_IFMT(unix_mode)
            if file_kind not in (0, stat.S_IFREG, stat.S_IFDIR):
                raise ValueError("symlink or special ZIP entry")
            if info.is_dir() and file_kind not in (0, stat.S_IFDIR):
                raise ValueError("invalid ZIP directory entry")
            if not info.is_dir() and file_kind == stat.S_IFDIR:
                raise ValueError("invalid ZIP file entry")
            if normalized == manifest_name:
                manifest_info = info
            if normalized == executable_name:
                executable_info = info
        if manifest_info is None or executable_info is None or executable_info.is_dir():
            raise ValueError("manifest or exact executable is missing")
        if manifest_info.is_dir():
            raise ValueError("manifest entry is a directory")
        with archive.open(manifest_info, "r") as stream:
            manifest = json.load(stream)
except (OSError, ValueError, zipfile.BadZipFile, json.JSONDecodeError, RuntimeError) as error:
    raise SystemExit("Artifact ZIP preflight failed: " + str(error))

if (not isinstance(manifest, dict) or
    manifest.get("schema") != "netratel.client.manifest.v1" or
    manifest.get("product") != "NetRatel.Client" or
    manifest.get("version") != requested_version or
    manifest.get("runtimeId") != runtime_id or
    manifest.get("executable") != executable_name or
    not isinstance(manifest.get("commitSha"), str) or
    not re.fullmatch(r"[0-9a-fA-F]{40}", manifest["commitSha"])):
    raise SystemExit("Artifact manifest does not match the authorized version, runtime, or executable.")
PY
""";

        var transactionCleanup = $$"""
restore_file() {
  local destination="$1" name="$2" backup="${TXN_DIR}/${2}"
  if [ -e "${destination}" ] || [ -L "${destination}" ]; then
    rm -f -- "${destination}" || return 1
  fi
  if [ -e "${backup}" ]; then
    cp -a -- "${backup}" "${destination}" || return 1
  fi
  return 0
}
finish_install() {
  local status=$?
  local rollback_failed=false
  trap - EXIT
  if [ "${status}" -ne 0 ] && [ "${TRANSACTION_MUTATED}" = true ]; then
{{rollbackService}}
    if [ "${rollback_safe:-true}" = true ]; then
      if [ "${TARGET_INSTALLED}" = true ] && { [ -e "${TARGET_DIR}" ] || [ -L "${TARGET_DIR}" ]; }; then
        if ! rm -rf -- "${TARGET_DIR}"; then rollback_failed=true; fi
      fi
      if [ "${TARGET_BACKED_UP}" = true ]; then
        if ! mv -- "${TARGET_BACKUP}" "${TARGET_DIR}"; then rollback_failed=true; fi
      fi
      if [ "${CURRENT_UPDATED}" = true ]; then
        if [ "${CURRENT_LINK_PRESENT}" = true ]; then
          if ! ln -s -- "${CURRENT_LINK_VALUE}" "${ROOT_DIR}/.current-rollback.$$" ||
             ! mv -Tf -- "${ROOT_DIR}/.current-rollback.$$" "${ROOT_DIR}/current"; then rollback_failed=true; fi
        elif ! rm -f -- "${ROOT_DIR}/current"; then
          rollback_failed=true
        fi
      fi
      if [ "${FILES_MUTATED}" = true ]; then
{{restoreUnitFiles}}
      fi
      if [ "${rollback_failed}" = false ] && [ "${SERVICE_WAS_ACTIVE}" = true ]; then
        if ! timeout --foreground 60s systemctl start netratel-client.service || ! wait_for_service_active; then
          rollback_failed=true
        fi
      fi
    else
      rollback_failed=true
    fi
    if [ "${rollback_failed}" = true ]; then
      echo "Installer activation failed and rollback could not fully restore the previous installation. Recovery snapshot: ${TXN_DIR}" >&2
    else
      echo "Installer activation failed; the previous installation was restored." >&2
    fi
  elif [ "${status}" -ne 0 ] && [ "${SERVICE_WAS_ACTIVE}" = true ]; then
    if service_is_running; then
      :
    else
      service_status=$?
      if [ "${service_status}" -eq 1 ]; then
        if ! timeout --foreground 60s systemctl start netratel-client.service || ! wait_for_service_active; then
          rollback_failed=true
          echo "The installation stopped the previous service but could not restart it." >&2
        fi
      else
        rollback_failed=true
        echo "The previous service state could not be verified; the recovery snapshot was retained." >&2
      fi
    fi
  fi
  if [ -n "${STAGE_DIR}" ] && [ -d "${STAGE_DIR}" ]; then
    if ! rm -rf -- "${STAGE_DIR}"; then rollback_failed=true; fi
  fi
  if [ -n "${TMP_DIR}" ] && ! rm -rf -- "${TMP_DIR}"; then rollback_failed=true; fi
  if [ "${status}" -eq 0 ] || [ "${rollback_failed}" = false ]; then
    if [ -n "${TXN_DIR}" ] && [ -d "${TXN_DIR}" ]; then
      if ! rm -rf -- "${TXN_DIR}"; then rollback_failed=true; fi
    fi
  fi
  if [ "${rollback_failed}" = true ]; then exit 70; fi
  exit "${status}"
}
trap finish_install EXIT
""";

        return $$"""
#!/usr/bin/env bash
set -euo pipefail
umask 077

API_BASE="{{request.ApiBaseUrl}}"
GATEWAY_ENDPOINT="{{request.GatewayEndpoint ?? string.Empty}}"
TENANT_ID={{request.TenantId}}
ENROLLMENT_CODE="{{request.EnrollmentCode}}"
RUNTIME="{{request.RuntimeId}}"
VERSION="{{(string.IsNullOrWhiteSpace(request.ArtifactVersion) ? "latest" : request.ArtifactVersion)}}"
EXPECTED_SHA="{{(string.IsNullOrWhiteSpace(request.ArtifactSha256) ? string.Empty : request.ArtifactSha256)}}"
ROOT_DIR="${NetRatel_ROOT:-{{defaultRoot}}}"
STATE_DIR="${NetRatel_STATE:-{{defaultState}}}"
SYSTEMD_UNIT_DIR="${NetRatel_SYSTEMD_UNIT_DIR:-/etc/systemd/system}"
UPDATER_DIR="${ROOT_DIR}/updater"
VERSIONS_DIR="${ROOT_DIR}/versions"
STAGING_DIR="${ROOT_DIR}/staging"
FAILED_DIR="${ROOT_DIR}/failed"
CLIENT_UNIT_PATH="${SYSTEMD_UNIT_DIR}/netratel-client.service"
UPDATE_UNIT_PATH="${SYSTEMD_UNIT_DIR}/netratel-update.service"
LAUNCHER_PATH="${ROOT_DIR}/netratel-client-start.sh"
TMP_DIR=""
STAGE_DIR=""
TXN_DIR=""
TARGET_DIR=""
TARGET_BACKUP=""
PREVIOUS_TARGET=""
CURRENT_LINK_VALUE=""
CURRENT_LINK_PRESENT=false
CURRENT_UPDATED=false
TARGET_BACKED_UP=false
TARGET_INSTALLED=false
TRANSACTION_MUTATED=false
FILES_MUTATED=false
SERVICE_WAS_ACTIVE=false
SERVICE_WAS_ENABLED=false

{{serviceStoppedCheck}}
{{transactionCleanup}}

{{privilegeCheck}}
for REQUIRED_COMMAND in {{requiredCommands}}; do
  if ! command -v "${REQUIRED_COMMAND}" >/dev/null 2>&1; then
    echo "Required command is unavailable: ${REQUIRED_COMMAND}" >&2
    exit 1
  fi
done

TMP_DIR=$(mktemp -d)

RESOLVED_VERSION="${VERSION}"
RESPONSE_HEADERS="${TMP_DIR}/response-headers.txt"
if ! [[ "${TENANT_ID}" =~ ^[1-9][0-9]*$ ]] ||
   ! [[ "${RUNTIME}" =~ ^linux-(x64|arm64)$ ]] ||
   { [ "${VERSION}" != "latest" ] &&
     ! [[ "${VERSION}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; }; then
  echo "The requested tenant, runtime, or version is invalid." >&2
  exit 1
fi
if [ -z "${API_BASE}" ] || [ -z "${ENROLLMENT_CODE}" ]; then
  echo "API origin and enrollment input are required." >&2
  exit 1
fi

{{directorySetup}}
{{lockFilePreflight}}
exec 9>"${STATE_DIR}/update.lock"
if ! flock -n 9; then
  echo "Another NetRatel installer or updater is already running." >&2
  exit 75
fi

curl -f --max-redirs 0 --connect-timeout 15 --max-time 120 \
  -H "X-NetRatel-Tenant-Id: ${TENANT_ID}" \
  -H "X-NetRatel-Enrollment-Code: ${ENROLLMENT_CODE}" \
  -D "${RESPONSE_HEADERS}" \
  -o "${TMP_DIR}/netratel.zip" \
  "${API_BASE}/api/v1/client-artifacts/${RUNTIME}/${RESOLVED_VERSION}/onboarding-download"
test -s "${TMP_DIR}/netratel.zip"
read_response_header() {
  awk -v wanted="$1" '/^HTTP\// { status=$2; value=""; next } status == "200" && tolower(substr($0, 1, length(wanted) + 1)) == tolower(wanted ":") { value=substr($0, index($0, ":") + 1); sub(/^[[:space:]]+/, "", value); sub(/[[:space:]]+$/, "", value); gsub(/\r/, "", value) } END { if (status != "200") exit 1; print value }' "${RESPONSE_HEADERS}"
}
REPORTED_RID=$(read_response_header "X-NetRatel-Artifact-Rid")
REPORTED_VERSION=$(read_response_header "X-NetRatel-Artifact-Version")
REPORTED_SHA=$(read_response_header "X-NetRatel-Artifact-Sha256")
REPORTED_SIZE=$(read_response_header "X-NetRatel-Artifact-Size")
if [ "${REPORTED_RID}" != "${RUNTIME}" ] ||
  ! [[ "${REPORTED_VERSION}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]] ||
  { [ "${VERSION}" != "latest" ] && [ "${REPORTED_VERSION}" != "${VERSION}" ]; } ||
  ! [[ "${REPORTED_SHA}" =~ ^[0-9A-Fa-f]{64}$ ]] ||
  ! [[ "${REPORTED_SIZE}" =~ ^[1-9][0-9]*$ ]]; then
  echo "The authorized artifact response metadata did not match the requested package." >&2
  exit 1
fi
if [ -n "${EXPECTED_SHA}" ] &&
  [ "$(printf '%s' "${REPORTED_SHA}" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "${EXPECTED_SHA}" | tr '[:upper:]' '[:lower:]')" ]; then
  echo "The authorized artifact response did not match the generated package snapshot." >&2
  exit 1
fi
ACTUAL_SIZE=$(wc -c < "${TMP_DIR}/netratel.zip" | tr -d '[:space:]')
if [ "${ACTUAL_SIZE}" != "${REPORTED_SIZE}" ]; then
  echo "Downloaded client package size did not match its authorized metadata." >&2
  exit 1
fi
RESOLVED_VERSION="${REPORTED_VERSION}"
EXPECTED_SHA="$(printf '%s' "${REPORTED_SHA}" | tr '[:upper:]' '[:lower:]')"
ACTUAL_SHA=$(sha256sum "${TMP_DIR}/netratel.zip" | awk '{print $1}')
if [ "${ACTUAL_SHA}" != "${EXPECTED_SHA}" ]; then
  echo "Downloaded client package failed SHA-256 verification." >&2
  exit 1
fi
TARGET_DIR="${VERSIONS_DIR}/${RESOLVED_VERSION}"
if ! [[ "${RESOLVED_VERSION}" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "Artifact version is not a canonical semantic version." >&2
  exit 1
fi
{{managedPathPreflight}}
{{ownedLayoutPreflight}}
{{effectiveServiceConfigPreflight}}
{{archivePreflight}}
STAGE_DIR=$(mktemp -d "${STAGING_DIR}/.${RESOLVED_VERSION}.XXXXXX")
python3 - "${TMP_DIR}/netratel.zip" "${STAGE_DIR}" <<'PY'
import os
import stat
import sys
import zipfile

archive_path, stage_dir = sys.argv[1:]
with zipfile.ZipFile(archive_path, "r") as archive:
    archive.extractall(stage_dir)

manifest_path = os.path.join(stage_dir, "netratel-client-manifest.json")
executable_path = os.path.join(stage_dir, "NetRatel.Client")
if not stat.S_ISREG(os.lstat(manifest_path).st_mode) or not stat.S_ISREG(os.lstat(executable_path).st_mode):
    raise SystemExit("Artifact manifest or executable is not a regular file after extraction.")
PY
CLIENT_EXE="${STAGE_DIR}/NetRatel.Client"
chmod +x "${CLIENT_EXE}"
{{preparationBlock}}
{{enrollCommand}}

{{transactionSnapshot}}
{{activationBlock}}

{{serviceBlock}}

echo "NetRatel Linux client installed as ${RESOLVED_VERSION}."
""";
    }

    private static string BuildMacBash(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var defaultRoot = request.InstallAsService ? "/opt/netratel/client" : "${HOME}/Library/Application Support/NetRatel/Client";
        var serviceBlock = request.InstallAsService ? """
            PLIST_PATH="${NetRatel_LAUNCHD_PLIST:-/Library/LaunchDaemons/co.za.netratel.client.plist}"
            LABEL="co.za.netratel.client"
            PRESERVED_CLIENT_ENVIRONMENT="$(python3 - "${PLIST_PATH}" "${API_BASE}" "${GATEWAY_ENDPOINT}" "${PREVIOUS_TARGET}" "${TARGET_DIR}" <<'PY'
            import html
            import json
            import os
            import plistlib
            import sys
            from urllib.parse import urlsplit

            path, api_base, gateway_endpoint, previous_dir, target_dir = sys.argv[1:]
            prefix = "NetRatelCLIENT__"

            def public_origin(value):
                try:
                    parsed = urlsplit(value.strip())
                    if not parsed.scheme or not parsed.netloc or parsed.query or parsed.fragment:
                        return None
                    if parsed.path.rstrip("/").lower() not in ("", "/api"):
                        return None
                    return f"{parsed.scheme.lower()}://{parsed.netloc.lower()}"
                except ValueError:
                    return None

            retired = {
                "transport__mode",
                "gateway__requiredpresenceauthority",
                "gateway__telemetryshadowenabled",
                "gateway__telemetryauthorityenabled",
                "gateway__commandauthorityenabled",
                "gateway__jobauthorityenabled",
                "gateway__terminalauthorityenabled",
                "gateway__fileauthorityenabled",
                "gateway__logauthorityenabled",
                "gateway__controlauthorityenabled",
                "gateway__remotesupportauthorityenabled",
                "gateway__remotesupportv1enabled",
                "gateway__remotesupportv2inventoryenabled",
                "gateway__remotesupportv2mediaenabled",
                "gateway__controlgatewayenabled",
                "gateway__filegatewayenabled",
                "gateway__loggatewayenabled",
                "gateway__remotesupportgatewayenabled",
                "gateway__terminalgatewayenabled",
            }
            retired_json_gateway = {
                "requiredpresenceauthority", "telemetryshadowenabled", "telemetryauthorityenabled",
                "controlauthorityenabled", "commandauthorityenabled", "fileauthorityenabled",
                "jobauthorityenabled", "logauthorityenabled", "remotesupportauthorityenabled",
                "terminalauthorityenabled", "remotesupportv1enabled", "controlgatewayenabled",
                "filegatewayenabled", "loggatewayenabled", "remotesupportgatewayenabled",
                "terminalgatewayenabled", "remotesupportv2inventoryenabled", "remotesupportv2mediaenabled",
            }

            environment = {}
            if os.path.isfile(path):
                with open(path, "rb") as stream:
                    current = plistlib.load(stream)
                for name, value in current.get("EnvironmentVariables", {}).items():
                    if not isinstance(name, str) or not isinstance(value, str):
                        continue
                    environment[name] = value

            previous_api_base = environment.get("NetRatelCLIENT__Client__ApiBaseUrl", "")
            previous_settings_path = os.path.join(previous_dir, "clientsettings.json") if previous_dir else ""
            target_settings_path = os.path.join(target_dir, "clientsettings.json")
            settings_path = previous_settings_path if previous_settings_path and os.path.isfile(previous_settings_path) else target_settings_path
            settings = None
            settings_source = "package defaults"
            if os.path.isfile(settings_path):
                with open(settings_path, encoding="utf-8-sig") as stream:
                    settings = json.load(stream)
                if settings_path == previous_settings_path:
                    settings_source = "installed per-version file"
            if settings is not None:
                def get_case_insensitive(mapping, name, fallback=None):
                    return next((value for key, value in mapping.items() if key.lower() == name.lower()), fallback)

                client = get_case_insensitive(settings, "Client", settings)
                if not previous_api_base:
                    previous_api_base = str(client.get("ApiBaseUrl", ""))
                if not previous_api_base and previous_dir:
                    try:
                        with open(os.path.join(previous_dir, "appsettings.json"), encoding="utf-8-sig") as stream:
                            previous_api_base = str(json.load(stream).get("Client", {}).get("ApiBaseUrl", ""))
                    except (OSError, ValueError, AttributeError):
                        pass
                gateway = get_case_insensitive(settings, "Gateway")
                if isinstance(gateway, dict):
                    for name in list(gateway):
                        lowered = name.lower()
                        if lowered in retired_json_gateway:
                            gateway.pop(name, None)
                    endpoint_key = next((key for key in gateway if key.lower() == "endpoint"), None)
                    old_endpoint = gateway.get(endpoint_key) if endpoint_key else None
                    old_origin = public_origin(str(old_endpoint or ""))
                    settings_api_origin = public_origin(str(client.get("ApiBaseUrl", "")))
                    if old_endpoint and (gateway_endpoint or (settings_api_origin and old_origin == settings_api_origin)):
                        gateway.pop(endpoint_key, None)
                transport_key = next((key for key in settings if key.lower() == "transport"), None)
                transport = settings.get(transport_key) if transport_key else None
                if isinstance(transport, dict):
                    mode_key = next((key for key in transport if key.lower() == "mode"), None)
                    if mode_key: transport.pop(mode_key, None)
                    if not transport: settings.pop(transport_key, None)
                os.makedirs(target_dir, exist_ok=True)
                with open(target_settings_path, "w", encoding="utf-8") as stream:
                    json.dump(settings, stream, indent=2)
                    stream.write("\n")
                print(f"Preserved {settings_source}; prior API source={'launchd service environment' if environment.get('NetRatelCLIENT__Client__ApiBaseUrl') else 'per-version file or package defaults'}.", file=sys.stderr)
            previous_api_origin = public_origin(previous_api_base)
            previous_gateway_origin = public_origin(environment.get("NetRatelCLIENT__Gateway__Endpoint", ""))
            previous_gateway_is_redundant = bool(previous_api_origin and previous_gateway_origin == previous_api_origin)
            for name in list(environment):
                if not name.lower().startswith(prefix.lower()):
                    continue
                setting = name[len(prefix):].lower()
                if (setting == "client__apibaseurl" or setting in retired or
                    (setting == "gateway__endpoint" and (gateway_endpoint or previous_gateway_is_redundant))):
                    environment.pop(name)

            environment["NetRatelCLIENT__Client__ApiBaseUrl"] = api_base
            if gateway_endpoint:
                environment["NetRatelCLIENT__Gateway__Endpoint"] = gateway_endpoint
            for name, value in sorted(environment.items()):
                print("                <key>{}</key><string>{}</string>".format(
                    html.escape(name, quote=True), html.escape(value, quote=True)))
            PY
            )"
            cat > "${PLIST_PATH}.new" <<PLIST
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>${LABEL}</string>
              <key>ProgramArguments</key><array><string>${ROOT_DIR}/current/NetRatel.Client</string><string>--service</string></array>
              <key>WorkingDirectory</key><string>${ROOT_DIR}/current</string>
              <key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
              <key>EnvironmentVariables</key><dict>
            ${PRESERVED_CLIENT_ENVIRONMENT}
              </dict>
            </dict></plist>
            PLIST
            chmod 0644 "${PLIST_PATH}.new"
            launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
            mv -f "${PLIST_PATH}.new" "${PLIST_PATH}"
            ln -s "${TARGET_DIR}" "${ROOT_DIR}/current.next"
            mv -f "${ROOT_DIR}/current.next" "${ROOT_DIR}/current"
            ACTIVATED=true
            launchctl bootstrap system "${PLIST_PATH}"
            launchctl print "system/${LABEL}" >/dev/null
            """ : string.Empty;
        var rootCheck = request.InstallAsService ? """
            if [ "${EUID}" -ne 0 ] && [ "${NetRatel_TEST_ALLOW_NONROOT:-false}" != "true" ]; then
              echo "A macOS launch daemon installation requires root. Inspect the script, then run it with sudo." >&2
              exit 1
            fi
            command -v launchctl >/dev/null || { echo "launchctl is required for a macOS service installation." >&2; exit 1; }
            """ : string.Empty;
        var rollback = request.InstallAsService ? """
            if [ "${ACTIVATED}" = true ]; then
              launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
              if [ -n "${PREVIOUS_TARGET}" ]; then
                ln -s "${PREVIOUS_TARGET}" "${ROOT_DIR}/current.rollback"
                mv -f "${ROOT_DIR}/current.rollback" "${ROOT_DIR}/current"
                launchctl bootstrap system "${PLIST_PATH}" || true
              else
                rm -f "${ROOT_DIR}/current"
              fi
            fi
            """ : ":";

        return $$"""
#!/usr/bin/env bash
set -euo pipefail
API_BASE="{{request.ApiBaseUrl}}"
GATEWAY_ENDPOINT="{{request.GatewayEndpoint ?? string.Empty}}"
TENANT_ID={{request.TenantId}}
ENROLLMENT_CODE="{{request.EnrollmentCode}}"
RUNTIME="{{request.RuntimeId}}"
VERSION="{{request.ArtifactVersion}}"
EXPECTED_SHA="{{request.ArtifactSha256}}"
ROOT_DIR="${NetRatel_ROOT:-{{defaultRoot}}}"
TMP_DIR=$(mktemp -d)
{{rootCheck}}
for required in curl unzip shasum python3; do
  command -v "${required}" >/dev/null || { echo "Required command is unavailable: ${required}" >&2; exit 1; }
done
cleanup() { rm -rf "${TMP_DIR}"; }
trap cleanup EXIT
if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
  echo "An immutable client version is required." >&2; exit 1
fi
if [ ! -d "${ROOT_DIR}" ]; then mkdir -p "${ROOT_DIR}"; fi
VERSIONS_DIR="${ROOT_DIR}/versions"
STAGING_DIR="${ROOT_DIR}/staging"
mkdir -p "${VERSIONS_DIR}" "${STAGING_DIR}"
TARGET_DIR="${VERSIONS_DIR}/${VERSION}"
if [ -e "${TARGET_DIR}" ]; then echo "Immutable target version already exists." >&2; exit 1; fi
RESPONSE_HEADERS="${TMP_DIR}/response-headers.txt"
curl -f --max-redirs 0 --connect-timeout 15 --max-time 120 \
  -H "X-NetRatel-Tenant-Id: ${TENANT_ID}" \
  -H "X-NetRatel-Enrollment-Code: ${ENROLLMENT_CODE}" \
  -D "${RESPONSE_HEADERS}" \
  -o "${TMP_DIR}/netratel.zip" \
  "${API_BASE}/api/v1/client-artifacts/${RUNTIME}/${VERSION}/onboarding-download"
test -s "${TMP_DIR}/netratel.zip"
read_response_header() {
  awk -v wanted="$1" '/^HTTP\// { status=$2; value=""; next } status == "200" && tolower(substr($0, 1, length(wanted) + 1)) == tolower(wanted ":") { value=substr($0, index($0, ":") + 1); sub(/^[[:space:]]+/, "", value); sub(/[[:space:]]+$/, "", value); gsub(/\r/, "", value) } END { if (status != "200") exit 1; print value }' "${RESPONSE_HEADERS}"
}
REPORTED_RID=$(read_response_header "X-NetRatel-Artifact-Rid")
REPORTED_VERSION=$(read_response_header "X-NetRatel-Artifact-Version")
REPORTED_SHA=$(read_response_header "X-NetRatel-Artifact-Sha256")
REPORTED_SIZE=$(read_response_header "X-NetRatel-Artifact-Size")
if [ "${REPORTED_RID}" != "${RUNTIME}" ] || [ "${REPORTED_VERSION}" != "${VERSION}" ] ||
  ! [[ "${REPORTED_SHA}" =~ ^[0-9A-Fa-f]{64}$ ]] ||
  ! [[ "${REPORTED_SIZE}" =~ ^[1-9][0-9]*$ ]]; then
  echo "The authorized artifact response metadata did not match the generated package snapshot." >&2; exit 1
fi
if [ -n "${EXPECTED_SHA}" ] &&
  [ "$(printf '%s' "${REPORTED_SHA}" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "${EXPECTED_SHA}" | tr '[:upper:]' '[:lower:]')" ]; then
  echo "The authorized artifact response did not match the generated package snapshot." >&2; exit 1
fi
ACTUAL_SIZE=$(wc -c < "${TMP_DIR}/netratel.zip" | tr -d '[:space:]')
if [ "${ACTUAL_SIZE}" != "${REPORTED_SIZE}" ]; then
  echo "Downloaded client package size did not match its authorized metadata." >&2; exit 1
fi
ACTUAL_SHA=$(shasum -a 256 "${TMP_DIR}/netratel.zip" | awk '{print $1}')
if [ "${ACTUAL_SHA}" != "$(printf '%s' "${REPORTED_SHA}" | tr '[:upper:]' '[:lower:]')" ]; then
  echo "Downloaded client package failed SHA-256 verification." >&2; exit 1
fi
STAGE_DIR=$(mktemp -d "${STAGING_DIR}/.${VERSION}.XXXXXX")
unzip -q "${TMP_DIR}/netratel.zip" -d "${STAGE_DIR}"
python3 - "${STAGE_DIR}/netratel-client-manifest.json" "${VERSION}" "${RUNTIME}" <<'PY'
import json, os, sys
with open(sys.argv[1], encoding='utf-8-sig') as file: manifest=json.load(file)
if (manifest.get('schema') != 'netratel.client.manifest.v1' or
    manifest.get('product') != 'NetRatel.Client' or
    manifest.get('version') != sys.argv[2] or
    manifest.get('runtimeId') != sys.argv[3] or
    manifest.get('executable') != 'NetRatel.Client' or
    not os.path.isfile(os.path.join(os.path.dirname(sys.argv[1]), 'NetRatel.Client'))):
    raise SystemExit('Client package manifest does not match the requested runtime and version.')
PY
chmod 0755 "${STAGE_DIR}/NetRatel.Client"
"${STAGE_DIR}/NetRatel.Client" --enroll "${ENROLLMENT_CODE}" --api "${API_BASE}" {{silentArg}}
PREVIOUS_TARGET=""
if [ -L "${ROOT_DIR}/current" ]; then PREVIOUS_TARGET=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "${ROOT_DIR}/current"); fi
ACTIVATED=false
rollback() {
  status=$?
  if [ "$status" -ne 0 ]; then
    {{rollback}}
  fi
  rm -rf "${TMP_DIR}"
  exit "$status"
}
trap rollback EXIT
mv "${STAGE_DIR}" "${TARGET_DIR}"
{{serviceBlock}}
echo "NetRatel macOS client installation complete."
""";
    }
}
