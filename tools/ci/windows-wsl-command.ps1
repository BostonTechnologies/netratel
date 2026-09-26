[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION)) {
    Write-Error 'NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION is required for the Windows WSL command shim.'
    exit 2
}
if ([string]::IsNullOrWhiteSpace($env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE)) {
    Write-Error 'NETRATEL_LOCAL_FIRST_WSL_WORKSPACE is required for the Windows WSL command shim.'
    exit 2
}
if ($Arguments.Count -eq 0) {
    Write-Error 'At least one Linux command argument is required for the Windows WSL command shim.'
    exit 2
}

$wslPath = Join-Path $env:SystemRoot 'System32\wsl.exe'
$env:NETRATEL_LOCAL_FIRST_WSL_ARGUMENTS = @($Arguments | ForEach-Object {
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))
    }) -join ' '
$dispatchPath = "$env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE/tools/ci/windows-wsl-dispatch.sh"
$wslArguments = @(
    '--distribution'
    $env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION
    '--user'
    'root'
    '--'
    'bash'
    $dispatchPath
)

& $wslPath @wslArguments
exit $LASTEXITCODE
