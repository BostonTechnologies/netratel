#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallDirectory,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This sidecar is pinned for Windows x64 endpoints.' }
if (-not [IO.Path]::IsPathFullyQualified($InstallDirectory)) { throw 'InstallDirectory must be an absolute path.' }
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'The pinned sidecar requires Windows x64.'
}

# This third-party pin is independent of the NetRatel product version.
$driverVersion = '0.38.0'
$archiveSha256 = '5edaa74d0afa1b55a9668dbc4f32736507bd2bd4d75b18ca1dc8881bb12402b5'
$executableSha256 = 'ffa58d88779ee219304b0c307f4cfa970fe822f830c6ecf9254eda5c29e22bf2'
$sourceCommit = '85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19'
$sourceUrl = 'https://github.com/trycua/cua/releases/download/cua-driver-rs-v0.38.0/cua-driver-rs-0.38.0-windows-x86_64-binary.zip'
$directory = [IO.Path]::GetFullPath($InstallDirectory)
if ($directory.TrimEnd('\') -eq [IO.Path]::GetPathRoot($directory).TrimEnd('\')) { throw 'Choose a dedicated sidecar directory, not a drive root.' }
$executable = Join-Path $directory 'cua-driver.exe'
$receiptPath = Join-Path $directory 'netratel-cua-driver.json'

if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'The installation receipt is missing; refusing to remove an unmanaged sidecar.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.schema -ne 'netratel.cua-driver.install.v1' -or $receipt.executableSha256 -ne $executableSha256) {
        throw 'The sidecar receipt does not match this managed installation.'
    }
    if (Test-Path -LiteralPath $executable -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $executableSha256) {
            throw 'The installed executable was modified; preserve it for inspection and remove it manually.'
        }
    }
    if ($PSCmdlet.ShouldProcess($directory, 'Remove the managed Cua Driver executable and receipt')) {
        if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable -Force }
        Remove-Item -LiteralPath $receiptPath -Force
        if ((Get-ChildItem -LiteralPath $directory -Force | Measure-Object).Count -eq 0) { Remove-Item -LiteralPath $directory }
    }
    return
}

if (-not $PSCmdlet.ShouldProcess($directory, "Install verified Cua Driver $driverVersion")) { return }
if (Test-Path -LiteralPath $directory) {
    if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'InstallDirectory must not be a reparse point.' }
    $unmanaged = @(Get-ChildItem -LiteralPath $directory -Force | Where-Object { $_.Name -notin @('cua-driver.exe', 'netratel-cua-driver.json') })
    if ($unmanaged.Count -ne 0) { throw 'Choose a dedicated directory; the selected directory contains unmanaged files.' }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('netratel-cua-install-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    $archivePath = Join-Path $temporary 'driver.zip'
    Invoke-WebRequest -Uri $sourceUrl -OutFile $archivePath -MaximumRedirection 5
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveSha256) {
        throw 'The downloaded archive does not match the pinned SHA-256.'
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'cua-driver.exe' })
        if ($entries.Count -ne 1) { throw 'The pinned archive must contain exactly one root cua-driver.exe.' }
        $stagedExecutable = Join-Path $temporary 'cua-driver.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $stagedExecutable, $false)
    } finally { $archive.Dispose() }
    if ((Get-FileHash -LiteralPath $stagedExecutable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $executableSha256) {
        throw 'The extracted executable does not match the pinned SHA-256.'
    }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    # Only Administrators and SYSTEM may modify the installed executable; interactive users need read/execute.
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagate = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', $inherit, $propagate, $allow))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'ReadAndExecute', $inherit, $propagate, $allow))
    Set-Acl -LiteralPath $directory -AclObject $acl
    if (Test-Path -LiteralPath $executable) {
        if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $executableSha256) {
            throw 'A different executable already exists. Stop the client and inspect/remove that installation before replacing it.'
        }
    } else {
        Copy-Item -LiteralPath $stagedExecutable -Destination $executable
    }
    [ordered]@{
        schema = 'netratel.cua-driver.install.v1'
        version = $driverVersion
        sourceCommit = $sourceCommit
        sourceUrl = $sourceUrl
        archiveSha256 = $archiveSha256
        executableSha256 = $executableSha256
    } | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    Write-Output "Installed $executable"
    Write-Output 'Set ComputerUse:Enabled=true and ComputerUse:CuaDriverPath to this executable in the client service configuration, then restart the client service.'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
