#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory)][string] $Profile,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][switch] $Prepare,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string] $Resource,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string] $CredentialFile,
    [Parameter(Mandatory, ParameterSetName = 'AccessPrepare')][switch] $AccessPrepare,
    [Parameter(Mandatory, ParameterSetName = 'AccessPrepare')][string] $RequestFile,
    [Parameter(Mandatory, ParameterSetName = 'AccessPrepare')][string] $StateFile,
    [Parameter(Mandatory, ParameterSetName = 'AccessPrepare')][string] $DestinationConfig,
    [Parameter(ParameterSetName = 'AccessPrepare')][string] $DestinationProfile = 'default'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$configurationPath = $null
$protectedCredential = $null
$result = 2

function Assert-LocalPath([string] $Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\')) { throw 'An absolute local path is required.' }
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Profile and credential paths must not traverse reparse points.'
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-OwnerControlled([string] $Path) {
    $trustedInstaller = [Security.Principal.NTAccount]::new('NT SERVICE', 'TrustedInstaller').Translate([Security.Principal.SecurityIdentifier]).Value
    $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544', $trustedInstaller)
    $write = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    Assert-PathAcl $Path $allowed $write $false

    # Creating a sibling under e.g. C:\ is not permission to replace an
    # existing protected path. Check the rights that can replace each ancestor,
    # including parent DeleteChild, without rejecting root CreateFiles grants.
    $replace = [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    while ($parent) {
        Assert-PathAcl $parent $allowed $replace $true
        $next = [IO.Path]::GetDirectoryName($parent)
        if ($next -eq $parent) { break }
        $parent = $next
    }
}

function Assert-PathAcl([string] $Path, [string[]] $Allowed, [Security.AccessControl.FileSystemRights] $Rights, [bool] $AncestorsOnly) {
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $Allowed) { throw 'The selected path or an ancestor is not owned by the current user or a trusted system identity.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        $inheritOnly = ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
        # An inherit-only ACE does not authorize replacing this ancestor.
        # For the selected installation directory, keep checking write grants
        # to its children; CREATOR OWNER templates do not grant another user
        # creation rights on the protected directory itself.
        if ($inheritOnly -and ($AncestorsOnly -or $rule.IdentityReference.Value -eq 'S-1-3-0')) { continue }
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            ($rule.FileSystemRights -band $Rights) -ne 0 -and $rule.IdentityReference.Value -notin $Allowed) {
            throw 'The selected path or an ancestor permits another identity to replace the credential, profile or installed bridge.'
        }
    }
}

function Assert-Resource([string] $Value) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -ne 'https' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/mcp') {
        throw 'Use the exact canonical HTTPS /mcp resource without user information, query or fragment.'
    }
    return $uri.AbsoluteUri
}

function Assert-PrivateStatePath([string] $Path) {
    Assert-LocalPath $Path
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    Assert-OwnerControlled $parent
    # The pending state holds a private pairing capability. Require a private
    # existing directory; never alter another directory's ACL on the operator's behalf.
    $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')
    Assert-PathAcl $parent $allowed ([Security.AccessControl.FileSystemRights]::ReadData) $false
    if (Test-Path -LiteralPath $Path) {
        Assert-OwnerControlled $Path
        Assert-PathAcl $Path $allowed ([Security.AccessControl.FileSystemRights]::ReadData) $false
    }
}

function Read-ProtectedCredential([string] $Path) {
    Assert-LocalPath $Path
    Assert-OwnerControlled $Path
    # Keep the same encrypted bytes selected while checking length, importing
    # and hashing. Rotation/replacement must wait; the original file is read-only.
    $locked = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($locked.Length -gt 65536) { throw 'The protected credential file is too large.' }
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($locked)).ToLowerInvariant()
        # Import-Clixml decrypts only standard Windows Export-Clixml SecureString/PSCredential values.
        # Plain strings, custom objects and non-Windows exports are not accepted as token files.
        $value = Import-Clixml -LiteralPath $Path
        $credential = if ($value -is [Security.SecureString]) { $value }
            elseif ($value -is [Management.Automation.PSCredential]) { $value.Password }
            else { throw 'Unsupported protected credential format.' }
        if ($credential.Length -eq 0 -or $credential.Length -gt 16384) {
            $credential.Dispose()
            throw 'The protected credential is empty or too large.'
        }
        return @{ Credential = $credential; Sha256 = $digest }
    }
    finally { $locked.Dispose() }
}

try {
    if (-not $IsWindows) { throw 'This protected launcher requires Windows under the credential-owning account.' }
    Assert-LocalPath $PSScriptRoot
    Assert-OwnerControlled $PSScriptRoot
    Assert-LocalPath $Profile
    $Profile = [IO.Path]::GetFullPath($Profile)
    $bridge = Join-Path $PSScriptRoot 'NetRatel.Mcp.exe'
    $relay = Join-Path $PSScriptRoot 'HttpMcpProcessRelay.cs'
    Assert-LocalPath $bridge
    Assert-LocalPath $relay
    Assert-OwnerControlled $bridge
    Assert-OwnerControlled $relay
    $bridgeVersion = (& $bridge http-bridge --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $bridgeVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw 'The installed bridge version could not be verified.'
    }
    if ($Prepare) {
        if (Test-Path -LiteralPath $Profile) { throw 'The destination profile already exists; choose a new file to preserve rollback.' }
        Assert-OwnerControlled ([IO.Path]::GetDirectoryName($Profile))
        $canonical = Assert-Resource $Resource
        $selectedCredential = Read-ProtectedCredential $CredentialFile
        $protectedCredential = $selectedCredential.Credential
        $record = [ordered]@{
            schemaVersion = 1; resource = $canonical; credentialPurpose = 'HttpMcp'
            credentialFormat = 'windows-powershell-clixml'; credentialFile = [IO.Path]::GetFullPath($CredentialFile)
            credentialFileSha256 = $selectedCredential.Sha256
            bridgeVersion = $bridgeVersion
        }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Depth 4))
        $stream = [IO.FileStream]::new($Profile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        Assert-OwnerControlled $Profile
        [ordered]@{
            schemaVersion = 1; state = 'prepared'; resource = $canonical; credentialPurpose = 'HttpMcp'
            protectedStorage = 'windows-powershell-clixml'; bridgeVersion = $bridgeVersion
            profile = $Profile; profileSha256 = (Get-FileHash -LiteralPath $Profile -Algorithm SHA256).Hash.ToLowerInvariant()
            launch = @{ command = (Get-Process -Id $PID).Path; args = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-Profile', $Profile) }
            consumerSwitch = 'pending'; consumerReconnect = 'pending'; nativeLoadedMetadata = 'not_observed'; effectiveAdmission = 'not_tested'
        } | ConvertTo-Json -Depth 5
        $result = 0
    }
    else {
        Assert-OwnerControlled $Profile
        if ((Get-Item -LiteralPath $Profile).Length -gt 16384) { throw 'The selected profile is too large.' }
        $record = Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json -AsHashtable
        $allowed = @('schemaVersion', 'resource', 'credentialPurpose', 'credentialFormat', 'credentialFile', 'credentialFileSha256', 'bridgeVersion')
        if ($record.Count -ne $allowed.Count -or @($record.Keys | Where-Object { $_ -notin $allowed }).Count -ne 0 -or
            $record.schemaVersion -ne 1 -or $record.credentialPurpose -ne 'HttpMcp' -or
            $record.credentialFormat -ne 'windows-powershell-clixml' -or $record.bridgeVersion -ne $bridgeVersion) {
            throw 'The protected bridge profile is invalid or belongs to a different installed release.'
        }
        $canonical = Assert-Resource $record.resource
        $selectedCredential = Read-ProtectedCredential $record.credentialFile
        $protectedCredential = $selectedCredential.Credential
        if ($selectedCredential.Sha256 -cne $record.credentialFileSha256) {
            throw 'The protected credential source changed. Inspect its lifecycle and prepare a new profile before reconnecting.'
        }
        if ($AccessPrepare) {
            foreach ($path in @($RequestFile, $StateFile, $DestinationConfig)) { Assert-LocalPath $path }
            $RequestFile = [IO.Path]::GetFullPath($RequestFile)
            $StateFile = [IO.Path]::GetFullPath($StateFile)
            $DestinationConfig = [IO.Path]::GetFullPath($DestinationConfig)
            if ($StateFile -ieq $DestinationConfig) { throw 'Pending state and the new destination must be distinct.' }
            foreach ($source in @($Profile, $record.credentialFile, $bridge, $relay, $RequestFile)) {
                if ($StateFile -ieq [IO.Path]::GetFullPath($source) -or $DestinationConfig -ieq [IO.Path]::GetFullPath($source)) {
                    throw 'Pending state and the new destination must not replace any selected input or installed bridge file.'
                }
            }
            Assert-OwnerControlled $RequestFile
            if ((Get-Item -LiteralPath $RequestFile).Length -gt 262144) { throw 'The exact proposal file is too large.' }
            Assert-PrivateStatePath $StateFile
            Assert-OwnerControlled ([IO.Path]::GetDirectoryName($DestinationConfig))
            if (Test-Path -LiteralPath $DestinationConfig) { throw 'The destination already exists. Use access resume with its saved state to reconcile; do not prepare a replacement.' }
            if ($DestinationProfile -notmatch '^[A-Za-z0-9_-]{1,64}$') { throw 'Use a bounded simple destination profile name.' }
        }
        # Only non-secret client settings are written; the original DPAPI file is never changed.
        $configurationPath = Join-Path ([IO.Path]::GetTempPath()) ('netratel-http-client-' + [Guid]::NewGuid().ToString('N') + '.json')
        Assert-LocalPath $configurationPath
        Assert-OwnerControlled ([IO.Path]::GetDirectoryName($configurationPath))
        $settings = @{ ApiBaseUrl = $canonical; CredentialPurpose = 'HttpMcp'; AuthenticationMode = 'IntegrationCredential' } | ConvertTo-Json
        $stream = [IO.FileStream]::new($configurationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($settings); $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        Add-Type -Path $relay
        $start = [Diagnostics.ProcessStartInfo]::new($bridge)
        if ($AccessPrepare) {
            foreach ($argument in @('access', 'prepare', '--instance', $canonical, '--request-file', $RequestFile,
                '--requester-config', $configurationPath, '--state-file', $StateFile, '--config', $DestinationConfig,
                '--profile', $DestinationProfile)) { $start.ArgumentList.Add($argument) }
            $result = [NetRatel.ClientTools.HttpMcpProcessRelay]::RunAccessPrepareAsync($start, $protectedCredential,
                [Console]::OpenStandardInput(), [Console]::OpenStandardOutput(), [Console]::OpenStandardError()).GetAwaiter().GetResult()
        }
        else {
            $start.ArgumentList.Add('http-bridge'); $start.ArgumentList.Add('--config'); $start.ArgumentList.Add($configurationPath)
            $result = [NetRatel.ClientTools.HttpMcpProcessRelay]::RunAsync($start, $protectedCredential,
                [Console]::OpenStandardInput(), [Console]::OpenStandardOutput(), [Console]::OpenStandardError()).GetAwaiter().GetResult()
        }
    }
}
catch {
    [Console]::Error.WriteLine('Protected HTTP MCP launcher could not continue. Check Windows account, supported DPAPI format, owner-controlled absolute paths, canonical resource and matching release. Reviewed auxiliary preparation needs a private existing state directory readable only by this account or trusted administrators, distinct request/state/destination paths, and a new destination profile. Inspect changed credentials before preparing a new profile. Reconcile outstanding operations with the same saved state before reconnecting; no operation was automatically retried. No secret or remote response is displayed.')
    $result = 2
}
finally {
    if ($protectedCredential) { $protectedCredential.Dispose() }
    if ($configurationPath -and [IO.File]::Exists($configurationPath)) { [IO.File]::Delete($configurationPath) }
}
exit $result
