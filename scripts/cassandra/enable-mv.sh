#!/bin/bash
set -e

# Force-enable materialized views and authentication/authorization
sed -i 's/^materialized_views_enabled:.*/materialized_views_enabled: true/' /etc/cassandra/cassandra.yaml
sed -i 's/^authenticator:.*/authenticator: PasswordAuthenticator/' /etc/cassandra/cassandra.yaml
sed -i 's/^authorizer:.*/authorizer: CassandraAuthorizer/' /etc/cassandra/cassandra.yaml

exec /usr/local/bin/docker-entrypoint.sh "$@"