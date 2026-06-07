#!/bin/bash
set -euo pipefail

# postgres-seed-secrets.sh — Upserts secrets from environment variables into internal.secrets.
#
# Standalone utility for re-seeding secrets without re-running full bootstrap.
# Connects using admin credentials provided via environment variables.
#
# Well-known environment variables (all optional — only non-empty values are seeded):
#   OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET
#   OCTOCON_DISCORD_OAUTH_CLIENT_SECRET
#   OCTOCON_APPLE_OAUTH_CLIENT_SECRET
#   OCTOCON_ENCRYPTION_PEPPER
#
# Required environment variables:
#   PG_ADMIN_USER     - Admin username (e.g. octocon_admin)
#   PG_ADMIN_PASSWORD - Admin password
#
# Usage:
#   postgres-seed-secrets.sh <db-host> <db-name>

DB_HOST="${1:?Usage: postgres-seed-secrets.sh <db-host> <db-name>}"
DB_NAME="${2:?Usage: postgres-seed-secrets.sh <db-host> <db-name>}"

DB_USER="${PG_ADMIN_USER:?PG_ADMIN_USER is required}"
DB_PASS="${PG_ADMIN_PASSWORD:?PG_ADMIN_PASSWORD is required}"

export PGPASSWORD="$DB_PASS"

echo "[pg-seed-secrets] Seeding secrets into ${DB_NAME} on ${DB_HOST}..."

upsert_secret() {
  local key="$1"
  local value="$2"
  if [ -z "$value" ]; then
    return
  fi
  # Use psql variables to avoid SQL injection from secret values.
  psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" \
    -v "secret_key=$key" -v "secret_value=$value" -c "
    INSERT INTO internal.secrets (key, value, created_by, updated_at)
    VALUES (:'secret_key', :'secret_value', 'bootstrap', now())
    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
  "
  echo "[pg-seed-secrets]   Seeded: $key"
}

upsert_secret "oauth:google:client_secret" "${OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET:-}"
upsert_secret "oauth:discord:client_secret" "${OCTOCON_DISCORD_OAUTH_CLIENT_SECRET:-}"
upsert_secret "oauth:apple:client_secret" "${OCTOCON_APPLE_OAUTH_CLIENT_SECRET:-}"
upsert_secret "encryption:pepper" "${OCTOCON_ENCRYPTION_PEPPER:-}"

echo "[pg-seed-secrets] Done."
