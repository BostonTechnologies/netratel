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

$wslPath = Join-Path $env:SystemRoot 'System32\wsl.exe'
$wslArguments = @(
    '--distribution'
    $env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION
    '--user'
    'root'
    '--'
) + @($Arguments)

& $wslPath @wslArguments
exit $LASTEXITCODE
