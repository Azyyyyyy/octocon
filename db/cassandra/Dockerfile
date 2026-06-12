FROM cassandra:5

# Bake required cassandra.yaml overrides into the image so we don't need a runtime
# wrapper script to mutate config inside the running container.
#
# - materialized_views_enabled: required by our schema (Cassandra 4.0+ default is false)
# - authenticator/authorizer:  required so the bootstrap container can create the app
#                              user as a non-superuser and lock the default account
RUN set -e \
 && sed -i 's/^materialized_views_enabled:.*/materialized_views_enabled: true/' /etc/cassandra/cassandra.yaml \
 && sed -i 's/^authenticator:.*/authenticator: PasswordAuthenticator/'           /etc/cassandra/cassandra.yaml \
 && sed -i 's/^authorizer:.*/authorizer: CassandraAuthorizer/'                   /etc/cassandra/cassandra.yaml
