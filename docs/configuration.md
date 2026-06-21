# Interfold configuration reference

This is the developer / architecture reference for how Interfold gets configured. If you are
self-hosting and just want the short list of env vars you must set by hand, jump to the
[README configuration block](../README.md#configuration--integrations) — it links back here
for the deep dive.

> **Audience.** Operators reading this should already understand Docker Compose, Postgres
> roles, and Kestrel HTTPS. Maintainers extending it should already know
> `IOptionsMonitor<T>`, `IHostedLifecycleService`, and the Aspire AppHost model.

## TL;DR

There are four configuration layers. Each row in a layer eventually lands on a strongly-typed
options object inside the API process or on a seeded row inside Postgres.


| Layer                        | Source of truth                             | Where it lives at rest                              | Read by                            |
| ---------------------------- | ------------------------------------------- | --------------------------------------------------- | ---------------------------------- |
| **1. Compile-time defaults** | C# constants and property initialisers      | Source code (`Interfold.Contracts.Configuration.*`) | DI binding helpers                 |
| **2. Operator file**         | `deploy/interfold.bootstrap.json`           | Operator's working copy                             | `Interfold.Bootstrapper` only      |
| **3. Environment variables** | `deploy/.env` (compose-bound) + process env | `OCTOCON_*`, `ASPNETCORE_*`, `Parameters:*`         | API at DI bind time                |
| **4. `internal.secrets`**    | Postgres row `internal.secrets` table       | Inside the application database (default `interfold`, configurable via `BootstrapConfig.postgresDatabase`) | API at startup via `ISecretsStore` |


Layers cascade right-to-left at boot: env binds first, then `SecretsBootstrapService`
overlays values from `internal.secrets` on top of `AuthenticationConfiguration`. The leaf
PFX password takes a separate one-shot Postgres lookup *before* the host is built so Kestrel
can unlock the cert when it binds the HTTPS endpoint.

## Layer 1 — Compile-time defaults

Every option lives on a `sealed class` in `Interfold.Contracts.Configuration`. Each property
has a default chosen for safety, not convenience — if an operator forgets to set anything,
the defaults aim for "loud failure in production, useful behaviour in dev".

Reference:

- `[PersistenceConfiguration](../csharp/Interfold.Contracts/Configuration/PersistenceConfiguration.cs)`
— DB mode, keyspace, retry/backoff knobs.
- `[AuthenticationConfiguration](../csharp/Interfold.Contracts/Configuration/AuthenticationConfiguration.cs)`
— JWT signing keys, OAuth client IDs, deep-link HMAC secret, encryption pepper, challenge
scheme metadata.
- `[ApiConfiguration](../csharp/Interfold.Contracts/Configuration/ApiConfiguration.cs)` —
frontend URLs, deep-link protocol.
- `[StorageConfiguration](../csharp/Interfold.Contracts/Configuration/StorageConfiguration.cs)`
— local avatar storage root + public base URL.
- `[SocketConfiguration](../csharp/Interfold.Contracts/Configuration/SocketConfiguration.cs)`
— WebSocket batch flush threshold.
- `[ObservabilityConfiguration](../csharp/Interfold.Contracts/Configuration/ObservabilityConfiguration.cs)`
— OTLP endpoint.
- `[ClusterConfiguration](../csharp/Interfold.Contracts/Configuration/ClusterConfiguration.cs)`
— node group (Primary / Auxiliary / Sidecar).
- `[TestingConfiguration](../csharp/Interfold.Contracts/Configuration/TestingConfiguration.cs)`
— test-only gating switches.

DI binding is centralised in
`[ConfigurationServiceCollectionExtensions.AddInterfoldOptions](../csharp/Interfold.Infrastructure/DependencyInjection/ConfigurationServiceCollectionExtensions.cs)`.
Each `Apply`* method is the single source of truth for that section's env → options mapping.

## Layer 2 — `interfold.bootstrap.json`

This file is the operator-facing input to the bootstrapper. It is *not* read by the API
directly — its values flow through the bootstrapper into either `secrets.json` (auto-generated
output) or the `internal.secrets` table (seeded by `DatabaseInitPhase`).

Shape lives on `[BootstrapConfig](../csharp/Interfold.Bootstrapper/Configuration/BootstrapConfig.cs)`:

```jsonc
{
  "deployment": {
    "outputDir": "./deploy",         // artifact root (relative to cwd)
    "domains":   ["api.example.com"],// SANs on the leaf cert
    "rootCaName":"Interfold Root CA",
    "certYears": 5,
    "trustStoreInstall": true        // add rootCA.crt to system trust store
  },
  "ports": {
    "apiHttp":  5000,
    "apiHttps": 5001,
    "webHttp":  8080,
    "webHttps": 8081
  },
  "scyllaMode": "single",            // "single" | "multi" | "cassandra"
  "apiImage":   "ghcr.io/interfold/api:latest",
  "postgresDatabase": "interfold",   // Postgres application DB name; any safe identifier
                                     // matching ^[A-Za-z_][A-Za-z0-9_]{0,62}$. Becomes the
                                     // Database= field on OCTOCON_POSTGRES_CONNECTION and
                                     // the target of DatabaseInitPhase's CREATE DATABASE.
  "clusterName": "InterfoldCluster", // Advertised CQL cluster identity. Lands on
                                     // CASSANDRA_CLUSTER_NAME (Cassandra) and the
                                     // --cluster-name CLI flag (Scylla). Pure metadata,
                                     // visible via `SELECT cluster_name FROM system.local`.
                                     // Allowed: 1..64 chars matching [A-Za-z0-9 ._-].
  "oauth": {
    "googleClientSecret":  "...",    // empty -> row skipped
    "discordClientSecret": "",       // empty -> row skipped
    "appleClientSecret":   ""        // empty -> row skipped
  }
}
```

OAuth **client IDs** are deliberately not part of this file. They are public values that
appear in OAuth redirect URLs, so the operator sets them via `OCTOCON_*_OAUTH_CLIENT_ID`
env vars at API runtime. The matching client *secrets* live in `internal.secrets` and the
bootstrapper seeds them from the `oauth` section above.

## Layer 3 — Environment variables

Two flavours:

- **Compose-bound** — `deploy/.env` is templated by `PublishPhase.BuildEnvReplacements` from
the AppHost's `Parameters:`* declarations. Operators usually never touch `.env` by hand;
rerunning the bootstrapper rewrites it.
- **Operator-bound** — variables the bootstrapper does *not* manage. Operators export them
via systemd, a sibling `.env.local`, or whatever orchestration they use. See the
README's [Configuration & integrations](../README.md#configuration--integrations) for the
short operator-facing list.

### Full env inventory

Variables marked **(bootstrapper-managed)** are written into `deploy/.env` by the
publish phase. Variables marked **(operator)** must be supplied by hand.

#### Persistence


| Env var                             | Default                                           | Notes                                                                                                  |
| ----------------------------------- | ------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| `OCTOCON_PERSISTENCE`               | `scylla-postgres`                                 | `scylla-postgres` or `inmemory`. (bootstrapper-managed)                                                |
| `OCTOCON_POSTGRES_CONNECTION`       | localhost fallback                                | Built from `Parameters:postgres-*` (including `Parameters:postgres-db`, default `interfold`, sourced from `BootstrapConfig.postgresDatabase`). (bootstrapper-managed) |
| `OCTOCON_SCYLLA_KEYSPACE`           | `nam`                                             | Per-instance region identity. **Single source.** No store fallback. (operator)                         |
| `OCTOCON_SINGLE_SCYLLA_INSTANCE`    | `true` (`single`/`cassandra`) / `false` (`multi`) | Whether the migration service creates all regional keyspaces or just one. (bootstrapper-managed)       |
| `OCTOCON_DB_RETRY_ATTEMPTS`         | `3`                                               | (operator)                                                                                             |
| `OCTOCON_DB_RETRY_INITIAL_DELAY_MS` | `100`                                             | (operator)                                                                                             |
| `OCTOCON_DB_RETRY_MAX_DELAY_MS`     | `1500`                                            | (operator)                                                                                             |
| `OCTOCON_HYDRATION_MAX_CONCURRENCY` | `8`                                               | (operator)                                                                                             |

`OCTOCON_COMPATIBILITY_MODE` was a "Postgres isn't reachable" escape hatch that forced
idempotency + token revocation into in-memory stores. It's been removed — Postgres is now
a hard dependency (`SecretsBootstrapService` requires `ISecretsStore` to load the
encryption pepper, and the API refuses to boot without it). If the value is still in your
`.env` it's a dead row — the API no longer reads it.


#### Authentication / OAuth


| Env var                               | Default         | Notes                                                                                                                                                 |
| ------------------------------------- | --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OCTOCON_AUTH_CALLBACK_BASE_URL`      | *null*          | (operator)                                                                                                                                            |
| `OCTOCON_JWT_AUTHORITY`               | `octocon-local` | (operator)                                                                                                                                            |
| `OCTOCON_GOOGLE_OAUTH_CLIENT_ID`      | *null*          | (operator)                                                                                                                                            |
| `OCTOCON_DISCORD_OAUTH_CLIENT_ID`     | *null*          | (operator)                                                                                                                                            |
| `OCTOCON_APPLE_OAUTH_CLIENT_ID`       | *null*          | (operator)                                                                                                                                            |
| `OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET`  | *null*          | Placeholder only; overwritten at startup from `internal.secrets:oauth:google:client_secret`. **Do not rely on the env value.** (bootstrapper-managed) |
| `OCTOCON_DISCORD_OAUTH_CLIENT_SECRET` | *null*          | Same handling. (bootstrapper-managed)                                                                                                                 |
| `OCTOCON_APPLE_OAUTH_CLIENT_SECRET`   | *null*          | Same handling. (bootstrapper-managed)                                                                                                                 |


Each provider's authorize URL, ASP.NET Core challenge scheme name, and static challenge
query parameters (scopes / `response_type` / `response_mode`) are baked into
`OAuthChallengeServiceCollectionExtensions` as constants — every provider serves a single
global URL and the scopes are functionally tied to the data the API's callback handlers
extract, so neither can move without a code change. The scheme is only registered when
the matching `OCTOCON_*_OAUTH_CLIENT_ID` is set; leaving the client ID empty disables the
provider (`/auth/<provider>` falls through to 403). The client ID itself is injected
into the redirect URL by the scheme registration, so operators never have to thread it
through any other env var.

Notably **absent** (intentionally — these moved to `internal.secrets`):

- `OCTOCON_AUTH_RSA_`* / `OCTOCON_AUTH_RSA_*_FILE`
- `OCTOCON_AUTH_EC_*` / `OCTOCON_AUTH_EC_*_FILE`
- `OCTOCON_AUTH_DEEP_LINK_SECRET`
- `OCTOCON_ENCRYPTION_PEPPER` — now strictly store-resident under `encryption:pepper`; `SecretsBootstrapService` refuses to start the API if the row is missing.
- `ASPNETCORE_Kestrel__Certificates__Default__Password`

Removed in favour of in-code constants:

- `OCTOCON_AUTH_CHALLENGE_{GOOGLE,DISCORD,APPLE}_SCHEME` (scheme name is a fixed ASP.NET Core registration key)
- `OCTOCON_AUTH_CHALLENGE_{GOOGLE,DISCORD,APPLE}_ENDPOINT` (provider authorize URLs are stable, single-region values)
- `OCTOCON_AUTH_CHALLENGE_{GOOGLE,DISCORD,APPLE}_PARAMS` (scopes + `response_type` + `response_mode` are tied to what the callback handlers consume — changing them needs a code change)

If any of these are still set in your `.env`, they are dead values — the API no longer
reads them.

#### CORS

| Env var                        | Default                | Notes                                                                                                                                                                                              |
| ------------------------------ | ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OCTOCON_CORS_ALLOWED_ORIGINS` | *null* (= allow any) | Comma-separated allow-list of origin URLs. Empty or unset falls back to "allow any origin", which is acceptable for solo-API dev but should always be set explicitly in production. (operator) |

`OCTOCON_FRONTEND`, `OCTOCON_BETA_FRONTEND`, and `OCTOCON_DEEPLINK_ADDRESS` have all been
removed. Their CORS allow-list use moved to `OCTOCON_CORS_ALLOWED_ORIGINS`; their
post-OAuth redirect-base use went away with the new client contract: clients are now
responsible for passing `redirect_uri` on the initial `GET /auth/{provider}` (or
`GET /auth/link/{provider}`), and the API surfaces a `400 missing_redirect_uri` instead
of falling back to a server-configured default. If any of the three are still set in
your `.env`, they are dead values — the API no longer reads them.


#### Storage


| Env var                       | Default | Notes      |
| ----------------------------- | ------- | ---------- |
| `OCTOCON_AVATAR_STORAGE_ROOT` | *null*  | (operator) |
| `OCTOCON_AVATAR_PUBLIC_BASE`  | *null*  | (operator) |


#### Observability


| Env var                 | Default | Notes                                                        |
| ----------------------- | ------- | ------------------------------------------------------------ |
| `OCTOCON_OTLP_ENDPOINT` | *null*  | gRPC OTLP endpoint, e.g. `http://localhost:4317`. (operator) |


#### Cluster


| Env var              | Default     | Notes                                             |
| -------------------- | ----------- | ------------------------------------------------- |
| `FLY_PROCESS_GROUP`  | *null*      | Fly.io automatic. Wins over `OCTOCON_NODE_GROUP`. |
| `OCTOCON_NODE_GROUP` | `auxiliary` | (operator)                                        |


#### Socket


| Env var                                | Default                       | Notes      |
| -------------------------------------- | ----------------------------- | ---------- |
| `OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD` | *null* (use built-in default) | (operator) |


#### Kestrel (self-host)


| Env var                                           | Default           | Notes                                                                                                              |
| ------------------------------------------------- | ----------------- | ------------------------------------------------------------------------------------------------------------------ |
| `ASPNETCORE_HTTP_PORTS`                           | `5100`            | Set by AppHost from `Ports:api-container-http`. (bootstrapper-managed)                                              |
| `ASPNETCORE_HTTPS_PORTS`                          | `5101`            | Set by AppHost from `Ports:api-container-https`. (bootstrapper-managed)                                             |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | `/certs/leaf.pfx` | Set by AppHost. (bootstrapper-managed)                                                                             |
| *password is **not** an env var*                  | —                 | Fetched from `internal.secrets:certs:leaf_pfx_password` before Kestrel binds. See [boot ordering](#boot-ordering). |

The API image itself (`/Dockerfile`) does not pin its listening ports — AppHost owns them
end-to-end via the env vars above, plus the matching compose `targetPort` and the
`curl http://localhost:<port>/health/ready` healthcheck. Operators who want to run the
container standalone (outside AppHost) need to set `ASPNETCORE_URLS` themselves; otherwise
ASP.NET Core falls through to its built-in default of `http://+:8080`.


#### Testing


| Env var                              | Default     | Notes       |
| ------------------------------------ | ----------- | ----------- |
| `OCTOCON_RUN_API_INTEGRATION`        | `false`     | (test-only) |
| `OCTOCON_RUN_LIVE_INTEGRATION`       | `false`     | (test-only) |
| `OCTOCON_TEST_SCYLLA_CONTACT_POINTS` | `127.0.0.1` | (test-only) |
| `OCTOCON_TEST_SCYLLA_USERNAME`       | `cassandra` | (test-only) |
| `OCTOCON_TEST_SCYLLA_PASSWORD`       | `cassandra` | (test-only) |
| `OCTOCON_TEST_REGION`                | `nam`       | (test-only) |


## Layer 4 — `internal.secrets`

The `internal.secrets` table is the durable, in-cluster source of truth for everything
sensitive that the API needs at runtime. It is:

- created by `DatabaseInitPhase` inside the bootstrapper, owned by the `<app>_admin` role;
- read-only granted to the app `interfold` user;
- seeded once on first bootstrap and re-seeded whenever the bootstrapper runs (writes are
idempotent — empty values are skipped to avoid clobbering operator-set rows).

Row inventory (see `[SeedKeys.cs](../csharp/Interfold.DatabaseBootstrap/SeedKeys.cs)`):


| Key                           | Origin                                      | Consumer                                                                                                                                 | Empty-skip? |
| ----------------------------- | ------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- | ----------- |
| `oauth:google:client_secret`  | `BootstrapConfig.OAuth.GoogleClientSecret`  | `SecretsBootstrapService` → `AuthenticationConfiguration.GoogleOAuthClientSecret`                                                        | yes         |
| `oauth:discord:client_secret` | `BootstrapConfig.OAuth.DiscordClientSecret` | `SecretsBootstrapService` → `AuthenticationConfiguration.DiscordOAuthClientSecret`                                                       | yes         |
| `oauth:apple:client_secret`   | `BootstrapConfig.OAuth.AppleClientSecret`   | `SecretsBootstrapService` → `AuthenticationConfiguration.AppleOAuthClientSecret`                                                         | yes         |
| `encryption:pepper`           | `GeneratedSecrets.EncryptionPepper`         | `SecretsBootstrapService` → `AuthenticationConfiguration.EncryptionPepper`                                                               | no          |
| `postgres:admin_username`     | constant `interfold_admin`                  | `PostgresMigrationService` (DDL connection)                                                                                              | no          |
| `postgres:admin_password`     | `GeneratedSecrets.PostgresAdminPassword`    | `PostgresMigrationService`                                                                                                               | no          |
| `scylla:admin_username`       | `GeneratedSecrets.ScyllaUser + "_admin"`    | `ScyllaMigrationService` (keyspace DDL)                                                                                                  | no          |
| `scylla:admin_password`       | `GeneratedSecrets.ScyllaAdminPassword`      | `ScyllaMigrationService`                                                                                                                 | no          |
| `scylla:contact_points`       | AppHost-resolved Scylla host                | `ScyllaSessionProvider` / health checker                                                                                                 | no          |
| `scylla:local_datacenter`     | `nam`                                       | `ScyllaSessionProvider`                                                                                                                  | no          |
| `scylla:username`             | `GeneratedSecrets.ScyllaUser`               | `ScyllaSessionProvider` (app session)                                                                                                    | no          |
| `scylla:password`             | `GeneratedSecrets.ScyllaPassword`           | `ScyllaSessionProvider`                                                                                                                  | no          |
| `scylla:port`                 | `Ports.scylla` (default `9042`)             | `ScyllaSessionProvider`                                                                                                                  | no          |
| `auth:jwt_rsa256_private_pem` | `GeneratedSecrets.JwtRsa256PrivateKeyPem`   | `SecretsBootstrapService.PatchRsa256` — populates `Rsa256PrivateKey` + derives `Rsa256PublicKey`                                         | yes         |
| `auth:jwt_es256_private_pem`  | `GeneratedSecrets.JwtEs256PrivateKeyPem`    | `SecretsBootstrapService.PatchEs256` — populates `JwtEs256PrivateKeyPem` + seeds `JwtEs256VerificationKeyPems[0]`                        | yes         |
| `auth:deep_link_secret`       | `GeneratedSecrets.DeepLinkSecret`           | `SecretsBootstrapService` → `AuthenticationConfiguration.DeepLinkSecret`                                                                 | yes         |
| `certs:leaf_pfx_password`     | `GeneratedSecrets.LeafPfxPassword`          | `Program.LoadLeafPfxPasswordFromStoreIfNeeded` — injected into `IConfiguration[Kestrel:Certificates:Default:Password]` before host build | yes         |


> **Deliberately absent:** there is no `scylla:keyspace` row. Keyspace is per-node region
> identity and must come from the deployment env (`OCTOCON_SCYLLA_KEYSPACE`), not from a
> shared cluster row.

The OAuth client-IDs do **not** appear in this table by design. They are public values; the
asymmetric split (IDs in env, secrets in store) is intentional and the seed list reflects it.

## Boot ordering

The order of operations on a self-hosted API container start is:

```mermaid
sequenceDiagram
    autonumber
    participant Operator
    participant Compose as Docker Compose
    participant API as API Program.cs
    participant PG as Postgres (internal.secrets)
    participant Host as ASP.NET Core host
    participant Kestrel
    participant SBS as SecretsBootstrapService<br/>(IHostedLifecycleService)
    participant Mig as Migration services

    Operator->>Compose: docker compose up -d
    Compose->>API: start container, exec dotnet Interfold.Api
    API->>API: build IConfiguration<br/>(env + appsettings)
    API->>PG: SELECT value WHERE key='certs:leaf_pfx_password'
    PG-->>API: leaf PFX password
    API->>API: inject into Kestrel:Certificates:Default:Password
    API->>Host: builder.Build()
    Host->>Kestrel: bind HTTPS endpoint (loads leaf.pfx with the password)
    Host->>SBS: StartingAsync
    SBS->>PG: load auth:* + oauth:* + encryption:pepper rows
    SBS->>SBS: patch IOptionsMonitor<AuthenticationConfiguration>
    Host->>Mig: StartingAsync (Postgres + Scylla migrations)
    Mig->>PG: read admin credentials, run DDL
    Host->>API: StartedAsync; serve traffic
```



Two ordering invariants are critical:

1. **Kestrel ↔ leaf PFX password.** Kestrel reads
  `Kestrel:Certificates:Default:Password` out of `IConfiguration` *during* `builder.Build()`
   (specifically, when it binds the HTTPS endpoint). `IHostedLifecycleService.StartingAsync`
   runs *after* the host is built, which is too late. Hence the dedicated `NpgsqlConnection`
   query in `Program.cs` *before* `builder.Build()`. The failure mode if Postgres is
   unreachable here is "the API fails to start before binding" — louder than a missing
   cert at request time, and the deliberate trade-off documented in the source comment.
2. `**SecretsBootstrapService` ↔ migration services.** Migration services need the admin
  credentials from `internal.secrets` (`postgres:admin_password`, `scylla:admin_`*). They
   read those directly via `ISecretsStore`, so they don't actually depend on
   `SecretsBootstrapService`. But the auth options consumed by controllers (JWT private
   keys, deep-link secret) *do* depend on `SecretsBootstrapService` having patched them
   first. .NET's `IHostedLifecycleService.StartingAsync` runs all registered services
   concurrently, but the API doesn't accept requests until `StartedAsync` returns, so any
   controller that reads `IOptionsMonitor<AuthenticationConfiguration>` already sees
   patched values.

## Migration ledger

Both database providers track which embedded migrations have already been applied so
subsequent startups skip already-applied files instead of relying purely on `IF NOT EXISTS`
guards in the SQL/CQL. The ledger also detects post-deploy edits to applied files
(SHA-256 checksum drift) and refuses to start the API until the drift is resolved.

### Where the ledger lives

| Provider | Table                                                            | Scope key              | Created by                                            |
| -------- | ---------------------------------------------------------------- | ---------------------- | ----------------------------------------------------- |
| Postgres | `internal.schema_migrations` (`version` primary key)             | filename only          | `PostgresMigrationService.EnsureLedgerAsync`          |
| Scylla   | `global.schema_migrations` (`PRIMARY KEY (scope, version)`)      | `(keyspace, filename)` | `ScyllaMigrationService.EnsureLedgerAsync`            |

Each row records `checksum` (hex SHA-256 of the embedded resource bytes), `applied_at`,
`duration_ms`, and `applied_by` (the assembly informational version of the runner that
wrote the row). For Scylla the `scope` column is the regional keyspace name for the per-
region templates (`001_create_interfold_keyspaces.cql`,
`002_create_interfold_schema.templated.cql`) and `grants:<keyspace>` for the GRANT loop
(version = `grants_v1`, bump that constant in
`[ScyllaMigrationService.cs](../csharp/Interfold.Infrastructure.Scylla/ScyllaMigrationService.cs)`
when the grant set changes).

### Bootstrap order

- **Postgres:** the runner acquires the session-level
  `pg_advisory_lock(MigrationAdvisoryLockId)` first, then `CREATE SCHEMA IF NOT EXISTS
  internal` + `CREATE TABLE IF NOT EXISTS internal.schema_migrations` *before* querying
  the ledger. Each not-yet-applied migration body and its ledger insert run inside the
  same `NpgsqlTransaction` so a partial failure leaves no orphan row.
- **Scylla:** the runner renders `000_create_singleton_keyspaces.cql` unconditionally to
  create `global` / `nam_nt` / `dummy` (the only "always-run" bootstrap step, untracked
  because `global` is the precondition for the ledger itself), then creates
  `global.schema_migrations` and reads it. Per-keyspace files (`001_*`,
  `002_*.templated.cql`) are then rendered and applied per regional keyspace, with each
  ledger insert issued as `INSERT IF NOT EXISTS` so concurrent migrators converge on a
  single row.

### Drift behaviour

If a recorded migration's recomputed checksum no longer matches the row, the runner
throws `InvalidOperationException` mentioning the version and both checksums and the API
refuses to start. Migration files are immutable once applied. To intentionally repurpose
an existing version (e.g., you re-wrote the file and want to mark it re-applied), pick the
appropriate path:

- **Force re-record (no DB change required):** update the ledger checksum to the new file
  hash before restart. The runner will see the row exists with the new checksum and skip
  the file body next start. Compute the new checksum locally with `sha256sum` and:
  - Postgres:
    `UPDATE internal.schema_migrations SET checksum = '<NEW_HEX>' WHERE version = '<file>';`
  - Scylla:
    `UPDATE global.schema_migrations SET checksum = '<NEW_HEX>' WHERE scope = '<keyspace>' AND version = '<file>';`
- **Force re-run from scratch (rare):** delete the ledger row(s) for the file. The runner
  will treat the file as new on the next start, run the body, and re-insert the row. Only
  do this if every statement in the file remains idempotent (`CREATE … IF NOT EXISTS`,
  etc.) — the runner does not roll back the schema before re-applying.

Both rewrites require admin credentials (the app user has no access to `internal.secrets`-
adjacent tables or `global.schema_migrations`); use the same `postgres:admin_*` /
`scylla:admin_*` rows the runner consumes.

## Rotation story

Three independent rotation surfaces, each with a single command:


| Rotation          | Command                                             | What changes                                                                         | What stays                                                            |
| ----------------- | --------------------------------------------------- | ------------------------------------------------------------------------------------ | --------------------------------------------------------------------- |
| **Secrets**       | `interfold-bootstrap rotate-secrets`                | All DB passwords, encryption pepper, JWT RSA + ES256 keypairs, deep-link HMAC secret | Certs (root CA + leaf), leaf PFX password (preserved across rotation) |
| **Certs**         | `interfold-bootstrap rotate-certs`                  | Root CA + leaf cert/key, leaf PFX wrapper (re-encrypted with the existing password)  | All secrets                                                           |
| **OAuth secrets** | edit `interfold.bootstrap.json` → rerun `bootstrap` | Only the OAuth secrets you changed                                                   | Everything else                                                       |


Mechanics:

- `rotate-secrets` deliberately preserves `LeafPfxPassword` (see `SecretsPhase.RunAsync`)
because the wrapped PFX bytes don't change — re-wrapping with a fresh password would
invalidate Kestrel's load with no security benefit.
- Backfill on idempotent reruns: if a `secrets.json` file from before the JWT-keys-in-store
migration is re-read without `--rotate-secrets`, `SecretsPhase` backfills any missing
fields (`DeepLinkSecret`, `JwtRsa256PrivateKeyPem`, `JwtEs256PrivateKeyPem`,
`PostgresAdminPassword`, `ScyllaAdminPassword`) so the next bootstrap pass writes them
into `internal.secrets`.
- OAuth client *secrets* can be rotated without touching the bootstrapper by issuing
`UPDATE internal.secrets SET value = '...' WHERE key = 'oauth:<provider>:client_secret';`
followed by an API restart. The bootstrapper will catch up on the next run.

## Local dev vs self-host vs tests


| Concern           | Local dev (`aspire run`)                                                                                  | Self-host (`interfold-bootstrap`)                                                                    | Integration tests                                                                                                                         |
| ----------------- | --------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| Encryption pepper | `GeneratedSecrets.EncryptionPepper` → `internal.secrets:encryption:pepper` (no env, no AppHost parameter) | Same                                                                                                 | Postgres fixtures seed `"TEST"` into the row via `PostgresSeedOptions`; the in-memory store is pre-seeded with `"TEST"` for inmemory mode |
| JWT keys          | Generated lazily by `SecretsPhase` and round-tripped through Postgres                                     | Same                                                                                                 | Seeded into the real store for Scylla/Postgres fixtures; in-memory store pre-seeded with `TestDbCredentials` PEMs for inmemory mode       |
| Leaf PFX password | *no leaf PFX in dev* (ASP.NET dev cert)                                                                   | `internal.secrets:certs:leaf_pfx_password`, loaded by `Program.LoadLeafPfxPasswordFromStoreIfNeeded` | not exercised                                                                                                                             |
| OAuth secrets     | `Parameters:google-oauth-client-secret` / `Parameters:discord-oauth-client-secret` user-secrets (legacy)  | `internal.secrets:oauth:*:client_secret`                                                             | empty / `"TEST"`                                                                                                                          |


Tests centralise the test-only material in
`[TestDbCredentials](../csharp/Interfold.IntegrationTests/TestServices/TestDbCredentials.cs)`
— a single source of lazy-generated in-process keypairs and deterministic passwords. The
real DB fixtures seed those values into `internal.secrets` via `PostgresSeedOptions`; the
in-memory `WebApplicationFactory` instead replaces `ISecretsStore` with a pre-seeded
`InMemorySecretsStore` (see
`[InterfoldWebApplicationFactory.BuildSeededInMemorySecretsStore](../csharp/Interfold.IntegrationTests/TestServices/InterfoldWebApplicationFactory.cs)`).
Either way, signing in `CreateToken` and verification on the server side use the same PEMs.

## Common gotchas

- `**OCTOCON_SCYLLA_KEYSPACE` is required for correct routing.** It used to fall back to
`internal.secrets:scylla:keyspace`; that row no longer exists. If the env is missing,
the API defaults to `"nam"` in code — which is correct for a single-region deployment
but the *wrong* answer for a `eur` or `gdpr` node. Failure mode is "wrong region", not
"crash".
- **Don't put `ASPNETCORE_Kestrel__Certificates__Default__Password` back in `.env`.** It
is intentionally not generated. If you set it, `Program.LoadLeafPfxPasswordFromStoreIfNeeded`
honours it as an override (legacy escape hatch), but you've now bypassed
`internal.secrets` and rotation via `rotate-secrets` will not propagate.
- `**internal.secrets:encryption:pepper` must exist before the API starts.** It's the
one row `SecretsBootstrapService` enforces — if the value is missing or empty the API
refuses to boot rather than failing on the first encryption request with an opaque
`NullReferenceException`. `DatabaseInitPhase` seeds it from `GeneratedSecrets.EncryptionPepper`;
tests pass `"TEST"` through `PostgresSeedOptions` or `InMemorySecretsStore.Seed`. The
pepper no longer has an env-var fallback — the previous `OCTOCON_ENCRYPTION_PEPPER` is dead.
- **OAuth client IDs in env, secrets in store.** Client IDs in env are public and that's
fine. Putting client *secrets* in env (other than the bootstrapper-written placeholders)
is a foot-gun: the env value is overwritten at startup, so an operator who manually
edits `.env` will be confused when their change has no effect.
- **The `/keys` bind mount is gone.** Earlier versions mounted `secrets/keys/*.pem` into
`/keys` inside the container. Those PEMs are no longer written to disk; the API reads
the same material from `internal.secrets`. Remove any old `/keys` mounts from custom
compose overrides.

