#!/usr/bin/env bash
set -euo pipefail

# Resolve cold test dependencies once, before concurrent Testcontainers startup.
deadline="${1:-120}"
if [[ "$#" -gt 1 || ! "$deadline" =~ ^([1-9]|[1-9][0-9]|1[01][0-9]|120)$ ]]; then
  echo "Usage: $0 [deadline-seconds: 1..120]" >&2
  exit 2
fi

images=(
  postgres:16-alpine
  # Default resource reaper in Testcontainers 4.15.0; keep its digest identical.
  testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0
)
stage_start="$SECONDS"

# One deadline covers the daemon probe, both serial pulls, and final inspection.
# Keep timeout's process group so its deadline also terminates Docker children.
if timeout --signal=TERM --kill-after=5s "${deadline}s" bash -s -- "${images[@]}" <<'PREPARE'
set -euo pipefail
# Keep the supervisor alive through timeout's KILL grace even if Docker exits
# on TERM while one of its descendants ignores it. The outer deadline bounds
# this trap and terminates the whole process group.
trap 'while :; do sleep 1; done' TERM
docker info --format '{{.ServerVersion}}' >/dev/null
for image in "$@"; do
  source=cached
  if ! docker image inspect --format '{{.Id}}' "$image" >/dev/null 2>&1; then
    source=pulled
    docker pull --quiet "$image"
  fi
  image_id="$(docker image inspect --format '{{.Id}}' "$image")"
  if [[ ! "$image_id" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    printf 'Docker returned no valid image identity for %s\n' "$image" >&2
    exit 1
  fi
  printf 'Fast regression container ready: %s id=%s source=%s\n' "$image" "$image_id" "$source"
done
PREPARE
then
  status=0
  outcome=ready
else
  status="$?"
  outcome="failed($status)"
fi

duration="$((SECONDS - stage_start))"
printf 'Fast regression containers: duration=%ss status=%s\n' "$duration" "$outcome"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  printf 'Fast regression containers: %ss (%s)\n\n' "$duration" "$outcome" >> "$GITHUB_STEP_SUMMARY" || true
fi
exit "$status"
