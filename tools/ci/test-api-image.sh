#!/usr/bin/env bash
# Optional hosted base-image investigation. Production defaults and registries are untouched.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
results="${NETRATEL_API_IMAGE_RESULTS:-$root/TestResults/api-image}"
mkdir -p "$results"
results="$(cd "$results" && pwd)"
revision="$(git rev-parse HEAD)"
version="$(python3 tools/ci/product-version.py)"
candidate=netratel-api:chiseled-candidate
control=netratel-api:noble-control
printf '%s\n' "$revision" > "$results/source-sha.txt"
dotnet --info > "$results/sdk.txt"
dotnet msbuild src/NetRatel/NetRatel.API/NetRatel.API.csproj -nologo \
  -p:Configuration=Release -p:UseAppHost=false \
  -getProperty:TargetFramework,RuntimeIdentifier,SelfContained,PublishTrimmed,PublishAot,UseAppHost \
  > "$results/publish-properties.json"
temporary="$(mktemp -d)"
container=""
cleanup() {
  status=$?
  if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
  find "$temporary" -depth -delete 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

# Logs and host obj files must not invalidate the common publish/SBOM build stage.
mkdir "$temporary/source"
git archive HEAD | tar -x -C "$temporary/source"
for arm in candidate control; do
  image="$candidate"; target=chiseled-candidate
  if [[ "$arm" == control ]]; then image="$control"; target=final; fi
  (cd "$temporary/source"
    bash tools/ci/build-public-image.sh --dockerfile docker/api/Dockerfile \
      --image "$image" --version "$version" --revision "$revision" --target "$target"
  ) 2>&1 | tee "$results/$arm-build.log"
  scan_args=()
  if [[ "$arm" == candidate ]]; then scan_args=(--shell-free); fi
  bash tools/ci/scan-public-image.sh --image "$image" --version "$version" --revision "$revision" \
    "${scan_args[@]}" 2>&1 | tee "$results/$arm-scan.log"
  docker image inspect "$image" > "$results/$arm-image.json"
  mkdir -p "$temporary/$arm/publish"
  container="$(docker create "$image")"
  docker cp "$container:/app/." "$temporary/$arm/publish/"
  docker cp "$container:/var/lib/dpkg/status" "$results/$arm-base-native-packages.txt"
  if [[ "$arm" == candidate ]]; then docker cp "$container:/var/lib/chisel" "$results/candidate-extra-native-manifest"; fi
  docker rm "$container" >/dev/null; container=""
  cp "$temporary/$arm/publish/NetRatel.API.runtimeconfig.json" "$results/$arm-runtimeconfig.json"
  cp "$temporary/$arm/publish/NetRatel.API.deps.json" "$results/$arm-deps.json"
  cp "$temporary/$arm/publish/runtime.spdx.json" "$results/$arm-runtime.spdx.json"
  python3 tools/ci/verify-runtime-sbom.py --sbom "$temporary/$arm/publish/runtime.spdx.json" --root "$temporary/$arm" \
    | tee "$results/$arm-sbom-check.log"
  docker run --rm --entrypoint dotnet "$image" --list-runtimes > "$results/$arm-runtimes.txt"
done
python3 - "$temporary" "$results" <<'PY'
import hashlib, json, sys
from pathlib import Path
temporary, results = map(Path, sys.argv[1:])
inventories = {}
for arm in ('candidate', 'control'):
    directory = temporary / arm / 'publish'
    inventories[arm] = {p.relative_to(directory).as_posix(): {'sha256': hashlib.sha256(p.read_bytes()).hexdigest(), 'bytes': p.stat().st_size}
                        for p in sorted(directory.rglob('*')) if p.is_file()}
    (results / (arm + '-file-hashes.json')).write_text(json.dumps(inventories[arm], indent=2) + '\n')
if inventories['candidate'] != inventories['control']:
    raise ValueError('Base-only comparison changed the application payload')
candidate = (results / 'candidate-runtimes.txt').read_text().splitlines()
control = (results / 'control-runtimes.txt').read_text().splitlines()
if candidate != control or len(candidate) != 2 or not any(line.startswith('Microsoft.AspNetCore.App ') for line in candidate):
    raise ValueError('Comparison requires identical CoreCLR and ASP.NET runtime patch versions')
for arm in ('candidate', 'control'):
    config = json.loads((results / (arm + '-image.json')).read_text())[0]
    if config['Architecture'] != 'amd64' or config['Os'] != 'linux' or config['Config']['User'] not in ('app', '1654', '1654:1654'):
        raise ValueError('Comparison requires the linux/amd64 nonroot application identity')
print('Application payloads are byte-identical; CoreCLR/ASP.NET patches and nonroot identity match.')
PY

# Keep the existing actual native loader and API negotiated-authentication smoke.
bash tools/ci/test-api-runtime-dependency.sh --image "$candidate" 2>&1 | tee "$results/gssapi-application-smoke.log"
dotnet new console --framework net10.0 --name RuntimeProbe --output "$temporary/probe" --no-restore
cat > "$temporary/probe/Program.cs" <<'CS'
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
if (Native.getuid() != 1654 || Native.getgid() != 1654) throw new Exception("Application UID/GID changed.");
foreach (var library in new[] { "libgssapi_krb5.so.2", "libssl.so.3" })
    NativeLibrary.Free(NativeLibrary.Load(library));
if (1234.5m.ToString("F1", CultureInfo.GetCultureInfo("fr-FR")) != "1234,5") throw new Exception("ICU formatting failed.");
if (TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg").BaseUtcOffset != TimeSpan.FromHours(2)) throw new Exception("Timezone lookup failed.");
using var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
roots.Open(OpenFlags.ReadOnly);
if (roots.Certificates.Count == 0) throw new Exception("System trust roots are missing.");
foreach (var file in new[] { "/app/licenses/LICENSE", "/app/licenses/NOTICE", "/app/THIRD-PARTY-NOTICES.txt", "/app/runtime.spdx.json" })
    if (File.ReadAllBytes(file).Length == 0) throw new Exception("Legal/SBOM files are unreadable or empty.");
foreach (var directory in new[] { "/app", "/var/netratel", "/var/netratel/keys" })
{
    var file = Path.Combine(directory, "api-image-diagnostic-probe");
    File.WriteAllText(file, "synthetic diagnostic");
    File.Delete(file);
}
Console.WriteLine("UID/GID 1654, app/keys writes, legal files, GSSAPI/OpenSSL, ICU, timezone and trust roots passed.");
static partial class Native
{
    [LibraryImport("libc")] internal static partial uint getuid();
    [LibraryImport("libc")] internal static partial uint getgid();
}
CS
dotnet publish "$temporary/probe/RuntimeProbe.csproj" --configuration Release --output "$temporary/probe-output" -p:AllowUnsafeBlocks=true
chmod -R a+rX "$temporary/probe-output"
docker run --rm --volume "$temporary/probe-output:/probe:ro" --entrypoint dotnet "$candidate" /probe/RuntimeProbe.dll \
  | tee "$results/native-runtime-probe.log"

# Candidate is unpublished: select the latest completed publication, including
# the current product version, and authenticate its fixed upgrade companions.
python3 - "$results/selected-release.json" <<'PY'
import importlib.util, json, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location('prior', 'tools/ci/select-prior-release.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
Path(sys.argv[1]).write_text(json.dumps(module.select(None), indent=2) + '\n')
PY
prior_tag="$(jq -r '.tag' "$results/selected-release.json")"
prior_version="${prior_tag#v}"
mkdir "$temporary/release"
gh release download "$prior_tag" --repo BostonTechnologies/netratel --dir "$temporary/release" \
  --pattern publication.json --pattern SHA256SUMS \
  --pattern "netratel-cli-${prior_version}-linux-x64.tar.gz" --pattern "netratel-compose-${prior_version}.tar.gz"
gh api "repos/BostonTechnologies/netratel/releases/tags/$prior_tag" > "$results/selected-release-metadata.json"
python3 - "$temporary/release" "$results/selected-release.json" "$results/selected-release-metadata.json" <<'PY'
import hashlib, json, sys
from pathlib import Path
directory, selected = Path(sys.argv[1]), json.loads(Path(sys.argv[2]).read_text())
metadata = json.loads(Path(sys.argv[3]).read_text())
if metadata.get('draft') is not False or not metadata.get('published_at') or metadata.get('tag_name') != selected['tag']:
    raise ValueError('Selected release is not published')
assets = {item['name']: item for item in metadata['assets'] if item.get('state') == 'uploaded'}
for path in directory.iterdir():
    asset = assets[path.name]
    if (asset.get('digest'), asset.get('size')) != ('sha256:' + hashlib.sha256(path.read_bytes()).hexdigest(), path.stat().st_size):
        raise ValueError('Downloaded release asset differs: ' + path.name)
record = json.loads((directory / 'publication.json').read_text())
if (record['productVersion'], record['images'], record['verification']['state']) != (selected['tag'][1:], selected['images'], 'complete'):
    raise ValueError('Downloaded publication differs from the selected completed release')
sums = dict(line.split(maxsplit=1)[::-1] for line in (directory / 'SHA256SUMS').read_text().splitlines())
for path in directory.glob('*.tar.gz'):
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != record['artifacts'][path.name] or digest != sums.get(path.name):
        raise ValueError('Downloaded release archive checksum differs: ' + path.name)
PY
export NETRATEL_UPGRADE_PRIOR_VERSION="$prior_tag"
export NETRATEL_UPGRADE_PRIOR_API_IMAGE="$(jq -r '.images.api' "$results/selected-release.json")"
export NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE="$(jq -r '.images.migrations' "$results/selected-release.json")"
export NETRATEL_UPGRADE_PRIOR_WEB_IMAGE="$(jq -r '.images.web' "$results/selected-release.json")"
export NETRATEL_UPGRADE_CURRENT_API_IMAGE="$candidate"
export NETRATEL_UPGRADE_CURRENT_WEB_IMAGE="$NETRATEL_UPGRADE_PRIOR_WEB_IMAGE"
export NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE="$NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE"
export NETRATEL_UPGRADE_COMPOSE_BUNDLE="$temporary/release/netratel-compose-${prior_version}.tar.gz"
export NETRATEL_UPGRADE_CURRENT_CLI_ARCHIVE="$temporary/release/netratel-cli-${prior_version}-linux-x64.tar.gz"
export NETRATEL_UPGRADE_API_SHUTDOWN_EVIDENCE="$results/api-shutdown.json"
bash tools/ci/smoke-postgresql-local-upgrade.sh 2>&1 | tee "$results/latest-release-upgrade.log"

stack_images=()
comparison_args=()
for component in web migrations mcp-http client; do
  published="$(jq -r --arg component "$component" '.images[$component]' "$results/selected-release.json")"
  docker pull "$published"
  alias="netratel-stack-${component}:measurement"
  docker tag "$published" "$alias"
  stack_images+=("$alias")
  comparison_args+=(--companion "$alias")
done
mkdir "$temporary/layers"
docker image save "$candidate" "$control" "${stack_images[@]}" -o "$temporary/images.tar"
tar -xf "$temporary/images.tar" -C "$temporary/layers"
python3 tools/ci/compare-stack-image-layers.py --directory "$temporary/layers" \
  --output "$results/layer-export-comparison.json" --source-sha "$revision" \
  --candidate "$candidate" --control "$control" "${comparison_args[@]}"
echo 'API image investigation passed its selected probes; production adoption and broader functional acceptance remain pending.'
