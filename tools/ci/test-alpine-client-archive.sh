#!/usr/bin/env bash
set -euo pipefail

archive="$(realpath "$1")"
version="$2"
revision="$3"
image=alpine:3.24

docker pull --platform linux/amd64 "$image"
image_digest="$(docker image inspect --format '{{index .RepoDigests 0}}' "$image")"
{
  echo "Alpine smoke image $image_digest (linux/amd64)"
  echo "Archive $(basename "$archive") ($(wc -c < "$archive") bytes)"
  sha256sum "$archive"
} | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"

docker run --rm -i --platform linux/amd64 --mount "type=bind,source=$archive,target=/client.tar.gz,readonly" \
  "$image_digest" /bin/sh -eu -s "$version" "$revision" <<'ALPINE'
packages="bash ca-certificates icu-data-full icu-libs krb5 libgcc libssl3 libstdc++ tzdata"
apk add --no-cache $packages
echo "Alpine $(cat /etc/alpine-release) $(uname -m)"
test "$(uname -m)" = x86_64
for package in $packages; do apk info -v "$package" | head -1; done
for package in dotnet-runtime aspnetcore-runtime gcompat libc6-compat glibc; do
  if apk info -e "$package"; then
    echo "Unexpected runtime or compatibility package $package" >&2
    exit 1
  fi
done
if command -v dotnet || command -v python3 || command -v python; then
  echo "The Alpine smoke requires a clean runtime without dotnet or Python" >&2
  exit 1
fi

work="$(mktemp -d)"
tar -xzf /client.tar.gz -C "$work"
cd "$work/netratel-client-linux-musl-x64"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work/extract" NetRatel_CLIENT_LOG_DIR="$work/logs"

actual="$(timeout 40 ./NetRatel.Client --version)"
test "$actual" = "$1+$2"
echo "Version passed ($actual)"

status=0
# Enable the informational startup line for this headless enrollment probe.
NetRatel__Logging__MinimumLevel=Information timeout 40 ./NetRatel.Client </dev/null > "$work/startup.log" 2>&1 || status=$?
if [ "$status" != 11 ] ||
   ! grep -F "Application $actual starting." "$work/startup.log" ||
   ! grep -F "Enrollment code is required for first run." "$work/startup.log"; then
  cat "$work/startup.log" >&2
  exit 1
fi
echo "Foreground startup reached the expected first-enrollment exit"

if ! timeout 40 ./NetRatel.Client --terminal-pty-self-test-native > "$work/pty.log" 2>&1; then
  cat "$work/pty.log" >&2
  exit 1
fi
grep -F '"backend":"unix-pty-native","forceNative":true' "$work/pty.log"
grep -Fq '"stage":"passed","reaped":true' "$work/pty.log"
echo "Forced-native PTY passed on Alpine without Python"
ALPINE

echo "Version, foreground startup and forced-native PTY passed on the extracted Alpine archive." \
  >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
