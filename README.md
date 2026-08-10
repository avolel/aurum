# Aurum

Gold price intelligence platform. See `plans/aurum_brd.md` for requirements and
`plans/aurum-phased-plan.md` for the engineering roadmap.

**Current state: Phase 0, in progress.** The schema, the quota governor and the polling scaffold
are done and green against a real Postgres; `docker compose` topology is not yet verified end to
end. `ops/decisions/phase-0.md` records what is decided and what is still open;
`ops/phase-0-todo.md` is the remaining work.

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

Requires Docker access. If `docker ps` says permission denied, add yourself to the `docker` group
(`sudo usermod -aG docker $USER`) and log back in — nothing here runs without it.

## Tests

```bash
dotnet test
```

Integration tests spin up `timescale/timescaledb-ha:pg17` via Testcontainers rather than mocking.
Hypertables and vector columns behave differently enough from vanilla Postgres that a mock would
validate nothing.

`QuotaGovernorTests` is the quota governor's specification, and it passes — including
`Concurrent_acquires_never_oversubscribe`, which races 40 callers on separate `DbContext`s against
a budget of 10.

## Why the quota governor matters

GoldAPI's free tier bills on a **monthly** quota. An in-memory rate limiter that resets when the
container restarts can spend a month of requests in an afternoon, and every individual request
looks fine while it happens. Accounting is therefore durable (Postgres) and enforced at the HTTP
boundary as a `DelegatingHandler` — above it, Phase 1's retries and failover chain would spend
budget without being counted.

The counter lives in `api_quota_windows`, one row per (source, accounting period). Acquiring a
request is a single `INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`, so the budget check and
the increment happen inside one row lock — a `SELECT` followed by an `UPDATE` leaves a gap where two
callers both see the last request available. Budget returns at a period boundary because the period
key changes and the next acquire creates a fresh row; nothing is scheduled and nothing is reset.

If you are about to replace that SQL with EF: read the class remarks on `PostgresQuotaGovernor`
first, and `ops/decisions/phase-0.md` (D-7) for the decisions behind it.
