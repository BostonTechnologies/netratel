# NetRatel Windows installer. Windows PowerShell 5.1; no online-readiness gate.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ApiBase = @@API_BASE_URL@@
$GatewayEndpoint = @@GATEWAY_ENDPOINT@@
[int]$TenantId = @@TENANT_ID@@
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

function Get-PathDiagnostic([string]$Path, [string]$Role, $Acl, $Rule, [string]$Reason) {
    $safe = [regex]::Replace($Path, '[\x00-\x1f\x7f]', '?')
    if ($safe.Length -gt 300) { $safe = $safe.Substring(0, 300) + '...' }
    $detail = "Owned-path rejection: path='$safe'; role=$Role; reason=$Reason"
    if ($Acl) { $detail += '; ownerSid=' + $Acl.GetOwner([Security.Principal.SecurityIdentifier]).Value }
    if ($Rule) { $detail += "; aceSid=$($Rule.IdentityReference.Value); type=$($Rule.AccessControlType); rights=$($Rule.FileSystemRights) ($([int]$Rule.FileSystemRights)); inherited=$($Rule.IsInherited); inheritance=$($Rule.InheritanceFlags); propagation=$($Rule.PropagationFlags)" }
    return $detail
}

# Inspect ancestors without changing them. ProgramData may allow creating siblings.
function Assert-OwnedPath([string]$Path, [switch]$AllowMissing, [switch]$File, [switch]$AncestorsOnly) {
    $leaf = Get-CanonicalPath $Path
    $component = if ($AncestorsOnly) { [IO.Path]::GetDirectoryName($leaf) } else { $leaf }
    while ($component) {
        $role = if ($component -eq $leaf) { 'leaf' } else { 'ancestor' }
        if (Test-Path -LiteralPath $component) {
            $item = Get-Item -LiteralPath $component -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw (Get-PathDiagnostic $component $role $null $null 'reparse point') }
            if ($component -eq $leaf -and $item.PSIsContainer -eq [bool]$File) { throw (Get-PathDiagnostic $component $role $null $null 'unexpected object type') }
            $acl = Get-Acl -LiteralPath $component
            if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedSids) { throw (Get-PathDiagnostic $component $role $acl $null 'untrusted owner') }
            foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
                if ($rule.AccessControlType -ne 'Allow' -or ($role -eq 'ancestor' -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly))) { continue }
                $unsafe = 0x000D0040 # Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership.
                if ($component -eq $leaf) { $unsafe = $unsafe -bor 0x116 }
                if ($rule.IdentityReference.Value -notin $trustedSids -and ([int]$rule.FileSystemRights -band $unsafe)) {
                    throw (Get-PathDiagnostic $component $role $acl $rule 'untrusted modification; inspect NetRatel_LEGACY_ACL_REPAIR=preview for canonical legacy paths')
                }
            }
        }
        elseif ($component -eq $leaf -and -not $AllowMissing) { throw (Get-PathDiagnostic $component $role $null $null 'missing object') }
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
    if ($Path -eq (Get-LegacyAclRoot)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Traverse', 'ContainerInherit', 'None', 'Allow'))
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

# Fixed product roots only; no caller-supplied repair target or recursive ACL reset.
function Get-LegacyAclRoot { return Get-CanonicalPath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetRatel') }
function Get-LegacyAclRecoveryRoot { return Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NetRatel\acl-repair' }

function Get-LegacyAclPlan {
    $root = Get-LegacyAclRoot
    Assert-OwnedPath $root -AllowMissing -AncestorsOnly
    if (-not (Test-Path -LiteralPath $root)) { return }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($root)
    $count = 0
    while ($queue.Count) {
        $path = $queue.Dequeue()
        if (++$count -gt 4096) { throw 'Legacy ACL repair exceeds the bounded 4096-object canonical tree.' }
        $relative = $path.Substring($root.Length).TrimStart('\').ToLowerInvariant()
        if ($relative -and $relative -notmatch '^(agent\.dat(?:\.[a-f0-9]{32}\.tmp)?|\.netratel-credential-machine-id|update(?:\\.*)?|logs(?:\\.*)?|remote-desktop(?:\\.*)?|client(?:\\(?:logs(?:\\(?:service|remote-support-console-provider|remote-desktop-helper)(?:\\.*)?)?|diagnostics(?:\\.*)?|remote-(?:desktop|support|support-firewall)-state\.json))?)$') { throw (Get-PathDiagnostic $path 'leaf' $null $null 'unrecognized canonical layout; manual review required') }
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw (Get-PathDiagnostic $path 'leaf' $null $null 'reparse point') }
        if ((($relative -in @('', 'update', 'logs', 'remote-desktop', 'client', 'client\logs', 'client\logs\remote-desktop-helper', 'client\logs\service', 'client\logs\remote-support-console-provider', 'client\diagnostics')) -and -not $item.PSIsContainer) -or
            (($relative -match '^(agent\.dat(?:\.[a-f0-9]{32}\.tmp)?|\.netratel-credential-machine-id|client\\remote-(?:desktop|support|support-firewall)-state\.json)$') -and $item.PSIsContainer)) { throw (Get-PathDiagnostic $path 'leaf' $null $null 'unexpected canonical object type') }
        $acl = Get-Acl -LiteralPath $path -Audit
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedSids) { throw (Get-PathDiagnostic $path 'leaf' $acl $null 'untrusted owner; ownership is never changed by repair') }
        $remove = @()
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            $rights = [int]$rule.FileSystemRights
            if ($rule.AccessControlType -eq 'Deny' -and $rule.IdentityReference.Value -in $trustedSids -and ($rights -band 0x1F01FF)) { throw (Get-PathDiagnostic $path 'leaf' $acl $rule 'conflicting trusted-account deny') }
            if ($rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -in $trustedSids -or -not ($rights -band 0xD0156)) { continue }
            $helperLog = $relative -match '^client\\logs\\remote-desktop-helper(?:\\|$)'
            $usersModify = $rule.IdentityReference.Value -eq 'S-1-5-32-545' -and -not ($rights -band (-bnot 0x1301BF))
            if ($helperLog -and $usersModify) { continue } # Intentional isolated interactive-user logs.
            if (-not $rule.IsInherited -or -not $usersModify) { throw (Get-PathDiagnostic $path 'leaf' $acl $rule 'not a reviewed inherited BUILTIN Users write pattern') }
            $remove += $rule
        }
        if ($remove.Count) { [pscustomobject]@{ Path=$path; Sddl=$acl.GetSecurityDescriptorSddlForm('All'); Remove=$remove } }
        if ($item.PSIsContainer -and $relative -ne 'client\logs\remote-desktop-helper') { foreach ($child in Get-ChildItem -LiteralPath $path -Force) { $queue.Enqueue($child.FullName) } }
    }
}

function Assert-LegacyAclOffline {
    $service = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'" -OperationTimeoutSec 10
    if ($service -and $service.State -ne 'Stopped') { throw 'Legacy ACL apply requires the NetRatel.Client service stopped by the operator; preview is read-only.' }
    foreach ($process in Get-CimInstance Win32_Process -OperationTimeoutSec 10) {
        if ($process.Name -eq 'NetRatel.Client.exe' -or ($process.Name -match '^(powershell|pwsh)(\.exe)?$' -and (-not $process.CommandLine -or $process.CommandLine -match 'netratel-update\.ps1|NetRatelSeedHandoffRequestPath'))) { throw 'Legacy ACL apply requires no active NetRatel client/updater process; an unsafe updater lock is never opened.' }
    }
}

function Invoke-LegacyAclRepair([string]$Mode) {
    if ($Mode -notin @('preview', 'apply')) { throw 'Use NetRatel_LEGACY_ACL_REPAIR=preview or apply; it repairs only the canonical offline product tree.' }
    $plan = @(Get-LegacyAclPlan)
    foreach ($entry in $plan) { foreach ($rule in $entry.Remove) { Write-Host (Get-PathDiagnostic $entry.Path 'leaf' (Get-Acl -LiteralPath $entry.Path) $rule 'eligible inherited-write repair') } }
    if ($Mode -eq 'preview') { Write-Host "Legacy ACL preview: $($plan.Count) descriptor(s) eligible; no files, locks or ACLs changed."; return }
    if (-not $plan.Count) { Write-Host 'Legacy ACL repair: already safe; no changes.'; return }
    Assert-LegacyAclOffline
    $recovery = Get-LegacyAclRecoveryRoot
    New-OwnedDirectory $recovery
    $recoveryLock = Join-Path $recovery 'repair.lock'
    Assert-OwnedPath $recoveryLock -AllowMissing -File
    $lock = [IO.File]::Open($recoveryLock, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-LegacyAclOffline
        $current = @(Get-LegacyAclPlan)
        if (($current | Select-Object Path,Sddl | ConvertTo-Json -Depth 3 -Compress) -ne ($plan | Select-Object Path,Sddl | ConvertTo-Json -Depth 3 -Compress)) { throw 'The canonical ACL tree changed during repair preparation; preview again.' }
        $backup = Join-Path $recovery ([Guid]::NewGuid().ToString('N') + '.json')
        Write-PrivateFile $backup (($plan | Select-Object Path,Sddl | ConvertTo-Json -Depth 3) + "`n")
        foreach ($entry in $plan) {
            $item = Get-Item -LiteralPath $entry.Path -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw (Get-PathDiagnostic $entry.Path 'leaf' $null $null 'path changed to a reparse point during repair') }
            $acl = Get-Acl -LiteralPath $entry.Path -Audit
            if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedSids) { throw (Get-PathDiagnostic $entry.Path 'leaf' $acl $null 'owner changed during repair') }
            # Snapshot inherited ACEs before protection: .NET Framework retains their
            # inherited flag in memory when asked to preserve inheritance. Rebuild them
            # explicitly in one write, reducing only reviewed Users Allow write rights.
            # Parent repairs may already have removed unsafe inheritance from children.
            $inherited = @($acl.GetAccessRules($false, $true, [Security.Principal.SecurityIdentifier]))
            $acl.SetAccessRuleProtection($true, $false)
            foreach ($rule in $inherited) {
                $rights = [int]$rule.FileSystemRights
                if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -eq 'S-1-5-32-545' -and ($rights -band 0xD0156) -and -not ($rights -band (-bnot 0x1301BF))) {
                    $rights = $rights -band 0x1200A9
                }
                if ($rights) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($rule.IdentityReference, $rights, $rule.InheritanceFlags, $rule.PropagationFlags, $rule.AccessControlType)) }
            }
            Set-Acl -LiteralPath $entry.Path -AclObject $acl
        }
        if (@(Get-LegacyAclPlan).Count) { throw 'Legacy ACL repair failed revalidation; protected original descriptors were retained.' }
        Write-Host "Legacy ACL repair complete; descriptors=$backup. Binary rollback does not restore known-insecure ACLs."
    }
    finally { $lock.Dispose() }
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

function Update-ClientSettings($Settings, [string]$ApiBase, [string]$GatewayEndpoint) {
    # Keep the legacy flat shape: introducing Client would hide its other options.
    $client = if ($Settings.Client -and @($Settings.Client.PSObject.Properties).Count) { $Settings.Client } else { $Settings }
    $oldApi = [string]$client.ApiBaseUrl
    if (-not $oldApi) { $oldApi = [string]$Settings.ApiBaseUrl }
    if ($oldApi -and $Settings.Gateway.Endpoint -and (Get-Origin $Settings.Gateway.Endpoint -Gateway) -eq (Get-Origin $oldApi)) { $Settings.Gateway.PSObject.Properties.Remove('Endpoint') }
    $client | Add-Member -NotePropertyName ApiBaseUrl -NotePropertyValue $ApiBase -Force
    if ($GatewayEndpoint) {
        if (-not $Settings.Gateway) { $Settings | Add-Member -NotePropertyName Gateway -NotePropertyValue ([pscustomobject]@{}) -Force }
        $Settings.Gateway | Add-Member -NotePropertyName Endpoint -NotePropertyValue $GatewayEndpoint -Force
    }
    return $Settings
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

function Initialize-ServiceController {
    try {
        Add-Type -AssemblyName System.ServiceProcess -ErrorAction Stop
        $null = [System.ServiceProcess.ServiceController]
        $null = [System.ServiceProcess.ServiceControllerStatus]
    }
    catch {
        throw [InvalidOperationException]::new('System.ServiceProcess could not be loaded. Repair the Windows .NET Framework installation and retry in clean Windows PowerShell 5.1 before changing the service.', $_.Exception)
    }
}

function Set-ServiceState([string]$State, [string]$Name = $serviceName) {
    $controller = [System.ServiceProcess.ServiceController]::new($Name)
    try {
        $controller.Refresh()
        if ([string]$controller.Status -ne $State) {
            if ($State -eq 'Stopped' -and $controller.Status -ne 'StopPending') { $controller.Stop() }
            elseif ($State -eq 'Running' -and $controller.Status -ne 'StartPending') { $controller.Start() }
            $controller.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]([Enum]::Parse([System.ServiceProcess.ServiceControllerStatus], $State)), [TimeSpan]::FromSeconds(60))
        }
    }
    finally { $controller.Dispose() }
}

function Write-CutoverState($Record) {
    $path = Join-Path $StateDir 'state.json'
    Assert-OwnedPath $path -AllowMissing -File
    $temporary = "$path.$PID.tmp"
    Write-PrivateFile $temporary ($Record | ConvertTo-Json -Depth 4)
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

function Restore-CutoverMode($Record) {
    if (-not $Record.startModeGuardActive) { return }
    $registered = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10
    if (-not $registered -or [string]$registered.PathName -notin @([string]$Record.previousPath, [string]$Record.activePath) -or
        [string]$Record.intendedStartMode -notin @('Auto', 'Manual') -or
        $registered.StartMode -notin @('Disabled', [string]$Record.intendedStartMode)) {
        throw 'The interrupted cutover service was changed by an administrator; inspect its preserved state before repair.'
    }
    $start = @{ Auto = 'auto'; Manual = 'demand' }[[string]$Record.intendedStartMode]
    Invoke-ServiceControl @('config', $serviceName, 'start=', $start)
    if ((Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10).StartMode -ne $Record.intendedStartMode) { throw 'The intended service start mode did not verify.' }
    $Record.startModeGuardActive = $false
    Write-CutoverState $Record
}

function Get-SafeDiagnosticLine([string]$Line) {
    # Only these fixed Auth messages may bypass the sensitive-line filter.
    # Anchor the entire message, including the optional production timestamp.
    $message = $Line -replace '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} - ', ''
    $known = @(
        '[Auth] Enrollment is required before starting the service. Run NetRatel.Client --enroll <code> --api <url>. Exiting.',
        '[Auth] Enrollment code is required for first run. Exiting.',
        '[Auth] Enrollment credentials were not persisted. Exiting.',
        '[Auth] Ignoring netratel.enroll.json: payload is empty.',
        '[Auth] Ignoring netratel.enroll.json: malformed JSON syntax.',
        '[Auth] Ignoring netratel.enroll.json: invalid payload field/type.',
        '[Auth] Ignoring netratel.enroll.json: unable to read enrollment file (I/O failure).',
        '[Auth] Ignoring netratel.enroll.json: schema must be netratel.enroll.v1.',
        '[Auth] Ignoring netratel.enroll.json: tenantId and enrollmentCode are required.',
        '[Auth] Ignoring netratel.enroll.json: enrollment payload is expired or missing validToUtc.',
        '[Auth] Ignoring netratel.enroll.json: issuer is required.',
        '[Auth] Ignoring netratel.enroll.json: issuer does not match the configured API base URL.',
        '[Auth] Ignoring netratel.enroll.json: issuer and configured API base URL must be an origin, optionally followed by /api.'
    )
    if ($EnrollmentCode -and $Line.Contains($EnrollmentCode)) { return '[redacted sensitive log line]' }
    if ($known -ccontains $message -or
        $message -cmatch '^\[Auth\] Ignoring netratel\.enroll\.json: invalid field/type \(field=(schema|tenantId|enrollmentCode|issuer|createdAtUtc|validToUtc); expected=(String|Int32|DateTime); actual=(Object|Array|String|Number|True|False|Null|Undefined)\)\.$' -or
        $message -cmatch '^\[Auth\] Ignoring netratel\.enroll\.json: invalid payload type \(expected=Object; actual=(Array|String|Number|True|False|Null|Undefined)\)\.$') { return $message }
    if ($Line -match '(?i)(bearer|token|secret|password|key|enroll|authorization|capability|grant)') { return '[redacted sensitive log line]' }
    $safe = [regex]::Replace($Line, 'https?://[^\s]+', '[redacted-url]')
    return $safe.Substring(0, [Math]::Min(500, $safe.Length))
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
                Write-Host (Get-SafeDiagnosticLine $line)
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
    Initialize-ServiceController
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run Windows PowerShell as Administrator.' }
    $trustedSids += $identity.User.Value
    $trustedSids += 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' # TrustedInstaller service SID.
    $administrators = Get-CimInstance Win32_Group -Filter "SID='S-1-5-32-544'" -OperationTimeoutSec 10
    $trustedSids += @(Get-CimAssociatedInstance -InputObject $administrators -Association Win32_GroupUser -ResultClassName Win32_UserAccount -OperationTimeoutSec 10 | Select-Object -ExpandProperty SID)
    if ($env:NetRatel_LEGACY_ACL_REPAIR) {
        Invoke-LegacyAclRepair $env:NetRatel_LEGACY_ACL_REPAIR
        if ($env:NetRatel_LEGACY_ACL_REPAIR -eq 'preview') { return }
    }
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
    $installedClient = if ($installedSettings.Client -and @($installedSettings.Client.PSObject.Properties).Count) { $installedSettings.Client } else { $installedSettings }
    $configuredState = Get-ClientSetting 'Client__AutoUpdate__StateDirectory'
    if (-not $configuredState) { $configuredState = $installedClient.AutoUpdate.StateDirectory }
    if (-not $configuredState) { $configuredState = $installedDefaults.Client.AutoUpdate.StateDirectory }
    $StateDir = Resolve-PathSetting $env:NetRatel_STATE @((Get-ServiceEnvironment 'NetRatel_UPDATE_STATE'), $configuredState) (Join-Path $env:ProgramData 'NetRatel\update') 'updater state'
    $clientRequest = Get-ClientSetting 'Client__AutoUpdate__RequestPath'
    if (-not $clientRequest) { $clientRequest = $installedClient.AutoUpdate.RequestPath }
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
    $cutoverStatePath = Join-Path $StateDir 'state.json'
    if ($existingService -and (Test-Path -LiteralPath $cutoverStatePath -PathType Leaf)) {
        Assert-OwnedPath $cutoverStatePath -File
        $interruptedCutover = Get-Content -LiteralPath $cutoverStatePath -Raw | ConvertFrom-Json
        if ($interruptedCutover.startModeGuardActive) {
            Restore-CutoverMode $interruptedCutover
            $existingService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10
            Write-Host 'Restored the intended start mode from the interrupted owned cutover; gateway activation remains unverified.'
        }
    }
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
    $settings = Update-ClientSettings $settings $ApiBase $GatewayEndpoint
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
    $cutoverRecord = $null
    $phase = 'local activation'
    try {
        if ($existingService -and -not $InstallAsService) { throw 'A registered service owns this installation; use its service repair installer.' }
        if ($existingService) {
            if ($existingService.StartMode -notin @('Auto', 'Manual')) { throw 'The existing service is disabled by an administrator; reconcile its start mode before repair.' }
            $cutoverRecord = [pscustomobject]@{ state = 'guarding_cutover'; version = $resolvedVersion;
                intendedStartMode = $existingService.StartMode; startModeGuardActive = $true;
                previousPath = $existingService.PathName; activePath = $existingService.PathName }
            Write-CutoverState $cutoverRecord
            Invoke-ServiceControl @('config', $serviceName, 'start=', 'disabled')
            if ((Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10).StartMode -ne 'Disabled') { throw 'Service cutover start-mode fence did not verify.' }
            $stopAttempted = $true; Set-ServiceState 'Stopped'
        }
        $cutover = $true
        if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup; $backedUp = $true }
        Move-Item -LiteralPath $stageDir -Destination $target
        $activated = $true
        $exe = Join-Path $target 'NetRatel.Client.exe'
        if ($InstallAsService) {
            $enrollment = @{ schema = 'netratel.enroll.v1'; tenantId = [int]$TenantId; enrollmentCode = $EnrollmentCode; issuer = $ApiBase; createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); validToUtc = $ValidToUtc } | ConvertTo-Json
            Write-PrivateFile (Join-Path $target 'netratel.enroll.json') $enrollment
            $updaterSource = Join-Path $target 'updater\netratel-update.ps1'
            Assert-OwnedPath $updaterSource -File
            Assert-OwnedPath $updaterPath -AllowMissing -File
            if (Test-Path -LiteralPath $updaterPath) { Move-Item -LiteralPath $updaterPath -Destination $updaterBackup }
            $updaterChanged = $true
            Write-PrivateFile $updaterPath ([IO.File]::ReadAllText($updaterSource))
            $image = '"' + $exe + '" --service'
            if (-not $existingService) {
                Invoke-ServiceControl @('create', $serviceName, 'binPath=', $image, 'obj=', 'LocalSystem', 'start=', 'auto'); $created = $true
                Invoke-ServiceControl @('failure', $serviceName, 'reset=', '@@WINDOWS_RECOVERY_RESET@@', 'actions=', @@WINDOWS_RECOVERY_ACTIONS@@)
                Invoke-ServiceControl @('failureflag', $serviceName, '1')
            }
            else {
                $cutoverRecord.activePath = $image
                Write-CutoverState $cutoverRecord
                Invoke-ServiceControl @('config', $serviceName, 'binPath=', $image)
            }
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
            if ($configured.PathName -ne $image -or $configured.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM') -or $configured.StartMode -ne $(if ($existingService) { 'Disabled' } else { 'Auto' })) { throw 'The service configuration did not verify.' }
            if ($cutoverRecord) { Restore-CutoverMode $cutoverRecord }
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
        $startupFailure = $_
        Write-Diagnostics
        if ($cutoverRecord -and -not $cutover) { Restore-CutoverMode $cutoverRecord }
        if ($stopAttempted -and -not $cutover -and $previousRunning) {
            try { Set-ServiceState 'Stopped'; Set-ServiceState 'Running'; Write-Host 'The previous service was restarted; package files were untouched.' }
            catch { Write-Host 'The previous service could not be restarted within the bounded recovery; package files were untouched.' }
        }
        if ($cutover -and $script:serviceControlExited) {
            try {
                if ($InstallAsService -and ($existingService -or $created)) {
                    if ($created -and -not $cutoverRecord) {
                        $cutoverRecord = [pscustomobject]@{ state='guarding_cutover'; version=$resolvedVersion;
                            intendedStartMode='Auto'; startModeGuardActive=$true; previousPath=$image; activePath=$image }
                    }
                    if ($cutoverRecord) {
                        $cutoverRecord.startModeGuardActive = $true
                        Write-CutoverState $cutoverRecord
                        Invoke-ServiceControl @('config', $serviceName, 'start=', 'disabled')
                        if ((Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -OperationTimeoutSec 10).StartMode -ne 'Disabled') { throw 'Rollback start-mode fence did not verify.' }
                    }
                    Set-ServiceState 'Stopped'
                }
                if ($created) {
                    Invoke-ServiceControl @('delete', $serviceName)
                    $cutoverRecord.startModeGuardActive = $false
                    Write-CutoverState $cutoverRecord
                }
                elseif ($existingService) {
                    $startMode = @{ Auto = 'auto'; Manual = 'demand'; Disabled = 'disabled' }[[string]$existingService.StartMode]
                    if (-not $startMode) { throw 'The previous service start mode is unsupported.' }
                    Invoke-ServiceControl @('config', $serviceName, 'binPath=', [string]$existingService.PathName)
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
                if ($cutoverRecord -and -not $created) { Restore-CutoverMode $cutoverRecord }
                if ($previousRunning) { Set-ServiceState 'Running' }
                Write-Host 'Previous binaries and service configuration restored; credentials were preserved.'
            }
            catch { Write-Host 'Rollback could not complete safely; retained packages require manual inspection.' }
        }
        else { Write-Host 'No safe cutover rollback was available; retained packages require manual inspection.' }
        throw $startupFailure
    }
    @@SEED_SUCCESS@@
}
catch {
    $installerFailure = $_
    Write-Host "Installer stopped during $phase ($($installerFailure.Exception.GetType().Name))."
    if ($installerFailure.Exception.Message -notmatch '(?i)(https?://|bearer|token|secret|password|enrollment.?code)') { Write-Host $installerFailure.Exception.Message }
    Write-Diagnostics
    @@SEED_FAILURE@@
    throw $installerFailure
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
