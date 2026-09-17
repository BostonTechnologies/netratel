#| NetRatel-MANIFEST
{
  "name": "Linux Disk Report",
  "version": 1,
  "params": []
}
#| END

set -u

log() {
  printf '[%s] [linux-disk-report] %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

section() {
  printf '\n[%s] [linux-disk-report] ===== %s =====\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

log "START Linux Disk Report"
log "Running as user: $(whoami 2>/dev/null || printf 'unknown')"
log "Host: $(hostname -f 2>/dev/null || hostname 2>/dev/null || printf 'unknown')"
log "Kernel: $(uname -a 2>/dev/null || printf 'unknown')"
log "Working directory: $(pwd)"

section "Filesystem Capacity"
log "Command: df -hT"
df -hT

section "Inode Usage"
log "Command: df -ihT"
df -ihT

section "Mounted Filesystems Summary"
log "Command: findmnt --df --target /"
if command -v findmnt >/dev/null 2>&1; then
  findmnt --df --target / || log "findmnt --df --target / returned a non-zero status"
else
  log "findmnt is not available on this host"
fi

section "Largest Top-Level Directories Under /var"
log "Command: du -xhd1 /var 2>/dev/null | sort -h | tail -10"
if [ -d /var ]; then
  du -xhd1 /var 2>/dev/null | sort -h | tail -10 || log "du summary for /var returned a non-zero status"
else
  log "/var does not exist on this host"
fi

log "END Linux Disk Report"
