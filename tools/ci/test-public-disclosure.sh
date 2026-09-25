#!/usr/bin/env bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source_gate="$source_root/tools/ci/check-public-disclosure.sh"
test_root="$(mktemp -d)"

cleanup() {
  find "$test_root" -depth -delete 2>/dev/null || true
}
trap cleanup EXIT

new_fixture() {
  local name="$1"
  local fixture="$test_root/$name"
  mkdir -p "$fixture/tools/ci"
  cp "$source_gate" "$fixture/tools/ci/check-public-disclosure.sh"
  chmod +x "$fixture/tools/ci/check-public-disclosure.sh"
  git -C "$fixture" init --quiet
  git -C "$fixture" config user.email test@example.invalid
  git -C "$fixture" config user.name "NetRatel disclosure test"
  printf '# Synthetic public fixture\n' > "$fixture/README.md"
  git -C "$fixture" add README.md
  printf '%s' "$fixture"
}

accepted_fixture="$(new_fixture accepted)"
"$accepted_fixture/tools/ci/check-public-disclosure.sh" >/dev/null

forbidden_path_fixture="$(new_fixture forbidden-path)"
printf 'synthetic only\n' > "$forbidden_path_fixture/.env"
git -C "$forbidden_path_fixture" add .env
if "$forbidden_path_fixture/tools/ci/check-public-disclosure.sh" >/dev/null 2>&1; then
  echo "Disclosure gate accepted a forbidden tracked configuration path." >&2
  exit 1
fi

staged_secret_fixture="$(new_fixture staged-secret)"
printf 'AKIA%s\n' '0000000000000000' > "$staged_secret_fixture/README.md"
git -C "$staged_secret_fixture" add README.md
printf '# Safe working copy after staging\n' > "$staged_secret_fixture/README.md"
if "$staged_secret_fixture/tools/ci/check-public-disclosure.sh" >/dev/null 2>&1; then
  echo "Disclosure gate accepted staged-only synthetic credential material." >&2
  exit 1
fi

xml_key_fixture="$(new_fixture xml-key)"
printf '<%s>synthetic</%s>\n' key key > "$xml_key_fixture/README.md"
git -C "$xml_key_fixture" add README.md
if "$xml_key_fixture/tools/ci/check-public-disclosure.sh" >/dev/null 2>&1; then
  echo "Disclosure gate accepted an XML key tag outside the installer plist template." >&2
  exit 1
fi

plist_fixture="$(new_fixture plist-template)"
mkdir -p "$plist_fixture/src/NetRatel/NetRatel.Infrastructure/Artifacts"
printf '<%s>Label</%s>\n' key key > "$plist_fixture/src/NetRatel/NetRatel.Infrastructure/Artifacts/ScriptTemplateService.cs"
git -C "$plist_fixture" add src/NetRatel/NetRatel.Infrastructure/Artifacts/ScriptTemplateService.cs
"$plist_fixture/tools/ci/check-public-disclosure.sh" >/dev/null

echo "Public disclosure gate synthetic tests passed."
