#!/bin/bash
set -euo pipefail

# ScyllaDB/Cassandra post-schema auth finalization.
#
# This script runs AFTER keyspace/schema loading to demote the app user
# from superuser to non-superuser. This ensures schema init succeeds
# (requires superuser) while the app runs with least privilege at runtime.
#
# Uses the admin account (created by bootstrap) to perform the demotion,
# since Scylla does not allow a role to demote itself.
#
# Required environment variables:
#   SCYLLA_USER      - App user to demote
#   SCYLLA_PASSWORD  - App user password
#
# Usage:
#   scylla-finalize-auth.sh <db-host>

DB_HOST="${1:?Usage: scylla-finalize-auth.sh <db-host>}"
RECOVERY_DIR="${RECOVERY_PATH:-/recovery}"
ADMIN_USER="${SCYLLA_USER}_admin"

# --- Load admin credentials from recovery volume ---
ADMIN_CREDS_FILE="${RECOVERY_DIR}/admin-credentials.txt"
if [ ! -f "$ADMIN_CREDS_FILE" ]; then
  echo "[auth-finalize] ERROR: Admin credentials file not found at ${ADMIN_CREDS_FILE}."
  echo "[auth-finalize]        Did scylla-bootstrap-auth run successfully?"
  exit 1
fi

ADMIN_PASS=$(grep -E "^password=" "$ADMIN_CREDS_FILE" | head -1 | cut -d= -f2-)
if [ -z "${ADMIN_PASS:-}" ]; then
  echo "[auth-finalize] ERROR: Could not read admin password from ${ADMIN_CREDS_FILE}."
  exit 1
fi

echo "[auth-finalize] Connecting to ${DB_HOST} as '${ADMIN_USER}'..."
if ! cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e "DESCRIBE CLUSTER" >/dev/null 2>&1; then
  echo "[auth-finalize] ERROR: Cannot authenticate as '${ADMIN_USER}'. Aborting."
  exit 1
fi

# Check current superuser status
IS_SUPER=$(cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" \
  -e "SELECT is_superuser FROM system_auth.roles WHERE role = '${SCYLLA_USER}';" 2>/dev/null || true)

if echo "$IS_SUPER" | grep -q "False"; then
  echo "[auth-finalize] '${SCYLLA_USER}' is already non-superuser."
  # Still ensure permissions are granted (idempotent — GRANT is safe to repeat)
  echo "[auth-finalize] Ensuring DML permissions are granted..."
  KEYSPACES=$(cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" \
    -e "DESCRIBE KEYSPACES;" 2>/dev/null \
    | tr -s ' ' '\n' \
    | grep -v "^$" \
    | grep -vE "^(system|system_auth|system_distributed|system_traces|system_schema|system_distributed_everywhere)$")
  for ks in $KEYSPACES; do
    cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
      "GRANT SELECT ON KEYSPACE \"${ks}\" TO '${SCYLLA_USER}';" 2>/dev/null || true
    cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
      "GRANT MODIFY ON KEYSPACE \"${ks}\" TO '${SCYLLA_USER}';" 2>/dev/null || true
  done
  echo "[auth-finalize] Permissions verified. Nothing else to do."
  exit 0
fi

echo "[auth-finalize] Demoting '${SCYLLA_USER}' to non-superuser..."
cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
  "ALTER ROLE '${SCYLLA_USER}' WITH SUPERUSER = false;"

# --- Revoke DDL grants (were needed for keyspace creation, no longer needed) ---
echo "[auth-finalize] Revoking DDL permissions from '${SCYLLA_USER}'..."
cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
  "REVOKE CREATE ON ALL KEYSPACES FROM '${SCYLLA_USER}';" 2>/dev/null || true
cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
  "REVOKE ALTER ON ALL KEYSPACES FROM '${SCYLLA_USER}';" 2>/dev/null || true
cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
  "REVOKE DROP ON ALL KEYSPACES FROM '${SCYLLA_USER}';" 2>/dev/null || true

# --- Grant least-privilege access to app user on all keyspaces ---
# After demotion, the app user needs explicit permissions (CassandraAuthorizer is enabled).
echo "[auth-finalize] Granting DML permissions to '${SCYLLA_USER}'..."

# Regional keyspaces + global
KEYSPACES=$(cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" \
  -e "DESCRIBE KEYSPACES;" 2>/dev/null \
  | tr -s ' ' '\n' \
  | grep -v "^$" \
  | grep -vE "^(system|system_auth|system_distributed|system_traces|system_schema|system_distributed_everywhere)$")

for ks in $KEYSPACES; do
  echo "[auth-finalize]   GRANT SELECT, MODIFY ON KEYSPACE ${ks} TO '${SCYLLA_USER}'"
  cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
    "GRANT SELECT ON KEYSPACE \"${ks}\" TO '${SCYLLA_USER}';"
  cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
    "GRANT MODIFY ON KEYSPACE \"${ks}\" TO '${SCYLLA_USER}';"
done

# Update recovery notes
cat >> "${ADMIN_CREDS_FILE}" 2>/dev/null <<EOF || true

# App user '${SCYLLA_USER}' demoted at $(date -u +"%Y-%m-%dT%H:%M:%SZ")
EOF

echo "[auth-finalize] '${SCYLLA_USER}' demoted to non-superuser."
echo "[auth-finalize] App will run with least privilege."
echo "[auth-finalize] For schema changes, use the admin account in ${ADMIN_CREDS_FILE}"