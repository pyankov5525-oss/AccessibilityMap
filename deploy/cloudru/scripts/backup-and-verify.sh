#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

: "${SOURCE_DATABASE_URL:?Set SOURCE_DATABASE_URL without putting it in shell history}"
BACKUP_DIR="${BACKUP_DIR:-./migration-backups}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DUMP="$BACKUP_DIR/accessibilitymap-$STAMP.dump"
CHECKSUM="$DUMP.sha256"
mkdir -p "$BACKUP_DIR"

command -v pg_dump >/dev/null || { echo "pg_dump is required" >&2; exit 1; }
command -v pg_restore >/dev/null || { echo "pg_restore is required" >&2; exit 1; }

echo "Creating a consistent custom-format backup..."
pg_dump --dbname="$SOURCE_DATABASE_URL" --format=custom --compress=9 \
  --no-owner --no-acl --file="$DUMP"
sha256sum "$DUMP" > "$CHECKSUM"
sha256sum --check "$CHECKSUM"
pg_restore --list "$DUMP" >/dev/null

echo "Backup created and structurally verified: $DUMP"
echo "Next, restore this exact file into a disposable test database before the target database."
