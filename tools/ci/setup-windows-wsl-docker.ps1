$ErrorActionPreference = 'Stop'

$distribution = "NetRatelDocker-$($env:GITHUB_RUN_ID)"
$rootfsUrl = 'https://cloud-images.ubuntu.com/wsl/releases/24.04/20240423/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz'
$rootfsSha256 = '8251e27ffff381a4af5f41dcb94d867de3e0d9774a9241908ab34555d99315ea'
$workspace = $env:GITHUB_WORKSPACE
if ($workspace -notmatch '^(?<drive>[A-Za-z]):[\\/](?<rest>.*)$') {
    throw "The GitHub workspace is not a local Windows drive path: $workspace"
}
$rootfsPath = Join-Path $env:RUNNER_TEMP 'ubuntu-noble-wsl.rootfs.tar.gz'
$distributionDirectory = Join-Path $workspace '.ci-wsl-docker-root'

@(
    "NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION=$distribution"
    "NETRATEL_LOCAL_FIRST_WSL_INSTALL_DIRECTORY=$distributionDirectory"
) | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append

New-Item -ItemType Directory -Path $distributionDirectory -Force | Out-Null
Invoke-WebRequest -Uri $rootfsUrl -OutFile $rootfsPath
$actualRootfsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $rootfsPath).Hash.ToLowerInvariant()
if ($actualRootfsSha256 -ne $rootfsSha256) {
    throw "Ubuntu WSL rootfs checksum mismatch: expected $rootfsSha256, got $actualRootfsSha256."
}

& wsl.exe --import $distribution $distributionDirectory $rootfsPath --version 2
if ($LASTEXITCODE -ne 0) {
    throw "WSL distribution import failed with exit code $LASTEXITCODE."
}

$dockerSetup = @'
set -eu
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y ca-certificates curl openssl
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
printf '%s\n' "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
service docker start
for attempt in $(seq 1 60); do
  if [ "$(docker info --format '{{.OSType}}' 2>/dev/null || true)" = linux ]; then
    exit 0
  fi
  sleep 1
done
cat /var/log/docker.log 2>/dev/null || true
exit 1
'@

$dockerSetup = $dockerSetup.Replace("`r`n", "`n").Replace("`r", "")
$encodedDockerSetup = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($dockerSetup))
& wsl.exe --distribution $distribution --user root -- bash -lc "echo $encodedDockerSetup | base64 -d | bash"
if ($LASTEXITCODE -ne 0) {
    throw "Linux Docker engine setup failed with exit code $LASTEXITCODE."
}

$wslPath = Join-Path $env:SystemRoot 'System32\wsl.exe'
$previousWslEnvironment = $env:WSLENV
try {
    # The keepalive needs no host environment values; in particular, do not
    # import the test credentials listed in WSLENV into its Linux process.
    $env:WSLENV = ''
    $keepaliveProcess = Start-Process -FilePath $wslPath `
        -ArgumentList @('--distribution', $distribution, '--user', 'root', '--', 'sleep', 'infinity') `
        -WindowStyle Hidden -PassThru
}
finally {
    if ($null -eq $previousWslEnvironment) {
        Remove-Item Env:WSLENV -ErrorAction SilentlyContinue
    }
    else {
        $env:WSLENV = $previousWslEnvironment
    }
}

$keepaliveProcess.Refresh()
if ($keepaliveProcess.HasExited) {
    throw "WSL keepalive exited immediately with code $($keepaliveProcess.ExitCode)."
}
$keepaliveStartTimeUtcTicks = $keepaliveProcess.StartTime.ToUniversalTime().Ticks
$keepaliveState = @(
    "NETRATEL_LOCAL_FIRST_WSL_KEEPALIVE_PID=$($keepaliveProcess.Id)"
    "NETRATEL_LOCAL_FIRST_WSL_KEEPALIVE_START_TIME_UTC_TICKS=$($keepaliveStartTimeUtcTicks.ToString([Globalization.CultureInfo]::InvariantCulture))"
)
$keepaliveState | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append

$keepaliveValidated = $false
$keepaliveValidationDeadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    $keepaliveProcess.Refresh()
    if ($keepaliveProcess.HasExited) {
        throw "WSL keepalive exited before distribution $distribution was confirmed running (code $($keepaliveProcess.ExitCode))."
    }

    $runningDistributions = & $wslPath --list --running --quiet 2>$null
    $runningDistributionsExitCode = $LASTEXITCODE
    $runningDistributionNames = @($runningDistributions | ForEach-Object {
        $_.ToString().TrimStart([char]0xFEFF).Trim()
    })
    $keepaliveProcess.Refresh()
    if ($keepaliveProcess.HasExited) {
        throw "WSL keepalive exited before distribution $distribution was confirmed running (code $($keepaliveProcess.ExitCode))."
    }
    if ($runningDistributionsExitCode -eq 0 -and $runningDistributionNames -contains $distribution) {
        $keepaliveValidated = $true
        break
    }

    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $keepaliveValidationDeadline)

if (-not $keepaliveValidated) {
    throw "WSL keepalive process $($keepaliveProcess.Id) did not keep distribution $distribution running."
}

$workspaceDrive = $Matches.drive
$workspaceRest = $Matches.rest
$workspaceLinux = "/mnt/$($workspaceDrive.ToLowerInvariant())/$($workspaceRest -replace '\\', '/')"
$dockerShim = Join-Path $workspace 'tools\ci\windows-wsl-docker.cmd'
$wslCommandShim = Join-Path $workspace 'tools\ci\windows-wsl-command.cmd'
$wslEnvironment = @(
    'POSTGRES_PASSWORD'
    'NETRATEL_AUTHENTICATION_MODE'
    'NETRATEL_PUBLIC_ORIGIN'
    'NETRATEL_PUBLIC_HOST'
    'NETRATEL_INSTALL_LINK_PUBLIC_ORIGIN'
    'NETRATEL_HTTPS_BIND_ADDRESS'
    'NETRATEL_HTTPS_PORT'
    'NETRATEL_HTTPS_CERTIFICATE'
    'NETRATEL_HTTPS_PRIVATE_KEY'
    'NETRATEL_AGENT_AUTH_PRIVATE_KEY'
    'NETRATEL_WEB_PORT'
    'NETRATEL_LOCAL_FIRST_WSL_ARGUMENTS'
) -join ':'

@(
    "NETRATEL_LOCAL_FIRST_WSL_WORKSPACE=$workspaceLinux"
    "NETRATEL_LOCAL_FIRST_DOCKER_COMMAND=$dockerShim"
    "NETRATEL_LOCAL_FIRST_WSL_COMMAND=$wslCommandShim"
    "WSLENV=$wslEnvironment"
) | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append

$env:NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION = $distribution
$env:NETRATEL_LOCAL_FIRST_WSL_INSTALL_DIRECTORY = $distributionDirectory
$env:NETRATEL_LOCAL_FIRST_WSL_KEEPALIVE_PID = [string]$keepaliveProcess.Id
$env:NETRATEL_LOCAL_FIRST_WSL_KEEPALIVE_START_TIME_UTC_TICKS = $keepaliveStartTimeUtcTicks.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:NETRATEL_LOCAL_FIRST_WSL_WORKSPACE = $workspaceLinux
$env:NETRATEL_LOCAL_FIRST_DOCKER_COMMAND = $dockerShim
$env:NETRATEL_LOCAL_FIRST_WSL_COMMAND = $wslCommandShim
$env:WSLENV = $wslEnvironment

Write-Host "Configured Linux Docker engine in WSL distribution $distribution."
