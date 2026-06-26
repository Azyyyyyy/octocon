# AGENTS.md

## Cursor Cloud specific instructions

Interfold is the .NET 10 (C#) backend for Octocon — an ASP.NET Core REST + WebSocket API
orchestrated with **.NET Aspire**, backed by **PostgreSQL/TimescaleDB** and **ScyllaDB**.
Standard build/test commands live in `.github/workflows/ci-cd.yml`; the dev/self-host story
is in `README.md` and `docs/configuration.md`. Notes below are the non-obvious, durable
caveats for this VM.

### Toolchain (already installed in the VM snapshot; refreshed by the update script)
- .NET SDK 10 lives at `/usr/local/dotnet` and is symlinked onto `PATH` (`/usr/local/bin/dotnet`);
  `~/.bashrc` also exports `PATH`/`DOTNET_ROOT`. `global.json` pins `10.*`.
- The Aspire CLI is a global tool (`~/.dotnet/tools/aspire`, installed via `dotnet tool install -g aspire.cli`).
- Solution file is `Interfold.slnx`. Build everything with `dotnet build Interfold.slnx`.

### Docker is required and is NOT auto-started
Aspire, the integration tests, and the bootstrapper all drive **Docker** (Postgres + Scylla
containers). On a fresh VM the daemon does not start on its own:
- Start it (backgrounded, e.g. in tmux): `sudo dockerd` and wait until `docker info` works.
- The daemon is preconfigured for Docker-in-Docker: `storage-driver=fuse-overlayfs` and
  `features.containerd-snapshotter=false` in `/etc/docker/daemon.json`, with `iptables-legacy`.
- The `ubuntu` user needs socket access each boot: `sudo chmod 666 /var/run/docker.sock`
  (or re-add to the `docker` group). Tests/Aspire invoke `docker` directly, not via `sudo`.
- ScyllaDB (Seastar) needs a raised AIO limit before any Scylla container starts:
  `sudo sysctl -w fs.aio-max-nr=1048576`. The test harness also tries to do this via a
  privileged `alpine` container, but pre-setting it avoids a failure if that path is blocked.

### Tests
- Fast, no Docker: `dotnet test csharp/Interfold.Bootstrapper.UnitTests/Interfold.Bootstrapper.UnitTests.csproj` (~4s).
- Full stack (needs Docker + the two DB images, first run pulls them): the Aspire integration
  tests spin up real Postgres + Scylla via the AppHost graph and seed/migrate themselves
  (see `Interfold.IntegrationTests/TestServices/SharedDbFixture.cs`). Scope a class with the
  Microsoft.Testing.Platform tree filter, e.g.:
  `dotnet test csharp/Interfold.IntegrationTests/Interfold.IntegrationTests.csproj --no-build -- --treenode-filter "/*/*/AuthControllerTests/*"`.
  The full suite also includes a 7-node multi-DC Scylla topology (heavy).

### Running the API in dev
- `aspire run` (from `csharp/Interfold.AppHost`) brings up Postgres + Scylla + the API under
  the Aspire dashboard. It requires non-default credentials via user-secrets on the AppHost,
  or it throws at startup:
  `dotnet user-secrets set "Parameters:postgres-user" "interfold" --project csharp/Interfold.AppHost`
  (also `postgres-password`, `scylla-user`, `scylla-password`; values must not be the
  well-known defaults `postgres`/`cassandra`).
- **Gotcha:** plain `aspire run` does NOT seed `internal.secrets` (that is owned by the
  `Interfold.Bootstrapper` `DatabaseInitPhase`), so in `scylla-postgres` mode the API
  fail-fasts on the missing `encryption:pepper` row until the DB is seeded.
- **Fastest self-contained run** (no databases): run the API in in-memory mode. Seed the
  in-memory secrets store via env vars so it boots (only `encryption:pepper` is mandatory):
  ```bash
  export OCTOCON_PERSISTENCE=inmemory
  export OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER=TEST
  export OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM="$(openssl ecparam -genkey -name prime256v1 -noout)"
  export OCTOCON_DISCORD_OAUTH_CLIENT_ID=dev-discord-client   # registers the scheme so the
                                                              # /auth/discord/callback dev path mints a token
  export ASPNETCORE_URLS="https://localhost:5443;http://localhost:5080"
  dotnet run --project csharp/Interfold.Api/Interfold.Api.csproj
  ```
  Auth is a self-issued ES256 JWT whose `sub` is the principal (no `X-Interfold-Principal`
  header needed for HTTP). A token can be minted via the dev callback
  `GET /auth/discord/callback?uid=<id>` with a `Cookie: octocon_auth_redirect_uri=<url>`
  (it returns `?token=...&id=...`); self-minted tokens are rejected unless their `jti` was
  recorded, so use the callback rather than hand-signing.

### Lint
There is no CI lint gate. `dotnet format Interfold.slnx --verify-no-changes` reports
**pre-existing** whitespace deltas in `csharp/Interfold.SPDump/Program.cs` (tabs) — unrelated
to most changes; do not treat it as a regression you introduced.
