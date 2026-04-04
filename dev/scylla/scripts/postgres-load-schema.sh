#!/bin/bash
set -euo pipefail

# Usage: postgres-load-schema.sh <db-host> <db-name> <db-user> <schema-file> [schema-file...]
DB_HOST="${1:-}"
DB_NAME="${2:-}"
DB_USER="${3:-}"
shift 3 || true
SCHEMA_FILES=("$@")

if [ -z "$DB_HOST" ] || [ -z "$DB_NAME" ] || [ -z "$DB_USER" ] || [ "${#SCHEMA_FILES[@]}" -eq 0 ]; then
  echo "Usage: $0 <db-host> <db-name> <db-user> <schema-file> [schema-file...]" >&2
  exit 1
fi

echo "Waiting for Postgres endpoint on ${DB_HOST}..."
for i in $(seq 1 90); do
  if pg_isready -h "$DB_HOST" -U "$DB_USER" -d postgres >/dev/null 2>&1; then
    break
  fi
  sleep 2
done

if ! pg_isready -h "$DB_HOST" -U "$DB_USER" -d postgres >/dev/null 2>&1; then
  echo "Postgres did not become ready in time." >&2
  exit 1
fi

echo "Ensuring database ${DB_NAME} exists..."
psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$DB_USER" -d postgres -c "CREATE DATABASE \"${DB_NAME}\"" >/dev/null 2>&1 || true

for schema_file in "${SCHEMA_FILES[@]}"; do
  if [ ! -f "$schema_file" ]; then
    echo "Schema file not found: ${schema_file}" >&2
    exit 1
  fi

  echo "Applying Postgres schema: ${schema_file}"
  psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -f "$schema_file"
done

echo "Postgres schema load complete."
