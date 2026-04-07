#!/bin/bash
set -euo pipefail

# Scylla keyspace/schema bootstrap loader.
#
# Usage:
#   scylla-load-keyspaces.sh <db-host> [dc1 dc2 dc3 ...]
#
# Examples:
#   scylla-load-keyspaces.sh scylla
#   scylla-load-keyspaces.sh scylla-nam eur sam sas eas ocn
#
# Defaults and template paths:
#   KEYSPACE_TEMPLATE_PATH defaults to /init.cql
#   SCHEMA_TEMPLATE_PATH   defaults to /schema.cql
#
# Compose mount expectations (current templates):
#   /init.cql   -> csharp/db/scylla/001_create_octocon_keyspaces.cql
#   /schema.cql -> csharp/db/scylla/001_create_octocon_schema.templated.cql
#
# Behavior:
# - If no DCs are provided, keyspaces are created with SimpleStrategy (single-node dev).
# - If DCs are provided, replication uses NetworkTopologyStrategy.
# - If schema template contains {{KEYSPACE}}, the schema is rendered/applied per regional keyspace.
# - Otherwise, schema is applied once as-is.

# Usage: scylla-load-keyspaces.sh <db-host> dc1 dc2 dc3 ...
DB_HOST="$1"
shift
DCS=("$@")
REGIONAL_KEYSPACES=(nam eur sam sas eas ocn gdpr)
KEYSPACE_TEMPLATE_PATH="${KEYSPACE_TEMPLATE_PATH:-/init.cql}"
SCHEMA_TEMPLATE_PATH="${SCHEMA_TEMPLATE_PATH:-/schema.cql}"

has_dc() {
  local target="$1"
  for dc in "${DCS[@]}"; do
    if [ "$dc" = "$target" ]; then
      return 0
    fi
  done

  return 1
}

get_visible_dcs() {
  {
    cqlsh --request-timeout=60 "$DB_HOST" -e "SELECT data_center FROM system.local" 2>/dev/null || true
    cqlsh --request-timeout=60 "$DB_HOST" -e "SELECT data_center FROM system.peers" 2>/dev/null || true
  } \
    | tr '[:upper:]' '[:lower:]' \
    | grep -Eo '[a-z][a-z0-9_]+' \
    | grep -E "^(nam|eur|sam|sas|eas|ocn|gdpr)$" \
    | sort -u
}

make_simple_replication() {
  echo "{'class':'SimpleStrategy', 'replication_factor':1}"
}

make_nts_replication() {
  local out="{'class':'org.apache.cassandra.locator.NetworkTopologyStrategy'"
  for dc in "$@"; do
    out+=" , '$dc':'1'"
  done
  out+="}"
  echo "$out"
}

regional_replication_for() {
  local keyspace="$1"

  if [ "${#DCS[@]}" -eq 0 ]; then
    make_simple_replication
    return
  fi

  # For multi-DC devpelopment, each regional keysace replicates to multiple DCs for resilience.
  # The primary DC plus 2 additional DCs ensures data survives temporary node outages.
  
  case "$keyspace" in
    nam)
      make_nts_replication nam eur sam
      ;;
    eur)
      if has_dc eur; then make_nts_replication nam eur sas; else make_nts_replication nam sam sas; fi
      ;;
    sam)
      make_nts_replication nam sam eas
      ;;
    sas)
      if has_dc sas; then make_nts_replication nam sas ocn; else make_nts_replication nam sas gdpr; fi
      ;;
    eas)
      if has_dc eas; then make_nts_replication nam eas ocn; else make_nts_replication nam eas gdpr; fi
      ;;
    ocn)
      if has_dc ocn; then make_nts_replication nam ocn gdpr; else make_nts_replication nam ocn sas; fi
      ;;
    gdpr)
      if has_dc gdpr; then make_nts_replication nam gdpr eur; else make_nts_replication nam eur ocn; fi
      ;;
    *)
      make_nts_replication nam eur sam
      ;;
  esac
}

global_replication() {
  if [ "${#DCS[@]}" -eq 0 ]; then
    make_simple_replication
    return
  fi

  # For multi-DC development, global keyspace replicates to all available DCs for maximum resilience.
  local dcs=(nam)
  if has_dc eur; then dcs+=(eur); fi
  if has_dc sam; then dcs+=(sam); fi
  if has_dc sas; then dcs+=(sas); fi
  if has_dc eas; then dcs+=(eas); fi
  if has_dc ocn; then dcs+=(ocn); fi
  if has_dc gdpr; then dcs+=(gdpr); fi
  make_nts_replication "${dcs[@]}"
}

nam_nt_replication() {
  if [ "${#DCS[@]}" -eq 0 ]; then
    make_simple_replication
  else
    make_nts_replication nam
  fi
}

render_keyspace_template() {
  local keyspace="$1"
  local regional_replication="$2"
  local global_replication_value="$3"
  local nam_nt_replication_value="$4"
  local output="$5"

  awk \
    -v keyspace="$keyspace" \
    -v keyspace_replication="$regional_replication" \
    -v global_replication="$global_replication_value" \
    -v nam_nt_replication="$nam_nt_replication_value" \
    '{
      gsub(/\{\{KEYSPACE\}\}/, keyspace);
      gsub(/\{\{KEYSPACE_REPLICATION\}\}/, keyspace_replication);
      gsub(/\{\{GLOBAL_REPLICATION\}\}/, global_replication);
      gsub(/\{\{NAM_NT_REPLICATION\}\}/, nam_nt_replication);
      print;
    }' "$KEYSPACE_TEMPLATE_PATH" > "$output"
}

render_schema_template() {
  local keyspace="$1"
  local output="$2"

  awk -v keyspace="$keyspace" '{ gsub(/\{\{KEYSPACE\}\}/, keyspace); print; }' "$SCHEMA_TEMPLATE_PATH" > "$output"
}

apply_with_retries() {
  local cql_file="$1"
  local label="$2"
  local success=0

  for i in $(seq 1 4); do
    if cqlsh --request-timeout=300 "$DB_HOST" -f "$cql_file"; then
      success=1
      break
    fi

    echo "$label apply attempt $i failed, retrying in 5s..."
    sleep 5
  done

  if [ "$success" -ne 1 ]; then
    echo "$label apply failed after retries"
    exit 1
  fi
}

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
  visible_dcs="$(get_visible_dcs)"
  missing=""
  for dc in "${DCS[@]}"; do
    echo "$visible_dcs" | grep -qw "$dc" || missing="$missing $dc"
  done
  if [ -z "$missing" ]; then
    echo All DCs joined.
    break
  fi
  missing_trimmed=$(echo "$missing" | xargs)
  seen_trimmed=$(echo "$visible_dcs" | xargs)
  echo "  Visible DC(s): ${seen_trimmed:-none}; missing DC(s): $missing_trimmed; waiting..."
  sleep 5
done
echo Loading Scylla keyspaces from template...
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

global_repl="$(global_replication)"
nam_nt_repl="$(nam_nt_replication)"

for keyspace in "${REGIONAL_KEYSPACES[@]}"; do
  regional_repl="$(regional_replication_for "$keyspace")"
  rendered_keyspace_cql="$tmp_dir/keyspaces-$keyspace.cql"
  render_keyspace_template "$keyspace" "$regional_repl" "$global_repl" "$nam_nt_repl" "$rendered_keyspace_cql"
  apply_with_retries "$rendered_keyspace_cql" "Keyspace ($keyspace)"
done

echo Loading canonical Scylla schema...
if grep -q "{{KEYSPACE}}" "$SCHEMA_TEMPLATE_PATH"; then
  for keyspace in "${REGIONAL_KEYSPACES[@]}"; do
    rendered_schema_cql="$tmp_dir/schema-$keyspace.cql"
    render_schema_template "$keyspace" "$rendered_schema_cql"
    apply_with_retries "$rendered_schema_cql" "Schema ($keyspace)"
  done
else
  apply_with_retries "$SCHEMA_TEMPLATE_PATH" "Schema"
fi

cqlsh --request-timeout=300 "$DB_HOST" -e "DESCRIBE TABLE nam.fronts" >/dev/null 2>&1
echo Scylla schema load complete.
