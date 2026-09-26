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

$wslPath = Join-Path $env:SystemRoot 'System32\wsl.exe'
$wslArguments = @(
    '--distribution'
    $env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION
    '--user'
    'root'
    '--cd'
    $env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE
    '--'
    'docker'
) + @($Arguments)

& $wslPath @wslArguments
exit $LASTEXITCODE
