# Aurum

Gold price intelligence platform.

**Current state: the foundation is complete and the Phase 1 backend has started.**

Built and verified: the schema and first migration against `timescale/timescaledb-ha:pg17`, with
`price_ticks` a hypertable on daily chunks under a 30-day retention policy and pgvector present
(D-3, D-4); `PostgresQuotaGovernor`, durable and race-safe, with per-source budgets resolved from
configuration rather than a fall-through default (D-7, D-9); a budget that survives a real
`docker compose restart api`, not just a fresh `DbContext` in a test; and
`docker compose down -v && docker compose up --build` from an empty volume reaching a healthy `api`
with `/health` and `/health/ready` both 200. The test suite is green.

GoldAPI's free-tier facts are checked against the account page rather than the docs — **100 requests
per calendar month, UTC** — so `MonthlyRequestLimit`, `QuotaPeriod` and the 8-hour `PollInterval`
reflect measured values, and the options validator refuses to boot a cadence that would overspend
them. D-8 keeps us on the free tier for development and makes the paid tier a gate on Phase 1
go-live, before anything user-facing ships. The tier is a configuration number and nothing branches
on 100 (D-9 removed the last place that did), so moving up is two environment variables and a
restart. The accepted cost is that Phase 1's "price visible within 2× polling interval" stays unmet
by choice until then — an 8-hour-old price on the free tier is correct behaviour, not a defect.
Development also yields ~3 ticks a day, so anything needing volume should generate it rather than
wait for the poller.

Not built yet: the failover chain, the delta engine, the SignalR hub and the REST endpoints. One
foundation item is also still outstanding — **no run against the live API**, which needs a real key
in `.env`. It is item 0 of the Phase 1 todo.

The three spikes were closed without being run: D-1 and D-2 are declared rather than measured, and
D-6 is deferred to Phase 2 planning. D-2 settles web on `lightweight-charts` and leaves the native
chart, and what a web/native split would cost BO-4, to Phase 4 where a physical device is actually in
play — simulator frame times will lie. Each entry says so in its own words; read the status line
before treating any of them as a measured result.

`ops/decisions/decisions.md` records what is decided and why, including the rejected alternatives.
`ops/phase-1-todo.md` is the remaining work, ordered so each item is verifiable when it is finished.

## Layout

```
docker-compose.yml           postgres (timescale+pgvector), ollama, api
ops/db/init/                 extension bootstrap, runs once on an empty volume
ops/decisions/               decision log (D-1 onward, IDs cited from code)
src/Aurum.Api/
  Modules/Pricing/           price sources, quota governance, polling
  Modules/Macro/             macro series schema (ingestion lands in Phase 2a)
  Shared/                    DbContext, audit fields
  Migrations/
src/Aurum.Api.Tests/         integration tests against a real Postgres
```

Modules are folders inside one project, not separate assemblies (D-5). Each exposes a single
`Add<Module>Module` registration method, and `Program.cs` knows nothing else about module internals.

## Running

```bash
cp .env.example .env      # fill in POSTGRES_PASSWORD and the GoldAPI key
docker compose up --build
```

The API applies migrations at startup. `/health` is liveness, `/health/ready` gates on Postgres.

Startup migration and `PricePollingService` both assume a **single instance**. Both are wrong the
moment the API scales out — the poller would double-spend the quota, and two instances would race
the migration.

Requires Docker access. If `docker ps` says permission denied, add yourself to the `docker` group
(`sudo usermod -aG docker $USER`) and log back in — nothing here runs without it.

## Tests

```bash
dotnet test
```

17 tests. Integration tests spin up `timescale/timescaledb-ha:pg17` via Testcontainers rather than
mocking: hypertables and vector columns behave differently enough from vanilla Postgres that a mock
would validate nothing. One container per collection, migrated once; tests clean up their own rows.

`QuotaGovernorTests` and `QuotaHandlerTests` are the quota subsystem's specification — including
`Concurrent_acquires_never_oversubscribe`, which races 40 callers on separate `DbContext`s against a
budget of 10.

Some of those tests exist to hold a decision in place rather than to catch a bug. "No refunds on a
failed request" is enforced by nothing in the code except a `catch` block that isn't there, so
`Transport_failure_still_spends_the_lease` is what makes reintroducing one fail out loud. Deleting a
test like that because it "tests nothing" removes the guard, not the redundancy.

**Known flake in the fixture.** `PostgresFixture` waits on `pg_isready`, which reports the server is
accepting connections before Patroni has finished creating `aurum_test`. When it loses that race
every test in the collection fails at setup with `database "aurum_test" does not exist`. A re-run
passes. The wait strategy needs to check for the database, not the server.

## Why the quota governor matters

GoldAPI's free tier bills on a **monthly** quota. An in-memory rate limiter that resets when the
container restarts can spend a month of requests in an afternoon, and every individual request looks
fine while it happens. Accounting is therefore durable (Postgres) and enforced at the HTTP boundary
as a `DelegatingHandler` — above it, Phase 1's retries and failover chain would spend budget without
being counted.

The counter lives in `api_quota_windows`, one row per (source, accounting period). Acquiring a
request is a single `INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`, so the budget check and the
increment happen inside one row lock — a `SELECT` followed by an `UPDATE` leaves a gap where two
callers both see the last request available. Budget returns at a period boundary because the period
key changes and the next acquire creates a fresh row; nothing is scheduled and nothing is reset.

When the provider rejects us for quota, the clamp is written as `ProviderRejectedAt` and never by
moving the counter — so a row can read "0 used of 100, rejected," and that contradiction is the
point. It is the only evidence that our count and the provider's have diverged, which is worth more
than making `Limit - Used` arithmetically tidy.

`PollInterval` and `MonthlyRequestLimit` are coupled: `PriceSourcesOptionsValidator` fails the
process at boot on a cadence that would overspend the period, and tells you the minimum interval
that fits. Changing one usually means changing the other.

Every timestamp in that table comes from the injected `TimeProvider`, including the audit fields that
`AurumDbContext` stamps. The context takes the clock as a required constructor parameter for that
reason: an optional one with a wall-clock default compiles everywhere and reverts silently.

If you are about to replace that SQL with EF: read the class remarks on `PostgresQuotaGovernor`
first, and `ops/decisions/decisions.md` (D-7) for the decisions behind it.
