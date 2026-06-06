#!/bin/bash
set -euo pipefail

# Usage: postgres-load-schema.sh <db-host> <db-name> <schema-file> [schema-file...]
#
# Uses admin credentials from recovery volume (created by postgres-bootstrap-auth.sh)
# to apply schema files. The admin account owns the database and has DDL privileges.
#
# Required environment variables:
#   RECOVERY_PATH  - Path to recovery volume (default: /recovery)
#
# The script no longer needs PGUSER/PGPASSWORD since it reads admin creds from recovery.

RECOVERY_DIR="${RECOVERY_PATH:-/recovery}"
ADMIN_CREDS_FILE="${RECOVERY_DIR}/pg-admin-credentials.txt"

# --- Load admin credentials from recovery volume ---
if [ ! -f "$ADMIN_CREDS_FILE" ]; then
  echo "[pg-load-schema] ERROR: Admin credentials file not found at ${ADMIN_CREDS_FILE}."
  echo "[pg-load-schema]        Did postgres-bootstrap-auth run successfully?"
  exit 1
fi

DB_USER=$(grep -E "^username=" "$ADMIN_CREDS_FILE" | head -1 | cut -d= -f2-)
DB_PASS=$(grep -E "^password=" "$ADMIN_CREDS_FILE" | head -1 | cut -d= -f2-)

if [ -z "${DB_USER:-}" ] || [ -z "${DB_PASS:-}" ]; then
  echo "[pg-load-schema] ERROR: Could not read admin credentials from ${ADMIN_CREDS_FILE}."
  exit 1
fi

export PGPASSWORD="$DB_PASS"

DB_HOST="${1:-}"
DB_NAME="${2:-}"
shift 2 || true
SCHEMA_FILES=("$@")

if [ -z "$DB_HOST" ] || [ -z "$DB_NAME" ] || [ "${#SCHEMA_FILES[@]}" -eq 0 ]; then
  echo "Usage: $0 <db-host> <db-name> <schema-file> [schema-file...]" >&2
  exit 1
fi

echo "[pg-load-schema] Waiting for Postgres endpoint on ${DB_HOST}..."
for i in $(seq 1 90); do
  if pg_isready -h "$DB_HOST" -U "$DB_USER" -d postgres >/dev/null 2>&1; then
    break
  fi
  sleep 2
done

if ! pg_isready -h "$DB_HOST" -U "$DB_USER" -d postgres >/dev/null 2>&1; then
  echo "[pg-load-schema] Postgres did not become ready in time." >&2
  exit 1
fi

echo "[pg-load-schema] Connected as '${DB_USER}' (admin)."

# Ensure database exists (should already be created by bootstrap, but idempotent)
psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$DB_USER" -d postgres -c "CREATE DATABASE \"${DB_NAME}\"" >/dev/null 2>&1 || true

for schema_file in "${SCHEMA_FILES[@]}"; do
  if [ ! -f "$schema_file" ]; then
    echo "[pg-load-schema] Schema file not found: ${schema_file}" >&2
    exit 1
  fi

  echo "[pg-load-schema] Applying schema: ${schema_file}"
  psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -f "$schema_file"
done

echo "[pg-load-schema] Schema load complete."
