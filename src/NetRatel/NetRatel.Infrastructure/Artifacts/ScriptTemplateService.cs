using NetRatel.Application.Artifacts;

namespace NetRatel.Infrastructure.Artifacts;

public sealed class ScriptTemplateService : IScriptTemplateService
{
    public string Build(DeploymentScriptTemplateRequest request)
    {
        if (request.RuntimeId.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
            return BuildBash(request);
        if (request.RuntimeId.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
            return BuildMacBash(request);
        return BuildPowerShell(request);
    }

    public string GetFileExtension(string runtimeId)
        => runtimeId.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ||
           runtimeId.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? "sh" : "ps1";

    private static string BuildPowerShell(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var enrollmentBlock = request.InstallAsService
            ? string.Empty
            : $$"""

    & $exe --enroll $EnrollmentCode --api $ApiBase {{silentArg}}
    if ($LASTEXITCODE -ne 0) {
        throw "Enrollment failed with exit code $LASTEXITCODE."
    }
""";

        var serviceBlock = request.InstallAsService
            ? $$"""
            $enrollmentPayload = @{
                schema = "netratel.enroll.v1"
                tenantId = $TenantId
                enrollmentCode = $EnrollmentCode
                issuer = $ApiBase
                createdAtUtc = (Get-Date).ToUniversalTime().ToString("O")
                validToUtc = "{{request.ValidToUtc.UtcDateTime:O}}"
            } | ConvertTo-Json -Depth 4
            Set-Content -Path (Join-Path $targetDir "netratel.enroll.json") -Value $enrollmentPayload -Encoding UTF8

            $updaterSource = Join-Path $targetDir "updater\netratel-update.ps1"
            if (Test-Path $updaterSource) {
                Copy-Item -Path $updaterSource -Destination (Join-Path $UpdaterDir "netratel-update.ps1") -Force
            }

            Write-Host "Installing Windows services..."
            $serviceName = "NetRatel.Client"
            $updateServiceName = "NetRatel.Update"
            foreach ($name in @($serviceName, $updateServiceName)) {
                $existingService = Get-Service -Name $name -ErrorAction SilentlyContinue
                if ($existingService) {
                    if ($existingService.Status -ne "Stopped") {
                        Stop-Service -Name $name -Force
                    }
                    sc.exe delete $name | Out-Null
                    Start-Sleep -Seconds 2
                }
            }
            New-Service `
                -Name $serviceName `
                -BinaryPathName "`"$exe`" --service" `
                -DisplayName "NetRatel Client" `
                -StartupType Automatic
            New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -PropertyType MultiString -Value @(
                "NetRatel_CLIENT_LOG_DIR=$LogDir",
                "NetRatelCLIENT__Transport__Mode=AkkaPresence",
                "NetRatelCLIENT__Gateway__Endpoint=$ApiBase",
                "NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka",
                "NetRatelCLIENT__Gateway__TelemetryShadowEnabled=true",
                "NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true",
                "NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true",
                "NetRatelCLIENT__Gateway__JobAuthorityEnabled=true",
                "NetRatelCLIENT__Gateway__FileGatewayEnabled=true",
                "NetRatelCLIENT__Gateway__LogGatewayEnabled=true",
                "NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true",
                "NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true",
                "NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true"
            ) -Force | Out-Null
            New-Service `
                -Name $updateServiceName `
                -BinaryPathName "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$UpdaterDir\netratel-update.ps1`"" `
                -DisplayName "NetRatel Client Updater" `
                -StartupType Manual
            try {
                Start-Service -Name $serviceName -ErrorAction Stop
                Start-Sleep -Seconds 3
                $service = Get-Service -Name $serviceName -ErrorAction Stop
                if ($service.Status -ne "Running") {
                    throw "Service status is $($service.Status)."
                }
            }
            catch {
                Write-Host "NetRatel.Client service failed to start: $($_.Exception.Message)"
                $logDir = $LogDir
                if (Test-Path $logDir) {
                    $latestLog = Get-ChildItem -Path $logDir -Filter "netratel-client-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                    if ($latestLog) {
                        Write-Host "Latest NetRatel.Client log tail ($($latestLog.FullName)):"
                        Get-Content -Path $latestLog.FullName -Tail 80
                    }
                }
                throw
            }
            """
            : string.Empty;

        return $$"""
# ================================
# NetRatel Automated Deployment Script
# ================================

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$ApiBase = "{{request.ApiBaseUrl}}"
$TenantId = {{request.TenantId}}
$EnrollmentCode = "{{request.EnrollmentCode}}"
$Runtime = "{{request.RuntimeId}}"
$Version = "{{(string.IsNullOrWhiteSpace(request.ArtifactVersion) ? "latest" : request.ArtifactVersion)}}"
$ExpectedSha256 = "{{(string.IsNullOrWhiteSpace(request.ArtifactSha256) ? string.Empty : request.ArtifactSha256)}}"
$RootDir = Join-Path $env:ProgramFiles "NetRatel\Client"
$StateDir = Join-Path $env:ProgramData "NetRatel\update"
$LogDir = Join-Path $env:ProgramData "NetRatel\logs"
$UpdaterDir = Join-Path $RootDir "updater"
$VersionsDir = Join-Path $RootDir "versions"
$StagingDir = Join-Path $RootDir "staging"
$FailedDir = Join-Path $RootDir "failed"

Write-Host "Starting NetRatel Client deployment..."
Write-Host "Enrollment code valid until {{request.ValidToUtc.UtcDateTime:O}}"
try {
    Write-Host "PowerShell version: $($PSVersionTable.PSVersion)"
    Write-Host "PowerShell edition: $($PSVersionTable.PSEdition)"
}
catch {
}

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

$tempDir = Join-Path $env:TEMP "netratel_install_$([Guid]::NewGuid())"
try {
    New-Item -ItemType Directory -Path $tempDir, $RootDir, $StateDir, $LogDir, $UpdaterDir, $VersionsDir, $StagingDir, $FailedDir -Force | Out-Null
    $resolvedVersion = $Version
    $zipPath = Join-Path $tempDir "netratel.zip"
    $downloadUri = "$ApiBase/api/v1/client-artifacts/$Runtime/$resolvedVersion/onboarding-download"
    $downloadHeaders = @{
        "X-NetRatel-Tenant-Id" = "$TenantId"
        "X-NetRatel-Enrollment-Code" = $EnrollmentCode
    }

    Write-Host "Downloading NetRatel Client package $resolvedVersion..."
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -fL --retry 3 --retry-delay 2 `
            -H "X-NetRatel-Tenant-Id: $TenantId" `
            -H "X-NetRatel-Enrollment-Code: $EnrollmentCode" `
            -o $zipPath $downloadUri
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Invoke-WebRequest -Uri $downloadUri -Method GET -UseBasicParsing -Headers $downloadHeaders -OutFile $zipPath
    }
    if (-not (Test-Path $zipPath) -or ((Get-Item $zipPath).Length -le 0)) {
        throw "Downloaded client package was empty."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $actualSha = Get-NetRatelSha256Hex -Path $zipPath
        if ($actualSha -ne $ExpectedSha256.ToLowerInvariant()) {
            throw "Downloaded client package failed SHA-256 verification."
        }
    }

    $targetDir = Join-Path $VersionsDir $resolvedVersion
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    Write-Host "Extracting NetRatel Client package..."
    Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $targetDir
    $exe = @(
        Join-Path $targetDir "NetRatel.Client.exe"
        Join-Path $targetDir "NetRatel.Client.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) {
        throw "Client executable was not found in extracted package."
    }

    {{enrollmentBlock}}
    {{serviceBlock}}

    Write-Host "NetRatel deployment complete."
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item $tempDir -Recurse -Force
    }
}
""";
    }

    private static string BuildBash(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var enrollCommand = $$"""
"${CLIENT_EXE}" --enroll "${ENROLLMENT_CODE}" --api "${API_BASE}" {{silentArg}}
""";

        var serviceBlock = request.InstallAsService
            ? """
            if [ -f "${TARGET_DIR}/updater/netratel-update.sh" ]; then
              install -m 0755 "${TARGET_DIR}/updater/netratel-update.sh" "${UPDATER_DIR}/.netratel-update.sh.$$"
              mv -f "${UPDATER_DIR}/.netratel-update.sh.$$" "${UPDATER_DIR}/netratel-update.sh"
            fi
            cat > "${ROOT_DIR}/netratel-client-start.sh" <<SH
            #!/usr/bin/env bash
            set -euo pipefail
            ROOT_DIR="${ROOT_DIR}"
            if [ -x "${ROOT_DIR}/current/NetRatel.Client" ]; then
              exec "${ROOT_DIR}/current/NetRatel.Client" --service
            fi
            echo "No NetRatel client executable found in ${ROOT_DIR}/current" >&2
            exit 78
            SH
            chmod 0755 "${ROOT_DIR}/netratel-client-start.sh"
            ln -sfn "${TARGET_DIR}" "${ROOT_DIR}/current.next"
            mv -Tf "${ROOT_DIR}/current.next" "${ROOT_DIR}/current"
            ACTIVATED=true
            cat > "${SYSTEMD_UNIT_DIR}/netratel-client.service" <<UNIT
            [Unit]
            Description=NetRatel Client
            After=network-online.target
            Wants=network-online.target
            [Service]
            WorkingDirectory=${ROOT_DIR}/current
            ExecStart=${ROOT_DIR}/netratel-client-start.sh
            Restart=always
            RestartPreventExitStatus=78
            Environment=NetRatel_CLIENT_LOG_DIR=/var/lib/netratel/logs
            Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence
            Environment=NetRatelCLIENT__Gateway__Endpoint=${API_BASE}
            Environment=NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka
            Environment=NetRatelCLIENT__Gateway__TelemetryShadowEnabled=true
            Environment=NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true
            Environment=NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true
            Environment=NetRatelCLIENT__Gateway__JobAuthorityEnabled=true
            Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=true
            Environment=NetRatelCLIENT__Gateway__LogGatewayEnabled=true
            Environment=NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true
            Environment=NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true
            Environment=NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true
            Environment=NetRatelCLIENT__Client__AutoUpdate__Mode=Service
            Environment=NetRatelCLIENT__Client__AutoUpdate__StateDirectory=${STATE_DIR}
            Environment=NetRatelCLIENT__Client__AutoUpdate__RequestPath=${STATE_DIR}/request.json
            Environment=NetRatelCLIENT__Client__AutoUpdate__ReadyPath=${STATE_DIR}/ready.json
            [Install]
            WantedBy=multi-user.target
            UNIT
            cat > "${SYSTEMD_UNIT_DIR}/netratel-update.service" <<UNIT
            [Unit]
            Description=NetRatel Client Updater
            After=network-online.target
            [Service]
            Type=oneshot
            ExecStart=${UPDATER_DIR}/netratel-update.sh
            Environment=NetRatel_UPDATE_ROOT=${ROOT_DIR}
            Environment=NetRatel_UPDATE_STATE=${STATE_DIR}
            Environment=NetRatel_UPDATE_REQUEST=${STATE_DIR}/request.json
            Environment=NetRatel_CLIENT_SERVICE=netratel-client.service
            [Install]
            WantedBy=multi-user.target
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
            """
            : string.Empty;

        var requiredCommands = request.InstallAsService
            ? "curl unzip sha256sum systemctl flock python3 install"
            : "curl unzip sha256sum flock python3";
        var privilegeCheck = request.InstallAsService ? """
            if [ "${EUID}" -ne 0 ] && [ "${NetRatel_TEST_ALLOW_NONROOT:-false}" != "true" ]; then
              echo "This NetRatel systemd installer must be run as root." >&2
              exit 1
            fi
            """ : string.Empty;
        var activationBlock = request.InstallAsService ? """
            if systemctl is-active --quiet netratel-client.service; then
              systemctl stop netratel-client.service
              if systemctl is-active --quiet netratel-client.service; then
                echo "NetRatel client service did not stop; no files were activated." >&2
                exit 1
              fi
            fi
            mv "${STAGE_DIR}" "${TARGET_DIR}"
            """ : "mv \"${STAGE_DIR}\" \"${TARGET_DIR}\"";
        var defaultRoot = request.InstallAsService ? "/opt/netratel/client" : "${HOME}/.local/share/netratel/client";
        var defaultState = request.InstallAsService ? "/var/lib/netratel/update" : "${HOME}/.local/state/netratel/update";

        return $$"""
#!/usr/bin/env bash
set -euo pipefail

API_BASE="{{request.ApiBaseUrl}}"
TENANT_ID={{request.TenantId}}
ENROLLMENT_CODE="{{request.EnrollmentCode}}"
RUNTIME="{{request.RuntimeId}}"
VERSION="{{(string.IsNullOrWhiteSpace(request.ArtifactVersion) ? "latest" : request.ArtifactVersion)}}"
EXPECTED_SHA="{{(string.IsNullOrWhiteSpace(request.ArtifactSha256) ? string.Empty : request.ArtifactSha256)}}"
TMP_DIR=$(mktemp -d)
ROOT_DIR="${NetRatel_ROOT:-{{defaultRoot}}}"
STATE_DIR="${NetRatel_STATE:-{{defaultState}}}"
SYSTEMD_UNIT_DIR="${NetRatel_SYSTEMD_UNIT_DIR:-/etc/systemd/system}"
UPDATER_DIR="${ROOT_DIR}/updater"
VERSIONS_DIR="${ROOT_DIR}/versions"
STAGING_DIR="${ROOT_DIR}/staging"
FAILED_DIR="${ROOT_DIR}/failed"

{{privilegeCheck}}
for REQUIRED_COMMAND in {{requiredCommands}}; do
  if ! command -v "${REQUIRED_COMMAND}" >/dev/null 2>&1; then
    echo "Required command is unavailable: ${REQUIRED_COMMAND}" >&2
    exit 1
  fi
done

cleanup() { rm -rf "${TMP_DIR}"; }
trap cleanup EXIT

echo "Enrollment code valid until {{request.ValidToUtc.UtcDateTime:O}}"
RESOLVED_VERSION="${VERSION}"

curl -fL --retry 3 --retry-delay 2 \
  -H "X-NetRatel-Tenant-Id: ${TENANT_ID}" \
  -H "X-NetRatel-Enrollment-Code: ${ENROLLMENT_CODE}" \
  -o "${TMP_DIR}/netratel.zip" \
  "${API_BASE}/api/v1/client-artifacts/${RUNTIME}/${RESOLVED_VERSION}/onboarding-download"
test -s "${TMP_DIR}/netratel.zip"
if [ -n "${EXPECTED_SHA}" ]; then
  ACTUAL_SHA=$(sha256sum "${TMP_DIR}/netratel.zip" | awk '{print $1}')
  if [ "${ACTUAL_SHA,,}" != "${EXPECTED_SHA,,}" ]; then
    echo "Downloaded client package failed SHA-256 verification." >&2
    exit 1
  fi
fi

mkdir -p "${ROOT_DIR}" "${STATE_DIR}" "${UPDATER_DIR}" "${VERSIONS_DIR}" "${STAGING_DIR}" "${FAILED_DIR}"
{{(request.InstallAsService ? "mkdir -p \"${SYSTEMD_UNIT_DIR}\"" : string.Empty)}}
chmod 0700 "${STATE_DIR}"
exec 9>"${STATE_DIR}/update.lock"
if ! flock -n 9; then
  echo "Another NetRatel installer or updater is already running." >&2
  exit 75
fi
TARGET_DIR="${VERSIONS_DIR}/${RESOLVED_VERSION}"
if ! [[ "${RESOLVED_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
  echo "Artifact version is not a canonical semantic version." >&2
  exit 1
fi
if [ -e "${TARGET_DIR}" ]; then
  echo "Immutable target version ${RESOLVED_VERSION} already exists; refusing to replace it." >&2
  exit 1
fi
STAGE_DIR=$(mktemp -d "${STAGING_DIR}/.${RESOLVED_VERSION}.XXXXXX")
unzip -o "${TMP_DIR}/netratel.zip" -d "${STAGE_DIR}" >/dev/null
python3 - "${STAGE_DIR}/netratel-client-manifest.json" "${RESOLVED_VERSION}" "${RUNTIME}" <<'PY'
import json, os, sys
with open(sys.argv[1], encoding="utf-8-sig") as f: manifest=json.load(f)
if (manifest.get("schema") != "netratel.client.manifest.v1" or
    manifest.get("product") != "NetRatel.Client" or
    manifest.get("version") != sys.argv[2] or
    manifest.get("runtimeId") != sys.argv[3] or
    not os.path.isfile(os.path.join(os.path.dirname(sys.argv[1]), manifest.get("executable", "")))):
    raise SystemExit("Artifact manifest does not match the requested version and runtime.")
PY
CLIENT_EXE="${STAGE_DIR}/NetRatel.Client"
if [ ! -f "${CLIENT_EXE}" ]; then
  CLIENT_EXE="${TARGET_DIR}/NetRatel.Client"
fi
if [ ! -f "${CLIENT_EXE}" ]; then
  echo "Client executable was not found in extracted package." >&2
  exit 1
fi
chmod +x "${CLIENT_EXE}"
{{enrollCommand}}

PREVIOUS_TARGET="$(readlink -f "${ROOT_DIR}/current" 2>/dev/null || true)"
ACTIVATED=false
rollback_install() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ] && [ "$ACTIVATED" = true ]; then
    {{(request.InstallAsService ? "systemctl stop netratel-client.service || true" : ":")}}
    if [ -n "${PREVIOUS_TARGET}" ] && [ -d "${PREVIOUS_TARGET}" ]; then
      ln -sfn "${PREVIOUS_TARGET}" "${ROOT_DIR}/current.rollback"
      mv -Tf "${ROOT_DIR}/current.rollback" "${ROOT_DIR}/current"
      {{(request.InstallAsService ? "systemctl start netratel-client.service || true" : ":")}}
    else
      rm -f "${ROOT_DIR}/current"
    fi
  fi
  exit "$status"
}
trap rollback_install EXIT
{{activationBlock}}

{{serviceBlock}}

echo "NetRatel deployment complete."
""";
    }

    private static string BuildMacBash(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var defaultRoot = request.InstallAsService ? "/opt/netratel/client" : "${HOME}/Library/Application Support/NetRatel/Client";
        var serviceBlock = request.InstallAsService ? """
            PLIST_PATH="${NetRatel_LAUNCHD_PLIST:-/Library/LaunchDaemons/co.za.netratel.client.plist}"
            LABEL="co.za.netratel.client"
            cat > "${PLIST_PATH}.new" <<PLIST
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>${LABEL}</string>
              <key>ProgramArguments</key><array><string>${ROOT_DIR}/current/NetRatel.Client</string><string>--service</string></array>
              <key>WorkingDirectory</key><string>${ROOT_DIR}/current</string>
              <key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
              <key>EnvironmentVariables</key><dict>
                <key>NetRatelCLIENT__Transport__Mode</key><string>AkkaPresence</string>
                <key>NetRatelCLIENT__Gateway__Endpoint</key><string>${API_BASE}</string>
                <key>NetRatelCLIENT__Gateway__RequiredPresenceAuthority</key><string>akka</string>
                <key>NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__CommandAuthorityEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__JobAuthorityEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__FileGatewayEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__LogGatewayEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__TerminalGatewayEnabled</key><string>true</string>
                <key>NetRatelCLIENT__Gateway__TerminalAuthorityEnabled</key><string>true</string>
              </dict>
            </dict></plist>
            PLIST
            chmod 0644 "${PLIST_PATH}.new"
            launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
            mv -f "${PLIST_PATH}.new" "${PLIST_PATH}"
            ln -s "${TARGET_DIR}" "${ROOT_DIR}/current.next"
            mv -f "${ROOT_DIR}/current.next" "${ROOT_DIR}/current"
            ACTIVATED=true
            launchctl bootstrap system "${PLIST_PATH}"
            launchctl print "system/${LABEL}" >/dev/null
            """ : string.Empty;
        var rootCheck = request.InstallAsService ? """
            if [ "${EUID}" -ne 0 ] && [ "${NetRatel_TEST_ALLOW_NONROOT:-false}" != "true" ]; then
              echo "A macOS launch daemon installation requires root. Inspect the script, then run it with sudo." >&2
              exit 1
            fi
            command -v launchctl >/dev/null || { echo "launchctl is required for a macOS service installation." >&2; exit 1; }
            """ : string.Empty;
        var rollback = request.InstallAsService ? """
            if [ "${ACTIVATED}" = true ]; then
              launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
              if [ -n "${PREVIOUS_TARGET}" ]; then
                ln -s "${PREVIOUS_TARGET}" "${ROOT_DIR}/current.rollback"
                mv -f "${ROOT_DIR}/current.rollback" "${ROOT_DIR}/current"
                launchctl bootstrap system "${PLIST_PATH}" || true
              else
                rm -f "${ROOT_DIR}/current"
              fi
            fi
            """ : ":";

        return $$"""
#!/usr/bin/env bash
set -euo pipefail
API_BASE="{{request.ApiBaseUrl}}"
TENANT_ID={{request.TenantId}}
ENROLLMENT_CODE="{{request.EnrollmentCode}}"
RUNTIME="{{request.RuntimeId}}"
VERSION="{{request.ArtifactVersion}}"
EXPECTED_SHA="{{request.ArtifactSha256}}"
ROOT_DIR="${NetRatel_ROOT:-{{defaultRoot}}}"
TMP_DIR=$(mktemp -d)
{{rootCheck}}
for required in curl unzip shasum python3; do
  command -v "${required}" >/dev/null || { echo "Required command is unavailable: ${required}" >&2; exit 1; }
done
cleanup() { rm -rf "${TMP_DIR}"; }
trap cleanup EXIT
if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
  echo "An immutable client version is required." >&2; exit 1
fi
if [ ! -d "${ROOT_DIR}" ]; then mkdir -p "${ROOT_DIR}"; fi
VERSIONS_DIR="${ROOT_DIR}/versions"
STAGING_DIR="${ROOT_DIR}/staging"
mkdir -p "${VERSIONS_DIR}" "${STAGING_DIR}"
TARGET_DIR="${VERSIONS_DIR}/${VERSION}"
if [ -e "${TARGET_DIR}" ]; then echo "Immutable target version already exists." >&2; exit 1; fi
curl -fL --retry 3 --retry-delay 2 \
  -H "X-NetRatel-Tenant-Id: ${TENANT_ID}" \
  -H "X-NetRatel-Enrollment-Code: ${ENROLLMENT_CODE}" \
  -o "${TMP_DIR}/netratel.zip" \
  "${API_BASE}/api/v1/client-artifacts/${RUNTIME}/${VERSION}/onboarding-download"
test -s "${TMP_DIR}/netratel.zip"
ACTUAL_SHA=$(shasum -a 256 "${TMP_DIR}/netratel.zip" | awk '{print $1}')
if [ "${ACTUAL_SHA}" != "${EXPECTED_SHA}" ]; then
  echo "Downloaded client package failed SHA-256 verification." >&2; exit 1
fi
STAGE_DIR=$(mktemp -d "${STAGING_DIR}/.${VERSION}.XXXXXX")
unzip -q "${TMP_DIR}/netratel.zip" -d "${STAGE_DIR}"
python3 - "${STAGE_DIR}/netratel-client-manifest.json" "${VERSION}" "${RUNTIME}" <<'PY'
import json, os, sys
with open(sys.argv[1], encoding='utf-8-sig') as file: manifest=json.load(file)
if (manifest.get('schema') != 'netratel.client.manifest.v1' or
    manifest.get('product') != 'NetRatel.Client' or
    manifest.get('version') != sys.argv[2] or
    manifest.get('runtimeId') != sys.argv[3] or
    manifest.get('executable') != 'NetRatel.Client' or
    not os.path.isfile(os.path.join(os.path.dirname(sys.argv[1]), 'NetRatel.Client'))):
    raise SystemExit('Client package manifest does not match the requested runtime and version.')
PY
chmod 0755 "${STAGE_DIR}/NetRatel.Client"
"${STAGE_DIR}/NetRatel.Client" --enroll "${ENROLLMENT_CODE}" --api "${API_BASE}" {{silentArg}}
PREVIOUS_TARGET=""
if [ -L "${ROOT_DIR}/current" ]; then PREVIOUS_TARGET=$(readlink "${ROOT_DIR}/current"); fi
ACTIVATED=false
rollback() {
  status=$?
  if [ "$status" -ne 0 ]; then
    {{rollback}}
  fi
  rm -rf "${TMP_DIR}"
  exit "$status"
}
trap rollback EXIT
mv "${STAGE_DIR}" "${TARGET_DIR}"
{{serviceBlock}}
echo "NetRatel macOS client installation complete."
""";
    }
}
