#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

failed=0
report() {
  printf '%s\n' "$1" >&2
  failed=1
}

# A public export is an allowlisted product snapshot. These paths are private
# deployment collateral or runtime material and must never be tracked there.
# Keep environment-specific indicators out of this public script: those belong
# in the private pre-export review, not in a public repository.
while IFS= read -r -d '' path; do
  case "$path" in
    .dockerignore|.env.example|.gitattributes|.gitignore|.gitleaks.toml|AGENTS.md|CODE_OF_CONDUCT.md|CONTRIBUTING.md|Directory.Build.props|Directory.Build.targets|LICENSE|NetRatel.sln|NOTICE|README.md|SECURITY.md|build-main.ps1|compose.yaml|coverage.runsettings|dotnet-install.sh|dotnet-tools.json|global.json|readme.trustmodel.md|setup-dotnet9.sh|setup.sh|.github/*|docker/*|docs/*|release/*|samples/*|scripts/*|src/NetRatel/*|tests/*|tools/*)
      ;;
    *)
      report "Public disclosure gate rejected an unclassified tracked path."
      continue
      ;;
  esac
  case "$path" in
    .env.example)
      ;;
    .keys/*|*.pem|*.key|*.pfx|*.p12|*.jks|*.keystore|docker/komodo/*|docker/traefik/*|docs/observability/*|docs/helpdesk/*|docs/system-connectivity-runtime.md|migration/private/*|*.env|.env.*)
      report "Public disclosure gate rejected private path: $path"
      ;;
  esac
  if [[ -L "$path" ]]; then
    resolved="$(realpath -m "$path")"
    case "$resolved" in
      "$root"/*) ;;
      *) report "Public disclosure gate rejected an external symlink." ;;
    esac
  fi
done < <(git ls-files -z)

# Reject credential/key material from both the checkout and its staged export
# without printing matching values. The explicit index scan prevents a staged
# public candidate from being approved merely because a local worktree changed
# after staging.
secret_pattern='-----BEGIN ([A-Z ]*PRIVATE KEY|CERTIFICATE)|<encryptedKey[[:space:]>]|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}'
if git grep -I -q -E -- "$secret_pattern" --; then
  report "Public disclosure gate found potential key material in the checkout."
fi
if git grep --cached -I -q -E -- "$secret_pattern" --; then
  report "Public disclosure gate found potential key material in the staged export."
fi
# The macOS installer embeds launchd plist property tags. Scan every
# other source for XML key elements while still scanning this file for the
# stronger private-key, certificate, encrypted-key and token signatures above.
xml_key_pattern='<key[[:space:]>]'
plist_template='src/NetRatel/NetRatel.Infrastructure/Artifacts/ScriptTemplateService.cs'
if git grep -I -q -E -- "$xml_key_pattern" -- . ":!$plist_template"; then
  report "Public disclosure gate found potential XML key material in the checkout."
fi
if git grep --cached -I -q -E -- "$xml_key_pattern" -- . ":!$plist_template"; then
  report "Public disclosure gate found potential XML key material in the staged export."
fi

# CI runs a pinned Gitleaks action against Git history. Keep its evolving
# credential rules outside this public-safe script and inspect failures only in
# the protected review context.

if (( failed != 0 )); then
  exit 1
fi

echo "Public disclosure gate passed."
