#!/usr/bin/env bash
# Runs the schema test suites against a throwaway local PostgreSQL cluster.
#   PG_BIN   directory containing initdb/pg_ctl/psql (default: PATH, or PostgreSQL 18 on Windows)
#   PG_PORT  port for the temporary cluster (default 54329)
#   GOLDEN_OUT  if set, also regenerate the Unity golden-vector test data into this file
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
supabase_dir="$(dirname "$here")"
if [[ -z "${PG_BIN:-}" ]]; then
  if command -v initdb >/dev/null 2>&1; then PG_BIN="$(dirname "$(command -v initdb)")"
  else PG_BIN="/c/Program Files/PostgreSQL/18/bin"; fi
fi
port="${PG_PORT:-54329}"
data="$(mktemp -d)"
log="$data.log"

cleanup() {
  "$PG_BIN/pg_ctl" -D "$data" stop -m immediate >/dev/null 2>&1 || true
  rm -rf "$data" "$log"
}
trap cleanup EXIT

"$PG_BIN/initdb" -D "$data" -U postgres -A trust -E UTF8 >/dev/null
"$PG_BIN/pg_ctl" -D "$data" -o "-p $port" -l "$log" start -w >/dev/null

psql() { "$PG_BIN/psql" -X -q -p "$port" -U postgres -d game -v ON_ERROR_STOP=1 "$@"; }
"$PG_BIN/psql" -X -q -p "$port" -U postgres -c "create database game"
psql -f "$here/supabase_stub.sql" -f "$supabase_dir/schema.sql" -f "$supabase_dir/seed.sql" >/dev/null

if [[ -n "${GOLDEN_OUT:-}" ]]; then
  psql -v golden_out="$GOLDEN_OUT" -f "$here/golden_vectors.sql"
  psql -c "delete from auth.users where id = '99999999-9999-9999-9999-999999999999'"
  echo "Wrote $GOLDEN_OUT"
fi

for suite in "$here"/[0-9][0-9]_*.sql; do
  psql -f "$suite"
done
echo "All schema suites passed."
