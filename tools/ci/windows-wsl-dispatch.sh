#!/usr/bin/env bash
set -euo pipefail

payload="${NETRATEL_LOCAL_FIRST_WSL_ARGUMENTS:?NETRATEL_LOCAL_FIRST_WSL_ARGUMENTS is required}"
read -r -a encoded_arguments <<< "$payload"
arguments=()
for encoded_argument in "${encoded_arguments[@]}"; do
  arguments+=("$(printf '%s' "$encoded_argument" | base64 --decode)")
done

[[ "${#arguments[@]}" -gt 0 ]] || {
  echo 'NETRATEL_LOCAL_FIRST_WSL_ARGUMENTS did not contain a command.' >&2
  exit 2
}

exec "${arguments[@]}"
