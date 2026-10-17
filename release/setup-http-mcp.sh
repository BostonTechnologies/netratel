#!/usr/bin/env bash
# Bounded host-side discovery and setup for the existing saved Compose deployment.
set -euo pipefail
umask 077

usage() {
  cat <<'USAGE'
Connect an existing HTTP MCP server to NetRatel. Run on its Docker host:
  ./setup-http-mcp.sh
  ./setup-http-mcp.sh --container CONTAINER [--verify]

The launcher discovers the saved Compose project, ordered sources, interpolation
files, image and persistent storage, then asks for the NetRatel Web URL. When
more than one instance exists, select one from the short container picker.
External deployments export a private complete document for reviewed manager
apply. --verify reuses the saved host generation with new temporary read-only
client consent. Missing ownership metadata never implies release ownership.

Explicit source recovery (repeat file options in their actual merge order):
  --compose-file /absolute/compose.yaml [--compose-file /absolute/override.yaml]
  --project STACK [--project-directory /absolute/directory]
  [--env-file /absolute/.env] [--manager external-compose|release-compose]
  [--compose-plugin /absolute/docker-compose | --isolated-compose /absolute/tools-directory]
  [--target http-host|http-client|cli|stdio] [-- wizard-options...]
USAGE
}

die() { printf 'Setup not started: %s\n' "$*" >&2; exit 1; }
compose_file= project= manager= env_file= target=http-host verify=false selected_container= compose_dir=
compose_files=() env_files=() wizard_args=()
found_manager= runtime_manager=
compose_plugin= isolated_compose=
launcher_arguments=("$@")
while (($#)); do
  case "$1" in
    --compose-file|--project|--manager|--env-file|--target|--container|--project-directory|--compose-plugin|--isolated-compose)
      (($# >= 2)) || die "Missing value for $1. Use --help."
      case "$1" in
        --compose-file) compose_files+=("$2") ;;
        --project) project=$2 ;;
        --manager) manager=$2 ;;
        --env-file) env_files+=("$2") ;;
        --target) target=$2 ;;
        --container) selected_container=$2 ;;
        --project-directory) compose_dir=$2 ;;
        --compose-plugin) compose_plugin=$2 ;;
        --isolated-compose) isolated_compose=$2 ;;
      esac
      shift 2 ;;
    --verify) verify=true; shift ;;
    --) shift; wizard_args=("$@"); break ;;
    --help|-h) usage; exit 0 ;;
    *) die "Unknown option $1. Use --help." ;;
  esac
done
[[ "$target" == http-host || "$target" == http-client || "$target" == cli || "$target" == stdio ]] || die 'Select --target http-host, http-client, cli or stdio.'
[[ "$verify" == false || "$target" == http-host ]] || die '--verify resumes saved server setup; client setup verifies within its own wizard.'
for command in docker realpath stat mktemp sha256sum python3 timeout; do command -v "$command" >/dev/null || die "Required host command is unavailable: $command. Install it, then rerun this launcher."; done

# Keep the selected Docker authentication, daemon and context for every host
# inspection, standalone Compose invocation and copied resume command.
docker_environment=(env)
for variable in DOCKER_CONFIG DOCKER_CONTEXT DOCKER_HOST DOCKER_TLS DOCKER_TLS_VERIFY DOCKER_CERT_PATH DOCKER_API_VERSION; do
  if [[ -v "$variable" ]]; then
    if [[ "$variable" == DOCKER_CONFIG || "$variable" == DOCKER_CERT_PATH ]] && [[ -n "${!variable}" ]]; then
      printf -v "$variable" '%s' "$(realpath -m -- "${!variable}")"; export "$variable"
    fi
    docker_environment+=("$variable=${!variable}")
  fi
done
[[ -z "$compose_plugin" || -z "$isolated_compose" ]] || die 'Select only --compose-plugin or --isolated-compose.'
if [[ -n "$isolated_compose" ]]; then
  # Deliberate task-local acquisition: pin both upstream version and bytes;
  # never alter the host plugin or Docker configuration/authentication files.
  [[ "$isolated_compose" == /* && "$isolated_compose" != *[[:cntrl:]]* && ! -L "$isolated_compose" ]] || die '--isolated-compose needs an absolute private tools directory, not a symlink.'
  if ! python3 - "$isolated_compose" "${DOCKER_CONFIG:-$HOME/.docker}" 2>/dev/null <<'PY_PLUGIN_DIRECTORY'
import json, pathlib, sys
selected = pathlib.Path(sys.argv[1]).resolve(); config = pathlib.Path(sys.argv[2])
active = [config / "cli-plugins", pathlib.Path.home() / ".docker/cli-plugins", *(pathlib.Path(prefix) / "docker/cli-plugins" for prefix in
          ("/usr/local/lib", "/usr/local/libexec", "/usr/lib", "/usr/libexec"))]
try:
    settings = json.loads((config / "config.json").read_text())
    active.extend(pathlib.Path(value) for value in settings.get("cliPluginsExtraDirs", []) if isinstance(value, str))
except (OSError, ValueError, TypeError):
    pass
sys.exit(1 if selected in [path.resolve() for path in active] else 0)
PY_PLUGIN_DIRECTORY
  then die 'Choose an isolated tools directory outside Docker plugin search directories; the ordinary host plugin must remain unchanged.'; fi
  case "$(uname -m)" in
    x86_64) compose_arch=x86_64; compose_digest=dba1915cf2f282527f5df0cd7a94b9503047ed200317801853abe8f22c8cd493 ;;
    aarch64|arm64) compose_arch=aarch64; compose_digest=a08457d837d5d4ed7c079f0721dc51ef3f21ce2d9654a6abd44944b74d975cd2 ;;
    *) die 'Isolated Compose acquisition supports Linux x86_64/aarch64. Supply a verified --compose-plugin for this host.' ;;
  esac
  [[ "$(uname -s)" == Linux ]] || die 'Isolated Compose acquisition supports Linux. Supply a verified --compose-plugin for this host.'
  if [[ ! -d "$isolated_compose" ]]; then mkdir -m 700 -- "$isolated_compose" || die 'The isolated Compose tools directory could not be created.'; fi
  isolated_compose=$(realpath -- "$isolated_compose")
  [[ $(stat -c %u -- "$isolated_compose") == "$(id -u)" && $(stat -c %a -- "$isolated_compose") == 700 ]] || die 'The isolated Compose tools directory must be owned by this user with mode 0700.'
  compose_plugin=$isolated_compose/docker-compose
  [[ ! -L "$compose_plugin" ]] || die 'The isolated Compose executable cannot be a symlink.'
  if [[ ! -e "$compose_plugin" ]]; then
    command -v curl >/dev/null || die 'Install curl to acquire the verified isolated Compose plugin, then rerun the same command.'
    compose_download=$(mktemp "$isolated_compose/.docker-compose.XXXXXX")
    if ! curl --fail --silent --show-error --location --connect-timeout 10 --max-time 45 \
        "https://github.com/docker/compose/releases/download/v2.35.0/docker-compose-linux-$compose_arch" --output "$compose_download" 2>/dev/null; then
      rm -f -- "$compose_download"; die 'The isolated Compose download failed. No plugin was installed; rerun the same command after restoring HTTPS access to GitHub.'
    fi
    actual_compose_digest=$(sha256sum <"$compose_download"); actual_compose_digest=${actual_compose_digest%% *}
    if [[ "$actual_compose_digest" != "$compose_digest" ]]; then rm -f -- "$compose_download"; die 'Isolated Compose integrity verification failed. No plugin was installed.'; fi
    chmod 700 -- "$compose_download"; mv -T -- "$compose_download" "$compose_plugin"
  fi
  [[ -f "$compose_plugin" && $(stat -c %u -- "$compose_plugin") == "$(id -u)" && $(stat -c %a -- "$compose_plugin") == 700 ]] || die 'The isolated Compose executable must be a private regular file owned by this user with mode 0700.'
  actual_compose_digest=$(sha256sum <"$compose_plugin"); actual_compose_digest=${actual_compose_digest%% *}
  [[ "$actual_compose_digest" == "$compose_digest" ]] || die 'Isolated Compose integrity verification failed. Remove the altered task-local executable and rerun --isolated-compose; the host plugin was not changed.'
fi
compose_command=(docker compose)
compose_selection=()
if [[ -n "$compose_plugin" ]]; then
  [[ "$compose_plugin" == /* && "$compose_plugin" != *[[:cntrl:]]* ]] || die '--compose-plugin must name an executable at its absolute path.'
  compose_plugin=$(realpath -m -- "$compose_plugin")
  compose_command=("$compose_plugin")
  # Retain integrity verification for an acquired plugin on every resume.
  if [[ -n "$isolated_compose" ]]; then compose_selection=(--isolated-compose "$isolated_compose"); else compose_selection=(--compose-plugin "$compose_plugin"); fi
fi
# Docker access stays here; the serving/tooling process remains UID 10001 with no
# socket mount. Inspect only bounded label/image/mount fields, never an Env dump.
if [[ -n "$selected_container" || ${#compose_files[@]} == 0 || -z "$project" ]]; then
  if [[ -z "$selected_container" ]]; then
    candidates=$(timeout 15s docker ps --all --filter label=com.docker.compose.service=mcp-http --filter label=com.docker.compose.oneoff=False --format '{{.ID}}') || die 'Docker inspection failed. Run this launcher as a host user with Docker access.'
    containers=(); while IFS= read -r item; do [[ -z "$item" ]] || containers+=("$item"); done <<< "$candidates"
    ((${#containers[@]} > 0 && ${#containers[@]} <= 32)) || die 'No unique saved MCP deployment was found. Supply only its --container, or its actual --compose-file and --project.'
    if ((${#containers[@]} == 1)); then selected_container=${containers[0]}; else
      [[ -t 0 && -t 1 ]] || die 'Multiple MCP containers were found. Rerun interactively to select an instance, or supply --container with its actual container name.'
      printf 'Select the MCP instance to configure:\n'
      for index in "${!containers[@]}"; do
        label=$(timeout 15s docker inspect --format '{{printf "%.80s" .Name}} · {{printf "%.80s" (index .Config.Labels "com.docker.compose.project")}}' "${containers[$index]}")
        [[ "$label" != *[[:cntrl:]]* ]] || die 'Container display metadata contains control characters. Select a safe --container explicitly.'
        printf '  %s) %s\n' "$((index + 1))" "$label"
      done
      read -r -p 'Instance number: ' choice
      [[ "$choice" =~ ^[0-9]+$ && ${#choice} -le 2 ]] && ((10#$choice >= 1 && 10#$choice <= ${#containers[@]})) || die 'Select one listed instance number.'
      selected_container=${containers[$((10#$choice - 1))]}
    fi
  fi
  metadata=$(timeout 15s docker inspect --format '{{printf "%.4097s" (index .Config.Labels "com.docker.compose.project")}}|{{printf "%.4097s" (index .Config.Labels "com.docker.compose.project.config_files")}}|{{printf "%.4097s" (index .Config.Labels "com.docker.compose.project.working_dir")}}|{{printf "%.4097s" (index .Config.Labels "com.docker.compose.project.environment_file")}}|{{printf "%.80s" (index .Config.Labels "com.netratel.deployment.manager")}}|{{printf "%.512s" .Config.Image}}|{{printf "%.80s" (index .Config.Labels "com.docker.compose.service")}}' "$selected_container") || die 'Selected container metadata is unavailable. Supply the actual saved source location.'
  [[ ${#metadata} -le 18000 && "$metadata" != *[[:cntrl:]]* ]] || die 'Selected container metadata is invalid or oversized.'
  IFS='|' read -r found_project found_files found_directory found_env found_manager selected_image found_service <<< "$metadata"
  [[ "$found_service" == mcp-http ]] || die 'The selected container is not the saved mcp-http service.'
  [[ -z "$project" || "$project" == "$found_project" ]] || die 'Selected container and supplied Compose project disagree. Select the actual saved deployment.'
  project=$found_project
  [[ -n "$compose_dir" ]] || compose_dir=$found_directory
  if ((${#compose_files[@]} == 0)); then
    [[ -n "$found_files" && "$found_files" != '<no value>' ]] || die 'The container has no saved Compose source metadata. Supply only --compose-file with the actual saved stack file.'
    IFS=',' read -r -a compose_files <<< "$found_files"
  fi
  if ((${#env_files[@]} == 0)) && [[ -n "$found_env" && "$found_env" != '<no value>' ]]; then IFS=',' read -r -a env_files <<< "$found_env"; fi
  [[ -n "$selected_image" && "$selected_image" != '<no value>' ]] || die 'The selected MCP image metadata is missing. Restore the saved deployment image before setup.'
fi
if [[ "$manager" == external-compose ]]; then manager=komodo; fi
[[ -z "$manager" || "$manager" == komodo || "$manager" == release-compose ]] || die 'Select --manager external-compose (external reviewed export) or explicitly owned release-compose.'
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || die 'Use the actual Compose project name shown by docker compose ls.'
((${#compose_files[@]} > 0 && ${#compose_files[@]} <= 16 && ${#env_files[@]} <= 16)) || die 'Supply the actual ordered saved Compose sources (at most 16 files).'
for index in "${!compose_files[@]}"; do
  item=${compose_files[$index]}
  [[ "$item" == /* && -f "$item" && -r "$item" && ! -L "$item" ]] || die 'A saved Compose source is missing or unreadable. --compose-file must name its actual existing absolute regular file, not a symlink.'
  case "$item" in *:*|*,*|*$'\n'*) die 'A Compose source path cannot contain a colon, comma or newline.' ;; esac
  compose_files[$index]=$(realpath -- "$item")
done
compose_file=${compose_files[0]}
compose_dir=${compose_dir:-${compose_file%/*}}
[[ "$compose_dir" == /* && -d "$compose_dir" ]] || die 'Supply only --project-directory with the actual saved Compose working directory.'
compose_dir=$(realpath -- "$compose_dir")
if ((${#env_files[@]} == 0)) && [[ -f "$compose_dir/.env" ]]; then env_files=("$compose_dir/.env"); fi
for index in "${!env_files[@]}"; do
  item=${env_files[$index]}
  [[ "$item" == /* && -f "$item" && -r "$item" && ! -L "$item" && "$item" != *[[:cntrl:]]* && "$item" != *,* ]] || die 'A saved interpolation environment file is missing or unreadable. Supply only --env-file with its actual absolute location.'
  env_files[$index]=$(realpath -- "$item")
done
# Probe the actual selected command before reading/rendering saved sources or
# creating containers. Distribution suffixes never determine capabilities.
compose_version=$(timeout 10s "${compose_command[@]}" version --short 2>/dev/null) || compose_version=unavailable
[[ -n "$compose_version" && ${#compose_version} -le 80 && "$compose_version" != *[[:cntrl:]]* ]] || compose_version=unavailable
compose_capabilities=$(timeout 10s "${compose_command[@]}" config --help 2>/dev/null) || compose_capabilities=
missing_capability=
for capability in --no-env-resolution --format --no-interpolate --no-path-resolution --quiet --services; do
  if [[ ! "$compose_capabilities" =~ (^|[[:space:]])$capability([[:space:]=]|$) ]]; then missing_capability=$capability; break; fi
done
if [[ -n "$missing_capability" || "$compose_version" == unavailable ]]; then
  remediation_arguments=()
  for ((index=0; index<${#launcher_arguments[@]}; index++)); do
    case "${launcher_arguments[$index]}" in
      --compose-plugin|--isolated-compose) ((index+=1)) ;;
      --) break ;; # Wizard arguments can contain credentials; never echo them.
      *) remediation_arguments+=("${launcher_arguments[$index]}") ;;
    esac
  done
  printf -v compose_remediation '%q ' "${docker_environment[@]}" bash "$(realpath -- "$0")" \
    --isolated-compose "$(pwd -P)/.netratel-compose-v2.35.0" "${remediation_arguments[@]}"
  die "compose_capability_unsupported: Docker Compose $compose_version lacks config ${missing_capability:-command execution}; requires upstream v2.35.0+ with these capabilities. Verified isolated tooling (no global install; repeat wizard options privately if needed): $compose_remediation"
fi

output_dir=$compose_dir/.netratel-http-mcp-$project
[[ ! -L "$output_dir" ]] || die 'The private setup output directory cannot be a symlink.'
if [[ ! -d "$output_dir" ]]; then mkdir -m 700 -- "$output_dir" || die 'The saved working directory is not writable. Provide its actual writable --project-directory; no approval was requested.'; fi
[[ $(stat -c %u -- "$output_dir") == "$(id -u)" && $(stat -c %a -- "$output_dir") == 700 ]] || die 'The existing private setup output directory must be owned by this user with mode 0700.'
compose_base=("${compose_command[@]}" --project-directory "$compose_dir" --project-name "$project")
if ((${#env_files[@]})); then for item in "${env_files[@]}"; do compose_base+=(--env-file "$item"); done; else compose_base+=(--env-file /dev/null); fi
compose=("${compose_base[@]}")
for item in "${compose_files[@]}"; do compose+=(-f "$item"); done
# Recover only the reviewed decision for this actual daemon, project, ordered
# source set and saved host generation. Container IDs deliberately are not part
# of this identity: a normal reviewed apply recreates them.
daemon_id=$(timeout 15s docker info --format '{{.ID}}' 2>"$output_dir/ownership.log") || die 'deployment_manager_unresolved: Docker daemon identity is unavailable; restore Docker access before choosing deployment ownership.'
[[ -n "$daemon_id" && ${#daemon_id} -le 128 && "$daemon_id" != *[[:cntrl:]]* ]] || die 'deployment_manager_unresolved: Docker daemon identity is invalid.'
if [[ -n "$selected_container" ]]; then
  manager_template='{{range .Config.Env}}{{if eq (index (split . "=") 0) "NETRATEL_SETUP_DEPLOYMENT_MANAGER"}}{{printf "%.257s\n" .}}{{end}}{{end}}'
  runtime_manager=$(timeout 15s docker inspect --format "$manager_template" "$selected_container" 2>>"$output_dir/ownership.log") || die 'deployment_manager_unresolved: Selected deployment metadata is unavailable.'
fi
# A saved Compose environment marker is also authoritative, including when
# explicit sources are used without selecting a running container. Keep the
# complete render private; only the recognized ownership value is interpreted.
if ! timeout 20s "${compose[@]}" config --format json >"$output_dir/.ownership-deployment.json" 2>"$output_dir/preflight.log"; then
  die "The saved Compose deployment failed validation. Restore its saved inputs. Private diagnostics: $output_dir/preflight.log. No approval or setup container was started."
fi
if ! resolved_manager=$(python3 - "$output_dir" "$verify" "$manager" "$found_manager" "$runtime_manager" "$daemon_id" "$project" "$compose_dir" "$selected_container" "${#compose_files[@]}" "${compose_files[@]}" "${env_files[@]}" 2>>"$output_dir/ownership.log" <<'PY_OWNER'
import hashlib, json, os, pathlib, stat, subprocess, sys, tempfile
root = pathlib.Path(sys.argv[1]); verify = sys.argv[2] == "true"
explicit, label, runtime, daemon, project, directory, container = sys.argv[3:10]
count = int(sys.argv[10]); compose = sys.argv[11:11 + count]; env = sys.argv[11 + count:]
def stop(code, detail):
    print(code + ": " + detail)
    sys.exit(1)
def manager(value):
    if value in ("", "<no value>"): return None
    if value in ("komodo", "external-compose"): return "komodo"
    if value == "release-compose": return value
    stop("deployment_manager_conflict", "Unrecognized deployment ownership metadata; repair the selected deployment before setup.")
markers = [manager(label)]
markers += [manager(value.removeprefix("NETRATEL_SETUP_DEPLOYMENT_MANAGER=")) for value in runtime.splitlines()]
try:
    configuration = root / ".ownership-deployment.json"
    if configuration.stat().st_size > 262144: raise ValueError()
    configured = json.loads(configuration.read_text()).get("services", {}).get("mcp-http", {}).get("environment", {})
    if not isinstance(configured, dict): raise ValueError()
    markers.append(manager(configured.get("NETRATEL_SETUP_DEPLOYMENT_MANAGER") or ""))
except (ValueError, TypeError, AttributeError, OSError):
    stop("deployment_manager_unresolved", "Configured ownership metadata is invalid; restore the saved deployment before setup.")
markers = {value for value in markers if value}
if len(markers) > 1 or explicit and markers and explicit not in markers:
    stop("deployment_manager_conflict", "Explicit ownership and deployment metadata disagree; repair the selected deployment metadata before setup.")
selected = explicit or next(iter(markers), "")
path = root / ".approved-source-state.json"
if path.exists() or path.is_symlink():
    try:
        info = path.lstat()
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600 or info.st_size > 262144:
            raise ValueError()
        saved = json.loads(path.read_text())
        if not isinstance(saved, dict): raise ValueError()
        remembered = manager(saved.get("manager", ""))
        binding = {"daemon": daemon, "project": project, "directory": directory, "composeFiles": compose, "envFiles": env}
        valid_binding = saved.get("binding") == binding
        approved_overlay = root / "compose.http-host.yaml"
        original_binding = saved.get("binding", {})
        if remembered == "release-compose" and isinstance(original_binding, dict) and compose == original_binding.get("composeFiles", []) + [str(approved_overlay)]:
            previous_binding = dict(binding, composeFiles=compose[:-1])
            valid_binding = previous_binding == original_binding and approved_overlay.is_file() and not approved_overlay.is_symlink() and hashlib.sha256(approved_overlay.read_bytes()).hexdigest() == saved.get("exportedHash")
        # Pre-feature source records cannot authorize ownership. Explicit
        # ordinary setup may replace stale/corrupt records after a new review.
        if remembered and (verify or not explicit):
            if not valid_binding:
                stop("deployment_manager_state_stale", "Saved ownership belongs to another daemon, project or ordered source set; run ordinary setup with its actual owner to review the change.")
            sources = saved["sources"]
            if not isinstance(sources, list) or not sources: raise ValueError()
            changed = []
            for source in sources:
                source_path = pathlib.Path(source["path"])
                if not source_path.is_file() or source_path.is_symlink() or source_path.stat().st_size > 262144: raise ValueError()
                current = hashlib.sha256(source_path.read_bytes()).hexdigest()
                if current != source["hash"]: changed.append((source_path, current))
            if changed and not (len(changed) == 1 and str(changed[0][0]) == compose[0] and changed[0][1] == saved["exportedHash"] and remembered == "komodo"):
                stop("deployment_manager_state_stale", "approved sources changed; run setup without --verify to review current sources.")
            if markers and remembered not in markers or explicit and explicit != remembered:
                stop("deployment_manager_conflict", "Reviewed ownership and current deployment metadata disagree; review the actual owner in ordinary setup before verification.")
            if not container:
                result = subprocess.run(["docker", "ps", "--filter", "label=com.docker.compose.project=" + project, "--filter", "label=com.docker.compose.service=mcp-http", "--filter", "label=com.docker.compose.oneoff=False", "--format", "{{.ID}}"], capture_output=True, timeout=15)
                containers = result.stdout.decode().splitlines()
                if result.returncode or len(containers) != 1: raise ValueError()
                container = containers[0]
            mount = subprocess.run(["docker", "inspect", "--format", '{{range .Mounts}}{{if eq .Destination "/setup-output"}}{{.Type}}|{{.Name}}{{end}}{{end}}', container], capture_output=True, timeout=15)
            if mount.returncode or mount.stdout.decode().strip() != "volume|" + saved["setupVolume"]:
                stop("deployment_manager_state_stale", "The serving deployment uses different setup storage; restore its reviewed generation or run ordinary setup.")
            fd, temporary = tempfile.mkstemp(prefix=".owner-generation-", dir=root); os.close(fd)
            try:
                copied = subprocess.run(["docker", "cp", container + ":/setup-output/.http-host.setup.json", temporary], capture_output=True, timeout=15)
                generation = pathlib.Path(temporary)
                if copied.returncode or generation.is_symlink() or generation.stat().st_size > 262144: raise ValueError()
                if hashlib.sha256(generation.read_bytes()).hexdigest() != saved["generationHash"]:
                    stop("deployment_manager_state_stale", "The serving host generation differs from reviewed ownership; restore the saved generation or run ordinary setup.")
            finally:
                pathlib.Path(temporary).unlink(missing_ok=True)
            selected = remembered
    except (ValueError, KeyError, TypeError, OSError, subprocess.SubprocessError):
        if verify or not explicit:
            stop("deployment_manager_state_invalid", "Saved ownership or host generation is invalid; preserve the setup volume and rerun ordinary setup with the actual --manager to review it.")
print(selected)
PY_OWNER
); then die "$resolved_manager No host approval or setup container was started."; fi
manager=$resolved_manager
# Missing ownership never authorizes an apply path. A protected reviewed
# decision, recognized deployment marker or explicit host choice is required.
if [[ -z "$manager" ]]; then
  if [[ -t 0 && -t 1 ]]; then
    printf 'Deployment ownership metadata is missing. Who applies this saved Compose project?\n'
    printf '  1) Existing deployment manager — export the reviewed complete document\n'
    printf '  2) Explicitly release-owned Compose bundle — export a reviewed release overlay\n'
    read -r -p 'Deployment ownership (1 or 2): ' ownership
    case "$ownership" in
      1) manager=komodo ;;
      2) manager=release-compose ;;
      *) die 'deployment_manager_unknown: Select the actual owner of the saved deployment before setup.' ;;
    esac
  else
    die 'deployment_manager_unknown: Supply only --manager external-compose for an existing deployment manager, or --manager release-compose for an explicitly release-owned bundle. No approval or setup container was started.'
  fi
fi
if ! "${compose[@]}" config --quiet >"$output_dir/preflight.log" 2>&1; then
  die "The saved Compose deployment failed validation. Review its sources and interpolation/env_file prerequisites. Private diagnostics: $output_dir/preflight.log. No setup container was started."
fi
services=$("${compose[@]}" config --services 2>>"$output_dir/preflight.log")
[[ $'\n'"$services"$'\n' == *$'\nmcp-http\n'* ]] || die 'The selected saved Compose sources have no mcp-http service.'

setup_volume=$project-http-host-setup
client_volume=$project-http-client-data
existing_setup_volume= existing_client_volume=
container_ids=$("${compose[@]}" ps --all --quiet mcp-http 2>>"$output_dir/preflight.log")
for container_id in $container_ids; do
  oneoff=$(timeout 15s docker inspect --format '{{index .Config.Labels "com.docker.compose.oneoff"}}' "$container_id")
  [[ "$oneoff" != True && "$oneoff" != true ]] || continue
  for destination in /setup-output /var/lib/netratel/clients; do
    mount=$(timeout 15s docker inspect --format '{{range .Mounts}}{{if eq .Destination "'"$destination"'"}}{{.Type}}|{{.Name}}{{end}}{{end}}' "$container_id")
    [[ -n "$mount" ]] || continue
    [[ "$mount" == volume\|* ]] || die "The serving container uses a managed bind mount at $destination. Supply its source through the existing manager; no persistent storage was substituted."
    volume=${mount#volume|}
    [[ "$volume" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] || die 'The existing named-volume metadata is invalid.'
    if [[ "$destination" == /setup-output ]]; then
      [[ -z "$existing_setup_volume" || "$existing_setup_volume" == "$volume" ]] || die 'Existing MCP containers disagree about their setup volume.'
      existing_setup_volume=$volume; setup_volume=$volume
    else
      [[ -z "$existing_client_volume" || "$existing_client_volume" == "$volume" ]] || die 'Existing MCP containers disagree about their client volume.'
      existing_client_volume=$volume; client_volume=$volume
    fi
  done
done

web_api_backend= web_mcp_backend= web_environment_observed=false
if [[ "$target" == http-host ]]; then
  web_ids=$("${compose[@]}" ps --status running --quiet web 2>>"$output_dir/preflight.log")
  web_container=
  for candidate in $web_ids; do
    oneoff=$(timeout 15s docker inspect --format '{{index .Config.Labels "com.docker.compose.oneoff"}}' "$candidate")
    [[ "$oneoff" != True && "$oneoff" != true ]] || continue
    [[ -z "$web_container" ]] || die 'More than one serving Web container belongs to this project. Select a single Web instance before server setup; no backend destination was guessed.'
    web_container=$candidate
  done
  if [[ -n "$web_container" ]]; then
    # Inspect only the two routing settings. Keep record boundaries and original
    # spellings so equal aliases coalesce and conflicts retain safe provenance.
    backend_template='{{$separator := ""}}[{{range .Config.Env}}{{$key := lower (index (split . "=") 0)}}{{if or (eq $key "apibaseurl") (eq $key "webgateway__mcp__backendurl") (eq $key "webgateway:mcp:backendurl")}}{{$separator}}{{json (printf "%.4097s" .)}}{{$separator = ","}}{{end}}{{end}}]'
    backend_metadata=$(timeout 15s docker inspect --format "$backend_template" "$web_container")
    backend_metadata=$(python3 -c '
import json, sys
try:
    payload = sys.stdin.read(262145)
    records = json.loads(payload)
    if len(payload) > 262144 or not isinstance(records, list) or len(records) > 1024: raise ValueError()
    seen = {}
    for index, record in enumerate(records, 1):
        if not isinstance(record, str) or len(record) > 4096: raise ValueError()
        key, separator, value = record.partition("=")
        normalized = key.replace(":", "__").casefold()
        if not separator or normalized not in ("apibaseurl", "webgateway__mcp__backendurl"): raise ValueError()
        if normalized in seen:
            first, first_value, first_index = seen[normalized]
            if first_value != value:
                raise SystemExit("environment_alias_conflict: service web; normalized key " + normalized + "; " + first + " at serving runtime environment entry " + str(first_index) + " / " + key + " at serving runtime environment entry " + str(index) + ". Make these entries exactly equal or remove the unintended alias.")
        else: seen[normalized] = (key, value, index)
    for normalized, (_, value, _) in seen.items():
        if len(value) > 2048 or any(c.isspace() or ord(c) < 32 or c in "@?#" for c in value): raise ValueError()
        print(("ApiBaseUrl" if normalized == "apibaseurl" else "WebGateway__Mcp__BackendUrl") + "=" + value)
except (ValueError, TypeError):
    raise SystemExit("deployment_prerequisite: The serving Web backend metadata is invalid or oversized. Review its source; no value was printed or substituted.")
' 2>"$output_dir/.web-backend-action" <<< "$backend_metadata") || {
      IFS= read -r backend_action < "$output_dir/.web-backend-action"
      die "$backend_action"
    }
    web_environment_observed=true
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
source_inputs=("${compose_files[@]}" "${env_files[@]}")
source_state=$output_dir/.source-state.json
if [[ "$target" == http-host ]]; then
  # Resolve exactly the selected source set; keep all secret-bearing output in
  # private files. The unresolved view is used solely to find service env_files.
  python3 - "$output_dir/.initial-source-state.json" "${source_inputs[@]}" <<'PY_INITIAL'
import hashlib, json, pathlib, sys
paths = [pathlib.Path(p) for p in sys.argv[2:]]
if any(path.stat().st_size > 262144 for path in paths): raise ValueError("source exceeds 256 KiB")
pathlib.Path(sys.argv[1]).write_text(json.dumps({str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}))
PY_INITIAL
  # Interpolating Compose renders discard env_file references even when
  # --no-env-resolution is advertised. Preserve the raw structure first, then
  # let the same Compose invocation interpolate only provider path strings in
  # an inert service model with no env_file and no container execution.
  if ! timeout 20s "${compose[@]}" config --no-env-resolution --no-interpolate --no-path-resolution --format json >"$output_dir/.compose-inputs.json" 2>>"$output_dir/preflight.log"; then
    die 'Saved Compose sources could not be read. Restore their locations and use Docker Compose with --no-env-resolution support; no approval was requested.'
  fi
  if ! python3 - "$output_dir" 2>>"$output_dir/preflight.log" <<'PY_PROVIDERS'
import json, pathlib, sys
root = pathlib.Path(sys.argv[1]); inputs = json.loads((root / ".compose-inputs.json").read_text())
values = {}; locations = {}
def reference(service, kind, index, field, value):
    if not isinstance(value, str): raise ValueError("provider reference is not text")
    key = "NETRATEL_SOURCE_" + str(len(values))
    values[key] = value; locations[key] = [service, kind, index, field]
for name, service in inputs.get("services", {}).items():
    files = service.get("env_file", [])
    for index, item in enumerate(files):
        if isinstance(item, str): item = files[index] = {"path": item, "required": True}
        if item.get("path"): reference(name, "env_file", index, "path", item["path"])
    for index, mount in enumerate(service.get("volumes", [])):
        if isinstance(mount, dict) and mount.get("type") == "bind":
            for field in ("source", "target"):
                if mount.get(field): reference(name, "volumes", index, field, mount[field])
(root / ".compose-inputs.json").write_text(json.dumps(inputs))
(root / ".compose-provider-locations.json").write_text(json.dumps(locations))
(root / ".compose-provider-probe.json").write_text(json.dumps({"services": {"netratel-source-providers": {
    "image": "scratch", "environment": values or {"NETRATEL_SOURCE_NONE": ""}}}}))
PY_PROVIDERS
  then die 'Saved source provider references could not be read. Restore the selected Compose sources; no approval was requested.'; fi
  if ! timeout 20s "${compose_base[@]}" -f "$output_dir/.compose-provider-probe.json" config --format json >"$output_dir/.compose-provider-values.json" 2>>"$output_dir/preflight.log"; then
    die 'Saved source provider paths could not be interpolated. Restore the selected interpolation inputs; no approval was requested.'
  fi
  if ! python3 - "$output_dir" "$compose_dir" 2>>"$output_dir/preflight.log" <<'PY_INPUTS'
import hashlib, json, os, pathlib, sys
root = pathlib.Path(sys.argv[1]); initial = json.loads((root / ".initial-source-state.json").read_text())
inputs = json.loads((root / ".compose-inputs.json").read_text())
locations = json.loads((root / ".compose-provider-locations.json").read_text())
resolved = json.loads((root / ".compose-provider-values.json").read_text())["services"]["netratel-source-providers"]["environment"]
for key, (name, kind, index, field) in locations.items():
    value = resolved[key]
    if not isinstance(value, str) or not value: raise ValueError("provider reference is empty or unset")
    # Interpolating `compose config` re-escapes literal dollars for a reusable
    # Compose document. Decode that output escape once; Compose owns expansion.
    value = value.replace("$$", "$")
    if field != "target":
        path = pathlib.Path(value).expanduser()
        if not path.is_absolute(): path = pathlib.Path(sys.argv[2]) / path
        value = os.path.abspath(path)
    inputs["services"][name][kind][index][field] = value
(root / ".compose-inputs.json").write_text(json.dumps(inputs))
for service in inputs.get("services", {}).values():
    paths = []
    for item in service.get("env_file", []):
        value = item if isinstance(item, str) else item.get("path")
        required = True if isinstance(item, str) else item.get("required", True)
        if value and (required or pathlib.Path(value).exists()): paths.append(pathlib.Path(value))
    for mount in service.get("volumes", []):
        if isinstance(mount, dict) and mount.get("type") == "bind" and "/app/appsettings" in mount.get("target", ""):
            paths.append(pathlib.Path(mount.get("source", "")))
    for path in paths:
        if not path.is_absolute() or not path.is_file() or path.is_symlink() or path.stat().st_size > 262144: raise ValueError("saved provider unreadable or oversized")
        initial[str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
(root / ".initial-source-state.json").write_text(json.dumps(initial))
PY_INPUTS
  then die 'A saved service env_file or protected appsettings source is missing or unreadable. Restore that saved source before setup; no approval was requested.'; fi
  if ! "${compose[@]}" config --format json >"$output_dir/.effective-deployment.json" 2>>"$output_dir/preflight.log" ||
      ! "${compose[@]}" config --no-env-resolution --no-interpolate --no-path-resolution >"$output_dir/.compose-source.yaml" 2>>"$output_dir/preflight.log"; then
    die 'The saved deployment could not be fully resolved. Restore its Compose/env_file sources before setup; no approval was requested.'
  fi
  rm -f -- "$output_dir/.preflight-action"
  export NETRATEL_LAUNCHER_PROJECT=$project
  export NETRATEL_LAUNCHER_SELECTED_IMAGE=${selected_image:-}
  export NETRATEL_LAUNCHER_MANAGER=$manager
  if ! python3 - "$output_dir" "$verify" "$manager" "${source_inputs[@]}" 2>>"$output_dir/preflight.log" <<'PY'
import hashlib, json, os, pathlib, subprocess, sys
root = pathlib.Path(sys.argv[1]); verify = sys.argv[2] == "true"
class Prerequisite(Exception): pass
def safe_failure(kind, error, trace):
    action = str(error) if isinstance(error, Prerequisite) else "The private effective deployment could not be resolved. Restore the saved configuration sources and local serving images, then rerun setup."
    (root / ".preflight-action").write_text("deployment_prerequisite: " + action + "\n")
    sys.__excepthook__(kind, error, trace)
sys.excepthook = safe_failure
paths = [pathlib.Path(p) for p in sys.argv[4:]]
inputs = json.loads((root / ".compose-inputs.json").read_text())
proposal = json.loads((root / ".effective-deployment.json").read_text())
configured_manager = proposal.get("services", {}).get("mcp-http", {}).get("environment", {}).get("NETRATEL_SETUP_DEPLOYMENT_MANAGER")
if configured_manager is not None:
    configured_manager = "komodo" if configured_manager == "external-compose" else configured_manager
    if configured_manager != os.environ["NETRATEL_LAUNCHER_MANAGER"]:
        raise Prerequisite("deployment_manager_conflict: Configured ownership disagrees with the selected owner; repair the saved deployment metadata before setup")
selected_image = os.environ.get("NETRATEL_LAUNCHER_SELECTED_IMAGE")
if selected_image and proposal.get("services", {}).get("mcp-http", {}).get("image") != selected_image:
    raise Prerequisite("saved sources and selected serving image disagree; restore the saved deployment source before setup")
for service in inputs.get("services", {}).values():
    for item in service.get("env_file", []):
        path = item if isinstance(item, str) else item.get("path")
        required = True if isinstance(item, str) else item.get("required", True)
        if path and (required or pathlib.Path(path).exists()): paths.append(pathlib.Path(path))
paths = list(dict.fromkeys(paths))
initial = json.loads((root / ".initial-source-state.json").read_text())
for path, digest in initial.items():
    if hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest() != digest: raise Prerequisite("sources changed during render")
state = []
for path in paths:
    if not path.is_absolute() or not path.is_file() or path.is_symlink(): raise Prerequisite("saved source missing")
    if path.stat().st_size > 262144: raise Prerequisite("saved source exceeds bound")
    state.append({"path": str(path), "hash": hashlib.sha256(path.read_bytes()).hexdigest()})
proposal["x-netratel-source-files"] = state
image_defaults = {}
image_ids = {}
protected_defaults = {}
# Emit only relevant, bounded application settings to the private snapshot.
# Neither container environments nor raw image metadata are printed/logged.
prefixes = ("NetRatel", "M2M", "ASPNETCORE", "DOTNET", "WebGateway", "HttpHostSetup", "Authentication", "Integration", "NETRATEL_MCP", "NETRATEL_AUTHENTICATION")
conditions = " ".join('(eq (printf "%.' + str(len(p)) + 's" $key) "' + p.lower() + '")' for p in prefixes)
template = '{{if gt (len .Config.Env) 1024}}{"error":"environment_entry_limit"}{{else}}{{$separator := ""}}[{{range .Config.Env}}{{$key := lower (index (split . "=") 0)}}{{if or ' + conditions + ' (eq $key "apibaseurl")}}{{$separator}}{{json (printf "%.4097s" .)}}{{$separator = ","}}{{end}}{{end}}]{{end}}'
configuration_prefixes = ("netratel__", "m2m__", "m2mclients__", "webgateway__", "httphostsetup__", "authentication__", "integration__", "netratel_mcp_", "netratel_authentication_", "netratel_client_")
configuration_names = {"netratel_keys_dir", "netratel_http_port", "apibaseurl", "dotnet_environment", "aspnetcore_environment", "aspnetcore_urls", "aspnetcore_http_ports"}
def recognized(key):
    normalized = key.replace(":", "__").casefold()
    return normalized in configuration_names or normalized.startswith(configuration_prefixes)
def identity(key):
    return key.replace(":", "__").casefold() if recognized(key) else key
def canonical(key, protected=False):
    normalized = identity(key)
    if normalized == "netratel_keys_dir": return "NetRatel_KEYS_DIR"
    if normalized == "netratel_http_port": return "NetRatel_HTTP_PORT"
    if recognized(key) and normalized.startswith(("netratel_mcp_", "netratel_authentication_", "netratel_client_", "dotnet_", "aspnetcore_")): return normalized.upper()
    return key if protected or not recognized(key) else key.replace(":", "__")
def safe_name(key):
    return key if len(key) <= 128 and all(c.isascii() and (c.isalnum() or c in "_:") for c in key) else "[redacted setting name]"
def effective_value(value):
    if value is None or isinstance(value, str): return value
    if isinstance(value, (bool, int, float)): return json.dumps(value, separators=(",", ":"))
    raise Prerequisite("application setting must have a scalar value")
def coalesce(entries, service, label, protected=False, allow_equal=True):
    settings = {}; seen = {}
    for index, (key, value) in enumerate(entries, 1):
        normalized = identity(key)
        if normalized in seen:
            previous_key, previous_value, previous_index = seen[normalized]
            if allow_equal and recognized(key) and effective_value(previous_value) == effective_value(value): continue
            code = "environment_alias_conflict" if allow_equal else "protected_configuration_duplicate"
            repair = "Make these entries exactly equal or remove the unintended alias." if allow_equal else "Remove the repeated property from this JSON provider; .NET requires unique configuration keys."
            raise Prerequisite(code + ": service " + service + "; normalized key " + safe_name(normalized) + "; " +
                safe_name(previous_key) + " at " + label + " entry " + str(previous_index) + " / " + safe_name(key) + " at " + label + " entry " + str(index) +
                ". " + repair)
        seen[normalized] = (key, value, index)
        settings[canonical(key, protected)] = value
    return settings
def overlay(lower, higher):
    # Duplicate detection is per provider. A later provider legitimately wins.
    replacing = {identity(key) for key in higher}
    for key in list(lower):
        if identity(key) in replacing: del lower[key]
    lower.update(higher)
def environment_records(payload, service, label):
    if len(payload) > 262144: raise Prerequisite(label + " settings exceed the inspection bound")
    records = json.loads(payload)
    if not isinstance(records, list) or len(records) > 1024: raise Prerequisite(label + " settings exceed the inspection bound")
    entries = []
    for record in records:
        if not isinstance(record, str): raise Prerequisite("ambiguous " + label + " settings")
        key, separator, value = record.partition("=")
        if not separator or not key or len(record) > 4096 or any(ord(character) < 32 for character in key):
            raise Prerequisite("ambiguous " + label + " settings")
        entries.append((key, value))
    return coalesce(entries, service, label + " environment")
for name in ("api", "mcp-http", "web"):
    image = proposal.get("services", {}).get(name, {}).get("image")
    if not image: continue
    identifier = subprocess.run(["docker", "image", "inspect", "--format", "{{.Id}}", image], capture_output=True, timeout=20)
    image_id = identifier.stdout.decode().strip()
    if identifier.returncode or not image_id.startswith("sha256:") or len(image_id) != 71: raise Prerequisite("The saved image identity is unavailable; pull the saved serving image before setup")
    image_ids[name] = image_id
    result = subprocess.run(["docker", "image", "inspect", "--format", template, image], capture_output=True, timeout=20)
    if result.returncode: raise Prerequisite("required serving image is unavailable locally; pull the saved image before setup")
    settings = environment_records(result.stdout, name, "image")
    image_defaults[name] = settings
    compose_environment = coalesce(proposal["services"][name].get("environment", {}).items(), name, "Compose environment")
    # Retain original spellings in this private resolved layer. The C# merge can
    # then prove that differing unresolved expressions for those exact aliases
    # resolved equally, without borrowing an image/protected-provider value.
    effective_environment = dict(settings)
    overlay(effective_environment, compose_environment)
    folded_environment = {identity(key): value for key, value in effective_environment.items()}
    deployment_environment = folded_environment.get("dotnet_environment") or folded_environment.get("aspnetcore_environment") or "Production"
    if not deployment_environment.replace("-", "").replace("_", "").isalnum() or len(deployment_environment) > 80: raise Prerequisite("invalid deployment environment")
    protected = {}
    configuration_hashes = {}
    def read_configuration(path):
        if path.stat().st_size > 262144: raise Prerequisite("protected configuration exceeds bound")
        entries = []; replaced_arrays = []
        class ObjectPairs(list): pass
        def flatten(value, prefix=""):
            if isinstance(value, ObjectPairs):
                for key, child in value: flatten(child, prefix + (":" if prefix else "") + key)
            elif isinstance(value, list):
                if prefix.casefold() in {"netratel:mcp:http:requiredgroups", "netratel:mcp:http:requiredscopes", "netratel:mcp:http:allowedorigins"}:
                    # Serving HTTP options replace each of these arrays at the
                    # highest JSON provider, including an explicit empty list.
                    replaced_arrays.append(prefix)
                    if not value: entries.append((prefix, ""))
                for index, child in enumerate(value): flatten(child, prefix + ":" + str(index))
            elif recognized(prefix): entries.append((prefix, value))
        content = path.read_bytes()
        configuration_hashes[str(path)] = hashlib.sha256(content).hexdigest()
        flatten(json.loads(content, object_pairs_hook=ObjectPairs))
        # The serving .NET JSON provider rejects duplicate properties even when
        # equal. Resolved snapshots may coalesce aliases; raw providers must be
        # valid themselves before setup can approve their effective contents.
        layer = coalesce(entries, name, "protected provider " + str(len(configuration_hashes)), protected=True, allow_equal=False)
        for prefix in replaced_arrays:
            normalized = identity(prefix)
            for inherited in list(protected):
                if identity(inherited) == normalized or identity(inherited).startswith(normalized + "__"): del protected[inherited]
        overlay(protected, layer)
    # Inspect image JSON providers without starting its entrypoint or connecting
    # it to a network. Always remove the stopped inspection container.
    created = subprocess.run(["docker", "create", "--network", "none", "--entrypoint", "/bin/true", image], capture_output=True, timeout=20)
    inspection = created.stdout.decode().strip()
    if created.returncode or not inspection or len(inspection) > 128 or not all(c.isalnum() or c in "_.-" for c in inspection): raise Prerequisite("image configuration inspection failed")
    try:
        for configuration_name in ("appsettings.json", "appsettings." + deployment_environment + ".json"):
            private_file = root / (".image-" + name + "-" + configuration_name)
            private_file.unlink(missing_ok=True)
            copied = subprocess.run(["docker", "cp", inspection + ":/app/" + configuration_name, str(private_file)], capture_output=True, timeout=20)
            if copied.returncode == 0:
                if private_file.is_symlink(): raise Prerequisite("Image application settings must be regular files; restore the saved image source before setup")
                private_file.chmod(0o600)
                read_configuration(private_file)
    finally:
        removed = subprocess.run(["docker", "rm", inspection], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)
        if removed.returncode: raise Prerequisite("temporary image inspection cleanup failed")
    # Host bind files replace their matching image provider. An unresolved
    # directory/volume provider is an actionable preflight stop, never guessed.
    mounts = proposal["services"][name].get("volumes", [])
    configuration_mounts = []
    for mount in mounts:
        destination = mount.get("target", "") if isinstance(mount, dict) else ""
        if destination in ("/app", "/config"):
            raise Prerequisite("protected configuration directory requires a private resolved source snapshot")
        if destination.endswith(".json") and not destination.startswith("/run/netratel"):
            if mount.get("type") != "bind": raise Prerequisite("protected configuration volume requires a readable saved source")
            source = pathlib.Path(mount.get("source", ""))
            if not source.is_absolute() or not source.is_file() or source.is_symlink(): raise Prerequisite("protected configuration source missing")
            if "/app/appsettings" not in destination: raise Prerequisite("unrecognized protected configuration provider")
            configuration_mounts.append((destination, source))
    # Rebuild image defaults if a base file is replaced, so a removed winning
    # key cannot silently survive from the hidden image provider.
    if configuration_mounts:
        protected.clear()
        replacements = dict(configuration_mounts)
        for configuration_name in ("appsettings.json", "appsettings." + deployment_environment + ".json"):
            destination = "/app/" + configuration_name
            private_file = replacements.get(destination, root / (".image-" + name + "-" + configuration_name))
            if private_file.is_file(): read_configuration(private_file)
        for _, path in configuration_mounts:
            if path not in paths:
                paths.append(path)
                state.append({"path": str(path), "hash": configuration_hashes[str(path)]})
    protected_defaults[name] = protected
proposal["x-netratel-image-environment"] = image_defaults
proposal["x-netratel-image-ids"] = image_ids
proposal["x-netratel-protected-configuration"] = protected_defaults
# Include mounted provider files in the persistent source guard too.
old = root / ".approved-source-state.json"
if verify and old.exists():
    saved = json.loads(old.read_text())
    approved_overlay = {"path": str(root / "compose.http-host.yaml"), "hash": saved.get("exportedHash")}
    release_transition = saved.get("manager") == "release-compose" and approved_overlay in state and [source for source in state if source != approved_overlay] == saved["sources"]
    if state != saved["sources"] and not release_transition:
        # A manager may replace the one saved Compose file with exactly the
        # reviewed complete export. Arbitrary source changes remain blocked.
        exported = saved.get("exportedHash")
        changed = [s for s in state if s not in saved["sources"]]
        if len(state) != len(saved["sources"]) or len(changed) != 1 or changed[0]["path"] != saved["sources"][0]["path"] or changed[0]["hash"] != exported:
            raise Prerequisite("approved sources changed; run setup without --verify to review current sources")
(root / ".source-state.json").write_text(json.dumps({"sources": state}))
proposal["x-netratel-source-files"] = state
# Observe the applied serving service separately from the next Compose proposal.
if verify:
    observed = {}
    observed_protected = {}
    observed_images = {}
    for name in ("mcp-http", "api", "web"):
        command = ["docker", "ps", "--filter", "label=com.docker.compose.project=" + os.environ["NETRATEL_LAUNCHER_PROJECT"], "--filter", "label=com.docker.compose.service=" + name, "--filter", "label=com.docker.compose.oneoff=False", "--format", "{{.ID}}"]
        result = subprocess.run(command, capture_output=True, timeout=20)
        containers = result.stdout.decode().splitlines()
        if result.returncode or len(containers) != 1: raise Prerequisite("a unique running " + name + " container is required before verification")
        result = subprocess.run(["docker", "inspect", "--format", template, containers[0]], capture_output=True, timeout=20)
        if result.returncode: raise Prerequisite("serving runtime unavailable")
        runtime = environment_records(result.stdout, name, "serving runtime")
        observed[name] = runtime
        if name == "mcp-http":
            runtime_owner = runtime.get("NETRATEL_SETUP_DEPLOYMENT_MANAGER")
            runtime_owner = "komodo" if runtime_owner == "external-compose" else runtime_owner
            label_owner = subprocess.run(["docker", "inspect", "--format", '{{index .Config.Labels "com.netratel.deployment.manager"}}', containers[0]], capture_output=True, timeout=15)
            label = label_owner.stdout.decode().strip()
            label = "komodo" if label == "external-compose" else label
            if label_owner.returncode or any(owner not in (None, "", "<no value>", os.environ["NETRATEL_LAUNCHER_MANAGER"]) for owner in (runtime_owner, label)):
                raise Prerequisite("deployment_manager_conflict: Serving deployment ownership disagrees with the reviewed owner; repair its metadata before verification")
        identifier = subprocess.run(["docker", "inspect", "--format", "{{.Image}}", containers[0]], capture_output=True, timeout=20)
        actual_image = identifier.stdout.decode().strip()
        if identifier.returncode or not actual_image.startswith("sha256:") or len(actual_image) != 71: raise Prerequisite("The applied serving image cannot be established; recreate the reviewed deployment before verification")
        observed_images[name] = actual_image
        protected = {}; configuration_hashes = {}
        folded_runtime = {identity(key): value for key, value in runtime.items()}
        actual_environment = folded_runtime.get("dotnet_environment") or folded_runtime.get("aspnetcore_environment") or "Production"
        if not actual_environment.replace("-", "").replace("_", "").isalnum() or len(actual_environment) > 80: raise Prerequisite("The serving deployment environment is invalid; restore its actual source before verification")
        # Read the serving container filesystem, including its mounted providers,
        # rather than assuming the selected image/source has already been applied.
        for configuration_name in ("appsettings.json", "appsettings." + actual_environment + ".json"):
            private_file = root / (".runtime-" + name + "-" + configuration_name)
            private_file.unlink(missing_ok=True)
            copied = subprocess.run(["docker", "cp", containers[0] + ":/app/" + configuration_name, str(private_file)], capture_output=True, timeout=20)
            if copied.returncode == 0:
                if private_file.is_symlink(): raise Prerequisite("Serving application settings must be regular readable files; restore the mounted provider before verification")
                private_file.chmod(0o600)
                read_configuration(private_file)
        observed_protected[name] = protected
    observed["x-netratel-protected-configuration"] = observed_protected
    observed["x-netratel-runtime-images"] = observed_images
    (root / ".runtime-environment.json").write_text(json.dumps(observed))
(root / ".effective-deployment.json").write_text(json.dumps(proposal))
for name in (".compose-source.yaml", ".effective-deployment.json"):
    if not 0 < (root / name).stat().st_size <= 262144: raise Prerequisite("resolved deployment exceeds 256 KiB")
PY
  then
    if [[ -f "$output_dir/.preflight-action" ]]; then
      IFS= read -r prerequisite < "$output_dir/.preflight-action"
      [[ ${#prerequisite} -le 2048 && "$prerequisite" != *[[:cntrl:]]* ]] || prerequisite='deployment_prerequisite: Private source validation failed. Restore the saved source and retry.'
      die "$prerequisite No host approval or temporary verifier was requested. Private diagnostics: $output_dir/preflight.log."
    fi
    die "deployment_prerequisite: Restore the saved source locations and local serving images, then rerun setup. Private diagnostics: $output_dir/preflight.log. No approval or verifier was requested."
  fi
  source_hash=$(sha256sum <"$output_dir/.compose-source.yaml"); source_hash=${source_hash%% *}
  stage_sources=(.compose-source.yaml .effective-deployment.json)
  if [[ "$verify" == true ]]; then stage_sources+=(.runtime-environment.json); fi
  for source_name in "${stage_sources[@]}"; do
    stage=("${compose[@]}" run --rm --no-deps -T --entrypoint /bin/sh
      --user 10001:10001 --volume "$setup_volume:/setup-output"
      mcp-http -eu -c 'umask 077
source_temp=$(mktemp /setup-output/.compose-source.XXXXXX)
trap '\''rm -f "$source_temp"'\'' EXIT
head -c 262145 > "$source_temp"
source_size=$(wc -c < "$source_temp")
test "$source_size" -gt 0 && test "$source_size" -le 262144
chmod 600 "$source_temp"
mv -f "$source_temp" "/setup-output/$1"
trap - EXIT
sha256sum < "/setup-output/$1"' setup-stage "$source_name")
    if ! staged_receipt=$("${stage[@]}" <"$output_dir/$source_name" 2>"$output_dir/source-stage.log"); then
      die "Persistent setup storage is not writable by UID 10001. Restore its existing ownership/mount; no approval was requested. Private diagnostics: $output_dir/source-stage.log."
    fi
    expected_hash=$(sha256sum <"$output_dir/$source_name"); expected_hash=${expected_hash%% *}
    [[ "$staged_receipt" == "$expected_hash  -" ]] || die 'The private effective deployment snapshot failed its source hash check. Retry with stable saved sources.'
  done
fi
check_sources() {
  python3 - "$source_state" <<'PY'
import hashlib, json, pathlib, sys
for source in json.loads(pathlib.Path(sys.argv[1]).read_text())["sources"]:
    if hashlib.sha256(pathlib.Path(source["path"]).read_bytes()).hexdigest() != source["hash"]: sys.exit(1)
PY
}
remember_approved_state() {
  local digest=$1 generation_temp
  generation_temp=$(mktemp "$output_dir/.owner-generation.XXXXXX")
  if ! docker cp "$tool_name:/setup-output/.http-host.setup.json" "$generation_temp" >>"$output_dir/export.log" 2>&1; then
    rm -f -- "$generation_temp"
    die 'deployment_manager_state_invalid: The reviewed host generation could not be recorded. Keep the setup container and volume; no ownership decision was saved.'
  fi
  if ! python3 - "$source_state" "$output_dir/.approved-source-state.json" "$digest" "$manager" "$daemon_id" "$project" "$compose_dir" "$setup_volume" "$generation_temp" "${#compose_files[@]}" "${compose_files[@]}" "${env_files[@]}" <<'PY_REMEMBER'
import hashlib, json, os, pathlib, stat, sys, tempfile
source, destination, exported, manager, daemon, project, directory, volume, generation_path = sys.argv[1:10]
count = int(sys.argv[10]); compose = sys.argv[11:11 + count]; env = sys.argv[11 + count:]
generation = pathlib.Path(generation_path)
if not stat.S_ISREG(generation.lstat().st_mode) or generation.stat().st_size > 262144: raise ValueError("invalid saved generation")
metadata = json.loads(generation.read_text())
if metadata.get("DeploymentManager") != manager: raise ValueError("saved generation ownership disagrees")
state = json.loads(pathlib.Path(source).read_text())
state.update({"manager": manager, "exportedHash": exported, "generationHash": hashlib.sha256(generation.read_bytes()).hexdigest(), "setupVolume": volume,
              "binding": {"daemon": daemon, "project": project, "directory": directory, "composeFiles": compose, "envFiles": env}})
target = pathlib.Path(destination)
fd, temporary = tempfile.mkstemp(prefix=".approved-source-state-", dir=target.parent)
try:
    with os.fdopen(fd, "w") as stream:
        os.fchmod(stream.fileno(), 0o600)
        json.dump(state, stream); stream.flush(); os.fsync(stream.fileno())
    os.replace(temporary, target)
finally:
    pathlib.Path(temporary).unlink(missing_ok=True)
PY_REMEMBER
  then
    rm -f -- "$generation_temp"
    die 'deployment_manager_state_invalid: The reviewed generation and owner disagree. Keep the setup container and volume; no ownership decision was saved.'
  fi
  rm -f -- "$generation_temp"
}
if [[ "$target" == http-host ]] && ! check_sources; then
  die 'The Compose/env_file sources changed while preparing the private snapshot. Retry with stable saved sources; no wizard was started.'
fi
run=("${compose[@]}" run --name "$tool_name" --no-deps --user 10001:10001)
if [[ ! -t 0 || ! -t 1 ]]; then run+=(-T); fi
host_apply=("${docker_environment[@]}" "${compose[@]}" -f "$output_dir/compose.http-host.yaml" up -d web api mcp-http)
printf -v host_apply_command '%q ' "${host_apply[@]}"
apply_guard='import hashlib,json,pathlib,sys; state=json.loads(pathlib.Path(sys.argv[1]).read_text()); expected=state["sources"]+[{"path":sys.argv[2],"hash":state["exportedHash"]}]; valid=all(pathlib.Path(s["path"]).is_file() and pathlib.Path(s["path"]).stat().st_size<=262144 and hashlib.sha256(pathlib.Path(s["path"]).read_bytes()).hexdigest()==s["hash"] for s in expected); sys.exit(0 if valid else "approved sources changed: stop apply and rerun ordinary setup to review the current deployment")'
printf -v apply_guard_command '%q ' python3 -c "$apply_guard" "$output_dir/.approved-source-state.json" "$output_dir/compose.http-host.yaml"
host_apply_command="${apply_guard_command% } && ${host_apply_command% }"
run+=(
  --env "NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY=$output_dir"
  --env "NETRATEL_SETUP_HOST_APPLY_COMMAND=${host_apply_command% }"
  --volume "$setup_volume:/setup-output"
  --volume "$client_volume:/var/lib/netratel/clients"
  --env NETRATEL_SETUP_DIRECTORY=/setup-output
  --env "NETRATEL_SETUP_VOLUME=$setup_volume"
  --env NETRATEL_CLIENT_DIRECTORY=/var/lib/netratel/clients
  --env "NETRATEL_CLIENT_VOLUME=$client_volume"
  --env "NETRATEL_SETUP_DEPLOYMENT_MANAGER=$manager"
  --env "NETRATEL_SETUP_COMPOSE_FILES=$(IFS=,; printf '%s' "${compose_files[*]}")"
  --env "NETRATEL_SETUP_PROJECT_NAME=$project")
if [[ "$target" == http-host && "$verify" == false ]]; then
  export_document=compose.komodo.yaml
  if [[ "$manager" == release-compose ]]; then export_document=compose.http-host.yaml; fi
  run+=(--env "NETRATEL_SETUP_EXPORT_RECEIPT=$export_receipt" --env "NETRATEL_SETUP_EXPORT_DOCUMENT=$export_document")
fi
if [[ "$target" == http-host ]]; then
  run+=(--env NETRATEL_SETUP_COMPOSE_SOURCE=/setup-output/.compose-source.yaml
    --env NETRATEL_SETUP_EFFECTIVE_COMPOSE_SOURCE=/setup-output/.effective-deployment.json)
  if [[ "$verify" == true ]]; then run+=(--env NETRATEL_SETUP_RUNTIME_ENVIRONMENT_SOURCE=/setup-output/.runtime-environment.json); fi
  if [[ "$web_environment_observed" == true ]]; then run+=(--env NETRATEL_SETUP_WEB_ENVIRONMENT_OBSERVED=true); fi
  if [[ -n "$web_api_backend" ]]; then run+=(--env "NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL=$web_api_backend"); fi
  if [[ -n "$web_mcp_backend" ]]; then run+=(--env "NETRATEL_SETUP_EXISTING_WEB_MCP_BACKEND_URL=$web_mcp_backend"); fi
fi
run+=(mcp-http setup --target "$target")
if [[ "$target" == http-host ]]; then
  run+=(--deployment-manager "$manager" --compose-file /setup-output/.compose-source.yaml)
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
  if ! check_sources; then
    printf 'The saved deployment sources changed during setup. Container %s and its generation were retained; export/completion stopped. Rerun ordinary setup against the current saved deployment.\n' "$tool_name" >&2
    exit 1
  fi
fi

if [[ "$target" == http-host && "$manager" == komodo && "$verify" == false && ( "$setup_status" == 0 || "$setup_status" == 2 ) ]]; then
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
    check_sources || die "The saved sources changed during export. Keep the approved generation and rerun ordinary setup; no file was published."
    chmod 600 -- "$export_temp"
    mv -f -- "$export_temp" "$output_dir/compose.komodo.yaml"
    remember_approved_state "$actual_digest"
    printf '\nPrivate complete externally managed Compose file: %s/compose.komodo.yaml\n' "$output_dir"
    if [[ "$setup_status" == 2 ]]; then
      printf 'Open that file privately in the existing deployment manager. Review the complete replacement, then Save and Deploy the same stack.\n'
      printf 'Saved — apply pending. After reviewed manager apply, verification remains pending. After deployment run:\n'
      resume=("${docker_environment[@]}" bash "$(realpath -- "$0")" "${compose_selection[@]}" --project "$project" --project-directory "$compose_dir" --manager external-compose)
      for item in "${compose_files[@]}"; do resume+=(--compose-file "$item"); done
      for item in "${env_files[@]}"; do resume+=(--env-file "$item"); done
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
if [[ "$target" == http-host && "$manager" == release-compose && "$verify" == false && ( "$setup_status" == 0 || "$setup_status" == 2 ) ]]; then
  overlay_temp=$(mktemp "$output_dir/.compose.http-host.XXXXXX")
  receipt_temp=$(mktemp "$output_dir/.export-receipt.XXXXXX")
  expected_digest=
  if ! docker cp "$tool_name:$export_receipt" "$receipt_temp" >"$output_dir/export.log" 2>&1 ||
      [[ $(stat -c %s -- "$receipt_temp") != 65 ]] ||
      ! IFS= read -r expected_digest < "$receipt_temp" || [[ ! "$expected_digest" =~ ^[0-9a-f]{64}$ ]] ||
      ! docker cp "$tool_name:/setup-output/compose.http-host.yaml" "$overlay_temp" >>"$output_dir/export.log" 2>&1; then
    rm -f -- "$overlay_temp" "$receipt_temp"
    die "No current export receipt and release overlay were available. Saved generation and container $tool_name were retained; no deployment was applied."
  fi
  rm -f -- "$receipt_temp"
  actual_digest=$(sha256sum < "$overlay_temp"); actual_digest=${actual_digest%% *}
  if [[ "$actual_digest" != "$expected_digest" ]]; then
    rm -f -- "$overlay_temp"
    die "The release overlay did not match this run's export receipt. Saved generation and container $tool_name were retained; no deployment was applied."
  fi
  check_sources || die "The saved sources changed during overlay export. Keep the approved generation and rerun ordinary setup."
  chmod 600 -- "$overlay_temp"
  mv -f -- "$overlay_temp" "$output_dir/compose.http-host.yaml"
  remember_approved_state "$actual_digest"
  printf '\nSaved — apply pending. Review the private overlay and run on this Docker host:\n  %s\n' "$host_apply_command"
  printf 'After apply, resume bash ./setup-http-mcp.sh --verify with the same instance selection.\n'
fi
if [[ "$target" == http-host && "$verify" == true && "$setup_status" == 0 ]]; then
  printf 'Server verification completed. The saved identity and applied deployment were checked; no Compose reimport is required.\n'
fi
if timeout 15s docker inspect --format '{{.State.Running}}' "$tool_name" 2>/dev/null | { read -r running; [[ "$running" == false ]]; }; then
  docker rm "$tool_name" >/dev/null
fi
exit "$setup_status"
