# Phase 0 decision log

Status of the cross-cutting decisions from `plans/aurum-phased-plan.md`. Anything marked OPEN
blocks a Phase 0 exit criterion.

## Measured environment

| | |
| --- | --- |
| GPU | NVIDIA GeForce RTX 5070 Ti Laptop, **12 GB VRAM** |
| .NET SDK | 10.0.110 |
| Docker / Compose | 29.6.2 / v5.3.1 |
| Node | 24.11.0 |

## D-1 — Expo vs bare React Native

**OPEN.** No app shell created yet. Recommendation stands (Expo + EAS); nothing in the backend
constrains it, so this can be decided alongside the D-2 spike.

## D-2 — Charting library

**OPEN.** Requires the 30k-tick spike. Not started.

## D-3 — Postgres image

**DECIDED: `timescale/timescaledb-ha:pg17`.** Tag confirmed to exist on Docker Hub. Extensions are
created in `ops/db/init/01-extensions.sql`, which runs once as superuser on an empty data volume —
so first boot fails loudly if the image ever stops shipping TimescaleDB or pgvector.

`SchemaTests.Pgvector_is_available_for_phase_two` asserts pgvector is actually present.
**Executed and passing** against the real image (2026-08-10).

Note for Phase 5: `AurumDbContext.OnModelCreating` also declares both extensions, so the migration
emits `CREATE EXTENSION IF NOT EXISTS`. That is harmless today only because the compose app role
*is* the bootstrap superuser. When a least-privilege application role is introduced, the migration's
`CREATE EXTENSION` will start mattering and the init script must be the sole owner.

## D-4 — Symbol on price entities

**DONE.** `PriceTick.Symbol` (`XAUUSD` default, `PriceSymbols.Gold`) exists in the first migration.

Two consequences worth knowing:

- `price_ticks` has a **composite primary key** `(ObservedAt, Id)`. TimescaleDB rejects any unique
  index that omits the partitioning column, so a surrogate-only PK makes `create_hypertable` fail.
- Hypertable conversion and the 30-day retention policy were **moved into the first migration**
  (the phase plan had them in Phase 1). On an empty table this is a DDL statement; on a quarter of
  accumulated ticks it is a data migration.

## D-5 — Module boundaries

**DONE.** Folder-per-module inside `Aurum.Api`, per the plan's repo layout. `Modules/Pricing` and
`Modules/Macro` exist; each module exposes a single `Add<Module>Module` extension method as its
registration seam.

## D-6 — Ollama host sizing

**OPEN, and the plan's assumption does not survive contact with the hardware.**

12 GB VRAM. A 30B-class model at q4_K_M is ~18 GB and will not fit; the plan's fallback of "bigger
local models" (§10) is not available. Realistic ceiling for a fully-resident primary is
**14B-class at q4_K_M (~9 GB)**.

The non-obvious consequence for Phase 2's model registry: at 12 GB you can hold roughly *one*
useful model resident. A 14B primary (~9 GB) plus a 4B relevance model (~3 GB) sits at the VRAM
edge, and Ollama will begin evicting between roles — turning a 200 ms relevance call into a 15 s
model load. **The registry's real constraint is minimising the number of distinct resident models**,
which pushes toward one 14B serving primary/validator/summariser via different prompts, plus one
embedding model.

Throughput is not the risk: ~40–60 tok/s on a 14B q4 puts a 400-token explanation at ~10 s, so the
5-minute SLA (§12/§16) is comfortable. **The risk is Phase 2's ≥70% first-pass validation at ≥60%
confidence.** Measure it early; if it misses, the answer has to be prompt and retrieval engineering,
not a bigger model.

Still to do: `ollama pull` the candidates and record real tokens/sec.

---

## Findings that change the plan

### GoldAPI's free tier cannot support a live dashboard

GoldAPI free is on the order of **100 requests per month**. Fitting that budget means a poll
interval of ~8 hours — the configured default in `appsettings.json`, and the reason
`PricePollingService.GuardPollBudget` refuses to start on a cadence that would overspend.

Three polls a day is not "live price" and cannot meet Phase 1's "price visible within 2× polling
interval" (§8) or FR-1.x in any meaningful sense. The quota governor keeps this honest instead of
failing quietly, but it does not make the tier usable. **Phase 1 needs either a paid tier or a
different primary source, and §12's cost model needs revisiting.** Confirm the actual free-tier
number against your own account before acting on this.

### GoldAPI's quota reset semantics are unverified

`GoldApiIoOptions.QuotaPeriod` defaults to `CalendarMonthUtc`. Whether the provider actually resets
on the calendar month or on a rolling 30 days from signup determines the governor's period key, and
a wrong choice is a silent one-in-twelve failure. **Verify against the account page, not the docs.**

---

## Exit criteria status

The earlier Docker blocker (`permission denied` on `/var/run/docker.sock`) is **resolved** — the
account is in the `docker` group and Testcontainers runs. `dotnet test` is green: 11 tests against
a real `timescale/timescaledb-ha:pg17` container.

Verified by that run:

- The first migration applies against the real image (the fixture migrates before every collection).
- `price_ticks` is a hypertable with daily chunks and a 30-day retention policy.
- pgvector is present — D-3's whole justification.
- The quota governor survives a restart: `Budget_survives_a_restart` spends three of five requests,
  drops the `DbContext`, and a second governor over a fresh context reads `Used = 3`, not zero.

Still **unverified**: `docker compose up` producing a running API. The tests exercise the database
image and the schema, not the compose topology or the API container.

---

## D-7 — Quota governor

**DONE.** `Modules/Pricing/Quota/PostgresQuotaGovernor` implements `IQuotaGovernor`;
`QuotaGovernorTests` (7 tests, 8 cases) passes.

Design is option A from the Phase 0 review — Postgres *is* the bucket. Acquire is a single
`INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`, so the budget check and the increment happen
inside one row lock and there is no window for a second caller to act on a stale count. The three
outcomes the original sketch had to disambiguate collapse into that one statement: a missing row is
the `INSERT`, and both "budget spent" and "provider rejected" are the `WHERE` failing. See the class
remarks for why this is raw SQL in an EF codebase.

### Anchor for `RollingThirtyDays`

**Supplied by the caller from configuration; `ResolvePeriod` throws if it is missing.** The anchor is
the provider's signup date — a fact only the provider holds — so there is no safe default.

Rejected `PriceSource.CreatedAt`: that records when *we* registered the source in our own database, a
different event, and using it would offset every period boundary by the gap between signup and first
deploy — silently, permanently, and in a way nothing would ever flag. Rejected deriving it from the
earliest `api_quota_windows` row: circular, and it would make boundaries depend on when the process
first happened to start.

Not implemented in Phase 0. `GoldApiIoOptions.QuotaPeriod` defaults to `CalendarMonthUtc`, so the
rolling branch is unreachable until the account page is checked (see the unverified-reset-semantics
finding above).

### Refunds on transport failure

**No refund path exists.** A lease taken before a request that then fails in transit stays spent.

The two failure modes are not symmetric. Not refunding leaks one request per network blip: bounded,
self-limiting, and visible as `RequestsUsed` drifting above real usage. Refunding risks unbounded
overspend against a hard monthly cap whose exhaustion is invisible until the provider starts
returning 429 — and on GoldAPI free that means no prices for the rest of the month. An over-count
costs one poll; an under-count can cost the month. The 8-hour cadence already leaves ~7 requests of
headroom against the 100-request budget, which absorbs the expected leak.

Consequence: `IQuotaGovernor` deliberately has no refund method. Adding one later is an interface
change, which is the right amount of friction for a decision this asymmetric.

### Denial logging

**Debug level in the governor; no rate-limiting machinery.**

Per-instance memoisation was considered and discarded: the governor is registered scoped, so a
"have I already logged this period?" field would be reconstructed on every call and never suppress
anything. The operator-facing event already exists one layer up — `PricePollingService` catches
`QuotaExhaustedException`, logs once at Error, and sleeps until `ResetsAt`, so a spent budget
produces one line per exhaustion episode rather than one per poll.

Known wart: the governor's debug line says "no budget left" even when the real cause was a provider
rejection, because the acquire statement cannot distinguish the two. Worth fixing if that log is ever
promoted above Debug.

### Authoritative clock

**The application clock (`TimeProvider`), exclusively.** `now()` never appears in the governor's SQL;
every timestamp written to `api_quota_windows` — `PeriodStartsAt`, `PeriodEndsAt`, `CreatedAt`,
`UpdatedAt` — is a parameter bound from the injected clock.

The period key is computed in C#, so the database clock could only be authoritative if the key were
computed in SQL too — which would make `FakeTimeProvider` unable to roll a period, and `ResolvePeriod`
untestable as a pure function. The failure this avoids is the mixed one: a key derived from the app
clock enforced against a boundary written under the database clock, which disagree by exactly the
skew and only near a rollover.

Residual risk: two instances with skewed clocks could briefly create separate rows either side of a
boundary, over-allocating budget for the width of the skew. Acceptable while `PricePollingService` is
single-instance by construction. If the API is ever scaled out, revisit this together with that
service's hosting note.

### Reading remaining budget

`ReportProviderRejectionAsync` stamps `ProviderRejectedAt` and deliberately leaves `RequestsUsed`
alone, so **`Limit - Used` is not the remaining budget** once the provider has rejected us — the
`ProviderRejectedAt IS NULL` predicate in the acquire statement is what clamps it to zero.

Setting `RequestsUsed = RequestLimit` would have made remaining arithmetically correct for any
reader, but it destroys the gap between what we counted and what the provider counted — the only
evidence we get that our accounting is drifting, and exactly what `QuotaHandler` logs on a 429.
