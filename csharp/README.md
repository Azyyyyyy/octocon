# C# Migration Scaffold

This folder contains the Phase D–E C# implementation for the offline-first migration.

## Projects

- Octocon.Contracts: command/query contracts and operation IDs.
- Octocon.Domain: domain abstractions, command models, and handler logic.
- Octocon.Infrastructure: persistence registration and Scylla/Postgres adapters.
- Octocon.Api: ASP.NET Core minimal API host (Phase E). Routes Phase D handlers over HTTP.
- Octocon.Cli: DI-composed command runner for local/dev exercises.

## Notes

- Persistence modes are selectable at runtime:
	- `inmemory`
	- `scylla-postgres`
- The CLI supports both `--key value` and `--key=value` option syntax.
- Live DB schema is now bootstrapped via scripts under `csharp/db` (not at runtime).

## Schema Alignment Log (Elixir -> C#)

Last updated: 2026-03-17

- Completed: removed C# runtime dependence on `global.users`; account/profile/encryption/primary-front reads-writes now target regional `<region>.users`.
- Completed: friendship/profile lookups now use `global.user_registry` for region discovery, then query regional `<region>.users` and `<region>.current_fronts`.
- Completed: front history compatibility restored by adding regional `fronts_by_time` materialized views in CQL bootstrap.
- Completed: bootstrap health checks now validate regional `users` and no longer require `global.users`.
- Completed: `global.aggregate_versions_by_region` added to CQL bootstrap to match required health checks.
- Completed: journal list-by-alter query now targets `<region>.alter_journals` rather than a non-table alias.
- Completed: Phase O settings field persistence now uses embedded `<region>.users.fields` UDT list semantics (`type`, `security_level`, `locked`, relocate by `index`) and no longer depends on `settings_fields` bridge tables.

Recommended follow-up after backend consolidation:

- Add/expand integration coverage for settings field CRUD + relocate to lock this parity in.

## Bootstrap schema (Scylla + Postgres)

PowerShell:

```powershell
# Postgres
$env:PGPASSWORD = 'YOUR_PASSWORD'
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -h 127.0.0.1 -d octocon -f csharp\db\postgres\001_create_octocon_idempotency.sql

# Scylla (running in local Docker container)
docker exec -i scylla_octocon cqlsh -u cassandra -p cassandra -f /dev/stdin < csharp\db\scylla\001_create_octocon_schema.cql
```

## Verify DB connectivity before commands

PowerShell:

```powershell
$env:OCTOCON_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=octocon;Username=postgres;Password=YOUR_PASSWORD'

dotnet run --project csharp\Octocon.Cli -- \
	--persistence=scylla-postgres \
	--scylla-contact-points=127.0.0.1 \
	--scylla-username=cassandra \
	--scylla-password=cassandra \
	bootstrap-check
```

## Quick smoke run (Scylla + Postgres)

PowerShell:

```powershell
$env:OCTOCON_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=octocon;Username=postgres;Password=YOUR_PASSWORD'

dotnet run --project csharp\Octocon.Cli -- \
	--persistence=scylla-postgres \
	--scylla-contact-points=127.0.0.1 \
	--scylla-username=cassandra \
	--scylla-password=cassandra \
	--region=nam \
	alter-create --system sys1 --name ExampleAlter

dotnet run --project csharp\Octocon.Cli -- \
	--persistence=scylla-postgres \
	--scylla-contact-points=127.0.0.1 \
	--scylla-username=cassandra \
	--scylla-password=cassandra \
	--region=nam \
	account-username-update --system sys1 --username ExampleSystem
```

## Automated live smoke test

PowerShell:

```powershell
$env:OCTOCON_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=octocon;Username=postgres;Password=YOUR_PASSWORD'
$env:OCTOCON_RUN_LIVE_INTEGRATION = 'true'

dotnet test --project csharp\Octocon.IntegrationTests\Octocon.IntegrationTests.csproj
```

## API auth smoke test (Phase F increment)

PowerShell:

```powershell
$env:OCTOCON_RUN_API_INTEGRATION = 'true'

dotnet test --project csharp\Octocon.IntegrationTests\Octocon.IntegrationTests.csproj
```

This validates:

- anonymous heartbeat + `X-Octocon-Contract` header
- 401 response on protected route without principal
- successful write with `X-Octocon-Dev-Principal`
- idempotent replay on duplicate `X-Octocon-Idempotency-Key`
- fail-fast startup guardrail when dev bypass is disabled and JWT authority is missing

Optional overrides:

- `OCTOCON_TEST_SCYLLA_CONTACT_POINTS` (default: `127.0.0.1`)
- `OCTOCON_TEST_SCYLLA_USERNAME` (default: `cassandra`)
- `OCTOCON_TEST_SCYLLA_PASSWORD` (default: `cassandra`)
- `OCTOCON_TEST_REGION` (default: `nam`)

## Running the API (Phase E)

### Local dev — in-memory persistence, no auth required

```powershell
$env:OCTOCON_PERSISTENCE = 'inmemory'
$env:OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL = 'true'

dotnet run --project csharp\Octocon.Api
```

Send a request using the dev principal header:

```powershell
Invoke-RestMethod `
    -Uri http://localhost:5000/api/systems/me/alters `
    -Method Post `
    -Headers @{ 'X-Octocon-Dev-Principal' = 'sys1'; 'X-Octocon-Idempotency-Key' = 'key1' } `
    -ContentType 'application/json' `
    -Body '{ "name": "TestAlter" }'
```

### Against live Scylla + Postgres

```powershell
$env:OCTOCON_POSTGRES_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=octocon;Username=postgres;Password=YOUR_PASSWORD'
$env:OCTOCON_SCYLLA_CONTACT_POINTS = '127.0.0.1'
$env:OCTOCON_SCYLLA_USERNAME = 'cassandra'
$env:OCTOCON_SCYLLA_PASSWORD = 'cassandra'
$env:OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL = 'true'

dotnet run --project csharp\Octocon.Api
```

### Auth (production)

Set `OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL` to `false` (the default) and supply a JWT in the `Authorization: Bearer <token>` header. JWT validation is enabled using `OCTOCON_JWT_AUTHORITY` and optional `OCTOCON_JWT_AUDIENCE`; on success, the API uses the `sub` claim as `principal_id`.

All API routes require authenticated access by default, except `GET /api/heartbeat`.

Startup guardrail: when `OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL=false`, `OCTOCON_JWT_AUTHORITY` is required and the API will fail fast if it is missing.

Local dev mode: set `OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL=true` to allow `X-Octocon-Dev-Principal` as a development-only authentication shim.

### Routes available

| Method | Path | Operation |
|---|---|---|
| GET | `/api/heartbeat` | public health check |
| POST | `/api/systems/me/alters` | cmd.alter.create |
| PATCH | `/api/systems/me/alters/{id}` | cmd.alter.update |
| POST | `/api/systems/me/front/start` | cmd.front.start |
| POST | `/api/systems/me/front/end` | cmd.front.end |
| POST | `/api/systems/me/front/primary` | cmd.front.primary |
| POST | `/api/settings/username` | cmd.settings.username.update |

