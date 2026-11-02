#!/usr/bin/env bash
# Run migrations + tests against a throwaway local PostgreSQL database.
# Usage: PGUSER=postgres ./db/tests/run_local.sh
set -euo pipefail
cd "$(dirname "$0")/.."

DB=pc_test
dropdb --if-exists "$DB"
createdb "$DB"

run() { psql -v ON_ERROR_STOP=1 -q -d "$DB" -f "$1"; }

run tests/00_supabase_stub.sql
run migrations/001_schema.sql
run migrations/002_rpc.sql
run migrations/003_tighten_grants.sql
run migrations/004_time_requests.sql
run migrations/005_locked_by_default.sql
run migrations/006_child_locked_by_default.sql
run migrations/007_hardening.sql
psql -v ON_ERROR_STOP=1 -q -d "$DB" -f tests/01_rpc_tests.sql 2>&1 >/dev/null | sed 's/^psql:[^ ]* NOTICE:  //'
