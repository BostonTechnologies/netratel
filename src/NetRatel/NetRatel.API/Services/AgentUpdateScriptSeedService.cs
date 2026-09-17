using NetRatel.Application.Scripts;

namespace NetRatel.API.Services;

public sealed class AgentUpdateScriptSeedService(IServiceScopeFactory scopeFactory, ILogger<AgentUpdateScriptSeedService> logger) : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<AgentUpdateScriptSeedService> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var scripts = scope.ServiceProvider.GetRequiredService<IScriptService>();
        await UpsertAsync(scripts, WindowsScript, cancellationToken).ConfigureAwait(false);
        await UpsertAsync(scripts, LinuxScript, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task UpsertAsync(IScriptService scripts, SeedScript seed, CancellationToken ct)
    {
        var existing = (await scripts.ListAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.FolderPath, seed.FolderPath, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(x.Name, seed.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            await scripts.CreateAsync(new CreateScriptCommand(seed.Name, seed.FolderPath, seed.Description, seed.Content, seed.ScriptType, ManifestRaw: null), ct).ConfigureAwait(false);
            _logger.LogInformation("Seeded NetRatel agent update script {Folder}/{Name}.", seed.FolderPath, seed.Name);
            return;
        }

        if (!string.Equals(existing.Content, seed.Content, StringComparison.Ordinal) ||
            !string.Equals(existing.ScriptType, seed.ScriptType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Description, seed.Description, StringComparison.Ordinal))
        {
            await scripts.UpdateAsync(new UpdateScriptCommand(existing.Id, seed.Name, seed.FolderPath, seed.Description, seed.Content, seed.ScriptType, ManifestRaw: null), ct).ConfigureAwait(false);
            _logger.LogInformation("Updated seeded NetRatel agent update script {Folder}/{Name}.", seed.FolderPath, seed.Name);
        }
    }

    private sealed record SeedScript(string Name, string FolderPath, string Description, string ScriptType, string Content);

    private static readonly SeedScript WindowsScript = new(
        "Update Client To Latest",
        "/Windows/NetRatel",
        "Repair or roll forward a Windows NetRatel service client using an enrollment-authenticated artifact download.",
        "PowerShell",
        """
#| NetRatel-MANIFEST
{
  "params": [
    { "name": "ApiBase", "type": "string", "required": true, "default": "https://netratel.example.invalid" },
    { "name": "TenantId", "type": "int", "required": true },
    { "name": "EnrollmentCode", "type": "string", "required": true },
    { "name": "Runtime", "type": "string", "required": false, "default": "win-x64" },
    { "name": "Version", "type": "string", "required": false, "default": "latest" }
  ]
}
#| END
param(
    [Parameter(Mandatory = $true)] [string] $ApiBase,
    [Parameter(Mandatory = $true)] [int] $TenantId,
    [Parameter(Mandatory = $true)] [string] $EnrollmentCode,
    [string] $Runtime = "win-x64",
    [string] $Version = "latest"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$ApiBase = $ApiBase.TrimEnd("/")
$RootDir = Join-Path $env:ProgramFiles "NetRatel\Client"
$StateDir = Join-Path $env:ProgramData "NetRatel\update"
$LogDir = Join-Path $env:ProgramData "NetRatel\logs"
$UpdaterDir = Join-Path $RootDir "updater"
$VersionsDir = Join-Path $RootDir "versions"
$StagingDir = Join-Path $RootDir "staging"
$FailedDir = Join-Path $RootDir "failed"
$tempDir = Join-Path $env:TEMP "netratel_update_$([Guid]::NewGuid())"

try {
    $tls12 = [System.Enum]::Parse([System.Net.SecurityProtocolType], "Tls12")
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor $tls12
}
catch {
    # Older frameworks may not expose Tls12; continue and let download report the real error.
}

function Get-NetRatelSha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $getFileHash = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($getFileHash) {
        return ((Get-FileHash -Path $Path -Algorithm SHA256).Hash).ToLowerInvariant()
    }

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read
    )

    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha.ComputeHash($stream)
            return ([System.BitConverter]::ToString($hashBytes)).Replace("-", "").ToLowerInvariant()
        }
        finally {
            if ($sha -is [System.IDisposable]) {
                $sha.Dispose()
            }
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Expand-NetRatelZip {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ZipPath,

        [Parameter(Mandatory = $true)]
        [string] $DestinationPath
    )

    $expandArchive = Get-Command Expand-Archive -ErrorAction SilentlyContinue
    if ($expandArchive) {
        Expand-Archive -Path $ZipPath -DestinationPath $DestinationPath -Force
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $DestinationPath)
}

try {
    New-Item -ItemType Directory -Path $tempDir, $RootDir, $StateDir, $LogDir, $UpdaterDir, $VersionsDir, $StagingDir, $FailedDir -Force | Out-Null
    $headers = @{ "X-NetRatel-Tenant-Id" = [string]$TenantId; "X-NetRatel-Enrollment-Code" = $EnrollmentCode }
    if ($Version -eq "latest") {
        $latest = Invoke-RestMethod -Headers $headers -Uri "$ApiBase/api/v1/client-artifacts/$Runtime/latest" -Method GET
        $Version = [string]$latest.version
        $expectedSha = [string]$latest.sha256
    }
    else {
        $expectedSha = $null
    }

    if ([string]::IsNullOrWhiteSpace($Version)) { throw "Could not resolve client artifact version." }
    $zipPath = Join-Path $tempDir "netratel-client.zip"
    Invoke-WebRequest -Headers $headers -Uri "$ApiBase/api/v1/client-artifacts/$Runtime/$Version/onboarding-download" -OutFile $zipPath -UseBasicParsing
    if ($expectedSha) {
        $actualSha = Get-NetRatelSha256Hex -Path $zipPath
        if ($actualSha -ne $expectedSha.ToLowerInvariant()) { throw "Downloaded artifact failed SHA-256 verification." }
    }

    $targetDir = Join-Path $VersionsDir $Version
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $targetDir
    $exe = @(Join-Path $targetDir "NetRatel.Client.exe"; Join-Path $targetDir "NetRatel.Client.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) { throw "Client executable was not found in artifact." }

    $updaterSource = Join-Path $targetDir "updater\netratel-update.ps1"
    if (Test-Path $updaterSource) { Copy-Item -Path $updaterSource -Destination (Join-Path $UpdaterDir "netratel-update.ps1") -Force }

    foreach ($name in @("NetRatel.Client", "NetRatel.Update")) {
        $existing = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($existing) {
            if ($existing.Status -ne "Stopped") { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
            sc.exe delete $name | Out-Null
            Start-Sleep -Seconds 2
        }
    }

    New-Service -Name "NetRatel.Client" -BinaryPathName "`"$exe`" --service" -DisplayName "NetRatel Client" -StartupType Automatic
    New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\NetRatel.Client" -Name Environment -PropertyType MultiString -Value @(
        "NetRatel_CLIENT_LOG_DIR=$LogDir",
        "NetRatelCLIENT__Transport__Mode=AkkaPresence",
        "NetRatelCLIENT__Gateway__Endpoint=$ApiBase",
        "NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka",
        "NetRatelCLIENT__Gateway__TelemetryShadowEnabled=true",
        "NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true",
        "NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true",
        "NetRatelCLIENT__Gateway__JobAuthorityEnabled=true",
        "NetRatelCLIENT__Gateway__ControlGatewayEnabled=true",
        "NetRatelCLIENT__Gateway__FileGatewayEnabled=true",
        "NetRatelCLIENT__Gateway__LogGatewayEnabled=true",
        "NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true",
        "NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true",
        "NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true"
    ) -Force | Out-Null
    New-Service -Name "NetRatel.Update" -BinaryPathName "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$UpdaterDir\netratel-update.ps1`"" -DisplayName "NetRatel Client Updater" -StartupType Manual
    Start-Service -Name "NetRatel.Client"
    Start-Sleep -Seconds 5
    $service = Get-Service -Name "NetRatel.Client" -ErrorAction Stop
    if ($service.Status -ne "Running") { throw "NetRatel.Client status is $($service.Status)." }
    Write-Host "NetRatel Windows client updated to $Version."
}
finally {
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
}
""");

    private static readonly SeedScript LinuxScript = new(
        "Update Client To Latest",
        "/Linux/NetRatel",
        "Repair or roll forward a Linux NetRatel service client using an enrollment-authenticated artifact download.",
        "Bash",
        """
#| NetRatel-MANIFEST
{
  "params": [
    { "name": "ApiBase", "type": "string", "required": true, "default": "https://netratel.example.invalid" },
    { "name": "TenantId", "type": "int", "required": true },
    { "name": "EnrollmentCode", "type": "string", "required": true },
    { "name": "Runtime", "type": "string", "required": false, "default": "linux-x64" },
    { "name": "Version", "type": "string", "required": false, "default": "latest" }
  ]
}
#| END
#!/usr/bin/env bash
set -euo pipefail

ApiBase="${ApiBase:?ApiBase is required}"
TenantId="${TenantId:?TenantId is required}"
EnrollmentCode="${EnrollmentCode:?EnrollmentCode is required}"
Runtime="${Runtime:-linux-x64}"
Version="${Version:-latest}"
ApiBase="${ApiBase%/}"
RootDir="${NetRatel_ROOT:-/opt/netratel/client}"
StateDir="${NetRatel_STATE:-/var/lib/netratel/update}"
BundleExtractDir="${NetRatel_BUNDLE_EXTRACT_DIR:-/var/lib/netratel/bundle}"
UpdaterDir="${RootDir}/updater"
VersionsDir="${RootDir}/versions"
StagingDir="${RootDir}/staging"
FailedDir="${RootDir}/failed"
TempDir="$(mktemp -d)"
cleanup() { rm -rf "$TempDir"; }
trap cleanup EXIT

if [ "${EUID}" -ne 0 ]; then
  echo "This NetRatel systemd installer must be run as root." >&2
  exit 1
fi
for required_command in curl unzip sha256sum systemctl flock python3 install; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    echo "Required command is unavailable: $required_command" >&2
    exit 1
  fi
done
if [ "$Version" = "latest" ] && ! command -v python3 >/dev/null 2>&1; then
  echo "Required command is unavailable: python3" >&2
  exit 1
fi

mkdir -p "$RootDir" "$StateDir" "$BundleExtractDir" "$UpdaterDir" "$VersionsDir" "$StagingDir" "$FailedDir"
chmod 0700 "$StateDir"
exec 9>"$StateDir/update.lock"
if ! flock -n 9; then
  echo "Another NetRatel installer or updater is already running." >&2
  exit 75
fi
if [ "$Version" = "latest" ]; then
  latest_json="$(curl -fsSL -H "X-NetRatel-Tenant-Id: $TenantId" -H "X-NetRatel-Enrollment-Code: $EnrollmentCode" "$ApiBase/api/v1/client-artifacts/$Runtime/latest")"
  Version="$(printf '%s' "$latest_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["version"])')"
  expected_sha="$(printf '%s' "$latest_json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("sha256") or "")')"
else
  expected_sha=""
fi

zip_path="$TempDir/netratel-client.zip"
curl -fsSL -H "X-NetRatel-Tenant-Id: $TenantId" -H "X-NetRatel-Enrollment-Code: $EnrollmentCode" -o "$zip_path" "$ApiBase/api/v1/client-artifacts/$Runtime/$Version/onboarding-download"
if [ -n "$expected_sha" ]; then
  actual_sha="$(sha256sum "$zip_path" | awk '{print $1}')"
  if [ "${actual_sha,,}" != "${expected_sha,,}" ]; then
    echo "Downloaded artifact failed SHA-256 verification." >&2
    exit 1
  fi
fi

target_dir="$VersionsDir/$Version"
if ! [[ "$Version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
  echo "Artifact version is not a canonical semantic version." >&2
  exit 1
fi
if [ -e "$target_dir" ]; then
  echo "Immutable target version $Version already exists; refusing to replace it." >&2
  exit 1
fi
stage_dir="$(mktemp -d "$StagingDir/.$Version.XXXXXX")"
unzip -o "$zip_path" -d "$stage_dir" >/dev/null
python3 - "$stage_dir/netratel-client-manifest.json" "$Version" "$Runtime" <<'PY'
import json, os, sys
with open(sys.argv[1], encoding="utf-8-sig") as f: manifest=json.load(f)
if (manifest.get("schema") != "netratel.client.manifest.v1" or
    manifest.get("product") != "NetRatel.Client" or
    manifest.get("version") != sys.argv[2] or
    manifest.get("runtimeId") != sys.argv[3] or
    not os.path.isfile(os.path.join(os.path.dirname(sys.argv[1]), manifest.get("executable", "")))):
    raise SystemExit("Artifact manifest does not match the requested version and runtime.")
PY
client_exe="$stage_dir/NetRatel.Client"
if [ ! -f "$client_exe" ]; then client_exe="$target_dir/NetRatel.Client"; fi
if [ ! -f "$client_exe" ]; then echo "Client executable was not found in artifact." >&2; exit 1; fi
chmod +x "$client_exe"
if [ -f "$RootDir/agent.dat" ] || [ -f "/var/lib/netratel/agent.dat" ]; then
  set +e
  DOTNET_BUNDLE_EXTRACT_BASE_DIR="$BundleExtractDir" "$client_exe" --auth-check >/dev/null 2>&1
  auth_check_exit=$?
  set -e
  case "$auth_check_exit" in
    0|10|12) ;;
    *) echo "Existing NetRatel identity could not be validated (auth check exit $auth_check_exit); refusing to replace the active client." >&2; exit "$auth_check_exit" ;;
  esac
fi
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$BundleExtractDir" "$client_exe" --enroll "$EnrollmentCode" --api "$ApiBase"
previous_target="$(readlink -f "$RootDir/current" 2>/dev/null || true)"
activated=false
rollback_install() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ] && [ "$activated" = true ]; then
    systemctl stop netratel-client.service || true
    if [ -n "$previous_target" ] && [ -d "$previous_target" ]; then
      ln -sfn "$previous_target" "$RootDir/current.rollback"
      mv -Tf "$RootDir/current.rollback" "$RootDir/current"
      systemctl start netratel-client.service || true
    else
      rm -f "$RootDir/current"
    fi
  fi
  exit "$status"
}
trap rollback_install EXIT
if systemctl is-active --quiet netratel-client.service; then
  systemctl stop netratel-client.service
  if systemctl is-active --quiet netratel-client.service; then
    echo "NetRatel client service did not stop; no files were activated." >&2
    exit 1
  fi
fi
mv "$stage_dir" "$target_dir"
if [ -f "$target_dir/updater/netratel-update.sh" ]; then install -m 0755 "$target_dir/updater/netratel-update.sh" "$UpdaterDir/.netratel-update.sh.$$"; mv -f "$UpdaterDir/.netratel-update.sh.$$" "$UpdaterDir/netratel-update.sh"; fi
cat >"$RootDir/netratel-client-start.sh" <<SH
#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$RootDir"
if [ -x "$RootDir/current/NetRatel.Client" ]; then
  exec "$RootDir/current/NetRatel.Client" --service
fi
if [ -x "$RootDir/current/NetRatel.Client" ]; then
  exec "$RootDir/current/NetRatel.Client" --service
fi
echo "No NetRatel client executable found in $RootDir/current" >&2
exit 78
SH
chmod 0755 "$RootDir/netratel-client-start.sh"
ln -sfn "$target_dir" "$RootDir/current.next"
mv -Tf "$RootDir/current.next" "$RootDir/current"
activated=true

cat >/etc/systemd/system/netratel-client.service <<UNIT
[Unit]
Description=NetRatel Client
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$RootDir/netratel-client-start.sh
Restart=always
RestartSec=10
RestartPreventExitStatus=78
Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$BundleExtractDir
Environment=NetRatel_CLIENT_LOG_DIR=/var/lib/netratel/logs
Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence
Environment=NetRatelCLIENT__Gateway__Endpoint=$ApiBase
Environment=NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka
Environment=NetRatelCLIENT__Gateway__TelemetryShadowEnabled=true
Environment=NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true
Environment=NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true
Environment=NetRatelCLIENT__Gateway__JobAuthorityEnabled=true
Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true
Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=true
Environment=NetRatelCLIENT__Gateway__LogGatewayEnabled=true
Environment=NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true
Environment=NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true
Environment=NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true

[Install]
WantedBy=multi-user.target
UNIT

cat >/etc/systemd/system/netratel-update.service <<UNIT
[Unit]
Description=NetRatel Client Updater

[Service]
Type=oneshot
ExecStart=$UpdaterDir/netratel-update.sh
UNIT

systemctl daemon-reload
if systemctl cat sto-client.service >/dev/null 2>&1; then
  systemctl disable --now sto-client.service
  if systemctl is-active --quiet sto-client.service; then
    echo "Legacy sto-client.service remains active; refusing a competing NetRatel service." >&2
    exit 1
  fi
fi
systemctl enable netratel-client.service
if ! systemctl start netratel-client.service || ! systemctl is-active --quiet netratel-client.service; then
  echo "NetRatel client service failed to start. Recent journal output:" >&2
  journalctl -u netratel-client.service -n 80 --no-pager >&2 || true
  exit 1
fi
echo "NetRatel Linux client updated to $Version."
""");
}
