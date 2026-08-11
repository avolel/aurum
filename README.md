# Aurum

Gold price intelligence platform.

**Current state: Phase 0, in progress.** The schema, the quota governor and the polling scaffold are
written and green against a real Postgres, and the compose stack boots to a healthy API from a clean
volume. What remains is mostly measurement rather than code: the GoldAPI account facts, a run
against the live API, and the two spikes (charting, Ollama) that Phase 1 and Phase 2 depend on.

`ops/decisions/phase-0.md` records what is decided and why, including the rejected alternatives.
`ops/phase-0-todo.md` is the remaining work, ordered so each item is verifiable when it is finished.

### Done

- First migration applies against `timescale/timescaledb-ha:pg17`; `price_ticks` is a hypertable with
  daily chunks and a 30-day retention policy; pgvector is present (D-3, D-4).
- `PostgresQuotaGovernor` — durable, race-safe quota accounting (D-7). 17 tests green.
- Budget survives a real `docker compose restart api`, not just a fresh `DbContext` in a test.
- `docker compose down -v && docker compose up --build` from an empty volume reaches a healthy `api`
  with `/health` and `/health/ready` both 200.

### Open, and each one blocks something

- **GoldAPI's real free-tier limit and reset semantics are unverified.** `MonthlyRequestLimit` and
  `QuotaPeriod` (`CalendarMonthUtc` by default) are guesses until they are checked against the
  account page rather than the docs. A wrong reset rule is a silent one-in-twelve failure.
- **The free tier cannot support Phase 1.** ~100 requests/month is ~3 polls/day, which cannot meet
  "price visible within 2× polling interval." Paid tier, a different primary source, or a re-scoped
  Phase 1 — the call has to be recorded before Phase 1 starts.
- **D-1 / D-2 (Expo vs bare RN, charting library)** await the 30k-tick spike, which needs a physical
  device; simulator frame times will lie.
- **D-6 (Ollama sizing)** awaits measured tokens/sec. 12 GB VRAM already rules out the phase plan's
  "just use a bigger local model" fallback, so the measurement decides Phase 2's model registry.

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

`PollInterval` and `MonthlyRequestLimit` are coupled: `PricePollingService.GuardPollBudget` refuses
to start on a cadence that would overspend the period, and tells you the minimum interval that fits.
Changing one usually means changing the other.

Every timestamp in that table comes from the injected `TimeProvider`, including the audit fields that
`AurumDbContext` stamps. The context takes the clock as a required constructor parameter for that
reason: an optional one with a wall-clock default compiles everywhere and reverts silently.

If you are about to replace that SQL with EF: read the class remarks on `PostgresQuotaGovernor`
first, and `ops/decisions/phase-0.md` (D-7) for the decisions behind it.
