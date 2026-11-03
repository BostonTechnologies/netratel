#!/usr/bin/env bash
# Optional hosted acceptance for the actual migrations image; never publishes images.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

results="${NETRATEL_MIGRATIONS_RESULTS:-$root/TestResults/migrations-image}"
mkdir -p "$results"
results="$(cd "$results" && pwd)"
revision="$(git rev-parse HEAD)"
version="$(python3 tools/ci/product-version.py)"
candidate=netratel-migrations:runtime-candidate
control=netratel-migrations:aspnet-control
git rev-parse HEAD > "$results/source-sha.txt"
dotnet --info > "$results/sdk.txt"
dotnet msbuild src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj -nologo \
  -p:Configuration=Release -p:UseAppHost=false \
  -getProperty:TargetFramework,RuntimeIdentifier,SelfContained,PublishTrimmed,PublishAot,UseAppHost \
  > "$results/publish-properties.json"
temporary="$(mktemp -d)"
network="netratel-migrations-${GITHUB_RUN_ID:-manual}-${RANDOM}"
postgres="${network}-postgres"
container=""
cleanup() {
  status=$?
  if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
  docker logs "$postgres" > "$results/postgres.log" 2>&1 || true
  docker rm -f "$postgres" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  find "$temporary" -depth -delete 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

# Both Docker builds must see the same immutable source; captured logs and
# evaluated host intermediates must not invalidate their shared publish stage.
mkdir -p "$temporary/source"
git archive HEAD | tar -x -C "$temporary/source"
python3 - "$temporary/control.Dockerfile" <<'PY'
from pathlib import Path
import sys
source = Path('docker/migrations/Dockerfile').read_text()
base = 'FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS final'
if source.count(base) != 1:
    raise ValueError('Expected the reviewed CoreCLR migrations base')
Path(sys.argv[1]).write_text(source.replace(base, base.replace('/runtime:', '/aspnet:')))
PY
(cd "$temporary/source"
  bash tools/ci/build-public-image.sh --dockerfile docker/migrations/Dockerfile \
    --image "$candidate" --version "$version" --revision "$revision"
) 2>&1 | tee "$results/candidate-build.log"
(cd "$temporary/source"
  bash tools/ci/build-public-image.sh --dockerfile "$temporary/control.Dockerfile" \
    --image "$control" --version "$version" --revision "$revision"
) 2>&1 | tee "$results/control-build.log"
bash tools/ci/scan-public-image.sh --image "$candidate" --version "$version" --revision "$revision" \
  2>&1 | tee "$results/candidate-scan.log"

for arm in candidate control; do
  image="$candidate"; if [[ "$arm" == control ]]; then image="$control"; fi
  docker image inspect "$image" > "$results/$arm-image.json"
  # The existing SBOM inventories the logical /out publish directory, copied to /app in the image.
  mkdir -p "$temporary/$arm/out"
  container="$(docker create "$image")"
  docker cp "$container:/app/." "$temporary/$arm/out/"
  docker rm "$container" >/dev/null; container=""
  cp "$temporary/$arm/out/NetRatel.Migrations.runtimeconfig.json" "$results/$arm-runtimeconfig.json"
  cp "$temporary/$arm/out/runtime.spdx.json" "$results/$arm-runtime.spdx.json"
  python3 tools/ci/verify-runtime-sbom.py --sbom "$temporary/$arm/out/runtime.spdx.json" --root "$temporary/$arm" \
    | tee "$results/$arm-sbom-check.log"
  docker run --rm --entrypoint sh "$image" -ceu '
    test "$(id -u)" != 0
    test -w /var/netratel
    id -u
    dotnet --list-runtimes
    dpkg-query -W ca-certificates libicu74 tzdata libgssapi-krb5-2
  ' > "$results/$arm-runtime.txt"
done
python3 - "$temporary" "$results" <<'PY'
import hashlib, json, sys
from pathlib import Path
temporary, results = map(Path, sys.argv[1:])
inventories = {}
for arm in ('candidate', 'control'):
    directory = temporary / arm / 'out'
    inventories[arm] = {p.relative_to(directory).as_posix(): {'sha256': hashlib.sha256(p.read_bytes()).hexdigest(), 'bytes': p.stat().st_size}
                        for p in sorted(directory.rglob('*')) if p.is_file()}
    (results / (arm + '-file-hashes.json')).write_text(json.dumps(inventories[arm], indent=2) + '\n')
if inventories['candidate'] != inventories['control']:
    raise ValueError('The base-only comparison changed the application payload')
config = json.loads((results / 'candidate-runtimeconfig.json').read_text())['runtimeOptions']
frameworks = config.get('frameworks', [config['framework']] if 'framework' in config else [])
if {item['name'] for item in frameworks} != {'Microsoft.NETCore.App'}:
    raise ValueError('Migrations requires a framework beyond Microsoft.NETCore.App')
candidate = (results / 'candidate-runtime.txt').read_text().splitlines()
control = (results / 'control-runtime.txt').read_text().splitlines()
candidate_core = [line for line in candidate if line.startswith('Microsoft.NETCore.App ')]
control_core = [line for line in control if line.startswith('Microsoft.NETCore.App ')]
if len(candidate_core) != 1 or candidate_core != control_core:
    raise ValueError('The controlled comparison requires identical CoreCLR patch versions')
if candidate[0] != control[0] or candidate[0] == '0' or any('Microsoft.AspNetCore.App' in line for line in candidate):
    raise ValueError('Candidate runtime or nonroot identity differs from the expected boundary')
print('Both application payloads are byte-identical; candidate requires CoreCLR only and retains the nonroot UID.')
PY

# Execute managed globalization/timezone/trust and native loading on the candidate runtime.
dotnet new console --framework net10.0 --name RuntimeProbe --output "$temporary/probe" --no-restore
cat > "$temporary/probe/Program.cs" <<'CS'
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
foreach (var library in new[] { "libgssapi_krb5.so.2", "libssl.so.3" })
    NativeLibrary.Free(NativeLibrary.Load(library));
if (1234.5m.ToString("F1", CultureInfo.GetCultureInfo("fr-FR")) != "1234,5")
    throw new Exception("ICU culture formatting failed.");
if (TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg").BaseUtcOffset != TimeSpan.FromHours(2))
    throw new Exception("Timezone lookup failed.");
using var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
roots.Open(OpenFlags.ReadOnly);
if (roots.Certificates.Count == 0) throw new Exception("System trust roots are missing.");
Console.WriteLine("GSSAPI, OpenSSL, ICU, timezone and trust roots passed.");
CS
dotnet publish "$temporary/probe/RuntimeProbe.csproj" --configuration Release --output "$temporary/probe-output"
chmod -R a+rX "$temporary/probe-output"
docker run --rm --volume "$temporary/probe-output:/probe:ro" --entrypoint dotnet "$candidate" /probe/RuntimeProbe.dll \
  | tee "$results/native-runtime-probe.log"

docker network create "$network" >/dev/null
docker run --detach --name "$postgres" --network "$network" \
  -e POSTGRES_DB=netratel -e POSTGRES_USER=netratel -e POSTGRES_PASSWORD=synthetic-migrations-only \
  --health-cmd='pg_isready -U netratel -d netratel' --health-interval=1s --health-retries=60 \
  postgres:17-alpine >/dev/null
for _ in $(seq 1 60); do
  [[ "$(docker inspect --format '{{.State.Health.Status}}' "$postgres")" == healthy ]] && break
  sleep 1
done
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$postgres")" == healthy ]]
connection="Host=$postgres;Database=netratel;Username=netratel;Password=synthetic-migrations-only"
migrate() {
  timeout 180 docker run --rm --network "$network" -e "ConnectionStrings__NetRatelDb=$connection" "$candidate"
}
sql() { docker exec -i "$postgres" psql -X -v ON_ERROR_STOP=1 -U netratel -d netratel -Atq "$@"; }
migrate > "$results/fresh-migrations.log" 2>&1
sql -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"' > "$results/applied-migrations.txt"
python3 - "$results/applied-migrations.txt" <<'PY'
from pathlib import Path
import re, sys
actual = set(Path(sys.argv[1]).read_text().splitlines())
for group in ('Persistence', 'Identity'):
    expected = {match for p in Path('src/NetRatel/NetRatel.Infrastructure', group, 'Migrations').glob('*.cs')
                for match in re.findall(r'\[Migration\("([^"]+)"\)\]', p.read_text())}
    if not expected or not expected <= actual:
        raise ValueError(f'{group} history missing: {sorted(expected - actual)}')
    print(f'{group}: all {len(expected)} source migrations applied.')
PY
role_checks="$(sql <<'SQL'
SELECT count(*) = 1 FROM "AccessRoles" WHERE "Name" = 'InstanceAdministrator' AND "IsBuiltIn" AND "IsInstanceAdministratorRole";
SELECT count(*) = 1 FROM "AccessRoles" WHERE "Name" = 'Observer' AND "IsBuiltIn" AND NOT "IsInstanceAdministratorRole";
SELECT count(*) = 1 FROM "AccessRolePermissions" WHERE "RoleId" IN (SELECT "Id" FROM "AccessRoles" WHERE "Name" = 'Observer') AND "Permission" = 'audit.read';
SQL
)"
[[ "$role_checks" == $'t\nt\nt' ]]
sql <<'SQL'
CREATE TABLE campaign_sentinel (id integer PRIMARY KEY, payload text NOT NULL);
INSERT INTO campaign_sentinel VALUES (1, 'retain synthetic migration state');
UPDATE "AccessRoles" SET "Description" = 'Retain synthetic role customization' WHERE "Name" = 'Observer';
DELETE FROM "AccessRolePermissions" WHERE "RoleId" IN (SELECT "Id" FROM "AccessRoles" WHERE "Name" = 'Observer') AND "Permission" = 'audit.read';
SQL
cat > "$temporary/snapshot.sql" <<'SQL'
SELECT json_build_object(
  'roles', (SELECT json_agg(r ORDER BY r."Id") FROM "AccessRoles" r),
  'permissions', (SELECT json_agg(p ORDER BY p."RoleId", p."Permission") FROM "AccessRolePermissions" p),
  'histories', (SELECT json_agg(h ORDER BY h."MigrationId") FROM "__EFMigrationsHistory" h),
  'sentinel', (SELECT json_agg(s ORDER BY s.id) FROM campaign_sentinel s));
SQL
sql < "$temporary/snapshot.sql" > "$results/state-before-rerun.json"
migrate > "$results/rerun-migrations.log" 2>&1
sql < "$temporary/snapshot.sql" > "$results/state-after-rerun.json"
cmp "$results/state-before-rerun.json" "$results/state-after-rerun.json"
set +e
timeout 30 docker run --rm "$candidate" > "$results/expected-failure.log" 2>&1
failure_status=$?
set -e
printf '%s\n' "$failure_status" > "$results/expected-failure-exit.txt"
[[ "$failure_status" != 0 && "$failure_status" != 124 ]]
grep -Fq 'A database connection string is required' "$results/expected-failure.log"
echo 'Fresh migration, state/role-preserving rerun and failure propagation passed.'

# Reuse the repository selector, including the current product version because this candidate is unpublished.
python3 - "$results/selected-release.json" <<'PY'
import importlib.util, json, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location('prior', 'tools/ci/select-prior-release.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
Path(sys.argv[1]).write_text(json.dumps(module.select(None), indent=2) + '\n')
PY
prior_tag="$(jq -r '.tag' "$results/selected-release.json")"
prior_version="${prior_tag#v}"
mkdir -p "$temporary/release"
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
export NETRATEL_UPGRADE_CURRENT_API_IMAGE="$NETRATEL_UPGRADE_PRIOR_API_IMAGE"
export NETRATEL_UPGRADE_CURRENT_WEB_IMAGE="$NETRATEL_UPGRADE_PRIOR_WEB_IMAGE"
export NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE="$candidate"
export NETRATEL_UPGRADE_COMPOSE_BUNDLE="$temporary/release/netratel-compose-${prior_version}.tar.gz"
export NETRATEL_UPGRADE_CURRENT_CLI_ARCHIVE="$temporary/release/netratel-cli-${prior_version}-linux-x64.tar.gz"
bash tools/ci/smoke-postgresql-local-upgrade.sh 2>&1 | tee "$results/latest-release-upgrade.log"

# Compare complete images and the five-image stack without any registry writes.
# A fixed gzip encoding makes local layer exports comparable; these are not registry pull bytes.
stack_images=()
comparison_args=()
for component in api web mcp-http client; do
  published="$(jq -r --arg component "$component" '.images[$component]' "$results/selected-release.json")"
  docker pull "$published"
  alias="netratel-stack-${component}:measurement"
  docker tag "$published" "$alias"
  stack_images+=("$alias")
  comparison_args+=(--companion "$alias")
done
mkdir -p "$temporary/layers"
docker image save "$candidate" "$control" "${stack_images[@]}" -o "$temporary/images.tar"
tar -xf "$temporary/images.tar" -C "$temporary/layers"
python3 tools/ci/compare-stack-image-layers.py --directory "$temporary/layers" \
  --output "$results/layer-export-comparison.json" --source-sha "$revision" \
  --candidate "$candidate" --control "$control" "${comparison_args[@]}"
echo 'Migrations image acceptance passed; candidate images remain local to this runner.'
