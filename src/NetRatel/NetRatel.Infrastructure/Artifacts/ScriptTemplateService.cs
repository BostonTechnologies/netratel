using System.Globalization;
using System.Text.RegularExpressions;
using NetRatel.Application.Artifacts;
using NetRatel.Shared.Client;

namespace NetRatel.Infrastructure.Artifacts;

public sealed class ScriptTemplateService : IScriptTemplateService
{
    public string Build(DeploymentScriptTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRuntime(request.RuntimeId);
        if (request.TenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.TenantId), "Tenant ID must be positive.");
        if (string.IsNullOrWhiteSpace(request.EnrollmentCode) || request.EnrollmentCode.Any(char.IsControl))
            throw new ArgumentException("An enrollment code without control characters is required.", nameof(request.EnrollmentCode));
        var version = string.IsNullOrWhiteSpace(request.ArtifactVersion) ? "latest" : request.ArtifactVersion;
        if (version != "latest" && !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z"))
            throw new ArgumentException("Artifact version must be a supported version or latest.", nameof(request.ArtifactVersion));
        if (!string.IsNullOrEmpty(request.ArtifactSha256) && !Regex.IsMatch(request.ArtifactSha256, @"\A[0-9a-fA-F]{64}\z"))
            throw new ArgumentException("Artifact SHA-256 must contain 64 hexadecimal characters.", nameof(request.ArtifactSha256));
        request = request with
        {
            ApiBaseUrl = ClientEndpointAddress.NormalizeApiBase(request.ApiBaseUrl),
            GatewayEndpoint = string.IsNullOrWhiteSpace(request.GatewayEndpoint)
                ? null : ClientEndpointAddress.NormalizeGatewayBase(request.GatewayEndpoint),
            ArtifactVersion = version
        };
        if (request.RuntimeId.StartsWith("osx-", StringComparison.Ordinal))
        {
            if (request.IsUpdateSeed) throw new ArgumentException("macOS update seeds are not supported.", nameof(request));
            return BuildMacBash(request);
        }
        return Render(request, request.RuntimeId.StartsWith("win-", StringComparison.Ordinal));
    }

    public string GetFileExtension(string runtimeId)
    {
        ValidateRuntime(runtimeId);
        return runtimeId.StartsWith("win-", StringComparison.Ordinal) ? "ps1" : "sh";
    }

    private static void ValidateRuntime(string runtimeId)
    {
        if (runtimeId is not ("win-x64" or "win-arm64" or "linux-x64" or "osx-x64" or "osx-arm64"))
            throw new ArgumentException("Unsupported client runtime ID.", nameof(runtimeId));
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string BashLiteral(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string Render(DeploymentScriptTemplateRequest request, bool windows)
    {
        string Literal(string value) => windows ? PowerShellLiteral(value) : BashLiteral(value);
        string Input(string value, string seedName)
        {
            if (!request.IsUpdateSeed) return Literal(value);
            if (windows) return seedName switch
            {
                "ApiBase" => "Get-NetRatelSeedApiBase $ApiBase",
                "GatewayEndpoint" => "Get-NetRatelSeedGatewayEndpoint $GatewayEndpoint $ApiBase",
                _ => "$" + seedName
            };
            var name = seedName switch
            {
                "ApiBase" => "API_BASE", "GatewayEndpoint" => "GATEWAY_ENDPOINT",
                "TenantId" => "TENANT_ID", "EnrollmentCode" => "ENROLLMENT_CODE",
                "Runtime" => "RUNTIME", "Version" => "VERSION", _ => throw new InvalidOperationException("Unsupported seed input.")
            };
            return "\"${NETRATEL_SEED_" + name + "}\"";
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["API_BASE_URL"] = Input(request.ApiBaseUrl, "ApiBase"),
            ["GATEWAY_ENDPOINT"] = Input(request.GatewayEndpoint ?? "", "GatewayEndpoint"),
            ["TENANT_ID"] = windows
                ? request.IsUpdateSeed ? "$TenantId" : request.TenantId.ToString(CultureInfo.InvariantCulture)
                : Input(request.TenantId.ToString(CultureInfo.InvariantCulture), "TenantId"),
            ["ENROLLMENT_CODE"] = Input(request.EnrollmentCode, "EnrollmentCode"),
            ["RUNTIME_ID"] = Input(request.RuntimeId, "Runtime"),
            ["ARTIFACT_VERSION"] = Input(request.ArtifactVersion ?? "latest", "Version"),
            ["ARTIFACT_SHA256"] = Literal(request.ArtifactSha256 ?? ""),
            ["VALID_TO_UTC"] = request.IsUpdateSeed ? windows
                ? "[DateTimeOffset]::UtcNow.AddHours(1).ToString('O')"
                : "\"${NETRATEL_SEED_VALID_TO_UTC}\""
                : Literal(request.ValidToUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ["INSTALL_AS_SERVICE"] = windows ? request.InstallAsService ? "$true" : "$false" : request.InstallAsService ? "true" : "false",
            ["SILENT_INSTALL"] = windows ? request.SilentInstall ? "$true" : "$false" : request.SilentInstall ? "true" : "false",
            ["SEED_INITIALIZE"] = "",
            ["SEED_BEFORE_MUTATION"] = request.IsUpdateSeed && windows ? "Start-NetRatelSeedHandoff" : "",
            ["SEED_SUCCESS"] = request.IsUpdateSeed && windows ? "Set-NetRatelSeedHandoffResult -State 'installed_started'" : "",
            ["SEED_FAILURE"] = request.IsUpdateSeed && windows ? "Complete-NetRatelSeedFailure $installerFailure" : "",
            ["SEED_CLEANUP"] = request.IsUpdateSeed && windows ? "Remove-NetRatelSeedHandoffFiles" : ""
        };
        var name = windows ? "install.ps1" : "install-linux.sh";
        using var stream = typeof(ScriptTemplateService).Assembly.GetManifestResourceStream("NetRatel.Installers." + name)
            ?? throw new InvalidOperationException("Installer resource is unavailable: " + name);
        using var reader = new StreamReader(stream);
        // Replace once so even synthetic grant text containing a marker stays literal.
        return Regex.Replace(reader.ReadToEnd(), "@@([A-Z0-9_]+)@@", match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value
                : throw new InvalidOperationException("Unknown installer input: " + match.Groups[1].Value));
    }

    private static string BuildMacBash(DeploymentScriptTemplateRequest request)
    {
        var silentArg = request.SilentInstall ? "--silent" : string.Empty;
        var defaultRoot = request.InstallAsService ? "/opt/netratel/client" : "${HOME}/Library/Application Support/NetRatel/Client";
        var serviceBlock = request.InstallAsService ? """
            PLIST_PATH="${NetRatel_LAUNCHD_PLIST:-/Library/LaunchDaemons/co.za.netratel.client.plist}"
            LABEL="co.za.netratel.client"
            PRESERVED_CLIENT_ENVIRONMENT="$(python3 - "${PLIST_PATH}" "${API_BASE}" "${GATEWAY_ENDPOINT}" "${PREVIOUS_TARGET}" "${TARGET_DIR}" <<'PY'
            import html
            import json
            import os
            import plistlib
            import sys
            from urllib.parse import urlsplit

            path, api_base, gateway_endpoint, previous_dir, target_dir = sys.argv[1:]
            prefix = "NetRatelCLIENT__"

            def public_origin(value):
                try:
                    parsed = urlsplit(value.strip())
                    if not parsed.scheme or not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment:
                        return None
                    if parsed.path.rstrip("/").lower() not in ("", "/api"):
                        return None
                    return f"{parsed.scheme.lower()}://{parsed.netloc.lower()}"
                except ValueError:
                    return None

            retired = {
                "transport__mode",
                "gateway__requiredpresenceauthority",
                "gateway__telemetryshadowenabled",
                "gateway__telemetryauthorityenabled",
                "gateway__commandauthorityenabled",
                "gateway__jobauthorityenabled",
                "gateway__terminalauthorityenabled",
                "gateway__fileauthorityenabled",
                "gateway__logauthorityenabled",
                "gateway__controlauthorityenabled",
                "gateway__remotesupportauthorityenabled",
                "gateway__remotesupportv1enabled",
                "gateway__remotesupportv2inventoryenabled",
                "gateway__remotesupportv2mediaenabled",
                "gateway__controlgatewayenabled",
                "gateway__filegatewayenabled",
                "gateway__loggatewayenabled",
                "gateway__remotesupportgatewayenabled",
                "gateway__terminalgatewayenabled",
            }
            retired_json_gateway = {
                "requiredpresenceauthority", "telemetryshadowenabled", "telemetryauthorityenabled",
                "controlauthorityenabled", "commandauthorityenabled", "fileauthorityenabled",
                "jobauthorityenabled", "logauthorityenabled", "remotesupportauthorityenabled",
                "terminalauthorityenabled", "remotesupportv1enabled", "controlgatewayenabled",
                "filegatewayenabled", "loggatewayenabled", "remotesupportgatewayenabled",
                "terminalgatewayenabled", "remotesupportv2inventoryenabled", "remotesupportv2mediaenabled",
            }

            environment = {}
            if os.path.isfile(path):
                with open(path, "rb") as stream:
                    current = plistlib.load(stream)
                for name, value in current.get("EnvironmentVariables", {}).items():
                    if not isinstance(name, str) or not isinstance(value, str):
                        continue
                    environment[name] = value

            previous_api_base = environment.get("NetRatelCLIENT__Client__ApiBaseUrl", "")
            previous_settings_path = os.path.join(previous_dir, "clientsettings.json") if previous_dir else ""
            target_settings_path = os.path.join(target_dir, "clientsettings.json")
            settings_path = previous_settings_path if previous_settings_path and os.path.isfile(previous_settings_path) else target_settings_path
            settings = None
            settings_source = "package defaults"
            if os.path.isfile(settings_path):
                with open(settings_path, encoding="utf-8-sig") as stream:
                    settings = json.load(stream)
                if settings_path == previous_settings_path:
                    settings_source = "installed per-version file"
            if settings is not None:
                def get_case_insensitive(mapping, name, fallback=None):
                    return next((value for key, value in mapping.items() if key.lower() == name.lower()), fallback)

                client = get_case_insensitive(settings, "Client", settings)
                if not previous_api_base:
                    previous_api_base = str(client.get("ApiBaseUrl", ""))
                if not previous_api_base and previous_dir:
                    try:
                        with open(os.path.join(previous_dir, "appsettings.json"), encoding="utf-8-sig") as stream:
                            previous_api_base = str(json.load(stream).get("Client", {}).get("ApiBaseUrl", ""))
                    except (OSError, ValueError, AttributeError):
                        pass
                gateway = get_case_insensitive(settings, "Gateway")
                if isinstance(gateway, dict):
                    for name in list(gateway):
                        lowered = name.lower()
                        if lowered in retired_json_gateway:
                            gateway.pop(name, None)
                    endpoint_key = next((key for key in gateway if key.lower() == "endpoint"), None)
                    old_endpoint = gateway.get(endpoint_key) if endpoint_key else None
                    old_origin = public_origin(str(old_endpoint or ""))
                    settings_api_origin = public_origin(str(client.get("ApiBaseUrl", "")))
                    if old_endpoint and (gateway_endpoint or (settings_api_origin and old_origin == settings_api_origin)):
                        gateway.pop(endpoint_key, None)
                transport_key = next((key for key in settings if key.lower() == "transport"), None)
                transport = settings.get(transport_key) if transport_key else None
                if isinstance(transport, dict):
                    mode_key = next((key for key in transport if key.lower() == "mode"), None)
                    if mode_key: transport.pop(mode_key, None)
                    if not transport: settings.pop(transport_key, None)
                os.makedirs(target_dir, exist_ok=True)
                with open(target_settings_path, "w", encoding="utf-8") as stream:
                    json.dump(settings, stream, indent=2)
                    stream.write("\n")
                print(f"Preserved {settings_source}; prior API source={'launchd service environment' if environment.get('NetRatelCLIENT__Client__ApiBaseUrl') else 'per-version file or package defaults'}.", file=sys.stderr)
            previous_api_origin = public_origin(previous_api_base)
            previous_gateway_origin = public_origin(environment.get("NetRatelCLIENT__Gateway__Endpoint", ""))
            previous_gateway_is_redundant = bool(previous_api_origin and previous_gateway_origin == previous_api_origin)
            for name in list(environment):
                if not name.lower().startswith(prefix.lower()):
                    continue
                setting = name[len(prefix):].lower()
                if (setting == "client__apibaseurl" or setting in retired or
                    (setting == "gateway__endpoint" and (gateway_endpoint or previous_gateway_is_redundant))):
                    environment.pop(name)

            environment["NetRatelCLIENT__Client__ApiBaseUrl"] = api_base
            if gateway_endpoint:
                environment["NetRatelCLIENT__Gateway__Endpoint"] = gateway_endpoint
            saved_gateway = next((value for name, value in (settings or {}).get("Gateway", {}).items() if name.lower() == "endpoint"), "")
            effective_gateway = environment.get("NetRatelCLIENT__Gateway__Endpoint") or saved_gateway or api_base
            gateway_source = "launchd environment" if environment.get("NetRatelCLIENT__Gateway__Endpoint") else (settings_source if saved_gateway else "API origin default")
            log_dir = environment.get("NetRatel_CLIENT_LOG_DIR") or environment.get("NetRatelCLIENT__Client__LogDirectory") or environment.get("NetRatelCLIENT__LogDirectory") or os.path.join(target_dir, "logs")
            print(f"API={public_origin(api_base) or '<invalid origin>'}; source=generated request. Gateway={public_origin(effective_gateway) or '<invalid origin>'}; source={gateway_source}. Logs={log_dir}", file=sys.stderr)
            for name, value in sorted(environment.items()):
                print("                <key>{}</key><string>{}</string>".format(
                    html.escape(name, quote=True), html.escape(value, quote=True)))
            PY
            )"
            XML_ROOT_DIR="$(python3 -c 'import html,sys; print(html.escape(sys.argv[1], quote=True))' "${ROOT_DIR}")"
            cat > "${PLIST_PATH}.new" <<PLIST
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>${LABEL}</string>
              <key>ProgramArguments</key><array><string>${XML_ROOT_DIR}/current/NetRatel.Client</string><string>--service</string></array>
              <key>WorkingDirectory</key><string>${XML_ROOT_DIR}/current</string>
              <key>RunAtLoad</key><true/><key>KeepAlive</key><true/>
              <key>EnvironmentVariables</key><dict>
            ${PRESERVED_CLIENT_ENVIRONMENT}
              </dict>
            </dict></plist>
            PLIST
            chmod 0644 "${PLIST_PATH}.new"
            PLIST_BACKUP="${TMP_DIR}/previous-launchd.plist"
            HAD_PREVIOUS_PLIST=false
            if [ -f "${PLIST_PATH}" ]; then
              cp -p "${PLIST_PATH}" "${PLIST_BACKUP}"
              HAD_PREVIOUS_PLIST=true
            fi
            # A failure after bootout must restart the previous launch daemon too.
            ACTIVATED=true
            launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
            mv -f "${PLIST_PATH}.new" "${PLIST_PATH}"
            replace_current "${TARGET_DIR}"
            launchctl bootstrap system "${PLIST_PATH}"
            launchctl print "system/${LABEL}" >/dev/null
            echo "Service=${LABEL}; account=root; status=loaded; executable=${ROOT_DIR}/current/NetRatel.Client"
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
              echo "launchd activation failed; restoring the previous client and service configuration." >&2
              launchctl print "system/${LABEL}" 2>/dev/null | awk '/^[[:space:]]*(state|program|pid|last exit code) =/ { print; if (++count == 8) exit }' >&2 || true
              launchctl bootout "system/${LABEL}" >/dev/null 2>&1 || true
              if [ -n "${PREVIOUS_TARGET}" ]; then
                replace_current "${PREVIOUS_TARGET}" || echo "Unable to restore the previous client pointer." >&2
              else
                rm -f "${ROOT_DIR}/current"
              fi
              if [ "${HAD_PREVIOUS_PLIST}" = true ]; then
                cp -p "${PLIST_BACKUP}" "${PLIST_PATH}"
                launchctl bootstrap system "${PLIST_PATH}" || echo "Unable to restart the previous launch daemon." >&2
              else
                rm -f "${PLIST_PATH}"
              fi
              rm -f "${PLIST_PATH}.new"
            fi
            """ : ":";

        return $$"""
#!/usr/bin/env bash
set -euo pipefail
API_BASE={{BashLiteral(request.ApiBaseUrl)}}
GATEWAY_ENDPOINT={{BashLiteral(request.GatewayEndpoint ?? string.Empty)}}
TENANT_ID={{request.TenantId}}
ENROLLMENT_CODE={{BashLiteral(request.EnrollmentCode)}}
RUNTIME={{BashLiteral(request.RuntimeId)}}
VERSION={{BashLiteral(request.ArtifactVersion ?? string.Empty)}}
EXPECTED_SHA={{BashLiteral(request.ArtifactSha256 ?? string.Empty)}}
ROOT_DIR="${NetRatel_ROOT:-{{defaultRoot}}}"
TMP_DIR=$(mktemp -d)
STAGE_DIR=""
cleanup() {
  if [ -n "${STAGE_DIR}" ] && [ -d "${STAGE_DIR}" ]; then rm -rf -- "${STAGE_DIR}"; fi
  rm -rf -- "${TMP_DIR}"
}
trap cleanup EXIT
{{rootCheck}}
for required in curl unzip shasum python3; do
  command -v "${required}" >/dev/null || { echo "Required command is unavailable: ${required}" >&2; exit 1; }
done
echo "Preparing macOS client version=${VERSION}; runtime=${RUNTIME}; API origin from generated request."
if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "An immutable client version is required." >&2; exit 1
fi
if [ ! -d "${ROOT_DIR}" ]; then mkdir -p "${ROOT_DIR}"; fi
VERSIONS_DIR="${ROOT_DIR}/versions"
STAGING_DIR="${ROOT_DIR}/staging"
mkdir -p "${VERSIONS_DIR}" "${STAGING_DIR}"
TARGET_DIR="${VERSIONS_DIR}/${VERSION}"
if [ -e "${TARGET_DIR}" ]; then echo "Immutable target version already exists." >&2; exit 1; fi
RESPONSE_HEADERS="${TMP_DIR}/response-headers.txt"
curl -f --max-redirs 0 --connect-timeout 15 --max-time 120 \
  -H "X-NetRatel-Tenant-Id: ${TENANT_ID}" \
  -H "X-NetRatel-Enrollment-Code: ${ENROLLMENT_CODE}" \
  -D "${RESPONSE_HEADERS}" \
  -o "${TMP_DIR}/netratel.zip" \
  "${API_BASE}/api/v1/client-artifacts/${RUNTIME}/${VERSION}/onboarding-download"
test -s "${TMP_DIR}/netratel.zip"
read_response_header() {
  awk -v wanted="$1" '/^HTTP\// { status=$2; value=""; next } status == "200" && tolower(substr($0, 1, length(wanted) + 1)) == tolower(wanted ":") { value=substr($0, index($0, ":") + 1); sub(/^[[:space:]]+/, "", value); sub(/[[:space:]]+$/, "", value); gsub(/\r/, "", value) } END { if (status != "200") exit 1; print value }' "${RESPONSE_HEADERS}"
}
REPORTED_RID=$(read_response_header "X-NetRatel-Artifact-Rid")
REPORTED_VERSION=$(read_response_header "X-NetRatel-Artifact-Version")
REPORTED_SHA=$(read_response_header "X-NetRatel-Artifact-Sha256")
REPORTED_SIZE=$(read_response_header "X-NetRatel-Artifact-Size")
if [ "${REPORTED_RID}" != "${RUNTIME}" ] || [ "${REPORTED_VERSION}" != "${VERSION}" ] ||
  ! [[ "${REPORTED_SHA}" =~ ^[0-9A-Fa-f]{64}$ ]] ||
  ! [[ "${REPORTED_SIZE}" =~ ^[1-9][0-9]*$ ]]; then
  echo "The authorized artifact response metadata did not match the generated package snapshot." >&2; exit 1
fi
if [ -n "${EXPECTED_SHA}" ] &&
  [ "$(printf '%s' "${REPORTED_SHA}" | tr '[:upper:]' '[:lower:]')" != "$(printf '%s' "${EXPECTED_SHA}" | tr '[:upper:]' '[:lower:]')" ]; then
  echo "The authorized artifact response did not match the generated package snapshot." >&2; exit 1
fi
ACTUAL_SIZE=$(wc -c < "${TMP_DIR}/netratel.zip" | tr -d '[:space:]')
if [ "${ACTUAL_SIZE}" != "${REPORTED_SIZE}" ]; then
  echo "Downloaded client package size did not match its authorized metadata." >&2; exit 1
fi
ACTUAL_SHA=$(shasum -a 256 "${TMP_DIR}/netratel.zip" | awk '{print $1}')
if [ "${ACTUAL_SHA}" != "$(printf '%s' "${REPORTED_SHA}" | tr '[:upper:]' '[:lower:]')" ]; then
  echo "Downloaded client package failed SHA-256 verification." >&2; exit 1
fi
STAGE_DIR=$(mktemp -d "${STAGING_DIR}/.${VERSION}.XXXXXX")
python3 - "${TMP_DIR}/netratel.zip" <<'PY'
import posixpath, re, stat, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    seen = set()
    for member in archive.infolist():
        name = member.filename
        parts = name.rstrip('/').split('/')
        kind = stat.S_IFMT(member.external_attr >> 16)
        if (not name or name.startswith('/') or '\\' in name or re.match(r'^[A-Za-z]:', name) or
            any(part in ('', '.', '..') for part in parts) or
            kind not in (0, stat.S_IFREG, stat.S_IFDIR) or member.flag_bits & 1 or
            posixpath.normpath(name).casefold() in seen):
            raise SystemExit('Client ZIP contains an unsafe or ambiguous archive entry.')
        seen.add(posixpath.normpath(name).casefold())
PY
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
if [ -L "${ROOT_DIR}/current" ]; then PREVIOUS_TARGET=$(python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "${ROOT_DIR}/current"); fi
ACTIVATED=false
replace_current() {
  python3 - "${ROOT_DIR}/current" "$1" <<'PY'
import os, sys, uuid
current, target = sys.argv[1:]
staged = os.path.join(os.path.dirname(current), ".current-" + uuid.uuid4().hex)
try:
    os.symlink(target, staged)
    os.replace(staged, current)
finally:
    if os.path.lexists(staged):
        os.unlink(staged)
PY
}
rollback() {
  status=$?
  if [ "$status" -ne 0 ]; then
    {{rollback}}
  fi
  cleanup
  exit "$status"
}
trap rollback EXIT
mv "${STAGE_DIR}" "${TARGET_DIR}"
{{serviceBlock}}
echo "NetRatel macOS client installed; enrollment completed. Gateway state is unverified."
""";
    }
}
