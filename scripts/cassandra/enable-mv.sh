#!/bin/bash
set -e

# Promote config files from the read-only overlay layer to the writable upper layer.
# Without this, the official docker-entrypoint.sh fails with "Read-only file system"
# when it tries to chown config files on certain container runtimes (e.g. GitHub Runners).
for f in /etc/cassandra/*; do
    [ -f "$f" ] && cp "$f" "$f.tmp" && mv "$f.tmp" "$f"
done

# Force-enable materialized views and authentication/authorization
sed -i 's/^materialized_views_enabled:.*/materialized_views_enabled: true/' /etc/cassandra/cassandra.yaml
sed -i 's/^authenticator:.*/authenticator: PasswordAuthenticator/' /etc/cassandra/cassandra.yaml
sed -i 's/^authorizer:.*/authorizer: CassandraAuthorizer/' /etc/cassandra/cassandra.yaml

exec /usr/local/bin/docker-entrypoint.sh "$@"