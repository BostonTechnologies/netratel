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
function ConvertTo-BashArgument {
    param([AllowEmptyString()][string] $Value)

    $singleQuote = [string][char]39
    $escaped = $Value.Replace($singleQuote, $singleQuote + [char]92 + $singleQuote + [char]92 + $singleQuote)
    return '{0}{1}{0}' -f $singleQuote, $escaped
}

$quotedArguments = @($Arguments | ForEach-Object { ConvertTo-BashArgument $_ })
$command = "docker $($quotedArguments -join ' ')"
$wslArguments = @(
    '--distribution'
    $env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION
    '--user'
    'root'
    '--cd'
    $env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE
    '--'
    'bash'
    '-lc'
    $command
)

& $wslPath @wslArguments
exit $LASTEXITCODE
