# [Octocon](https://octocon.app) backend

**Octocon is the modern, all-in-one toolkit for people with DID and OSDD to manage their disorder and express themselves.**

> [!WARNING]
> This branch is currently heavily AI generated. 
> While it is functional and provides everything required for the client to work, it's still not as performant or reliable as it should be!
> A lot of [work](https://github.com/users/Azyyyyyy/projects/1) is pending to get this new backend built with the care it should have, please be patient <3

It's also a wacky monolith built with [.NET](https://dotnet.microsoft.com/en-us/learn/dotnet/what-is-dotnet), [ScyllaDB](https://www.scylladb.com/) and [PostgreSQL](https://www.postgresql.org/). Designed to run on bare-metal hardware!

## Project structure TO EDIT
This repository contains the backend code for Octocon, which is structured into three main components:
- **octocon**: The core Elixir application that handles the business logic, data processing, clustering, node differentiation, and other backend functionalities.
- **octocon-web**: The Phoenix web application that serves the REST API, metrics, and admin dashboard.
- **octocon-discord**: Legacy Discord bot integration (currently being phased out and disabled by default).

## Self-hosting (production)

Interfold ships a single bootstrapper binary that brings a fresh Linux box from `git clone` to a
running stack. The bootstrapper IS an Aspire AppHost — it reuses the same resource graph as the
dev `aspire run` flow, with a Docker Compose publisher and a host-side prep wrapper around it.

### One-shot install

1. Download the latest release tarball (TO BE CREATED) and unpack it on a fresh Ubuntu 22.04+/Debian 12+/Fedora 40+/RHEL 9+ box:

   ```bash
   tar -xzf interfold-bootstrap-linux-x64.tar.gz
   cd interfold-bootstrap
   ```

2. Author `interfold.bootstrap.json` (or run the binary with no arguments to be prompted):

   ```json
   {
     "deployment": {
       "outputDir": "./deploy",
       "domains": ["api.example.com"],
       "rootCaName": "Interfold Root CA",
       "certYears": 5,
       "trustStoreInstall": true
     },
     "ports": { "apiHttp": 5000, "apiHttps": 5001, "webHttp": 8080, "webHttps": 8081 },
     "databaseMode": "single",
     "apiImage": "ghcr.io/interfold/api:latest",
     "oauth": {
       "googleClientSecret": "...",
       "discordClientSecret": ""
     }
   }
   ```

3. Run the bootstrapper as root:

   ```bash
   sudo ./interfold-bootstrap bootstrap --config interfold.bootstrap.json
   ```

   This walks through six phases — prereqs → config → secrets → certs → publish → launch —
   ending with the API's `/health/ready` returning 200.

Generated artifacts land under `./deploy/`:

```
deploy/
  interfold.bootstrap.json    # canonical config (re-emitted on first run)
  secrets/secrets.json        # mode 0600, never overwritten without --rotate-secrets
  certs/{rootCA,leaf}.{crt,key,pfx}
  docker-compose.yaml         # emitted by the embedded AppHost
  .env                        # bound to compose `${VAR}` references
```

### Operational commands

| Command | Effect |
|---|---|
| `interfold-bootstrap` (default `bootstrap`) | Run all six phases. Idempotent on rerun. |
| `interfold-bootstrap publish` | Run config + secrets + certs + compose-emit; do not `docker compose up`. |
| `interfold-bootstrap up` | Run only `docker compose up -d` + health wait against an already-generated compose file. |
| `interfold-bootstrap rotate-secrets` | Regenerate DB/admin passwords + encryption keypair + pepper, re-emit compose, restart the API. Certs unchanged. |
| `interfold-bootstrap rotate-certs` | Regenerate root CA + leaf cert, re-install into the trust store, re-emit compose. Secrets unchanged. |

Common flags:

- `--config <path>` — point at a populated `interfold.bootstrap.json` instead of using the
  default `deploy/interfold.bootstrap.json` lookup or interactive prompt.
- `--output-dir <path>` — override the artifact root (default `./deploy`).
- `--skip-prereqs` — skip the Docker/openssl/AIO install. Use on re-runs where the host is
  already configured.
- `--non-interactive` — fail rather than prompt when config values are missing.

### Local dev (Aspire)

Self-hosting and dev use the same resource graph. For local development, run the Aspire
AppHost directly:

```bash
cd csharp/Interfold.AppHost
aspire run
```

This brings the same Postgres + ScyllaDB + bootstrap-auth + API stack up under the Aspire
dashboard. Use `dotnet user-secrets` to populate the `Parameters:*` secrets the AppHost
guards on (see `csharp/Interfold.AppHost/InterfoldAppHost.cs`).

## Contributing

We welcome contributions to Octocon! If you'd like to contribute, please follow these steps:
1. Fork the repository on GitHub and create a new branch for your feature or bug fix.
2. Use [conventional commits](https://www.conventionalcommits.org/en/v1.0.0/) for your commit messages.
3. Run `mix format` then `mix lint` before submitting a PR to ensure code quality.
4. Submit a pull request to this repository for review.

While we respect your time, please note that not every contribution will be accepted; certain features may not align with our project's goals or privacy/security standards. If you'd like to contribute a new feature, your best bet is to reach out to us in the `#development` channel on our [Discord server](https://discord.gg/octocon) first to discuss its feasibility. Alternatively, we welcome PRs implementing accepted suggestions posted on [our issue tracker](https://github.com/octocondev/issues/issues?q=is%3Aissue%20state%3Aopen%20type%3ASuggestion%20(label%3A%22Low%20Priority%22%20OR%20label%3A%22High%20Priority%22%20OR%20label%3AUrgent))

## Deployment structure
Octocon is designed as a distributed monolith, meaning that while the components have a clear separation of concerns, they share a common codebase which is compiled and deployed as one executable.

Octocon is generally run in a cluster of nodes, which are designed to be globally distributed across the world. One "primary" node interfaces with a generally larger database instance and handles certain types of global state, while "auxiliary" nodes interface with smaller database instances. This allows for low-latency access to the data from anywhere in the world.

When not running on Fly.io, an Octocon node knows its role in the overall cluster through an environment variable (`NODE_GROUP`), which determines which parts of the supervision tree it will run, and how it will advertise itself to its peers.

In production, Octocon is configured to discover other nodes using the [libcluster](https://github.com/bitwalker/libcluster) library with a custom Tailscale strategy. All that is necessary is for each node to form a Distributed Erlang cluster; Octocon has internal logic to determine and cache each node's role in the cluster through an RPC communication step.

There are 3 node groups:
- `primary`: A node running in the primary region, which interfaces with a larger database instance and handles certain types of global state. Other nodes proxy some requests to a `primary` node.
- `auxiliary`: A node running in an auxiliary region, which interfaces with a smaller database instance. These nodes are largely only responsible for serving HTTP requests to the API.
- `sidecar`: A node responsible for isolating CPU-bound tasks from the rest of the cluster, such as image processing and heavy encryption tasks. Ideally, **at least one** sidecar should be present in the cluster. If no sidecar is present, nodes will run these tasks themselves.

**Note**: Running multiple `primary` nodes is heavily experimental and not currently recommended - our Discord library (Nostrum) still doesn't behave well in this type of distributed environment.

## Configuration & integrations

> **Full reference:** [`docs/configuration.md`](docs/configuration.md) documents every
> configuration layer, every env var, the `internal.secrets` row inventory, the boot-time
> ordering between Kestrel / `SecretsBootstrapService` / migration services, and the
> rotation story. This README only covers the short operator-facing surface.

For a self-hosted deployment, the bootstrapper writes:

- `deploy/secrets/secrets.json` (mode 0600) — auto-generated DB passwords, encryption
  pepper, JWT signing keypairs (RSA-2048 + ES256), deep-link HMAC secret, leaf PFX
  password. Never overwritten without `--rotate-secrets`.
- `deploy/.env` — compose-bound subset that needs to be visible to Docker at `up` time
  (DB usernames/passwords, encryption private key, encryption pepper). Re-emitted from
  `secrets.json` on every bootstrapper run.
- `internal.secrets` rows inside Postgres — the durable, in-cluster source of truth for
  everything the API reads at runtime (auth/OAuth secrets, JWT keys, deep-link HMAC,
  leaf PFX password, Scylla credentials). Seeded by `DatabaseInitPhase`.

The only secrets you set by hand live in [`interfold.bootstrap.json`](#one-shot-install):
OAuth client secrets (`googleClientSecret`, `discordClientSecret`, `appleClientSecret`).
Leave them empty to skip the corresponding `internal.secrets` row.

### Env vars you must set by hand

These are *not* generated by the bootstrapper. Export them via systemd, a sibling
`.env.local`, your orchestrator, or the AppHost user-secrets in dev.

| Variable | Purpose |
|---|---|
| `OCTOCON_SCYLLA_KEYSPACE` | Per-node region identity (`nam`, `eur`, `ocn`, `eas`, `sam`, `sas`, `gdpr`). Default `nam`. **No store fallback.** |
| `OCTOCON_NODE_GROUP` | `primary`, `auxiliary`, or `sidecar`. Default `auxiliary`. (Wins over `FLY_PROCESS_GROUP` only when Fly isn't injecting one.) |
| `OCTOCON_CORS_ALLOWED_ORIGINS` | Comma-separated CORS allow-list. Empty falls back to "allow any origin". |
| `OCTOCON_GOOGLE_OAUTH_CLIENT_ID` | Public OAuth client ID. (Client *secret* lives in `internal.secrets`.) |
| `OCTOCON_DISCORD_OAUTH_CLIENT_ID` | Same. |
| `OCTOCON_APPLE_OAUTH_CLIENT_ID` | Same. |
| `OCTOCON_AUTH_CALLBACK_BASE_URL` | Base URL the OAuth callbacks return to (e.g. `https://api.example.com`). |
| `OCTOCON_AVATAR_STORAGE_ROOT` / `OCTOCON_AVATAR_PUBLIC_BASE` | Local avatar storage root and public CDN base URL. |
| `OCTOCON_OTLP_ENDPOINT` | OTLP gRPC endpoint for traces/metrics, e.g. `http://localhost:4317`. |

### Env vars the bootstrapper manages (do not set by hand)

These are written into `deploy/.env` by the publish phase and overwritten by
`SecretsBootstrapService` at API startup. Editing them by hand has no effect — the
authoritative value lives in `internal.secrets`.

- `OCTOCON_{GOOGLE,DISCORD,APPLE}_OAUTH_CLIENT_SECRET`
- `OCTOCON_POSTGRES_CONNECTION`
- `OCTOCON_PERSISTENCE`, `OCTOCON_SINGLE_SCYLLA_INSTANCE`

The encryption pepper, JWT signing keys, deep-link HMAC secret, and leaf PFX password
have no env-var representation at all — they live in `internal.secrets` exclusively and
are read directly by `SecretsBootstrapService` (or `Program.LoadLeafPfxPasswordFromStoreIfNeeded`
for the PFX password) at startup.