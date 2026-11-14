#!/usr/bin/env bash
# KITE database tests: builds a fresh database from the migrations and runs every test file.
# Needs a PostgreSQL 16+ superuser connection (PGHOST/PGPORT/PGUSER/PGPASSWORD or local peer auth).
#   db/tests/run.sh                 # uses database kite_dbtest
#   KITE_TEST_DB=other db/tests/run.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DB_DIR="$(cd "$HERE/.." && pwd)"
TEST_DB="${KITE_TEST_DB:-kite_dbtest}"
PSQL=(psql -X -q -v ON_ERROR_STOP=1 --no-psqlrc)

echo "== recreating database $TEST_DB"
"${PSQL[@]}" -d postgres -c "DROP DATABASE IF EXISTS \"$TEST_DB\" WITH (FORCE)"
"${PSQL[@]}" -d postgres -c "CREATE DATABASE \"$TEST_DB\""

echo "== bootstrap (superuser)"
"${PSQL[@]}" -d "$TEST_DB" -f "$DB_DIR/bootstrap/00_bootstrap.sql"

echo "== migrations (as kite_migrator)"
for f in "$DB_DIR"/migrations/V*.sql; do
  echo "   $(basename "$f")"
  PGOPTIONS="-c role=kite_migrator" "${PSQL[@]}" -d "$TEST_DB" -1 -f "$f"
done

echo "== tests"
pass=0
for f in "$HERE"/[0-9][0-9]_*.sql; do
  name="$(basename "$f")"
  if "${PSQL[@]}" -d "$TEST_DB" -f "$f" > /tmp/kite_dbtest_out.txt 2>&1; then
    echo "   PASS $name"
    pass=$((pass+1))
  else
    echo "   FAIL $name"
    cat /tmp/kite_dbtest_out.txt
    exit 1
  fi
done
echo "== all $pass database test files passed"
