#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

if [[ -e compose.sqlite.yaml || -e release/compose.local-sqlite.yaml || -e src/NetRatel/NetRatel.SqliteMigrations ]]; then
  echo "An active first-party SQLite deployment or migration artifact remains." >&2
  exit 1
fi

if rg -n 'Microsoft\.EntityFrameworkCore\.Sqlite|NetRatel\.SqliteMigrations|UseSqlite\(' \
  src/NetRatel --glob '*.csproj' --glob '*.cs' --glob '!**/obj/**' --glob '!**/bin/**'; then
  echo "An active first-party SQLite package, reference, or registration remains." >&2
  exit 1
fi

if rg -n 'compose\.local-sqlite|compose\.sqlite\.yaml|provider: sqlite' \
  .github/workflows release tools/ci \
  --glob '!check-postgresql-only.sh'; then
  echo "An active SQLite recipe or CI matrix entry remains." >&2
  exit 1
fi

echo "PostgreSQL-only provider and deployment contract passed."
