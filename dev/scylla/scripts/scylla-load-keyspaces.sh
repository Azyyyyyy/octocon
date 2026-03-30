#!/bin/bash
set -euo pipefail
# Usage: scylla-load-keyspaces.sh <db-host> dc1 dc2 dc3 ...
DB_HOST="$1"
shift
DCS=("$@")
if [ -z "$DB_HOST" ]; then
  echo "Usage: $0 <db-host> dc1 dc2 ..." >&2
  exit 1
fi
echo Waiting for Scylla CQL endpoint...
for i in $(seq 1 90); do
  if cqlsh --request-timeout=300 "$DB_HOST" -e "DESCRIBE KEYSPACES" >/dev/null 2>&1; then break; fi
  sleep 2
done
echo Waiting for all DCs to join cluster...
for i in $(seq 1 120); do
  peers=$(cqlsh --request-timeout=60 "$DB_HOST" -e "SELECT data_center FROM system.peers" 2>/dev/null | tr '[:upper:]' '[:lower:]')
  missing=""
  for dc in "${DCS[@]}"; do
    echo "$peers" | grep -qw "$dc" || missing="$missing $dc"
  done
  if [ -z "$missing" ]; then
    echo All DCs joined.
    break
  fi
  missing_trimmed=$(echo "$missing" | xargs)
  echo "  Missing DC(s): $missing_trimmed; waiting..."
  sleep 5
done
echo Loading Scylla keyspaces...
cqlsh --request-timeout=300 "$DB_HOST" -f /init.cql
echo Loading canonical Scylla schema...
ok=0
for i in $(seq 1 4); do
  if cqlsh --request-timeout=300 "$DB_HOST" -f /schema.cql; then ok=1; break; fi
  echo "Schema apply attempt $i failed, retrying in 5s..."
  sleep 5
done
if [ "$ok" -ne 1 ]; then echo "Schema apply failed after retries"; exit 1; fi
cqlsh --request-timeout=300 "$DB_HOST" -e "DESCRIBE TABLE nam.fronts" >/dev/null 2>&1
echo Scylla schema load complete.
