#!/usr/bin/env bash

chromium_nss_xdg_data_home=""

require_chromium_nss_legacy_store_absent() {
  local legacy_database="$1"

  if [[ -e "$legacy_database" || -L "$legacy_database" ]]; then
    echo 'Refusing to run the Chromium smoke with a pre-existing ~/.pki/nssdb store.' >&2
    return 1
  fi
}

prepare_chromium_nss_database() {
  local xdg_data_home="$1"
  local ca_certificate="$2"
  local database_path="$xdg_data_home/pki/nssdb"

  command -v certutil >/dev/null 2>&1 || {
    echo 'The Chromium OIDC smoke requires libnss3-tools (certutil).' >&2
    return 1
  }
  [[ -s "$ca_certificate" ]] || {
    echo 'The Chromium OIDC smoke CA certificate is missing.' >&2
    return 1
  }

  mkdir -m 0700 -p "$database_path" || return 1
  certutil -N --empty-password -d "sql:$database_path" || return 1
  certutil -A -d "sql:$database_path" -n netratel-smoke-root -t 'C,,' -i "$ca_certificate" || return 1
  certutil -L -d "sql:$database_path" -n netratel-smoke-root >/dev/null || return 1
}

prepare_chromium_nss_trust() {
  local ca_certificate="$1"
  local home_directory="${HOME:-}"
  local legacy_database
  local temporary_root

  [[ -n "$home_directory" ]] || {
    echo 'A home directory is required to safely check Chromium NSS database precedence.' >&2
    return 1
  }

  legacy_database="$home_directory/.pki/nssdb"
  require_chromium_nss_legacy_store_absent "$legacy_database" || return 1

  temporary_root="${TMPDIR:-/tmp}"
  if [[ "$temporary_root" != /* ]]; then
    temporary_root=/tmp
  fi
  chromium_nss_xdg_data_home="$(mktemp -d "${temporary_root%/}/netratel-chromium-nss.XXXXXX")" || return 1
  chmod 0700 "$chromium_nss_xdg_data_home" || return 1
  prepare_chromium_nss_database "$chromium_nss_xdg_data_home" "$ca_certificate" || return 1

  export NETRATEL_BROWSER_SMOKE_NSS_DATA_HOME="$chromium_nss_xdg_data_home"
}

cleanup_chromium_nss_trust() {
  local temporary_root="${TMPDIR:-/tmp}"
  local owned_directory="${chromium_nss_xdg_data_home:-}"

  [[ -n "$owned_directory" ]] || return 0
  if [[ "$temporary_root" != /* ]]; then
    temporary_root=/tmp
  fi
  case "$owned_directory" in
    "${temporary_root%/}"/netratel-chromium-nss.*) ;;
    *)
      echo 'Refusing to remove a Chromium NSS directory outside the task-owned temporary path.' >&2
      return 1
      ;;
  esac

  find "$owned_directory" -depth -delete
  chromium_nss_xdg_data_home=""
  unset NETRATEL_BROWSER_SMOKE_NSS_DATA_HOME
}
