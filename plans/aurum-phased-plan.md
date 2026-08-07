# Aurum — Engineering Phase Breakdown

## Context

`aurum_brd.md` defines a gold price intelligence platform (ASP.NET 10 modular monolith, PostgreSQL, EF Core, React Native Web, Docker, self-hosted Ollama) and a Phase 0–7 business roadmap in §15. That roadmap names milestones but not work. This document decomposes each phase into executable engineering deliverables with exit criteria, sized for a **single developer working sequentially**.

`/home/avolel/Code/aurum` is currently empty. Everything below is greenfield.

Two things I'd flag before you commit to the sequence, then I'll build against it as written:

1. **Phase 1 ships a product Aurum's own problem statement says is worthless.** Live price + chart with no explanations is exactly the "shows what, not why" tool §3 defines as the competition. Recommend Phase 1 and Phase 2 be treated as one internal release with no public launch between them — keep the numbering, skip the marketing beat.
2. **Phases 3 and 4 both target Q1 2027.** Solo, they cannot overlap. This doc sequences them; assume Phase 4 slips to Q2 2027.

---

## Cross-cutting decisions to lock in Phase 0

These are expensive to reverse later. Each needs a call before the first line of Phase 1.

| # | Decision | Recommendation | Why it can't wait |
| --- | --- | --- | --- |
| D-1 | RN app shell: Expo vs bare React Native | **Expo (SDK + EAS Build)** | `react-native-web`, push notifications (FR-4.2), and iOS/Android CI builds (Phase 4) all come free. Bare RN means hand-rolling three build pipelines solo. Constrains every native dep chosen from here on. |
| D-2 | Charting library (FR-6.4, 60fps, canvas-based) | Spike two: `@shopify/react-native-skia` custom chart vs a web/native split (`lightweight-charts` on web, `victory-native` on native) | TradingView's `lightweight-charts` is the best-in-class option and is **web-only** — the "one codebase" objective (BO-4) may cost you the best chart. Decide with a real 30k-tick spike, not a doc. |
| D-3 | Postgres image | `timescale/timescaledb-ha` (bundles TimescaleDB **and** pgvector) | Avoids building a custom image later when Phase 2 needs pgvector. |
| D-4 | Symbol on price entities | Add `Symbol` (default `XAUUSD`) to `PriceTick`/`PriceEvent` in the **first** migration | Phase 6 is multi-metal. A nullable column now costs nothing; retrofitting a partitioned, hypertabled tick store with 2 years of rows is a migration weekend. |
| D-5 | Module boundary enforcement | Folder-per-module inside one `Aurum.Api` project, not separate class libraries | Solo dev: assembly boundaries buy discipline you can enforce by hand and cost you constant project-reference friction. Extract to libraries only if a module needs independent deployment. |
| D-6 | Ollama host sizing | Pick the model mix against real hardware before Phase 2 planning | §7.2's "70B-class" is aspirational. If you're on a 24GB consumer GPU, the primary causation model is a ~30B quant and the whole quality mitigation story (§9.4) matters more. |

**Repo layout** (D-5 concrete form):

```
/docker-compose.yml            postgres, ollama, api, (later) caddy
/src/Aurum.Api/
  Modules/Pricing/             Entities, Sources, DeltaEngine, Endpoints, Jobs
  Modules/Macro/
  Modules/News/
  Modules/Causation/
  Modules/Alerts/
  Modules/Identity/
  Modules/ApiPlatform/
  Shared/                      EF DbContext, audit fields, Ollama client, config
  Hubs/                        SignalR
/src/Aurum.Api.Tests/
/app/                          Expo + react-native-web
/ops/                          seed data, prompt templates (versioned)
```

Modules talk via in-process `Channel<T>` and MediatR-style handlers — the BRD's stated Kafka replacement (§9.1).

---

## Phase 0 — Foundation (Q3 2026)

**Goal:** `docker compose up` produces a running API writing real gold prices to Postgres, and D-1/D-2 are answered.

**Work**
- `docker-compose.yml`: postgres (D-3, volume-mounted), ollama (volume for model cache), api. Health checks on all three.
- ASP.NET 10 Web API skeleton: Serilog structured logging, OpenAPI, `/health`, `/health/ready`, options-bound config, secrets via env.
- EF Core `AurumDbContext` + first migration. Entities: `PriceTick`, `PriceSource`, `MacroSeries`, `MacroObservation`. Audit fields (`CreatedAt`, `UpdatedAt`) on a base entity per §9.1.
- `IPriceSource` abstraction + `GoldApiIoSource` implementation. Normalize to the canonical tick (FR-1.2: `timestamp, bid, ask, mid, source`).
- **Quota governor**: persisted token-bucket per source. GoldAPI's free tier is a *monthly* quota — an in-memory limiter that resets on container restart will burn a month of requests in a day. This is the single most likely way Phase 0 fails quietly.
- Charting spike (D-2) in a throwaway Expo app: 30k synthetic ticks, pan/zoom, measure frame times on web + a real phone.
- `ollama pull` of candidate models; measure tokens/sec on your actual hardware for D-6.

**Exit criteria**
- Ticks landing in Postgres on a schedule, quota governor demonstrably surviving a container restart.
- D-1 and D-2 decided in writing with spike numbers attached.
- Measured tokens/sec for the chosen model mix, and a back-of-envelope check that "dozens of events/day × explanation prompt size" fits the 5-minute SLA (§12).

---

## Phase 1 — MVP: Live Tracking (Q4 2026)

**Goal:** BR-01, BR-02, FR-1.x complete. Web dashboard shows live price and flags significant moves.

**Work**
- Two more `IPriceSource` implementations + failover chain (FR-1.3): ordered providers, circuit breaker per source, source + freshness recorded on every tick (mitigates the §14 "delayed data misleading users" risk — surface it in the UI, not just the DB).
- Server-side latest-quote cache; one upstream poll fans out to all clients (§12's core cost assumption).
- **Delta engine** (FR-1.4). *This is the first genuinely interesting piece.* Rolling deltas over 1m/5m/15m/1h/4h/1D. Design fork:
  - *In-memory ring buffer per window*, rehydrated from Postgres on startup — O(1) per tick, trivially testable, but state lives in one process (fine: the poller is a singleton, only the API is horizontally scaled).
  - *SQL window functions on every tick* — stateless and scale-out-safe, but a query per tick per window.
  - Recommend the ring buffer; the poller is inherently a singleton anyway, so the scale-out argument for SQL is hypothetical.
- Significance classifier → `PriceEvent` rows with direction, magnitude, velocity, volatility (FR-1.4). Thresholds in config, not code (BR-02 says "configurable").
- `price_ticks` as a TimescaleDB hypertable, daily chunks, 30-day hot retention (§11).
- SignalR `PriceHub`: tick + event channels (FR-5.3).
- Expo/RNW app: Live Dashboard, chart from D-2, SignalR client, financial disclaimer component used app-wide (BR-12).
- `/v1/price/live`, `/v1/price/history`, `/v1/events`.

**Exit criteria**
- FCP < 1.5s on web (§16); price visible within 2× polling interval (§8).
- Kill the primary provider → failover happens without a client-visible gap.
- Replaying a historical volatile session produces the expected `PriceEvent` set.

---

## Phase 2 — AI Causation Engine v1 (Q4 2026)

**Goal:** BR-03, BR-04, BR-05, BR-13 and FR-2.x. Every significant event gets an explanation within 5 minutes.

**Dependency the BRD doesn't phase:** FR-2.1 context assembly requires macro data (4h lookback) and news (8h lookback). Neither has a phase assigned before Phase 3. Both must land here, at minimum thickness.

**2a — Macro ingestion** (BR-07)
- FRED client (CPI, PCE, Fed Funds, 10Y/2Y, dollar index, VIX), BLS client (CPI detail, NFP), Treasury FiscalData client.
- Scheduled hosted services; release-calendar awareness so a CPI print is ingested promptly rather than on the next poll.
- `/v1/macro/snapshot`.

**2b — Thin news slice**
- 3 RSS feeds only (Kitco, MarketWatch, CNBC), stored as headline/link/snippet/metadata per FR-3.7. No scoring, no sentiment, no trust — those are Phase 3.

**2c — Ollama integration**
- `IOllamaClient`: chat + embeddings, streaming, timeouts, retry.
- **Model registry**: config maps *roles* (primary / validator / summarizer / relevance / sentiment / embedding) to model names, per §7.2's table. Never hardcode a model name at a call site — quarterly model refresh (§9.4) must be a config change.
- Force structured output via Ollama's `format` JSON-schema parameter rather than parsing prose. Open-weight models drift badly on free-form output; this is the cheapest quality win available.

**2d — pgvector knowledge base** (FR-2.2)
- `ExplanationEmbedding` table, `vector` column, HNSW index, cosine distance.
- Backfill: embed historical price movements + their macro context so retrieval isn't empty on day one. Without a seed corpus, similar-movement retrieval returns noise for months.

**2e — Explanation pipeline**
- `Channel<PriceEvent>` → context assembler (FR-2.1) → generator (FR-2.3) → validator (FR-2.4) → persist → SignalR `explanation-ready`.
- **Priority queue**: explanations preempt digests (§14 burst mitigation). One Ollama request in flight at a time unless hardware says otherwise.
- Entities: `Explanation`, `ExplanationFactor` (taxonomy from FR-2.5, per-factor confidence), `ExplanationSource` (3–5 links, BR-04).
- Auditability (§8): store model name, model version, **prompt template version**, confidence, validation score on every row. Prompt templates live in `/ops/prompts/` and are versioned files, not string literals.
- `AwaitingConfirmation` state on validation failure (BR-13) — a status enum, and the UI must render it distinctly, not hide it.
- Explanation Feed screen; progressive loading per FR-6.3 (price first, explanation streamed in).

**Exit criteria**
- 100% of events from a replayed volatile week get an explanation ≤ 5 min (§16).
- ≥ 70% pass validation first attempt with confidence ≥ 60% — **measure this before you believe it.** If the chosen open-weight model can't clear it, that's D-6 feeding back, and the answer per §10 is bigger local models, not a hosted API (§13 forbids it).
- Explanations replayable from stored prompt version + model version.

---

## Phase 3 — News Intelligence (Q1 2027)

**Goal:** BR-06, FR-3.x. 10+ sources, sentiment index, trust scoring.

**Work**
- **Per-source parser isolation** (§14 risk): one adapter per source, a failure in one cannot stall ingestion of others. Ingestion failure rate metric + alert at the <1% daily target (§16).
- Source set: 7 RSS (Kitco, MarketWatch, CNBC, Mining.com, Investing.com, BullionVault, WGC) + 4 API (Marketaux, Finnhub, Alpha Vantage, GNews/NewsData) — each API source needs its own quota governor from Phase 0.
- SimHash content fingerprinting → `content_hash` dedup (FR-3.2).
- Relevance scoring 0.0–1.0 via small Ollama model with structured output; the 0.60/0.80 thresholds route to archived / background / Active Feed (FR-3.3).
- Topic tagging to the 8 labels (FR-3.4), sentiment BULLISH/NEUTRAL/BEARISH (FR-3.5).
- Hourly Sentiment Index + "Conflicting Signals" flag when sentiment diverges from price direction (FR-3.5).
- Trust Score per source, weighted 30/25/20/15/10 (FR-3.6). Pre-model the deferred paid wires (Reuters, Bloomberg, FT, WSJ) so §9.3's "drop-in when licensed" claim is actually true.
- Full-text search via `tsvector` + GIN index (§9.1, replaces Elasticsearch).
- News Intelligence screen; Phase 2's thin slice retired in favor of the full pipeline feeding causation context.

**Exit criteria**
- ≥ 10 sources ingesting, < 1% daily failure rate.
- Dedup verified against a day where the same wire story ran on 4 sources.
- Explanation quality re-measured — richer news context should move the Phase 2 validation numbers.

---

## Phase 4 — Mobile Apps & Alerts (Q1→Q2 2027, solo)

**Goal:** BR-08, BR-09, FR-4.x, FR-6.1.

**Work**
- Alert entities + evaluation engine: price thresholds, % moves, new explanations matching selected causal factors (FR-4.1). Evaluate off the existing delta-engine and explanation-published events — no new polling.
- Push via Expo Notifications (free); email via a free-tier transactional provider. Per-user alert history (FR-4.2).
- Digest generation (FR-4.3): daily narrative + top 3 stories + factor summary, weekly summary. Runs on the low-priority Ollama queue from Phase 2e.
- iOS + Android builds via EAS. Budget real time for app store review, permissions, icons, and the first rejection.
- Alerts Manager + Daily Digest screens.

**Exit criteria**
- Both stores accepted; a threshold alert delivers to a real device end-to-end.
- Digest generation never delays an explanation past SLA under load.

---

## Phase 5 — API Platform (Q2 2027)

**Goal:** BR-10, FR-5.1, FR-5.4.

**Work**
- Identity: registration, JWT RS256 with refresh rotation (§8), profile, notification prefs.
- API key issuance/rotation per user.
- Tiered rate limiting via ASP.NET's rate limiting middleware, partitioned by API key: Free 30/min, Starter 300/min, Pro 1000/min (FR-5.4).
- Tier-gated **history depth** (30d / 1y / 5y) and **stream delay** for free tier — enforced in query filters and the SignalR hub, not just documented.
- Stripe billing, developer portal, published OpenAPI.
- 99.9% target (§8): Postgres read replica, metrics/alerting, backup + restore drill.
- SOC 2 readiness assessment (§8), GDPR deletion endpoint (BR-15), explanation flagging loop (BR-14) with a review queue.

**Exit criteria**
- Load test confirms each tier's limits; free-tier key provably cannot read 5-year history or an undelayed stream.
- A GDPR deletion request removes all PII across every table.

---

## Phase 6 — Advanced AI & Multi-Metal (Q3 2027)

Generalize on the `Symbol` column from D-4: silver, platinum, palladium sources, per-metal thresholds, per-metal causal taxonomy weights. Quarterly model refresh (§9.4) formalized as a benchmark suite replaying a fixed event set against candidate models so upgrades are measured, not vibes.

## Phase 7 — Enterprise (Q4 2027)

Refinitiv/LSEG feed behind the existing `IPriceSource` abstraction, paid wire services behind the existing news adapters, white-label theming, Bloomberg Terminal integration.

---

## Verification

**Per phase, before moving on:**
- `docker compose down -v && docker compose up` from a clean checkout reaches a working system — this is the portability NFR (§8) and it rots silently if unverified.
- Integration tests against a Testcontainers Postgres, not mocks, for anything touching TimescaleDB or pgvector (both behave differently from vanilla).
- External API clients tested against recorded fixtures; a separate, manually-run smoke suite hits the real providers so quota isn't burned in CI.

**Phase-specific:**
- **P1:** replay a recorded volatile trading session through the delta engine; assert the `PriceEvent` set. Chaos-test failover by blackholing the primary source.
- **P2:** a golden set of ~20 historical price events with human-written expected causes; score generated explanations against it. Re-run on every prompt or model change — this is the only defense against silent quality regression when you swap models.
- **P3:** dedup and relevance scored against a hand-labeled article set.
- **P4:** end-to-end alert delivery on physical devices.
- **P5:** k6/Bombardier load test per tier; manual GDPR deletion audit.

## Open questions

- D-2's outcome may force a web/native chart split, which dents BO-4. Acceptable?
- API Ninjas' free tier prohibits commercial use (§12) — it must be replaced or paid before Phase 5 billing goes live, not Phase 1.
- §11's warm/cold tiers (object storage, Parquet) have no phase assigned. Suggest Phase 5, when data volume first justifies it.
