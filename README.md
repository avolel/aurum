# Aurum

Gold price intelligence platform. See `plans/aurum_brd.md` for requirements and
`plans/aurum-phased-plan.md` for the engineering roadmap.

**Current state: Phase 0, incomplete.** `ops/decisions/phase-0.md` records what is decided, what
is still open, and what is unverified. `ops/phase-0-todo.md` is the remaining work.

## Layout

```
docker-compose.yml           postgres (timescale+pgvector), ollama, api
ops/db/init/                 extension bootstrap, runs once on an empty volume
ops/decisions/               phase decision log
src/Aurum.Api/
  Modules/Pricing/           price sources, quota governance, polling
  Modules/Macro/             macro series schema (ingestion lands in Phase 2a)
  Shared/                    DbContext, audit fields
  Migrations/
src/Aurum.Api.Tests/         integration tests against a real Postgres
```

Modules are folders inside one project, not separate assemblies (D-5). Each exposes a single
`Add<Module>Module` registration method.

## Running

```bash
cp .env.example .env      # fill in POSTGRES_PASSWORD and the GoldAPI key
docker compose up --build
```

The API applies migrations at startup. `/health` is liveness, `/health/ready` gates on Postgres.

Requires Docker access — if `docker ps` says permission denied, see the blocker note in
`ops/decisions/phase-0.md`.

## Tests

```bash
dotnet test
```

Integration tests spin up `timescale/timescaledb-ha:pg17` via Testcontainers rather than mocking.
Hypertables and vector columns behave differently enough from vanilla Postgres that a mock would
validate nothing.

The `QuotaGovernorTests` suite fails by design: `PostgresQuotaGovernor` is a stub, and those tests
are its specification.

## Why the quota governor matters

GoldAPI's free tier bills on a **monthly** quota. An in-memory rate limiter that resets when the
container restarts can spend a month of requests in an afternoon, and every individual request
looks fine while it happens. Accounting is therefore durable (Postgres) and enforced at the HTTP
boundary as a `DelegatingHandler` — above it, Phase 1's retries and failover chain would spend
budget without being counted.
