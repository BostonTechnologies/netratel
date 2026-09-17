#| NetRatel-MANIFEST
{
  "name": "Linux System Snapshot",
  "version": 1,
  "params": []
}
#| END

set -u

log() {
  printf '[%s] [linux-system-snapshot] %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

section() {
  printf '\n[%s] [linux-system-snapshot] ===== %s =====\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

log "START Linux System Snapshot"
log "Running as user: $(whoami 2>/dev/null || printf 'unknown')"
log "User identity: $(id 2>/dev/null || printf 'unknown')"
log "Host short name: $(hostname 2>/dev/null || printf 'unknown')"
log "Host fqdn: $(hostname -f 2>/dev/null || printf 'unknown')"
log "Working directory: $(pwd)"

section "Operating System"
log "Command: uname -a"
uname -a
if [ -r /etc/os-release ]; then
  log "Command: sed -n '1,12p' /etc/os-release"
  sed -n '1,12p' /etc/os-release
else
  log "/etc/os-release is not readable"
fi

section "Uptime And Load"
log "Command: uptime"
uptime

section "Memory"
if command -v free >/dev/null 2>&1; then
  log "Command: free -h"
  free -h
else
  log "free is not available on this host"
fi

section "Top Processes By CPU"
log "Command: ps -eo pid,ppid,user,stat,pcpu,pmem,comm --sort=-pcpu | head -12"
ps -eo pid,ppid,user,stat,pcpu,pmem,comm --sort=-pcpu | head -12

section "Service-Safe Environment Context"
log "Selected environment values only; secrets and full environment are intentionally not printed"
printf 'HOME=%s\n' "${HOME:-}"
printf 'SHELL=%s\n' "${SHELL:-}"
printf 'PATH=%s\n' "${PATH:-}"
printf 'PWD=%s\n' "${PWD:-}"
printf 'DOTNET_ENVIRONMENT=%s\n' "${DOTNET_ENVIRONMENT:-}"

log "END Linux System Snapshot"
