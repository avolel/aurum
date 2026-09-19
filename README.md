# Aurum

Gold price intelligence platform.

**Current state: the foundation is complete, and Phase 1 items 0–2 are done.**

Built and verified: the schema and first migration against `timescale/timescaledb-ha:pg17`, with
`price_ticks` a hypertable on daily chunks under a 30-day retention policy and pgvector present
(D-3, D-4); `PostgresQuotaGovernor`, durable and race-safe, with per-source budgets resolved from
configuration rather than a fall-through default (D-7, D-9); a budget that survives a real
`docker compose restart api`, not just a fresh `DbContext` in a test; and
`docker compose down -v && docker compose up --build` from an empty volume reaching a healthy `api`
with `/health` and `/health/ready` both 200. Real ticks have landed from the live GoldAPI account,
with the mid sanity-checked against a public spot quote (2026-09-07). The test suite is green.

### The three sources

Three free tiers rather than the paid GoldAPI upgrade, so the aggregate cadence is usable at $0 and
the failover chain has something real to fail over to (D-11).

| Priority | Code | Free tier | Outage coverage at 5 min | Notes |
|---|---|---|---|---|
| 1 | `api-ninjas` | ~10,000 / month | primary — funds the cadence | mid only, **gold only** |
| 2 | `goldapi.io` | 100 / calendar month | 0.3 days | bid + ask + provider mid |
| 3 | `metalprice-api` | ~1,000 / month | 3.5 days | mid only; query-parameter auth; quotes ounces per USD |

**API Ninjas is primary because it is the only tier that funds a useful cadence.** At five minutes
the feed needs ~8,928 requests in a worst-case 31-day period; GoldAPI's 100 funds one poll every
7h26m, at which the delta engine's 1m and 5m windows hold one sample each and return `null`
forever. The cost of the order is that the normal path carries **mid only** — GoldAPI's bid/ask
now appear only while it is serving as a fallback, and `Bid`/`Ask` are not comparable across
sources anyway (D-1 note), so they are per-source readings rather than a feed-wide spread.

Each keeps its own `api_quota_windows` ledger. They register through one private
`AddPriceSource<T>` helper in `PricingModule`, which attaches the `QuotaHandler` as part of
registering the client — a source registered without it compiles, works, and spends its budget
uncounted, which is the one wiring mistake in this module that produces no symptom at all. Sources
resolve as `IEnumerable<IPriceSource>` ordered by `Priority` rather than through keyed DI, so the
chain never carries its own list of key strings (D-12).

**The aggregate is not a budget.** Only the *primary* — the enabled entry with the lowest
`Priority` — has to fund the cadence for a whole period; backups are exempt and may exhaust
mid-outage, where the governor counts and clamps them cleanly (D-10). So the cadence ceiling is set
by whichever source sits at Priority 1, not by the ~11,100 total. The other ~11,000 buy outage
coverage, not speed.

GoldAPI's free-tier facts are checked against the account page rather than the docs — **100 requests
per calendar month, UTC** — so its `MonthlyRequestLimit` and `QuotaPeriod` reflect measured values,
and `PriceSourcesOptionsValidator` refuses to boot a cadence the primary cannot fund, naming the
minimum interval that fits. D-8 keeps us on the free tier for development and makes the paid tier a
gate on Phase 1 go-live, before anything user-facing ships. The tier is a configuration number and
nothing branches on 100 (D-9 removed the last place that did), so moving up is two environment
variables and a restart.

> **The other two tiers are taken from published documentation, not from an account page.** That
> now matters more than it did: `api-ninjas` is the primary, so its 10,000 is the single number the
> whole cadence rests on, and it is the one that has not been verified the way GoldAPI's 100 was.
> If the real tier is lower, or resets on a rolling window rather than a calendar month, the
> governor counts and clamps correctly against the *configured* number while the provider clamps
> against a different one — the divergence shows up as `ProviderRejectedAt` on a row that still
> reads as having budget. Confirming both against their account pages is worth doing before the
> failover chain starts leaning on them.

Not built yet: the failover chain and circuit breaker, the delta engine, the SignalR hub and the
REST endpoints — items 3 onward in `ops/phase-1-todo.md`. Until the chain lands,
`PricePollingService` selects the lowest `Priority` unconditionally: it does **not** filter on
`Enabled` or on remaining quota, so a disabled source is still registered and can be selected.

The three spikes were closed without being run: D-1 and D-2 are declared rather than measured, and
D-6 is deferred to Phase 2 planning. D-2 settles web on `lightweight-charts` and leaves the native
chart, and what a web/native split would cost BO-4, to Phase 4 where a physical device is actually in
play — simulator frame times will lie. Each entry says so in its own words; read the status line
before treating any of them as a measured result.

`ops/decisions/decisions.md` records what is decided and why, including the rejected alternatives —
**D-1 through D-12** so far. `ops/phase-1-todo.md` is the remaining work, ordered so each item is
verifiable when it is finished; D-13 onward are allocated there against the items that will take
them.

## Layout

```
docker-compose.yml           postgres (timescale+pgvector), ollama, api
ops/db/init/                 extension bootstrap, runs once on an empty volume
ops/decisions/               decision log (D-1 onward, IDs cited from code)
src/Aurum.Api/
  Modules/Pricing/           price sources, quota governance, polling
  Modules/Constants/         symbols shared across modules (SupportedSymbol)
  Modules/Macro/             macro series schema (ingestion lands in Phase 2a)
  Shared/                    DbContext, audit fields
  Migrations/
src/Aurum.Api.Tests/         integration tests against a real Postgres
```

Modules are folders inside one project, not separate assemblies (D-5). Each exposes a single
`Add<Module>Module` registration method, and `Program.cs` knows nothing else about module internals.

## Running

```bash
cp .env.example .env      # fill in POSTGRES_PASSWORD and the three provider API keys
docker compose up --build
```

The API applies migrations at startup. `/health` is liveness, `/health/ready` gates on Postgres.

Every provider key is `[Required]`, and `appsettings.json` ships each `ApiKey` as the empty string
on purpose: a missing key fails the process at boot rather than spending the month one 401 at a
time. A placeholder there would satisfy `[Required]` and put the hole straight back.

Startup migration and `PricePollingService` both assume a **single instance**. Both are wrong the
moment the API scales out — the poller would double-spend the quota, and two instances would race
the migration.

Requires Docker access. If `docker ps` says permission denied, add yourself to the `docker` group
(`sudo usermod -aG docker $USER`) and log back in — nothing here runs without it.

## Tests

```bash
dotnet test
```

38 tests. `ShippedConfigurationTests` binds the API's real `appsettings.json` and runs the validator
over it, because every other test here builds configuration in memory — which is how a five-minute
cadence shipped against a 100-request primary and failed only at `docker compose up`. Integration
tests spin up `timescale/timescaledb-ha:pg17` via Testcontainers rather than
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

Every source here bills on a period quota, and the smallest is 100 requests a month. An in-memory
rate limiter that resets when the container restarts can spend a month of requests in an afternoon,
and every individual request looks fine while it happens. Accounting is therefore durable (Postgres)
and enforced at the HTTP boundary as a `DelegatingHandler` — above it, Phase 1's retries and failover
chain would spend budget without being counted.

**The governor is per-source and knows nothing about any particular provider.** It takes a source
code, reads that source's `MonthlyRequestLimit` and `QuotaPeriod` from configuration, and accounts
against them. There is no shared budget, no default budget, and no branch on a provider name
anywhere in it — `RequireByCode` throws on an unconfigured source rather than guessing, because a
fabricated budget is indistinguishable downstream from a real one (D-9). Two period shapes are
supported, chosen per source: `CalendarMonthUtc` resets at 00:00 UTC on the first, and
`RollingThirtyDays` resets every 30 days from that source's own `PeriodAnchor` — providers really do
differ on this, and rolling our counter on a different day from theirs makes the ledger and the
account disagree with nothing in either saying so.

The counter lives in `api_quota_windows`, **one row per (source, accounting period)** — so the three
sources hold three independent ledgers that clamp independently, and one exhausting its budget says
nothing about the others. Acquiring a request is a single
`INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`, so the budget check and the increment happen
inside one row lock — a `SELECT` followed by an `UPDATE` leaves a gap where two callers both see the
last request available. Budget returns at a period boundary because the period key changes and the
next acquire creates a fresh row; nothing is scheduled and nothing is reset, and spent rows are kept
as history.

When a provider rejects us for quota, the clamp is written to that source's row as
`ProviderRejectedAt` and never by moving the counter — so a row can read "0 used of 10,000,
rejected," and that contradiction is the point. It is the only evidence that our count and that
provider's have diverged, which is worth more than making `Limit - Used` arithmetically tidy.
`QuotaStatus.Limit - Used` is therefore not remaining budget: once `ProviderRejected` is set,
remaining is zero whatever the counter says.

`PollInterval` and `MonthlyRequestLimit` are coupled, but only through the **primary**:
`PriceSourcesOptionsValidator` fails the process at boot on a cadence the lowest-`Priority` enabled
source cannot fund for a period, and tells you the minimum interval that fits. Changing one usually
means changing the other — or changing which source is primary. Holding *every* source to the full
period reads as the safe rule and is worse: it pegs the cadence to the smallest budget in the file,
so each added provider could only slow the feed (D-10).

The cadence is one feed-wide value, not a property of each source. A per-source `PollInterval` was
removed for that reason — the poller builds one `PeriodicTimer` and asks for one price per tick, so
every value but the primary's was read by nothing. A stale `PriceSources__GoldApiIo__PollInterval`
in an operator's `.env` binds to nothing and raises no error.

Every timestamp in that table comes from the injected `TimeProvider`, including the audit fields that
`AurumDbContext` stamps. The context takes the clock as a required constructor parameter for that
reason: an optional one with a wall-clock default compiles everywhere and reverts silently.

If you are about to replace that SQL with EF: read the class remarks on `PostgresQuotaGovernor`
first, and `ops/decisions/decisions.md` (D-7) for the decisions behind it.
