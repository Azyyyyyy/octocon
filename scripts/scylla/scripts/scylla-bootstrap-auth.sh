#!/bin/bash
set -euo pipefail

# ScyllaDB/Cassandra authentication bootstrap.
#
# This script runs ONCE to:
#   1. Create the app user (SCYLLA_USER) as a temporary superuser
#   2. Create a dedicated admin account (<SCYLLA_USER>_admin) with a random password
#   3. Lock the default 'cassandra' account
#   4. Demote SCYLLA_USER to non-superuser (app runs with least privilege)
#
# The admin account credentials are saved to /recovery/ for schema migrations.
#
# Required environment variables:
#   SCYLLA_USER      - App-level username (will be demoted to non-superuser after setup)
#   SCYLLA_PASSWORD  - App-level password
#
# Usage:
#   scylla-bootstrap-auth.sh <db-host>

DB_HOST="${1:?Usage: scylla-bootstrap-auth.sh <db-host>}"

# Default credentials for the built-in account
DEFAULT_USER="cassandra"
DEFAULT_PASSWORD="cassandra"
ADMIN_USER="${SCYLLA_USER}_admin"
RECOVERY_DIR="${RECOVERY_PATH:-/recovery}"

# --- Guard: reject the well-known default password ---
if [ "$SCYLLA_PASSWORD" = "$DEFAULT_PASSWORD" ]; then
  echo "[auth-bootstrap] ========================================"
  echo "[auth-bootstrap] ERROR: SCYLLA_PASSWORD is set to the well-known default ('${DEFAULT_PASSWORD}')."
  echo "[auth-bootstrap]        This is not allowed — all accounts must use a non-default password."
  echo "[auth-bootstrap]"
  echo "[auth-bootstrap] To fix, set a secure password in user-secrets:"
  echo "[auth-bootstrap]   dotnet user-secrets set 'Parameters:scylla-password' '<your-password>'"
  echo "[auth-bootstrap]   (run from csharp/Interfold.AppHost)"
  echo "[auth-bootstrap] ========================================"
  exit 1
fi

echo "[auth-bootstrap] Waiting for CQL on ${DB_HOST}:9042..."
TIMEOUT=90
ELAPSED=0
until cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" -e "DESCRIBE CLUSTER" >/dev/null 2>&1; do
  sleep 2
  ELAPSED=$((ELAPSED + 2))
  if [ "$ELAPSED" -ge "$TIMEOUT" ]; then
    echo "[auth-bootstrap] ERROR: Timed out waiting for CQL (${TIMEOUT}s)"
    exit 1
  fi
done
echo "[auth-bootstrap] CQL is ready."

# --- Step 1: Create the app user (temporarily as superuser for remaining setup) ---
EXISTING=$(cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" \
  -e "LIST ROLES OF '${SCYLLA_USER}';" 2>/dev/null || true)

if echo "$EXISTING" | grep -q "$SCYLLA_USER"; then
  echo "[auth-bootstrap] App user '${SCYLLA_USER}' already exists."
  # Always update the password to SCYLLA_PASSWORD (invalidates the well-known default)
  if [ "$SCYLLA_USER" = "$DEFAULT_USER" ]; then
    echo "[auth-bootstrap] Updating '${SCYLLA_USER}' password to configured value..."
    cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" -e \
      "ALTER ROLE '${SCYLLA_USER}' WITH PASSWORD = '${SCYLLA_PASSWORD}';"
  fi
else
  echo "[auth-bootstrap] Creating app user '${SCYLLA_USER}' (temporary superuser)..."
  cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" -e \
    "CREATE ROLE IF NOT EXISTS '${SCYLLA_USER}' WITH PASSWORD = '${SCYLLA_PASSWORD}' AND SUPERUSER = true AND LOGIN = true;"
fi

# Verify the app user can connect
echo "[auth-bootstrap] Verifying '${SCYLLA_USER}' login..."
if ! cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" -e "DESCRIBE CLUSTER" >/dev/null 2>&1; then
  echo "[auth-bootstrap] ERROR: Cannot authenticate as '${SCYLLA_USER}'. Aborting."
  exit 1
fi

# --- Step 2: Create dedicated admin superuser with random password ---
ADMIN_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)

ADMIN_EXISTING=$(cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" \
  -e "LIST ROLES OF '${ADMIN_USER}';" 2>/dev/null || true)

if echo "$ADMIN_EXISTING" | grep -q "$ADMIN_USER"; then
  echo "[auth-bootstrap] Admin user '${ADMIN_USER}' already exists. Rotating password..."
  cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" -e \
    "ALTER ROLE '${ADMIN_USER}' WITH PASSWORD = '${ADMIN_PASS}';"
else
  echo "[auth-bootstrap] Creating admin superuser '${ADMIN_USER}'..."
  cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" -e \
    "CREATE ROLE '${ADMIN_USER}' WITH PASSWORD = '${ADMIN_PASS}' AND SUPERUSER = true AND LOGIN = true;"
fi

# --- Step 3: Lock the default 'cassandra' account (unless SCYLLA_USER IS the default) ---
if [ "$SCYLLA_USER" = "$DEFAULT_USER" ]; then
  echo "[auth-bootstrap] SCYLLA_USER ('${SCYLLA_USER}') is the default account — skipping lock."
  echo "[auth-bootstrap] The app will continue using the default account with the configured password."
  CASSANDRA_RANDOM_PASS="(not locked - app user is default account)"
else
  CASSANDRA_RANDOM_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)

  echo "[auth-bootstrap] Locking default '${DEFAULT_USER}' account..."
  cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" -e \
    "ALTER ROLE '${DEFAULT_USER}' WITH PASSWORD = '${CASSANDRA_RANDOM_PASS}' AND LOGIN = false;"
fi

# --- Step 4: Grant SCYLLA_USER permissions needed for keyspace/schema creation ---
# The demotion to non-superuser happens AFTER schema init (scylla-finalize-auth.sh).
# For now, SCYLLA_USER remains a superuser so the keyspace loader can run.
echo "[auth-bootstrap] NOTE: '${SCYLLA_USER}' remains superuser until schema init completes."
echo "[auth-bootstrap]       scylla-finalize-auth.sh will demote it after keyspace loading."

# --- Save recovery credentials ---
mkdir -p "$RECOVERY_DIR" 2>/dev/null || true

cat > "${RECOVERY_DIR}/admin-credentials.txt" 2>/dev/null <<EOF || true
# Admin superuser for schema migrations and emergency access.
# This account has SUPERUSER = true and LOGIN = true.
username=${ADMIN_USER}
password=${ADMIN_PASS}
created_at=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Usage:
#   cqlsh ${DB_HOST} -u '${ADMIN_USER}' -p '${ADMIN_PASS}'
#
# To re-promote the app user temporarily:
#   ALTER ROLE '${SCYLLA_USER}' WITH SUPERUSER = true;
# Then demote again after:
#   ALTER ROLE '${SCYLLA_USER}' WITH SUPERUSER = false;
EOF

cat > "${RECOVERY_DIR}/cassandra-locked.txt" 2>/dev/null <<EOF || true
# The default 'cassandra' account has been locked (LOGIN = false).
# To re-enable (requires admin):
#   cqlsh -u '${ADMIN_USER}' -e "ALTER ROLE '${DEFAULT_USER}' WITH LOGIN = true;"
username=${DEFAULT_USER}
password=${CASSANDRA_RANDOM_PASS}
locked_at=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
EOF

chmod 600 "${RECOVERY_DIR}/admin-credentials.txt" "${RECOVERY_DIR}/cassandra-locked.txt" 2>/dev/null || true

echo "[auth-bootstrap] ========================================"
echo "[auth-bootstrap] ADMIN ACCOUNT: ${ADMIN_USER}"
echo "[auth-bootstrap] Credentials saved to: ${RECOVERY_DIR}/admin-credentials.txt"
echo "[auth-bootstrap] ========================================"
if [ "$SCYLLA_USER" = "$DEFAULT_USER" ]; then
  echo "[auth-bootstrap] Default '${DEFAULT_USER}' account: ACTIVE (is app user)"
else
  echo "[auth-bootstrap] Default '${DEFAULT_USER}' account: LOCKED"
fi
echo "[auth-bootstrap] Credentials saved to: ${RECOVERY_DIR}/cassandra-locked.txt"
echo "[auth-bootstrap] App user '${SCYLLA_USER}': superuser (will be demoted after schema init)"
echo "[auth-bootstrap] ========================================"
echo "[auth-bootstrap] Bootstrap complete."
echo "[auth-bootstrap]   Schema changes:  ${ADMIN_USER} (superuser, see recovery folder)"
echo "[auth-bootstrap]   Next step: run scylla-finalize-auth.sh after keyspace loading to demote '${SCYLLA_USER}'"
