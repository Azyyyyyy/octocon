#!/bin/bash
set -e

# Force-enable materialized views by replacing the existing line
sed -i 's/^materialized_views_enabled:.*/materialized_views_enabled: true/' /etc/cassandra/cassandra.yaml

exec /usr/local/bin/docker-entrypoint.sh "$@"