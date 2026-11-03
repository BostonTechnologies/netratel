#!/usr/bin/env bash
# Optional hosted RID comparison and actual-image checks; never publishes images.
set -euo pipefail
[[ "${GITHUB_ACTIONS:-false}" == true ]] || { echo 'Use the optional http-host-setup workflow profile.' >&2; exit 2; }
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
results="${NETRATEL_MCP_HTTP_IMAGE_RESULTS:-$root/TestResults/mcp-http-image}"
mkdir -p "$results"
results="$(cd "$results" && pwd)"
revision="$(git rev-parse HEAD)"
version="$(python3 tools/ci/product-version.py)"
candidate=netratel-mcp-http:manual-setup
control=netratel-mcp-http:rid-neutral-control
temporary="$(mktemp -d)"
container=""
stdio_container=""
cleanup() {
  status=$?
  if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
  if [[ -n "$stdio_container" ]]; then docker rm -f "$stdio_container" >/dev/null 2>&1 || true; fi
  find "$temporary" -depth -delete 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT
printf '%s\n' "$revision" > "$results/source-sha.txt"
dotnet --info > "$results/sdk.txt"
for project in NetRatel.Mcp.Http NetRatel.Cli NetRatel.Mcp; do
  dotnet msbuild "src/NetRatel/$project/$project.csproj" -nologo \
    -p:Configuration=Release -p:RuntimeIdentifier=linux-x64 -p:SelfContained=false -p:UseAppHost=false \
    -getProperty:TargetFramework,RuntimeIdentifier,SelfContained,PublishTrimmed,PublishAot,UseAppHost \
    > "$results/$project-publish-properties.json"
done

# Both arms get an immutable source snapshot. Only RID selections differ;
# shared framework mode, source identity and every packaging step stay fixed.
mkdir "$temporary/source"
git archive HEAD | tar -x -C "$temporary/source"
python3 - "$temporary/source/docker/mcp-http/Dockerfile" "$temporary/control.Dockerfile" <<'PY'
from pathlib import Path
import sys
source = Path(sys.argv[1]).read_text()
selection = ' --runtime linux-x64'
if source.count(selection) != 7:
    raise ValueError('Expected one Linux RID selection for each restore, publish and combined SBOM')
neutral = source.replace(selection, '')
Path(sys.argv[2]).write_text(''.join(line for line in neutral.splitlines(keepends=True) if line.strip() != '\\'))
PY
cp "$temporary/source/docker/mcp-http/Dockerfile" "$results/candidate.Dockerfile"
cp "$temporary/control.Dockerfile" "$results/control.Dockerfile"
for arm in candidate control; do
  image="$candidate"; dockerfile=docker/mcp-http/Dockerfile
  if [[ "$arm" == control ]]; then image="$control"; dockerfile="$temporary/control.Dockerfile"; fi
  (cd "$temporary/source"
    bash tools/ci/build-public-image.sh --dockerfile "$dockerfile" \
      --image "$image" --version "$version" --revision "$revision"
  ) 2>&1 | tee "$results/$arm-build.log"
  bash tools/ci/scan-public-image.sh --image "$image" --version "$version" --revision "$revision" \
    2>&1 | tee "$results/$arm-scan.log"
  docker image inspect "$image" > "$results/$arm-image.json"
  mkdir -p "$temporary/$arm/out"
  container="$(docker create --network none --entrypoint /bin/true "$image")"
  docker cp "$container:/app/." "$temporary/$arm/out/"
  docker rm "$container" >/dev/null; container=""
  cp "$temporary/$arm/out/runtime.spdx.json" "$results/$arm-runtime.spdx.json"
  python3 - "$temporary/$arm/out" "$results/$arm-runtime-graphs" <<'PY'
from pathlib import Path
import shutil, sys
root, output = map(Path, sys.argv[1:])
for path in root.rglob('*.json'):
    if path.name.endswith(('.deps.json', '.runtimeconfig.json')):
        target = output / path.relative_to(root)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)
PY
  python3 tools/ci/verify-runtime-sbom.py --sbom "$temporary/$arm/out/runtime.spdx.json" --root "$temporary/$arm" \
    | tee "$results/$arm-sbom-check.log"
  docker run --rm --network none --entrypoint sh "$image" -ceu '
    test "$(id -u)" = 10001
    test -w /setup-output
    test -w /var/lib/netratel/clients
    test -s /etc/ssl/certs/ca-certificates.crt
    test -r /app/licenses/LICENSE
    test -r /app/licenses/NOTICE
    id
    dotnet --list-runtimes
  ' > "$results/$arm-runtime.txt"
  docker run --rm --network none "$image" cli --help > "$results/$arm-cli-help.txt"
  docker run --rm --network none "$image" cli --version > "$results/$arm-cli-version.txt"
  docker run --rm --network none "$image" stdio http-bridge --version > "$results/$arm-bridge-version.txt"
  docker run --rm --network none "$image" setup --help > "$results/$arm-setup-help.txt"

  # Exercise the actual bundled stdio dispatcher, with no backend network.
  stdio_container="netratel-rid-stdio-${GITHUB_RUN_ID}-${arm}"
  coproc MCP_STDIO {
    docker run --rm --interactive --name "$stdio_container" --network none \
      --mount "type=bind,source=$root/tests/fixtures/mcp-http-smoke-config.json,target=/run/netratel/mcp-config.json,readonly" \
      -e NETRATEL_MCP_CONFIG=/run/netratel/mcp-config.json -e NETRATEL_MCP_INSTANCE=dev \
      "$image" stdio 2> "$results/$arm-stdio.stderr"
  }
  mcp_pid="$MCP_STDIO_PID"
  output_fd="${MCP_STDIO[0]}"
  input_fd="${MCP_STDIO[1]}"
  call() {
    printf '%s\n' "$1" >&"$input_fd"
    IFS= read -r -t 10 response <&"$output_fd"
    printf '%s\n' "$response" >> "$results/$arm-stdio.jsonl"
  }
  call '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"rid-image-check","version":"1"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}' >&"$input_fd"
  call '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  call '{"jsonrpc":"2.0","id":3,"method":"resources/list","params":{}}'
  call '{"jsonrpc":"2.0","id":4,"method":"prompts/list","params":{}}'
  call '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"get"}}}'
  call '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"status"}}}'
  docker rm -f "$stdio_container" >/dev/null; stdio_container=""
  wait "$mcp_pid" || true
done
python3 tools/ci/inspect-mcp-http-publish.py --candidate "$temporary/candidate/out" --control "$temporary/control/out" \
  --source-sha "$revision" --output "$results/file-comparison.json" | tee "$results/file-comparison-summary.json"
python3 - "$results" "$version" "$revision" <<'PY'
import json, sys
from pathlib import Path
root, version, revision = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
configs = {}
protocol = {}
for arm in ('candidate', 'control'):
    image = json.loads((root / (arm + '-image.json')).read_text())[0]
    if image['Os'] != 'linux' or image['Architecture'] != 'amd64':
        raise ValueError('RID comparison requires the supported Linux x64 image')
    configs[arm] = {key: image['Config'].get(key) for key in (
        'User', 'Entrypoint', 'Cmd', 'WorkingDir', 'Env', 'Healthcheck', 'ExposedPorts', 'Labels', 'Volumes', 'StopSignal', 'Shell')}
    for name in ('cli', 'bridge'):
        value = (root / (arm + '-' + name + '-version.txt')).read_text().strip()
        if value != version + '+' + revision:
            raise ValueError('Bundled tooling release identity differs: ' + arm + '/' + name)
    responses = [json.loads(line) for line in (root / (arm + '-stdio.jsonl')).read_text().splitlines()]
    if [item.get('id') for item in responses] != list(range(1, 7)):
        raise ValueError('Stdio emitted non-protocol stdout or incomplete responses')
    first = responses[0]['result']['serverInfo']
    if first['name'] != 'NetRatel.Mcp' or first['version'] not in (version, version + '+' + revision):
        raise ValueError('Stdio initialize identity differs')
    for response, field in zip(responses[1:4], ('tools', 'resources', 'prompts')):
        if not response['result'][field]: raise ValueError('Stdio catalog is empty: ' + field)
    if not responses[4]['result']['structuredContent']['success']:
        raise ValueError('Stdio capabilities call failed')
    status = responses[5]['result']['structuredContent']
    data = status['data']
    if not status['success'] or data['hostRelease']['productVersion'] != version or data['hostRelease']['sourceRevision'] != revision:
        raise ValueError('Stdio status release identity differs')
    protocol[arm] = {'catalog': [item['result'] for item in responses[1:4]], 'digest': data['catalog']['digest']}
if configs['candidate'] != configs['control']:
    raise ValueError('RID selection changed the image entrypoint, health, user or environment configuration')
if protocol['candidate'] != protocol['control']:
    raise ValueError('RID selection changed the stdio catalog/schema/resources/prompts digest')
(root / 'stdio-catalog-comparison.json').write_text(json.dumps(protocol, indent=2) + '\n')
print('Both actual images retain tooling identities, nonroot state writes and identical stdio catalog/schema/resources/prompts.')
PY
cmp "$results/candidate-setup-help.txt" "$results/control-setup-help.txt"
grep -Fq 'Windows protected launcher' "$results/candidate-setup-help.txt"

# Existing authenticated TLS/OIDC protocol checks run against the candidate.
NETRATEL_MCP_HTTP_SMOKE_IMAGE="$candidate" VERSION="$version" SOURCE_SHA="$revision" \
  NETRATEL_MCP_HTTP_SMOKE_RESULTS="$results/http-protocol" \
  bash tools/ci/smoke-mcp-http-image.sh 2>&1 | tee "$results/http-protocol.log"

# Hold all four companion images at one completed publication. These are local
# normalized layer exports, not registry pull sizes or a publication rehearsal.
python3 - "$results/selected-release.json" <<'PY'
import importlib.util, json, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location('prior', 'tools/ci/select-prior-release.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
Path(sys.argv[1]).write_text(json.dumps(module.select(None), indent=2) + '\n')
PY
stack_images=(); comparison_args=()
for component in api web migrations client; do
  published="$(jq -r --arg component "$component" '.images[$component]' "$results/selected-release.json")"
  docker pull "$published"
  alias="netratel-stack-${component}:mcp-measurement"
  docker tag "$published" "$alias"
  stack_images+=("$alias"); comparison_args+=(--companion "$alias")
done
mkdir "$temporary/layers"
docker image save "$candidate" "$control" "${stack_images[@]}" | tar -xf - -C "$temporary/layers"
python3 tools/ci/compare-stack-image-layers.py --directory "$temporary/layers" \
  --output "$results/layer-export-comparison.json" --source-sha "$revision" \
  --candidate "$candidate" --control "$control" "${comparison_args[@]}"
echo 'RID image comparison passed; the following existing setup smoke owns durable host/client and tenant-policy acceptance.'
