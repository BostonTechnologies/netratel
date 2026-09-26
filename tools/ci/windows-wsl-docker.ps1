[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION)) {
    Write-Error 'NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION is required for the Windows WSL Docker shim.'
    exit 2
}
if ([string]::IsNullOrWhiteSpace($env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE)) {
    Write-Error 'NETRATEL_LOCAL_FIRST_WSL_WORKSPACE is required for the Windows WSL Docker shim.'
    exit 2
}
if ($Arguments.Count -eq 0) {
    Write-Error 'At least one Docker command argument is required for the Windows WSL Docker shim.'
    exit 2
}

$wslPath = Join-Path $env:SystemRoot 'System32\wsl.exe'
$dockerArguments = @('docker') + @($Arguments)
$env:NETRATEL_LOCAL_FIRST_WSL_ARGUMENTS = @($dockerArguments | ForEach-Object {
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))
    }) -join ' '
$wslArguments = @(
    '--distribution'
    $env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION
    '--user'
    'root'
    '--cd'
    $env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE
    '--'
    'bash'
    "$env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE/tools/ci/windows-wsl-dispatch.sh"
)

& $wslPath @wslArguments
exit $LASTEXITCODE
