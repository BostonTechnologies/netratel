#!/usr/bin/env bash
# One-shot setup for an existing single-file Compose/Komodo deployment.
set -euo pipefail
umask 077

usage() {
  cat <<'USAGE'
Connect an existing HTTP MCP server to NetRatel using its actual Compose file.

Usage:
  ./setup-http-mcp.sh --compose-file /absolute/compose.yaml --project STACK \
    --manager komodo [--env-file /absolute/.env] [--target http-host|http-client|cli|stdio] [--verify] \
    [-- wizard-options...]

Run on the deployment host as a user allowed to use Docker. The default target
is http-host (server to platform). http-client saves a separate client profile.
No compose.setup.yaml is required. Existing mounts and service settings are
inherited; missing private setup/client volumes are attached for the wizard.

For host setup, choose Export and exit after Save. This launcher copies the
complete private compose.komodo.yaml beside your existing deployment file.
Import that file into Komodo's Compose editor, Save/Deploy, then run the printed
launcher command with --verify. Keep the private setup volume.
USAGE
}

die() { printf 'Setup not started: %s\n' "$*" >&2; exit 1; }
compose_file= project= manager= env_file= target=http-host verify=false
wizard_args=()
while (($#)); do
  case "$1" in
    --compose-file|--project|--manager|--env-file|--target)
      (($# >= 2)) || die "Missing value for $1. Use --help."
      case "$1" in
        --compose-file) compose_file=$2 ;;
        --project) project=$2 ;;
        --manager) manager=$2 ;;
        --env-file) env_file=$2 ;;
        --target) target=$2 ;;
      esac
      shift 2 ;;
    --verify) verify=true; shift ;;
    --) shift; wizard_args=("$@"); break ;;
    --help|-h) usage; exit 0 ;;
    *) die "Unknown option $1. Use --help." ;;
  esac
done

[[ -n "$compose_file" && -n "$project" && -n "$manager" ]] || die 'Supply --compose-file, --project and --manager komodo. Use --help.'
[[ "$manager" == komodo ]] || die 'This launcher supports --manager komodo for an existing single Compose file. Use the release-bundle setup command for its overlay deployment.'
[[ "$target" == http-host || "$target" == http-client || "$target" == cli || "$target" == stdio ]] || die 'Select --target http-host, http-client, cli or stdio.'
[[ "$verify" == false || "$target" == http-host ]] || die '--verify resumes saved server setup; client setup verifies within its own wizard.'
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || die 'Use the actual Compose project name shown by docker compose ls.'
[[ "$compose_file" == /* && -f "$compose_file" && ! -L "$compose_file" ]] || die '--compose-file must name an existing absolute regular file, not a symlink.'
case "$compose_file" in *:*|*,*|*$'\n'*) die 'The Compose file path cannot contain a colon, comma or newline.' ;; esac
for command in docker realpath stat mktemp sha256sum; do command -v "$command" >/dev/null || die "Required command is unavailable: $command"; done
compose_file=$(realpath -- "$compose_file")
compose_dir=${compose_file%/*}
if [[ -z "$env_file" && -f "$compose_dir/.env" ]]; then env_file=$compose_dir/.env; fi
if [[ -n "$env_file" ]]; then
  [[ "$env_file" == /* && -f "$env_file" ]] || die '--env-file must name the actual existing absolute environment file.'
  env_file=$(realpath -- "$env_file")
fi

# Keep exported documents and any validation diagnostics private. The source may
# already contain owner-supplied secrets; never print a rendered Compose file.
output_dir=$compose_dir/.netratel-http-mcp-$project
[[ ! -L "$output_dir" ]] || die 'The private setup output directory cannot be a symlink.'
if [[ ! -d "$output_dir" ]]; then mkdir -m 700 -- "$output_dir"; fi
[[ $(stat -c %u -- "$output_dir") == "$(id -u)" && $(stat -c %a -- "$output_dir") == 700 ]] || die 'The existing private setup output directory must be owned by this user with mode 0700.'
compose=(docker compose --project-directory "$compose_dir" --project-name "$project")
if [[ -n "$env_file" ]]; then
  compose+=(--env-file "$env_file")
else
  # Do not accidentally load an unrelated .env from the launcher's directory.
  compose+=(--env-file /dev/null)
fi
compose+=(-f "$compose_file")
if ! "${compose[@]}" config --quiet >"$output_dir/preflight.log" 2>&1; then
  die "The actual Compose deployment failed validation. Private diagnostics: $output_dir/preflight.log. No setup container was started."
fi
services=$("${compose[@]}" config --services 2>>"$output_dir/preflight.log")
[[ $'\n'"$services"$'\n' == *$'\nmcp-http\n'* ]] || die 'The selected Compose file has no mcp-http service.'

setup_volume=$project-http-host-setup
client_volume=$project-http-client-data
existing_setup_volume= existing_client_volume=
# Inspect only mount/label metadata here, never complete environment values.
container_ids=$("${compose[@]}" ps --all --quiet mcp-http 2>>"$output_dir/preflight.log")
for container_id in $container_ids; do
  oneoff=$(docker inspect --format '{{index .Config.Labels "com.docker.compose.oneoff"}}' "$container_id")
  [[ "$oneoff" != True && "$oneoff" != true ]] || continue
  for destination in /setup-output /var/lib/netratel/clients; do
    mount=$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "'"$destination"'"}}{{.Type}}|{{.Name}}{{end}}{{end}}' "$container_id")
    [[ -n "$mount" ]] || continue
    [[ "$mount" == volume\|* ]] || die "The serving container uses a custom bind mount at $destination. Keep that managed source; this named-volume launcher cannot substitute for it."
    volume=${mount#volume|}
    [[ "$volume" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] || die 'The existing named-volume metadata is invalid.'
    if [[ "$destination" == /setup-output ]]; then
      [[ -z "$existing_setup_volume" || "$existing_setup_volume" == "$volume" ]] || die 'Existing MCP containers disagree about their setup volume.'
      existing_setup_volume=$volume
      setup_volume=$volume
    else
      [[ -z "$existing_client_volume" || "$existing_client_volume" == "$volume" ]] || die 'Existing MCP containers disagree about their client volume.'
      existing_client_volume=$volume
      client_volume=$volume
    fi
  done
done

web_api_backend= web_mcp_backend= web_environment_observed=false
if [[ "$target" == http-host ]]; then
  web_ids=$("${compose[@]}" ps --status running --quiet web 2>>"$output_dir/preflight.log")
  web_container=
  for candidate in $web_ids; do
    oneoff=$(docker inspect --format '{{index .Config.Labels "com.docker.compose.oneoff"}}' "$candidate")
    [[ "$oneoff" != True && "$oneoff" != true ]] || continue
    [[ -z "$web_container" ]] || die 'More than one serving Web container belongs to this project. Select a single Web instance before server setup; no backend destination was guessed.'
    web_container=$candidate
  done
  if [[ -n "$web_container" ]]; then
    # Select exactly two non-secret routing values. Each captured record is
    # bounded, duplicates fail, and no unrelated environment entry is emitted.
    backend_template='{{$api := ""}}{{$mcp := ""}}{{$seenApi := false}}{{$seenMcp := false}}{{$duplicate := false}}{{range .Config.Env}}{{$key := index (split . "=") 0}}{{if eq $key "ApiBaseUrl"}}{{if $seenApi}}{{$duplicate = true}}{{end}}{{$seenApi = true}}{{$api = printf "%.4097s" .}}{{end}}{{if eq $key "WebGateway__Mcp__BackendUrl"}}{{if $seenMcp}}{{$duplicate = true}}{{end}}{{$seenMcp = true}}{{$mcp = printf "%.4097s" .}}{{end}}{{end}}{{if $duplicate}}DUPLICATE_WEB_BACKEND{{else}}{{if $seenApi}}{{println $api}}{{end}}{{if $seenMcp}}{{println $mcp}}{{end}}{{end}}'
    backend_metadata=$(docker inspect --format "$backend_template" "$web_container")
    web_environment_observed=true
    [[ "$backend_metadata" != DUPLICATE_WEB_BACKEND ]] || die 'The serving Web container has duplicate backend settings. Resolve the ambiguous source before server setup.'
    while IFS= read -r backend_record; do
      [[ -n "$backend_record" ]] || continue
      backend_value=${backend_record#*=}
      [[ ${#backend_value} -le 2048 && "$backend_value" != *[[:space:]]* &&
          "$backend_value" != *'@'* && "$backend_value" != *'?'* && "$backend_value" != *'#'* ]] ||
        die 'A serving Web backend URL is unsafe or oversized. Review its source; no value was printed or substituted.'
      case "$backend_record" in
        ApiBaseUrl=*) web_api_backend=$backend_value ;;
        WebGateway__Mcp__BackendUrl=*) web_mcp_backend=$backend_value ;;
        *) die 'Unexpected Web backend metadata. No destination was substituted.' ;;
      esac
    done <<< "$backend_metadata"
  fi
fi

tool_name=$project-mcp-setup-$$-$RANDOM
# The wizard writes this unique receipt only after explicit Export and exit
# or a successful reviewed headless generation. Old saved YAML is insufficient.
export_receipt=/setup-output/.export-$tool_name
if [[ "$target" == http-host ]]; then
  # A host-owned 0600 Compose file is deliberately unreadable to UID 10001.
  # Stream a bounded exact snapshot into the private volume as that UID rather
  # than relaxing the source permissions or running tooling as root.
  source_size=$(stat -c %s -- "$compose_file")
  [[ "$source_size" -gt 0 && "$source_size" -le 65536 ]] || die 'The single Compose source must be nonempty and at most 64 KiB.'
  source_hash=$(sha256sum <"$compose_file")
  source_hash=${source_hash%% *}
  stage=("${compose[@]}" run --rm --no-deps -T --entrypoint /bin/sh
    --user 10001:10001 --volume "$setup_volume:/setup-output"
    mcp-http -eu -c 'umask 077
source_temp=$(mktemp /setup-output/.compose-source.XXXXXX)
trap '\''rm -f "$source_temp"'\'' EXIT
head -c 65537 > "$source_temp"
source_size=$(wc -c < "$source_temp")
test "$source_size" -gt 0 && test "$source_size" -le 65536
chmod 600 "$source_temp"
mv -f "$source_temp" /setup-output/.compose-source.yaml
trap - EXIT
sha256sum < /setup-output/.compose-source.yaml')
  if ! staged_receipt=$("${stage[@]}" <"$compose_file" 2>"$output_dir/source-stage.log"); then
    die "The private Compose snapshot could not be prepared by the non-root tooling identity. Private diagnostics: $output_dir/source-stage.log. The original file was not changed."
  fi
  staged_hash=${staged_receipt%% *}
  source_after=$(sha256sum <"$compose_file")
  source_after=${source_after%% *}
  [[ "$staged_receipt" == "$staged_hash  -" && "$staged_hash" == "$source_hash" && "$source_after" == "$source_hash" ]] ||
    die 'The Compose source changed while preparing its private snapshot. Retry with a stable saved deployment file; no wizard was started.'
fi
run=("${compose[@]}" run --name "$tool_name" --no-deps --user 10001:10001)
if [[ ! -t 0 || ! -t 1 ]]; then run+=(-T); fi
run+=(
  --volume "$setup_volume:/setup-output"
  --volume "$client_volume:/var/lib/netratel/clients"
  --env NETRATEL_SETUP_DIRECTORY=/setup-output
  --env "NETRATEL_SETUP_VOLUME=$setup_volume"
  --env NETRATEL_CLIENT_DIRECTORY=/var/lib/netratel/clients
  --env "NETRATEL_CLIENT_VOLUME=$client_volume"
  --env NETRATEL_SETUP_DEPLOYMENT_MANAGER=komodo
  --env "NETRATEL_SETUP_COMPOSE_FILES=$compose_file"
  --env "NETRATEL_SETUP_PROJECT_NAME=$project")
if [[ "$target" == http-host && "$verify" == false ]]; then
  run+=(--env "NETRATEL_SETUP_EXPORT_RECEIPT=$export_receipt")
fi
if [[ "$target" == http-host ]]; then
  run+=(--env NETRATEL_SETUP_COMPOSE_SOURCE=/setup-output/.compose-source.yaml)
  if [[ "$web_environment_observed" == true ]]; then run+=(--env NETRATEL_SETUP_WEB_ENVIRONMENT_OBSERVED=true); fi
  if [[ -n "$web_api_backend" ]]; then run+=(--env "NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL=$web_api_backend"); fi
  if [[ -n "$web_mcp_backend" ]]; then run+=(--env "NETRATEL_SETUP_EXISTING_WEB_MCP_BACKEND_URL=$web_mcp_backend"); fi
fi
run+=(mcp-http setup --target "$target")
if [[ "$target" == http-host ]]; then
  run+=(--deployment-manager komodo --compose-file /setup-output/.compose-source.yaml)
  if [[ "$verify" == true ]]; then run+=(--verify); fi
fi
run+=("${wizard_args[@]}")
printf 'Using existing Compose project %s. Setup target: %s.\n' "$project" "$target"
printf 'Private server setup and client storage remain separate and persistent.\n'
set +e
"${run[@]}"
setup_status=$?
set -e
if [[ "$target" == http-host && ( "$setup_status" == 0 || "$setup_status" == 2 ) ]]; then
  source_after=$(sha256sum <"$compose_file")
  source_after=${source_after%% *}
  if [[ "$source_after" != "$source_hash" ]]; then
    printf 'The saved deployment Compose file changed during setup. Container %s and its generation were retained; no file was exported. Rerun against the current saved deployment.\n' "$tool_name" >&2
    exit 1
  fi
fi

if [[ "$target" == http-host && "$verify" == false && ( "$setup_status" == 0 || "$setup_status" == 2 ) ]]; then
  export_temp=$(mktemp "$output_dir/.compose.komodo.XXXXXX")
  receipt_temp=$(mktemp "$output_dir/.export-receipt.XXXXXX")
  expected_digest=
  receipt_valid=false
  if docker cp "$tool_name:$export_receipt" "$receipt_temp" >"$output_dir/export.log" 2>&1 &&
      [[ $(stat -c %s -- "$receipt_temp") == 65 ]] &&
      IFS= read -r expected_digest <"$receipt_temp" &&
      [[ "$expected_digest" =~ ^[0-9a-f]{64}$ ]]; then
    receipt_valid=true
  fi
  rm -f -- "$receipt_temp"
  if [[ "$receipt_valid" == true ]] &&
      docker cp "$tool_name:/setup-output/compose.komodo.yaml" "$export_temp" >>"$output_dir/export.log" 2>&1; then
    actual_digest=$(sha256sum <"$export_temp")
    actual_digest=${actual_digest%% *}
    if [[ "$actual_digest" != "$expected_digest" ]]; then
      rm -f -- "$export_temp"
      printf 'The generated Compose file did not match this run'\''s export receipt. Container %s and setup generation were retained; nothing was exported.\n' "$tool_name" >&2
      exit 1
    fi
    chmod 600 -- "$export_temp"
    mv -f -- "$export_temp" "$output_dir/compose.komodo.yaml"
    printf '\nPrivate complete Komodo Compose file: %s/compose.komodo.yaml\n' "$output_dir"
    if [[ "$setup_status" == 2 ]]; then
      printf 'Open that file privately and replace the entire Komodo Compose editor contents. Save and Deploy the same stack.\n'
      printf 'Configuration saved; deployment apply and verification remain pending. After deployment run:\n'
      resume=("$(realpath -- "$0")" --compose-file "$compose_file" --project "$project" --manager komodo)
      if [[ -n "$env_file" ]]; then resume+=(--env-file "$env_file"); fi
      resume+=(--verify)
      printf '  '; printf '%q ' "${resume[@]}"; printf '\n'
    fi
  else
    rm -f -- "$export_temp"
    printf 'No valid export receipt and matching Komodo file were available for this run. The setup container %s and persistent volume were retained.\n' "$tool_name" >&2
    printf 'Private diagnostics: %s/export.log. Do not apply a different generation.\n' "$output_dir" >&2
    exit 1
  fi
fi
if [[ "$target" == http-host && "$verify" == true && "$setup_status" == 0 ]]; then
  printf 'Server verification completed. The saved identity and applied deployment were checked; no Compose reimport is required.\n'
fi
if docker inspect --format '{{.State.Running}}' "$tool_name" 2>/dev/null | { read -r running; [[ "$running" == false ]]; }; then
  docker rm "$tool_name" >/dev/null
fi
exit "$setup_status"
