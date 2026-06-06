#!/bin/bash
set -euo pipefail

# PostgreSQL authentication bootstrap.
#
# This script runs ONCE after the postgres container is healthy to:
#   1. Create the app user (PGUSER) as a non-superuser with CREATEDB
#   2. Create a dedicated admin superuser (<PGUSER>_admin) with a random password
#   3. Create the application database owned by the app user
#   4. Scramble the cluster owner (pg_init) password
#
# The container starts with a disposable init superuser 'pg_init' (cluster owner).
# We can't fully lock or demote the cluster owner in PostgreSQL, so instead we
# randomize its password and save it to recovery.
#
# Required environment variables:
#   PGUSER       - App-level username (will be non-superuser)
#   PGPASSWORD   - App-level password
#
# The container's POSTGRES_PASSWORD (used by pg_init) is passed separately via
# PG_INIT_PASSWORD so we can connect as the cluster owner during bootstrap.
#
# Usage:
#   postgres-bootstrap-auth.sh <db-host> <db-name>

DB_HOST="${1:?Usage: postgres-bootstrap-auth.sh <db-host> <db-name>}"
DB_NAME="${2:?Usage: postgres-bootstrap-auth.sh <db-host> <db-name>}"

INIT_USER="db_init"
INIT_PASSWORD="${PG_INIT_PASSWORD:?PG_INIT_PASSWORD is required}"
APP_USER="${PGUSER:-postgres}"
APP_PASSWORD="${PGPASSWORD:?PGPASSWORD is required}"
ADMIN_USER="${APP_USER}_admin"
RECOVERY_DIR="${RECOVERY_PATH:-/recovery}"

# --- Guard: reject the well-known default password ---
if [ "$APP_PASSWORD" = "postgres" ]; then
  echo "[pg-auth-bootstrap] ========================================"
  echo "[pg-auth-bootstrap] ERROR: PGPASSWORD is set to the well-known default ('postgres')."
  echo "[pg-auth-bootstrap]        This is not allowed — all accounts must use a non-default password."
  echo "[pg-auth-bootstrap]"
  echo "[pg-auth-bootstrap] To fix, set a secure password in user-secrets:"
  echo "[pg-auth-bootstrap]   dotnet user-secrets set 'Parameters:postgres-password' '<your-password>'"
  echo "[pg-auth-bootstrap]   (run from csharp/Interfold.AppHost)"
  echo "[pg-auth-bootstrap] ========================================"
  exit 1
fi

echo "[pg-auth-bootstrap] Waiting for PostgreSQL on ${DB_HOST}:5432..."
TIMEOUT=90
ELAPSED=0
until pg_isready -h "$DB_HOST" -U "$INIT_USER" -d postgres >/dev/null 2>&1; do
  sleep 2
  ELAPSED=$((ELAPSED + 2))
  if [ "$ELAPSED" -ge "$TIMEOUT" ]; then
    echo "[pg-auth-bootstrap] ERROR: Timed out waiting for PostgreSQL (${TIMEOUT}s)"
    exit 1
  fi
done
echo "[pg-auth-bootstrap] PostgreSQL is ready."

# Helper to run SQL as the app user (works after creation)
psql_as_app() {
  PGPASSWORD="$APP_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$APP_USER" -d postgres "$@"
}

# --- Idempotency: check if bootstrap already completed ---
# If the app user can connect AND is non-superuser, bootstrap already ran.
if psql_as_app -tAc "SELECT 1 FROM pg_roles WHERE rolname = '${APP_USER}' AND NOT rolsuper" 2>/dev/null | grep -q 1; then
  echo "[pg-auth-bootstrap] App user '${APP_USER}' exists as non-superuser — bootstrap previously completed."
  echo "[pg-auth-bootstrap] Bootstrap already complete (idempotent re-run)."
  exit 0
fi

# --- First-time bootstrap ---

# Helper to run SQL as the cluster owner (init superuser)
psql_as_init() {
  PGPASSWORD="$INIT_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$INIT_USER" -d postgres "$@"
}

# Verify we can connect as the init superuser
if ! psql_as_init -c "SELECT 1" >/dev/null 2>&1; then
  echo "[pg-auth-bootstrap] ERROR: Cannot authenticate as '${INIT_USER}'."
  echo "[pg-auth-bootstrap]        The cluster owner password may have been scrambled from a prior run."
  echo "[pg-auth-bootstrap]        Try deleting the 'msg_pgdata' volume for a fresh start."
  exit 1
fi

echo "[pg-auth-bootstrap] Connected as '${INIT_USER}' (cluster owner)."

# --- Step 1: Create the app user (non-superuser with CREATEDB) ---
echo "[pg-auth-bootstrap] Creating app user '${APP_USER}' (non-superuser, CREATEDB)..."
psql_as_init -c "
DO \$\$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '${APP_USER}') THEN
    ALTER ROLE \"${APP_USER}\" WITH PASSWORD '${APP_PASSWORD}' NOSUPERUSER LOGIN CREATEDB;
    RAISE NOTICE 'Role ${APP_USER} already exists, updated.';
  ELSE
    CREATE ROLE \"${APP_USER}\" WITH PASSWORD '${APP_PASSWORD}' NOSUPERUSER LOGIN CREATEDB;
  END IF;
END
\$\$;
"

# --- Step 2: Create dedicated admin superuser with random password ---
ADMIN_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)

echo "[pg-auth-bootstrap] Creating admin superuser '${ADMIN_USER}'..."
psql_as_init -c "
DO \$\$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '${ADMIN_USER}') THEN
    ALTER ROLE \"${ADMIN_USER}\" WITH PASSWORD '${ADMIN_PASS}' SUPERUSER LOGIN;
    RAISE NOTICE 'Role ${ADMIN_USER} already exists, rotated password.';
  ELSE
    CREATE ROLE \"${ADMIN_USER}\" WITH PASSWORD '${ADMIN_PASS}' SUPERUSER LOGIN;
  END IF;
END
\$\$;
"

# --- Step 3: Create the application database owned by app user ---
echo "[pg-auth-bootstrap] Ensuring database '${DB_NAME}' exists..."
if ! psql_as_init -tAc "SELECT 1 FROM pg_database WHERE datname = '${DB_NAME}'" | grep -q 1; then
  psql_as_init -c "CREATE DATABASE \"${DB_NAME}\" OWNER \"${APP_USER}\";"
  echo "[pg-auth-bootstrap] Database '${DB_NAME}' created."
else
  echo "[pg-auth-bootstrap] Database '${DB_NAME}' already exists."
  psql_as_init -c "ALTER DATABASE \"${DB_NAME}\" OWNER TO \"${APP_USER}\";" 2>/dev/null || true
fi

# --- Step 4: Scramble the cluster owner password ---
INIT_SCRAMBLED_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)
echo "[pg-auth-bootstrap] Scrambling cluster owner '${INIT_USER}' password..."
psql_as_init -c "ALTER ROLE \"${INIT_USER}\" WITH PASSWORD '${INIT_SCRAMBLED_PASS}';"

# --- Save recovery credentials ---
mkdir -p "$RECOVERY_DIR" 2>/dev/null || true

cat > "${RECOVERY_DIR}/pg-admin-credentials.txt" 2>/dev/null <<EOF || true
# PostgreSQL admin superuser for schema migrations and emergency access.
# This account has SUPERUSER and LOGIN privileges.
username=${ADMIN_USER}
password=${ADMIN_PASS}
host=${DB_HOST}
database=${DB_NAME}
created_at=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Usage:
#   PGPASSWORD='${ADMIN_PASS}' psql -h ${DB_HOST} -U '${ADMIN_USER}' -d ${DB_NAME}
#
# To temporarily grant superuser to app user:
#   ALTER ROLE "${APP_USER}" WITH SUPERUSER;
# Then revoke after:
#   ALTER ROLE "${APP_USER}" WITH NOSUPERUSER;
EOF

cat > "${RECOVERY_DIR}/pg-init-scrambled.txt" 2>/dev/null <<EOF || true
# Cluster owner account (cannot be fully locked in PostgreSQL).
# Password has been scrambled. Use admin account for superuser operations.
username=${INIT_USER}
password=${INIT_SCRAMBLED_PASS}
scrambled_at=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
EOF

chmod 600 "${RECOVERY_DIR}/pg-admin-credentials.txt" "${RECOVERY_DIR}/pg-init-scrambled.txt" 2>/dev/null || true

echo "[pg-auth-bootstrap] ========================================"
echo "[pg-auth-bootstrap] ADMIN ACCOUNT: ${ADMIN_USER}"
echo "[pg-auth-bootstrap] Credentials saved to: ${RECOVERY_DIR}/pg-admin-credentials.txt"
echo "[pg-auth-bootstrap] Cluster owner '${INIT_USER}': password scrambled"
echo "[pg-auth-bootstrap] App user '${APP_USER}': non-superuser, CREATEDB, LOGIN"
echo "[pg-auth-bootstrap] ========================================"
echo "[pg-auth-bootstrap] Bootstrap complete."
