#!/usr/bin/env bash
set -Eeuo pipefail

: "${SOURCE_DATABASE_URL:?Set SOURCE_DATABASE_URL}"
: "${TARGET_DATABASE_URL:?Set TARGET_DATABASE_URL}"
: "${1:?Usage: restore-and-compare.sh path/to/backup.dump}"
DUMP="$1"

sha256sum --check "$DUMP.sha256"

# Target must be a new, empty database. --clean is intentionally omitted to
# prevent accidental destruction of a populated database.
if [[ "$(psql "$TARGET_DATABASE_URL" -Atqc "select count(*) from pg_catalog.pg_tables where schemaname='public'")" != "0" ]]; then
  echo "Refusing restore: target public schema is not empty" >&2
  exit 1
fi

pg_restore --dbname="$TARGET_DATABASE_URL" --no-owner --no-acl --exit-on-error "$DUMP"

TABLES=("AspNetUsers" "AspNetRoles" "AspNetUserRoles" "Placemarks" "Photos" "ActivityLogs" "PlacemarkVotes")
failed=0
printf '%-24s %12s %12s\n' TABLE SOURCE TARGET
for table in "${TABLES[@]}"; do
  source_count="$(psql "$SOURCE_DATABASE_URL" -Atqc "select count(*) from \"$table\"")"
  target_count="$(psql "$TARGET_DATABASE_URL" -Atqc "select count(*) from \"$table\"")"
  printf '%-24s %12s %12s\n' "$table" "$source_count" "$target_count"
  [[ "$source_count" == "$target_count" ]] || failed=1
done

if (( failed )); then
  echo "Row-count verification failed. Keep the old infrastructure active." >&2
  exit 1
fi
echo "Restore and critical table row counts verified. Continue with application smoke tests."
