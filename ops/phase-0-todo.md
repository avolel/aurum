# Phase 0 — what's left to write

Everything below is yours. The scaffold is in place and builds; see `ops/decisions/phase-0.md`
for what's already decided and what's unverified.

Ordered so each item is verifiable when you finish it. Items 1–5 are the critical path to the
Phase 0 exit criteria; 6–8 are independent and can be done in any gap.

---

## 0. Unblock Docker  ·  5 min  ·  blocks 1, 2, 3, 5

```bash
sudo usermod -aG docker $USER   # then log out and back in
docker ps                        # must succeed before anything below works
```

Nothing in Phase 0 can be verified until this works. No code depends on it.

---

## 1. Verify the scaffold you were handed  ·  30 min

Do this *before* writing the governor, so a failure here is unambiguously mine and not yours.

- [x] `cp .env.example .env`, set `POSTGRES_PASSWORD`, leave the GoldAPI key blank for now.
- [x] `docker compose up --build` reaches a healthy `api`.
- [x] `curl localhost:8080/health` and `/health/ready` both 200.
- [x] `dotnet test --filter SchemaTests` — three tests, all green. These assert the hypertable
      exists with daily chunks, the 30-day retention policy is registered, and pgvector is
      installed. **If the pgvector one fails, D-3 was wrong and the image choice needs revisiting
      before anything else.**
- [x] `docker compose down -v && docker compose up --build` from clean — this is the §8
      portability NFR, and it rots silently if you don't check it now.

---

## 2. Implement `PostgresQuotaGovernor`  ·  the CORE piece  ·  DONE

`src/Aurum.Api/Modules/Pricing/Quota/PostgresQuotaGovernor.cs`. The class remarks carry the design
and full contract; `QuotaGovernorTests` and `QuotaHandlerTests` are the spec, 17 tests green.

- [x] **`ResolvePeriod`** — pure, with the two `[Theory]` cases pinning the calendar boundary.
      `RollingThirtyDays` takes its anchor from configuration and throws without one (D-7); the
      branch is unreachable while `QuotaPeriod` defaults to `CalendarMonthUtc`.
- [x] **`AcquireAsync`** — a single `INSERT … ON CONFLICT DO UPDATE … WHERE … RETURNING`. The three
      outcomes collapse into that one statement rather than being told apart in C#: a missing row is
      the `INSERT`, and both "budget spent" and "provider rejected" are the `WHERE` failing.
- [x] **Race safety** — `Concurrent_acquires_never_oversubscribe`, 40 callers against a budget of 10
      on separate DbContexts.
- [x] **`ReportProviderRejectionAsync`** — clamps via `ProviderRejectedAt`, never by moving the
      counter, and upserts so it can clamp a period that has no row yet.
- [x] **`GetStatusAsync`** — including the "no row yet" case.

Three decisions, recorded in `ops/decisions/phase-0.md` (D-7) with the reasoning and the rejected
alternatives, and each now pinned by a test rather than by the shape of the code:

- [x] **Refund policy.** No refunds. An over-count costs one poll; an under-count can cost the
      month. `QuotaHandlerTests.Transport_failure_still_spends_the_lease`.
- [x] **Denial logging.** Debug in the governor, naming the cause it read back; the once-per-episode
      operator line already lives in `PricePollingService`. No rate-limiting machinery — the governor
      is scoped, so per-instance memoisation would suppress nothing.
      `Denial_after_provider_rejection_names_the_provider` and its negative twin.
- [x] **Authoritative clock.** `TimeProvider` exclusively, including
      `AurumDbContext.ApplyAuditFields`, which was still on `DateTimeOffset.UtcNow` and is the
      reason this box could not honestly be ticked earlier.
      `Audit_timestamps_come_from_the_injected_clock`.

Found and fixed while pinning the above: `ReportProviderRejectionAsync` used an `UPDATE`, which
changes zero rows and reports success when the period has no row yet — losing the clamp whenever a
request straddles a period boundary. Now an upsert; see D-7, "Clamping a period with no row."

---

## 3. Prove it survives a real restart  ·  1 hr  ·  exit criterion

The unit test simulates a restart with a fresh `DbContext`. That is not the same claim as the
Phase 0 exit criterion, which is about a container.

- [x] Set `MonthlyRequestLimit` to something small (say 5) and a short `PollInterval` in
      `appsettings.Development.json` so you can burn the budget in minutes.
- [x] Let it spend 3 of 5. `docker compose restart api`. Confirm from the logs and from
      `SELECT * FROM api_quota_windows` that it resumes at 3, not 0.
- [x] Let it hit 5. Confirm the poller sleeps until the period end rather than spinning.
- [x] Revert the test config.

---

## 4. Nail down the GoldAPI facts  ·  30 min  ·  blocks 5

I could not verify either of these, and both are silent-failure shaped.

- [ ] **Actual free-tier request limit** from your account page. Set `MonthlyRequestLimit` to it.
- [ ] **Reset semantics** — calendar month UTC or rolling 30 days from signup? Set `QuotaPeriod`
      accordingly. Getting this wrong is a one-in-twelve failure you won't notice for a year.
- [ ] Set `PollInterval` to fit. `GuardPollBudget` will refuse to start if it doesn't, and will
      tell you the minimum.
- [ ] **Decide what to do about the finding**: ~100 requests/month is ~3 polls/day, which cannot
      support Phase 1's live dashboard. Paid tier, different primary source, or re-scope Phase 1 —
      record the call in the decision log.

---

## 5. End-to-end: real ticks on a schedule  ·  1 hr  ·  exit criterion

- [ ] Real API key in `.env`, `docker compose up`.
- [ ] `SELECT * FROM price_ticks ORDER BY "ObservedAt" DESC LIMIT 10;` — real prices, sane
      `ObservedAt`/`ReceivedAt` gap, `Symbol = 'XAUUSD'`, `SourceCode = 'goldapi.io'`.
- [ ] `SELECT * FROM price_sources;` — `LastSuccessAt` advancing.
- [ ] Sanity-check the mid against a public spot quote. `PriceQuote.Normalize` prefers GoldAPI's
      `price` field over `(bid+ask)/2`; confirm that's the number you actually want on the chart.

---

## 6. D-2 — charting spike  ·  1–2 days  ·  exit criterion, independent

Throwaway Expo app, not in this repo. 30k synthetic ticks.

- [ ] `@shopify/react-native-skia` custom chart — pan/zoom, frame times on web and on a physical
      phone.
- [ ] Web/native split: `lightweight-charts` on web, `victory-native` on native — same measurements.
- [ ] Write the numbers into `ops/decisions/phase-0.md` with the decision. If the split wins,
      note explicitly what that costs BO-4 ("one codebase") so the tradeoff is on record.

You need a physical device for this. Simulator frame times will lie to you.

---

## 7. D-1 — Expo vs bare RN  ·  falls out of 6

- [ ] Decide and record. Nothing in the backend constrains it; the spike in item 6 will tell you
      whether any native dep you need is awkward under Expo.

---

## 8. D-6 — Ollama benchmark  ·  half a day  ·  exit criterion, independent

Read the D-6 section of `ops/decisions/phase-0.md` first — 12GB VRAM rules out the plan's stated
fallback, so this measurement matters more than it was going to.

- [ ] `ollama pull` a 14B-class q4_K_M candidate and a 4B-class small model.
- [ ] Record real tokens/sec for each on your hardware.
- [ ] **Measure the model-swap cost**: time a 4B call immediately after a 14B call and see whether
      Ollama evicted and reloaded. This is what decides whether the Phase 2 model registry can map
      roles to different models at all, or has to reuse one model with different prompts.
- [ ] Back-of-envelope the §12 SLA: dozens of events/day × (context + generation + validation pass).
- [ ] Record the model mix in the decision log.

---

## Done when

- [ ] Ticks landing in Postgres on a schedule (5)
- [ ] Quota governor demonstrably surviving a container restart (3)
- [ ] D-1 and D-2 decided in writing with spike numbers attached (6, 7)
- [ ] Measured tokens/sec for the chosen model mix + the SLA envelope check (8)
- [ ] `docker compose down -v && docker compose up` from clean still works (1, re-run at the end)
