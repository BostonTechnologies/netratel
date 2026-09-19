#!/usr/bin/env bash
# Fast launcher contract test: no Docker daemon or checkout .env is required.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
temporary="$(mktemp -d -t netratel-oidc-evaluation-test.XXXXXX)"
trap 'rm -rf "$temporary"' EXIT
mock_bin="$temporary/bin"
mkdir -p "$mock_bin"

cat > "$mock_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
arguments=("$@")
for ((index = 0; index < ${#arguments[@]}; index++)); do
  if [[ "${arguments[index]}" == "--env-file" ]]; then
    printf '%q ' "${arguments[@]}" >> "$(dirname "${arguments[index + 1]}")/docker.log"
    printf '\n' >> "$(dirname "${arguments[index + 1]}")/docker.log"
    exit 0
  fi
done
echo "mock docker did not receive --env-file" >&2
exit 1
EOF
chmod +x "$mock_bin/docker"

workspace_one="$temporary/one"
workspace_two="$temporary/two"
run_clean() {
  env -i PATH="$mock_bin:$PATH" "$@"
}

run_clean NETRATEL_WEB_PORT=30123 "$root/tools/dev/oidc-evaluation.sh" start "$workspace_one"
run_clean "$root/tools/dev/oidc-evaluation.sh" start "$workspace_two"

source "$workspace_one/state.env"
project_one="$NETRATEL_EVALUATION_PROJECT"
[[ "$NETRATEL_WEB_PORT" == 30123 ]]
[[ "$NETRATEL_EVALUATION_WORKSPACE" == "$workspace_one" ]]
source "$workspace_two/state.env"
project_two="$NETRATEL_EVALUATION_PROJECT"
[[ "$project_one" != "$project_two" ]]
[[ "$NETRATEL_WEB_PORT" != 30123 ]]
[[ "$(grep -c -- '--project-name' "$workspace_one/docker.log")" == 2 ]]

(cd "$temporary" && run_clean "$root/tools/dev/oidc-evaluation.sh" stop one)
grep -Fq -- "--project-name $project_one" "$workspace_one/docker.log"
grep -Fq -- ' down --volumes --remove-orphans' "$workspace_one/docker.log"
[[ "$(grep -c -- '--project-name' "$workspace_two/docker.log")" == 2 ]]
echo "OIDC evaluation launcher state/isolation checks passed."
