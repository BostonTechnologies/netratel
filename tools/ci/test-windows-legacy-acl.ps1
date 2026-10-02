# Offline function probes only. Overrides below are confined to this test process.
param([Parameter(Mandatory=$true)][string]$TemplatePath, [switch]$Native)
$ErrorActionPreference = 'Stop'
$source = [regex]::Replace([IO.File]::ReadAllText($TemplatePath), '@@[A-Z0-9_]+@@', '1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    Invoke-Expression $function.Extent.Text
}
$trustedSids = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
function Expect-Refusal([scriptblock]$Probe, [string]$Reason) {
    try { & $Probe | Out-Null } catch { if ($_.Exception.Message -match $Reason) { return }; throw }
    throw "Expected refusal: $Reason"
}
$actualOwnedPath = (Get-Item Function:\Assert-OwnedPath).ScriptBlock
if (-not $Native) {
    # Synthetic ACLs use numeric SID/rights inputs and actual production decision code.
    $script:root = 'C:\fixture\NetRatel'
    $script:fixture = @{}
    $script:ancestorUnsafe = $false
    function Get-LegacyAclRoot { $script:root }
    function Get-CanonicalPath([string]$Path) { return $Path }
    function Assert-OwnedPath([string]$Path, [switch]$AllowMissing, [switch]$File, [switch]$AncestorsOnly) {
        if (-not $AncestorsOnly) { throw 'Fixture expected the production ancestor guard.' }
        if ($script:ancestorUnsafe) { throw 'synthetic unsafe ancestor' }
    }
    function Test-Path([string]$LiteralPath) { $script:fixture.ContainsKey($LiteralPath) }
    function Get-Item([string]$LiteralPath, [switch]$Force) { $script:fixture[$LiteralPath].Item }
    function Get-ChildItem([string]$LiteralPath, [switch]$Force) { $script:fixture[$LiteralPath].Children }
    function Get-Acl([string]$LiteralPath, [switch]$Audit) { $script:fixture[$LiteralPath].Acl }
    function Add-Fixture([string]$Path, [bool]$Directory, [string]$Owner, [int]$Rights, [bool]$Inherited, [string]$Sid='S-1-5-32-545', [string]$Type='Allow') {
        $rule = [pscustomobject]@{ IdentityReference=[pscustomobject]@{Value=$Sid}; FileSystemRights=$Rights; IsInherited=$Inherited; AccessControlType=$Type; InheritanceFlags=3; PropagationFlags=0 }
        $acl = [pscustomobject]@{ Owner=$Owner; Rules=@($rule); Sddl='synthetic-original-descriptor' }
        $acl | Add-Member ScriptMethod GetOwner { param($type) [pscustomobject]@{Value=$this.Owner} }
        $acl | Add-Member ScriptMethod GetAccessRules { param($a,$b,$type) $this.Rules }
        $acl | Add-Member ScriptMethod GetSecurityDescriptorSddlForm { param($sections) $this.Sddl }
        $script:fixture[$Path] = [pscustomobject]@{ Item=[pscustomobject]@{FullName=$Path; PSIsContainer=$Directory; Attributes=0}; Acl=$acl; Children=@() }
    }
    Add-Fixture $script:root $true 'S-1-5-18' 0x1301BF $true
    if (@(Get-LegacyAclPlan).Count -ne 1) { throw 'Inherited Users Modify was not eligible.' }
    Invoke-LegacyAclRepair 'preview'
    if ($script:fixture.Count -ne 1 -or $script:fixture[$script:root].Acl.Sddl -ne 'synthetic-original-descriptor') { throw 'Preview changed a synthetic object.' }
    $script:fixture[$script:root].Acl.Rules[0].PropagationFlags = 2
    Expect-Refusal { & $actualOwnedPath $script:root } 'role=leaf.*untrusted modification'
    $script:fixture[$script:root].Acl.Rules[0].PropagationFlags = 0
    $script:fixture[$script:root].Item.PSIsContainer = $false
    Expect-Refusal { Get-LegacyAclPlan } 'unexpected canonical object type'
    $script:fixture[$script:root].Item.PSIsContainer = $true
    $script:fixture[$script:root].Acl.Rules[0].AccessControlType = 'Deny'
    $script:fixture[$script:root].Acl.Rules[0].IdentityReference.Value = 'S-1-5-18'
    Expect-Refusal { Get-LegacyAclPlan } 'type=Deny.*conflicting|conflicting.*type=Deny'
    $script:fixture[$script:root].Acl.Rules[0].AccessControlType = 'Allow'
    $script:fixture[$script:root].Acl.Rules[0].IdentityReference.Value = 'S-1-5-32-545'
    $script:fixture[$script:root].Acl.Rules[0].IsInherited = $false
    Expect-Refusal { Get-LegacyAclPlan } 'aceSid=S-1-5-32-545.*inherited=False.*not a reviewed|not a reviewed.*aceSid=S-1-5-32-545.*inherited=False'
    $script:fixture[$script:root].Acl.Rules[0].IsInherited = $true
    $script:fixture[$script:root].Acl.Owner = 'S-1-5-21-999'
    Expect-Refusal { Get-LegacyAclPlan } 'ownerSid=S-1-5-21-999'
    $script:fixture[$script:root].Acl.Owner = 'S-1-5-18'
    $script:fixture[$script:root].Acl.Rules[0].FileSystemRights = 0x1F01FF
    Expect-Refusal { Get-LegacyAclPlan } 'not a reviewed'
    $script:fixture[$script:root].Acl.Rules[0].FileSystemRights = 0x1301BF
    $script:fixture[$script:root].Acl.Rules[0].IdentityReference.Value = 'S-1-1-0'
    Expect-Refusal { Get-LegacyAclPlan } 'aceSid=S-1-1-0'
    $script:fixture[$script:root].Acl.Rules[0].IdentityReference.Value = 'S-1-5-32-545'
    $script:fixture[$script:root].Item.Attributes = 1024
    Expect-Refusal { Get-LegacyAclPlan } 'reparse point'
    $script:fixture[$script:root].Item.Attributes = 0
    $script:ancestorUnsafe = $true
    Expect-Refusal { Get-LegacyAclPlan } 'unsafe ancestor'
    $script:ancestorUnsafe = $false
    $unknown = $script:root + '\custom-control'
    Add-Fixture $unknown $true 'S-1-5-18' 0x1200A9 $true
    $script:fixture[$script:root].Children = @($script:fixture[$unknown].Item)
    Expect-Refusal { Get-LegacyAclPlan } 'unrecognized canonical layout'
    $script:fixture[$script:root].Children = @()
    $script:fixture[$script:root].Acl.Rules[0].FileSystemRights = 0x1200A9
    if (@(Get-LegacyAclPlan).Count) { throw 'Safe Users read was marked for repair.' }
    foreach ($known in @('Client\logs\service', 'Client\logs\remote-support-console-provider', 'Client\diagnostics', 'Client\remote-support-state.json', 'Client\remote-desktop-state.json', 'Client\remote-support-firewall-state.json', 'agent.dat.01234567890123456789012345678901.tmp')) {
        $path = $script:root + '\' + $known
        Add-Fixture $path (-not ($known -match '\.(json|tmp)$')) 'S-1-5-18' 0x1301BF $true
        $script:fixture[$script:root].Children = @($script:fixture[$path].Item)
        if (@(Get-LegacyAclPlan).Count -ne 1) { throw ('Known runtime path refused: ' + $known) }
    }
    $helper = $script:root + '\Client\logs\remote-desktop-helper'
    Add-Fixture $helper $true 'S-1-5-18' 0x1301BF $false
    $script:fixture[$script:root].Children = @($script:fixture[$helper].Item)
    # User-created logs are not inspected or used as trusted service evidence.
    $script:fixture[$helper].Children = @([pscustomobject]@{FullName=$helper+'\user-log'; PSIsContainer=$false; Attributes=1024})
    if (@(Get-LegacyAclPlan).Count) { throw 'Intentional Users helper-log Modify was changed.' }
    Write-Output 'PASS: synthetic eligibility, explicit/unknown-owner/full-control/SID/layout/reparse/ancestor refusals, safe-read and isolated-helper decisions.'
    exit 0
}
if ($env:OS -ne 'Windows_NT') { throw 'Native ACL probes require Windows.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$trustedSids += $identity.User.Value
$script:fixtureBase = Join-Path ([IO.Path]::GetTempPath()) ('netratel-acl-offline-' + [Guid]::NewGuid().ToString('N'))
$script:root = Join-Path $script:fixtureBase 'NetRatel'
$script:recovery = Join-Path $script:fixtureBase 'recovery'
# Explicit test-only canonical and backup roots; the production entry point never runs.
function Get-LegacyAclRoot { $script:root }
function Get-LegacyAclRecoveryRoot { $script:recovery }
function Get-CimInstance { return } # No service/process queries or changes in the fixture.
try {
    New-OwnedDirectory $script:fixtureBase
    $parent = Get-Acl -LiteralPath $script:fixtureBase
    $parent.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'ContainerInherit,ObjectInherit', 'InheritOnly', 'Allow'))
    $parent.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-1-0'), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'InheritOnly', 'Allow'))
    $parent.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'WriteExtendedAttributes', 'ContainerInherit,ObjectInherit', 'InheritOnly', 'Deny'))
    Set-Acl -LiteralPath $script:fixtureBase -AclObject $parent
    [void][IO.Directory]::CreateDirectory($script:root)
    $credential = Join-Path $script:root 'agent.dat'
    [IO.File]::WriteAllBytes($credential, [byte[]]@(1,2,3,255))
    $launcher = Join-Path $script:root 'remote-desktop'
    [void][IO.Directory]::CreateDirectory($launcher)
    [IO.File]::WriteAllText((Join-Path $launcher 'helper.vbs'), 'synthetic launcher')
    foreach ($logs in @('Client\logs\service', 'Client\logs\remote-support-console-provider')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $script:root $logs))
        [IO.File]::WriteAllText((Join-Path (Join-Path $script:root $logs) 'synthetic.log'), 'offline fixture')
    }
    $helper = Join-Path $script:root 'Client\logs\remote-desktop-helper'
    [void][IO.Directory]::CreateDirectory($helper)
    $helperAcl = Get-Acl -LiteralPath $helper
    $helperAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $helper -AclObject $helperAcl
    $parentBefore = (Get-Acl -LiteralPath $script:fixtureBase -Audit).Sddl
    $credentialBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($credential))
    $rootBefore = (Get-Acl -LiteralPath $script:root -Audit).Sddl
    $plan = @(Get-LegacyAclPlan)
    if (-not $plan.Count) { throw 'Native inherited fixture was not eligible.' }
    Invoke-LegacyAclRepair 'preview'
    if ((Get-Acl -LiteralPath $script:root -Audit).Sddl -ne $rootBefore -or (Test-Path $script:recovery)) { throw 'Preview mutated the fixture.' }
    Invoke-LegacyAclRepair 'apply'
    Assert-OwnedPath $script:root
    Assert-OwnedPath $credential -File
    $rootRules = @((Get-Acl -LiteralPath $script:root).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    if (-not @($rootRules | Where-Object { $_.IdentityReference.Value -eq 'S-1-1-0' -and $_.AccessControlType -eq 'Allow' -and ([int]$_.FileSystemRights -band 0x1200A9) -eq 0x1200A9 }).Count) { throw 'Repair removed unrelated inherited read access.' }
    if (-not @($rootRules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Deny' -and ([int]$_.FileSystemRights -band 0x10) }).Count) { throw 'Repair removed an unrelated inherited deny.' }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($credential)) -ne $credentialBytes) { throw 'Repair changed credential bytes.' }
    if ((Get-Acl -LiteralPath $script:fixtureBase -Audit).Sddl -ne $parentBefore) { throw 'Repair changed an ancestor.' }
    $backups = @(Get-ChildItem $script:recovery -Filter '*.json')
    if ($backups.Count -ne 1) { throw 'Missing exact descriptor snapshot.' }
    Assert-OwnedPath $backups[0].FullName -File
    $saved = @(Get-Content $backups[0].FullName -Raw | ConvertFrom-Json)
    if (($saved | Where-Object Path -eq $script:root).Sddl -ne $rootBefore) { throw 'Original descriptor snapshot did not match.' }
    $usersHelper = @((Get-Acl -LiteralPath $helper).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::WriteData) })
    if (-not $usersHelper.Count) { throw 'Intentional helper-log Users write access was removed.' }
    $after = (Get-Acl -LiteralPath $script:root -Audit).Sddl
    Invoke-LegacyAclRepair 'apply'
    if ((Get-Acl -LiteralPath $script:root -Audit).Sddl -ne $after -or @(Get-ChildItem $script:recovery -Filter '*.json').Count -ne 1) { throw 'Repeated repair was not a no-op.' }
    $explicit = Get-Acl -LiteralPath $script:root
    $explicit.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Write', 'Allow'))
    Set-Acl -LiteralPath $script:root -AclObject $explicit
    Expect-Refusal { Invoke-LegacyAclRepair 'apply' } 'not a reviewed'
    Write-Output 'PASS: native inherited repair, preview, safe snapshots, credential preservation, no ancestor mutation, helper access, idempotence and explicit-write refusal.'
}
finally { if (Test-Path -LiteralPath $script:fixtureBase) { Remove-Item -LiteralPath $script:fixtureBase -Recurse -Force } }
