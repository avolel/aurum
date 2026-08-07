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
**Not yet executed — see the Docker blocker below.**

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

## Blocker: Docker is not usable from this account

`docker ps` → `permission denied` on `/var/run/docker.sock`. The `avolel` account is not in the
`docker` group, so neither `docker compose up` nor the Testcontainers integration tests can run.

```
sudo usermod -aG docker $USER   # then log out and back in
```

Until then these Phase 0 exit criteria are **unverified**:

- `docker compose up` produces a running API.
- The first migration applies against the real image.
- `price_ticks` is a hypertable with daily chunks and 30-day retention.
- pgvector is present (D-3's whole justification).
- The quota governor survives a container restart.

What *is* verified: the solution builds clean, and `dotnet ef migrations script --idempotent`
contains the extension creation, `create_hypertable`, `add_retention_policy` and the source seed.

---

## Left to implement

`Modules/Pricing/Quota/PostgresQuotaGovernor` is a deliberate stub throwing
`NotImplementedException`. Its contract, intended design, and the decisions left open (refund
policy on transport failure, denial log volume, authoritative clock) are documented in the class
remarks. `QuotaGovernorTests` encodes the contract as seven currently-failing tests, the first of
which is the Phase 0 exit criterion about surviving a restart.
